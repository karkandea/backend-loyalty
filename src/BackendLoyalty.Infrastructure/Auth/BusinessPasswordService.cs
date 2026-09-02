using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Auth;

public sealed class BusinessPasswordService(
    StandaloneAuthDbContext db,
    IStandaloneCredentialService credentials,
    IRefreshTokenSessionService refreshSessions) : IBusinessPasswordService
{
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan ResetCooldown = TimeSpan.FromMinutes(1);

    public async Task<PasswordResetIssue?> CreateResetAsync(
        string email,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var candidates = await db.BusinessUsers
            .Where(x => x.IsActive && x.Email.ToLower() == normalizedEmail)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
            return null;

        var groups = candidates
            .GroupBy(x => string.IsNullOrWhiteSpace(x.AuthUserId) ? x.Id : x.AuthUserId!)
            .ToList();

        if (groups.Count != 1)
            return null;

        var group = groups[0].ToList();
        var userId = groups[0].Key;
        var canonicalEmail = group[0].Email.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        var recentlyIssued = await db.AuthPasswordResets.AnyAsync(
            x => x.UserId == userId &&
                 x.AuthKind == "business" &&
                 x.UsedAt == null &&
                 x.CreatedAt > now - ResetCooldown,
            cancellationToken);

        if (recentlyIssued)
            return null;

        await db.AuthPasswordResets
            .Where(x => x.UserId == userId && x.AuthKind == "business" && x.UsedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = now + ResetLifetime;

        db.AuthPasswordResets.Add(new AuthPasswordReset
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AuthKind = "business",
            Email = canonicalEmail,
            TokenHash = Hash(rawToken),
            ExpiresAt = expiresAt,
            UsedAt = null,
            Ip = NullIfBlank(ip),
            UserAgent = NullIfBlank(userAgent),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken);
        return new PasswordResetIssue(userId, canonicalEmail, rawToken, expiresAt);
    }

    public async Task<PasswordResetResult> ResetPasswordAsync(
        string rawToken,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var tokenHash = Hash(rawToken.Trim());
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var reset = await db.AuthPasswordResets
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.TokenHash == tokenHash &&
                     x.AuthKind == "business" &&
                     x.UsedAt == null &&
                     x.ExpiresAt > now,
                cancellationToken);

        if (reset is null)
            return PasswordResetResult.InvalidOrExpired;

        var consumed = await db.AuthPasswordResets
            .Where(x => x.Id == reset.Id && x.UsedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.UsedAt, now),
                cancellationToken);

        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PasswordResetResult.InvalidOrExpired;
        }

        var rows = await ResolveRowsAsync(reset.UserId, cancellationToken);
        if (rows.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PasswordResetResult.InvalidOrExpired;
        }

        var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        foreach (var row in rows)
        {
            row.PasswordHash = newHash;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await DeleteLegacyBridgeAsync(reset.UserId, cancellationToken);
        await MarkPasswordSetAsync(reset.UserId, now, cancellationToken);
        await refreshSessions.RevokeAllAsync(
            reset.UserId,
            "business",
            "password_reset",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return PasswordResetResult.Success;
    }

    public async Task<PasswordUpdateResult> UpdatePasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var rows = await ResolveRowsAsync(userId, cancellationToken);
        if (rows.Count == 0)
            return PasswordUpdateResult.UserNotFound;

        var email = rows[0].Email;
        BusinessLoginProfile profile;
        try
        {
            profile = await credentials.AuthenticateBusinessAsync(
                email,
                currentPassword,
                cancellationToken);
        }
        catch (CredentialException)
        {
            return PasswordUpdateResult.InvalidCurrentPassword;
        }

        if (!string.Equals(profile.UserId, userId, StringComparison.OrdinalIgnoreCase))
            return PasswordUpdateResult.InvalidCurrentPassword;

        var now = DateTime.UtcNow;
        var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        foreach (var row in rows)
        {
            row.PasswordHash = newHash;
            row.UpdatedAt = now;
        }

        await db.AuthPasswordResets
            .Where(x => x.UserId == userId && x.AuthKind == "business" && x.UsedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.UsedAt, now),
                cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await DeleteLegacyBridgeAsync(userId, cancellationToken);
        await MarkPasswordSetAsync(userId, now, cancellationToken);
        await refreshSessions.RevokeAllAsync(
            userId,
            "business",
            "password_changed",
            cancellationToken);

        return PasswordUpdateResult.Success;
    }

    public async Task<PasswordPolicyStatus> GetPasswordPolicyAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "requiresPassword", "graceExpiresAt", "passwordSetAt"
                FROM "AuthPasswordPolicy"
                WHERE "authUserId" = @userId
                LIMIT 1
                """;
            AddParameter(command, "userId", userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new PasswordPolicyStatus(false, null, null, false);

            var requiresPassword = reader.GetBoolean(0);
            DateTime? graceExpiresAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTime>(1);
            DateTime? passwordSetAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2);
            var isExpired = requiresPassword && graceExpiresAt is not null && graceExpiresAt <= DateTime.UtcNow;

            return new PasswordPolicyStatus(
                requiresPassword,
                graceExpiresAt,
                passwordSetAt,
                isExpired);
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    public async Task<RequiredPasswordSetResult> SetRequiredPasswordAsync(
        string userId,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var policy = await GetPasswordPolicyAsync(userId, cancellationToken);
        if (!policy.RequiresPassword)
            return RequiredPasswordSetResult.AlreadySet;

        var rows = await ResolveRowsAsync(userId, cancellationToken);
        var standaloneEmail = rows.Count == 0
            ? await GetActiveStandaloneIdentityEmailAsync(userId, cancellationToken)
            : null;

        if (rows.Count == 0 && standaloneEmail is null)
            return RequiredPasswordSetResult.UserNotFound;

        var now = DateTime.UtcNow;
        var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        if (rows.Count > 0)
        {
            foreach (var row in rows)
            {
                row.PasswordHash = newHash;
                row.UpdatedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
            await DeleteLegacyBridgeAsync(userId, cancellationToken);
        }
        else
        {
            await UpsertStandalonePasswordAsync(
                userId,
                standaloneEmail!,
                newHash,
                cancellationToken);
        }

        await MarkPasswordSetAsync(userId, now, cancellationToken);
        await refreshSessions.RevokeAllAsync(
            userId,
            "business",
            "required_password_set",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return RequiredPasswordSetResult.Success;
    }

    private Task<List<BusinessUser>> ResolveRowsAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(userId, out var guid))
        {
            var normalized = guid.ToString();
            return db.BusinessUsers
                .Where(x => x.Id == userId || x.AuthUserId == normalized)
                .ToListAsync(cancellationToken);
        }

        return db.BusinessUsers
            .Where(x => x.Id == userId)
            .ToListAsync(cancellationToken);
    }

    private async Task<string?> GetActiveStandaloneIdentityEmailAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(userId, out var parsed))
            return null;

        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT email
                FROM "StandaloneAuthIdentity"
                WHERE id = @userId
                  AND "emailConfirmedAt" IS NOT NULL
                  AND "deletedAt" IS NULL
                  AND ("bannedUntil" IS NULL OR "bannedUntil" <= now())
                LIMIT 1
                """;
            AddParameter(command, "userId", parsed);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToString(value)?.Trim().ToLowerInvariant();
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private Task<int> UpsertStandalonePasswordAsync(
        string userId,
        string email,
        string passwordHash,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "LegacyAuthUserPassword" ("authUserId", "email", "passwordHash", "importedAt")
            VALUES ({userId}, {email}, {passwordHash}, now())
            ON CONFLICT ("authUserId") DO UPDATE SET
                "email" = EXCLUDED."email",
                "passwordHash" = EXCLUDED."passwordHash",
                "importedAt" = now();
            UPDATE "StandaloneAuthIdentity"
            SET "hasPassword" = true, "importedAt" = now()
            WHERE id::text = {userId};
            """, cancellationToken);

    private Task<int> MarkPasswordSetAsync(
        string userId,
        DateTime now,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "AuthPasswordPolicy"
            SET "requiresPassword" = false,
                "passwordSetAt" = {now},
                "updatedAt" = {now}
            WHERE "authUserId" = {userId};
            """, cancellationToken);

    private Task<int> DeleteLegacyBridgeAsync(
        string userId,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"LegacyAuthUserPassword\" WHERE \"authUserId\" = {userId}",
            cancellationToken);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
