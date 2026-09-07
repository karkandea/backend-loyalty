using System.Data;
using System.Security.Cryptography;
using System.Text;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Scrypt;

namespace BackendLoyalty.Infrastructure.Members;

public sealed class MemberAuthService(
    LoyaltyDbContext loyaltyDb,
    StandaloneAuthDbContext authDb) : IMemberAuthService
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(7);

    public async Task<MemberLoginResult> LoginAsync(
        string? email,
        string? password,
        string? businessId,
        string? businessSlug,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedEmail) || string.IsNullOrEmpty(password))
            throw InvalidCredentials();

        var normalizedBusinessId = businessId?.Trim();
        var normalizedSlug = businessSlug?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedBusinessId) && string.IsNullOrWhiteSpace(normalizedSlug))
            throw new MemberAuthException(
                MemberAuthErrorCode.BusinessContextRequired,
                "Business context is required (provide businessId or businessSlug)");

        var businessQuery = authDb.Businesses.AsNoTracking().AsQueryable();
        var business = !string.IsNullOrWhiteSpace(normalizedBusinessId)
            ? await businessQuery.SingleOrDefaultAsync(x => x.Id == normalizedBusinessId, cancellationToken)
            : await businessQuery.SingleOrDefaultAsync(x => x.Slug.ToLower() == normalizedSlug, cancellationToken);

        if (business is null)
            throw new MemberAuthException(MemberAuthErrorCode.BusinessNotFound, "Business not found");
        if (!business.IsActive)
            throw new MemberAuthException(MemberAuthErrorCode.BusinessInactive, "Business is inactive");

        var identity = await LoadIdentityByEmailAsync(business.Id, normalizedEmail, cancellationToken);
        if (identity is null)
            throw InvalidCredentials();

        if (!VerifyPassword(password, identity.PasswordHash))
        {
            await RecordFailedLoginAsync(identity.Id, cancellationToken);
            throw InvalidCredentials();
        }

        var member = await loyaltyDb.Members
            .SingleOrDefaultAsync(
                x => x.Id == identity.MemberId && x.BusinessId == business.Id,
                cancellationToken);
        if (member is null)
            throw InvalidCredentials();

        var now = DateTime.UtcNow;
        await RecordSuccessfulLoginAsync(identity.Id, now, cancellationToken);

        var sessionToken = CreateOpaqueToken();
        var sessionHash = HashToken(sessionToken);
        var expiresAt = now.Add(SessionTtl);

        loyaltyDb.MemberSessions.Add(new MemberSession
        {
            Id = Guid.NewGuid().ToString(),
            BusinessId = business.Id,
            MemberId = member.Id,
            SessionTokenHash = sessionHash,
            ExpiresAt = expiresAt,
            RevokedAt = null,
            Ip = string.IsNullOrWhiteSpace(ip) ? null : ip,
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await loyaltyDb.SaveChangesAsync(cancellationToken);

        return new MemberLoginResult(
            member.Id,
            business.Id,
            business.Name,
            business.Slug,
            member.Email ?? normalizedEmail,
            member.Name,
            member.MemberBarcode,
            sessionToken,
            expiresAt);
    }

    public async Task<bool> LogoutAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
            return false;

        var tokenHash = HashToken(sessionToken);
        var session = await loyaltyDb.MemberSessions
            .SingleOrDefaultAsync(
                x => x.SessionTokenHash == tokenHash && x.RevokedAt == null,
                cancellationToken);
        if (session is null)
            return false;

        var now = DateTime.UtcNow;
        session.RevokedAt = now;
        session.UpdatedAt = now;
        await loyaltyDb.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task ChangePasswordAsync(
        string? sessionToken,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentPassword)
            || string.IsNullOrWhiteSpace(newPassword)
            || newPassword.Length is < 8 or > 128)
        {
            throw new MemberAuthException(MemberAuthErrorCode.InvalidCredentials, "Invalid password payload");
        }

        var sessionIdentity = await LoadSessionIdentityAsync(sessionToken, cancellationToken);
        if (sessionIdentity is null)
            throw new MemberAuthException(MemberAuthErrorCode.InvalidCredentials, "Unauthorized");

        if (!VerifyPassword(currentPassword, sessionIdentity.PasswordHash))
            throw new MemberAuthException(MemberAuthErrorCode.InvalidCredentials, "Password lama salah");

        var now = DateTime.UtcNow;
        var newHash = HashPassword(newPassword);

        await using var transaction = await loyaltyDb.Database.BeginTransactionAsync(cancellationToken);
        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberIdentity"
            SET "passwordHash" = {newHash},
                "updatedAt" = {now}
            WHERE "id" = {sessionIdentity.IdentityId}
              AND "businessId" = {sessionIdentity.BusinessId}
              AND "memberId" = {sessionIdentity.MemberId}
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberSession"
            SET "revokedAt" = {now},
                "updatedAt" = {now}
            WHERE "businessId" = {sessionIdentity.BusinessId}
              AND "memberId" = {sessionIdentity.MemberId}
              AND "revokedAt" IS NULL
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAccountAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default)
    {
        var sessionIdentity = await LoadSessionIdentityAsync(sessionToken, cancellationToken);
        if (sessionIdentity is null)
            throw new MemberAuthException(MemberAuthErrorCode.InvalidCredentials, "Unauthorized");

        var now = DateTime.UtcNow;
        var anonymizedEmail = $"deleted+{sessionIdentity.MemberId}@member.invalid";
        var replacementPasswordHash = HashPassword(CreateOpaqueToken());

        await using var transaction = await loyaltyDb.Database.BeginTransactionAsync(cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberIdentity"
            SET "email" = {anonymizedEmail},
                "passwordHash" = {replacementPasswordHash},
                "verifiedAt" = NULL,
                "lockedAt" = {now},
                "failedLoginCount" = 0,
                "updatedAt" = {now}
            WHERE "businessId" = {sessionIdentity.BusinessId}
              AND "memberId" = {sessionIdentity.MemberId}
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Member"
            SET "name" = {"Deleted Member"},
                "email" = NULL,
                "phone" = NULL,
                "AvatarUrl" = NULL,
                "updatedAt" = {now}
            WHERE "id" = {sessionIdentity.MemberId}
              AND "businessId" = {sessionIdentity.BusinessId}
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberCard"
            SET "isActive" = false,
                "completedAt" = COALESCE("completedAt", {now}),
                "updatedAt" = {now}
            WHERE "memberId" = {sessionIdentity.MemberId}
              AND "businessId" = {sessionIdentity.BusinessId}
              AND "isActive" = true
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberReward"
            SET "status" = {"REVOKED"},
                "updatedAt" = {now}
            WHERE "memberId" = {sessionIdentity.MemberId}
              AND "businessId" = {sessionIdentity.BusinessId}
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RewardToken"
            SET "status" = {"REVOKED"},
                "usedAt" = COALESCE("usedAt", {now}),
                "updatedAt" = {now}
            WHERE "memberId" = {sessionIdentity.MemberId}
              AND "businessId" = {sessionIdentity.BusinessId}
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberSession"
            SET "revokedAt" = {now},
                "updatedAt" = {now}
            WHERE "memberId" = {sessionIdentity.MemberId}
              AND "businessId" = {sessionIdentity.BusinessId}
              AND "revokedAt" IS NULL
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IdentityRow?> LoadIdentityByEmailAsync(
        string businessId,
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        var connection = loyaltyDb.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "id", "memberId", "passwordHash"
                FROM "MemberIdentity"
                WHERE "businessId" = @businessId
                  AND lower("email") = @email
                LIMIT 1
                """;
            AddParameter(command, "@businessId", businessId);
            AddParameter(command, "@email", normalizedEmail);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new IdentityRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2));
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private async Task<SessionIdentityRow?> LoadSessionIdentityAsync(
        string? sessionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
            return null;

        var tokenHash = HashToken(sessionToken);
        var now = DateTime.UtcNow;
        var session = await loyaltyDb.MemberSessions.AsNoTracking()
            .Where(x => x.SessionTokenHash == tokenHash
                        && x.RevokedAt == null
                        && x.ExpiresAt > now)
            .Select(x => new { x.MemberId, x.BusinessId })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
            return null;

        var connection = loyaltyDb.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "id", "passwordHash"
                FROM "MemberIdentity"
                WHERE "businessId" = @businessId
                  AND "memberId" = @memberId
                LIMIT 1
                """;
            AddParameter(command, "@businessId", session.BusinessId);
            AddParameter(command, "@memberId", session.MemberId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new SessionIdentityRow(
                reader.GetString(0),
                session.MemberId,
                session.BusinessId,
                reader.GetString(1));
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private async Task RecordFailedLoginAsync(string identityId, CancellationToken cancellationToken)
    {
        await ExecuteIdentityUpdateAsync(
            """
            UPDATE "MemberIdentity"
            SET "failedLoginCount" = COALESCE("failedLoginCount", 0) + 1,
                "updatedAt" = @now
            WHERE "id" = @id
            """,
            identityId,
            DateTime.UtcNow,
            cancellationToken);
    }

    private async Task RecordSuccessfulLoginAsync(
        string identityId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await ExecuteIdentityUpdateAsync(
            """
            UPDATE "MemberIdentity"
            SET "failedLoginCount" = 0,
                "lastLoginAt" = @now,
                "updatedAt" = @now
            WHERE "id" = @id
            """,
            identityId,
            now,
            cancellationToken);
    }

    private async Task ExecuteIdentityUpdateAsync(
        string sql,
        string identityId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var connection = loyaltyDb.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@id", identityId);
            AddParameter(command, "@now", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        if (storedHash.StartsWith("$2", StringComparison.Ordinal))
            return BCrypt.Net.BCrypt.Verify(password, storedHash);

        var parts = storedHash.Split('$');
        if (parts.Length != 3 || !string.Equals(parts[0], "scrypt", StringComparison.Ordinal))
            return false;

        try
        {
            var salt = Convert.FromHexString(parts[1]);
            var expected = Convert.FromHexString(parts[2]);
            var derived = ScryptEncoder.CryptoScrypt(
                Encoding.UTF8.GetBytes(password),
                salt,
                16384,
                8,
                1,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(derived, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var derived = ScryptEncoder.CryptoScrypt(
            Encoding.UTF8.GetBytes(password),
            salt,
            16384,
            8,
            1,
            32);
        return "scrypt$"
               + Convert.ToHexString(salt).ToLowerInvariant()
               + "$"
               + Convert.ToHexString(derived).ToLowerInvariant();
    }

    private static string CreateOpaqueToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static MemberAuthException InvalidCredentials() =>
        new(MemberAuthErrorCode.InvalidCredentials, "Invalid email or password");

    private sealed record IdentityRow(string Id, string MemberId, string PasswordHash);
    private sealed record SessionIdentityRow(
        string IdentityId,
        string MemberId,
        string BusinessId,
        string PasswordHash);

}
