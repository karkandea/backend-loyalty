using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Auth;

public sealed class StandaloneInvitationIdentityService(StandaloneAuthDbContext db)
    : IStandaloneInvitationIdentityService
{
    private const string ManagedBySupabase = "managed-by-supabase-auth";

    public async Task<bool> NormalizeInvitationForExistingIdentityAsync(
        string invitationId,
        string email,
        CancellationToken cancellationToken)
    {
        var identity = await FindActiveStandaloneIdentityByEmailAsync(
            NormalizeEmail(email),
            cancellationToken);
        if (identity is null)
            return false;

        var affected = await db.BusinessInvitations
            .Where(x => x.Id == invitationId &&
                        x.UsedAt == null &&
                        x.RevokedAt == null &&
                        x.RequiresPassword)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.RequiresPassword, false)
                    .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
                cancellationToken);

        return affected == 1;
    }

    public async Task<bool> InvitationTargetsExistingIdentityAsync(
        string rawToken,
        CancellationToken cancellationToken)
    {
        var tokenHash = Hash(rawToken.Trim());
        var now = DateTime.UtcNow;
        var invite = await db.BusinessInvitations.AsNoTracking().SingleOrDefaultAsync(
            x => x.TokenHash == tokenHash &&
                 x.UsedAt == null &&
                 x.RevokedAt == null &&
                 x.ExpiresAt > now,
            cancellationToken);

        if (invite is null)
            return false;

        return await FindActiveStandaloneIdentityByEmailAsync(
            NormalizeEmail(invite.Email),
            cancellationToken) is not null;
    }

    public async Task<InvitationAcceptanceOutcome?> AcceptZeroMembershipAsync(
        string userId,
        string rawToken,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(userId, out var parsedUserId))
            return null;

        var identity = await FindActiveStandaloneIdentityByIdAsync(parsedUserId, cancellationToken);
        if (identity is null)
            return null;

        var passwordHash = await GetBridgeHashAsync(userId, cancellationToken);
        if (!IsUsableBcryptHash(passwordHash))
            return null;

        var tokenHash = Hash(rawToken.Trim());
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var invite = await db.BusinessInvitations.AsNoTracking().SingleOrDefaultAsync(
            x => x.TokenHash == tokenHash &&
                 x.UsedAt == null &&
                 x.RevokedAt == null &&
                 x.ExpiresAt > now,
            cancellationToken);

        if (invite is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new InvitationAcceptanceOutcome(
                InvitationAcceptanceStatus.InvalidOrExpired,
                null,
                0,
                false);
        }

        var email = NormalizeEmail(identity.Email);
        if (!string.Equals(email, NormalizeEmail(invite.Email), StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new InvitationAcceptanceOutcome(
                InvitationAcceptanceStatus.EmailMismatch,
                null,
                0,
                false);
        }

        var existing = await db.BusinessUsers.SingleOrDefaultAsync(
            x => x.BusinessId == invite.BusinessId && x.Email.ToLower() == email,
            cancellationToken);

        var consumed = await db.BusinessInvitations
            .Where(x => x.Id == invite.Id &&
                        x.UsedAt == null &&
                        x.RevokedAt == null &&
                        x.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.UsedAt, now)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new InvitationAcceptanceOutcome(
                InvitationAcceptanceStatus.InvalidOrExpired,
                null,
                0,
                false);
        }

        var role = invite.Role.Trim().ToUpperInvariant();
        if (existing is null)
        {
            db.BusinessUsers.Add(new BusinessUser
            {
                Id = Guid.NewGuid().ToString(),
                BusinessId = invite.BusinessId,
                AuthUserId = userId,
                Email = email,
                PasswordHash = passwordHash!,
                FullName = DisplayNameFromEmail(email),
                Role = role,
                Permissions = ClonePermissions(invite.Permissions),
                IsActive = true,
                LastLoginAt = null,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else if (!string.Equals(existing.Role, "OWNER", StringComparison.OrdinalIgnoreCase))
        {
            existing.AuthUserId = userId;
            existing.PasswordHash = passwordHash!;
            existing.Role = role;
            existing.Permissions = ClonePermissions(invite.Permissions);
            existing.IsActive = true;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        var membershipCount = await db.BusinessUsers.CountAsync(
            x => x.IsActive && x.AuthUserId == userId,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new InvitationAcceptanceOutcome(
            InvitationAcceptanceStatus.Success,
            invite.BusinessId,
            membershipCount,
            false);
    }

    private async Task<StandaloneIdentity?> FindActiveStandaloneIdentityByEmailAsync(
        string normalizedEmail,
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
                SELECT id::text, email
                FROM "StandaloneAuthIdentity"
                WHERE lower(trim(email)) = @email
                  AND "emailConfirmedAt" IS NOT NULL
                  AND "deletedAt" IS NULL
                  AND ("bannedUntil" IS NULL OR "bannedUntil" <= now())
                LIMIT 2
                """;
            AddParameter(command, "email", normalizedEmail);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            var identity = new StandaloneIdentity(reader.GetString(0), reader.GetString(1));
            if (await reader.ReadAsync(cancellationToken))
                return null;

            return identity;
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<StandaloneIdentity?> FindActiveStandaloneIdentityByIdAsync(
        Guid userId,
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
                SELECT id::text, email
                FROM "StandaloneAuthIdentity"
                WHERE id = @userId
                  AND "emailConfirmedAt" IS NOT NULL
                  AND "deletedAt" IS NULL
                  AND ("bannedUntil" IS NULL OR "bannedUntil" <= now())
                LIMIT 1
                """;
            AddParameter(command, "userId", userId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new StandaloneIdentity(reader.GetString(0), reader.GetString(1))
                : null;
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<string?> GetBridgeHashAsync(
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
                SELECT "passwordHash"
                FROM "LegacyAuthUserPassword"
                WHERE "authUserId" = @userId
                LIMIT 1
                """;
            AddParameter(command, "userId", userId);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToString(value);
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static JsonDocument ClonePermissions(JsonDocument? permissions) =>
        JsonDocument.Parse(permissions?.RootElement.GetRawText() ?? "[]");

    private static bool IsUsableBcryptHash(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, ManagedBySupabase, StringComparison.OrdinalIgnoreCase) &&
        value.StartsWith("$2", StringComparison.Ordinal) &&
        value.Length >= 50;

    private static string DisplayNameFromEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 ? email[..at] : email;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record StandaloneIdentity(string UserId, string Email);
}
