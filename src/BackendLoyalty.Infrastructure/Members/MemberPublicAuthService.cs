using System.Data;
using System.Security.Cryptography;
using System.Text;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Members;

public sealed class MemberPublicAuthService(
    LoyaltyDbContext loyaltyDb,
    StandaloneAuthDbContext authDb) : IMemberPublicAuthService
{
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(45);

    public async Task<MemberRegistrationResult> RegisterAsync(
        MemberRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(
            request.BusinessId,
            request.BusinessSlug,
            cancellationToken);

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        if (await LoadIdentityMemberIdByEmailAsync(business.Id, normalizedEmail, cancellationToken) is not null)
        {
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.Conflict,
                "Akun Anda sudah terdaftar di brand ini. Silakan login.");
        }

        var defaultCardId = await loyaltyDb.Cards.AsNoTracking()
            .Where(x => x.BusinessId == business.Id
                        && x.Status == "ACTIVE"
                        && !x.IsDeleted
                        && x.Level == 1)
            .OrderBy(x => x.CreatedAt)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(defaultCardId))
        {
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.CardNotReady,
                "Loyalty Card sedang disiapkan. Silakan kembali lagi nanti.");
        }

        var maxMembers = await LoadMaxMembersAsync(business.Id, cancellationToken);
        if (maxMembers is > 0)
        {
            var memberCount = await loyaltyDb.Members.CountAsync(
                x => x.BusinessId == business.Id,
                cancellationToken);
            if (memberCount >= maxMembers.Value)
            {
                throw new MemberPublicAuthException(
                    MemberPublicAuthErrorCode.MemberLimitReached,
                    "Member limit reached for this business tier");
            }
        }

        var now = DateTime.UtcNow;
        var memberId = Guid.NewGuid().ToString();
        var identityId = Guid.NewGuid().ToString();
        var memberCardId = Guid.NewGuid().ToString();
        var barcode = CreateMemberBarcode(business.Id);
        var passwordHash = HashPassword(request.Password);

        await using var transaction = await loyaltyDb.Database.BeginTransactionAsync(cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Member"
                ("id", "businessId", "name", "email", "phone", "memberBarcode",
                 "totalStamps", "dateJoined", "DateOfBirth", "createdAt", "updatedAt")
            VALUES
                ({memberId}, {business.Id}, {request.Name.Trim()}, {normalizedEmail}, NULL, {barcode},
                 {0}, {now}, CAST({request.DateOfBirth} AS date), {now}, {now})
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemberIdentity"
                ("id", "businessId", "memberId", "email", "passwordHash", "verifiedAt",
                 "lastLoginAt", "failedLoginCount", "lockedAt", "createdAt", "updatedAt")
            VALUES
                ({identityId}, {business.Id}, {memberId}, {normalizedEmail}, {passwordHash}, NULL,
                 NULL, {0}, NULL, {now}, {now})
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemberCard"
                ("id", "businessId", "memberId", "cardId", "currentStamps", "isActive",
                 "startedAt", "completedAt", "createdAt", "updatedAt")
            VALUES
                ({memberCardId}, {business.Id}, {memberId}, {defaultCardId}, {0}, {true},
                 {now}, NULL, {now}, {now})
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new MemberRegistrationResult(
            memberId,
            business.Id,
            business.Slug,
            normalizedEmail,
            barcode,
            false,
            "Registrasi berhasil. Silakan login menggunakan email Anda.");
    }

    public async Task<MemberPasswordResetIssue?> CreatePasswordResetAsync(
        string email,
        string? businessId,
        string? businessSlug,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, businessSlug, cancellationToken);
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var memberId = await LoadIdentityMemberIdByEmailAsync(
            business.Id,
            normalizedEmail,
            cancellationToken);

        if (memberId is null)
            return null;

        var memberExists = await loyaltyDb.Members.AsNoTracking().AnyAsync(
            x => x.Id == memberId && x.BusinessId == business.Id,
            cancellationToken);
        if (!memberExists)
            return null;

        var now = DateTime.UtcNow;
        var rawToken = CreateOpaqueToken();
        var tokenHash = HashToken(rawToken);
        var resetId = Guid.NewGuid().ToString();
        var expiresAt = now + ResetLifetime;

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "MemberPasswordReset"
            WHERE "memberId" = {memberId}
              AND "businessId" = {business.Id}
              AND "usedAt" IS NULL
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemberPasswordReset"
                ("id", "businessId", "memberId", "tokenHash", "expiresAt",
                 "usedAt", "ip", "userAgent", "createdAt")
            VALUES
                ({resetId}, {business.Id}, {memberId}, {tokenHash}, {expiresAt},
                 NULL, {NullIfBlank(ip)}, {NullIfBlank(userAgent)}, {now})
            """, cancellationToken);

        return new MemberPasswordResetIssue(
            memberId,
            business.Id,
            business.Slug,
            normalizedEmail,
            rawToken,
            expiresAt);
    }

    public async Task<MemberPasswordResetResult> ResetPasswordAsync(
        string rawToken,
        string newPassword,
        string? tenantSlug,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return MemberPasswordResetResult.InvalidOrExpired;

        var now = DateTime.UtcNow;
        var reset = await LoadResetAsync(HashToken(rawToken.Trim()), now, cancellationToken);
        if (reset is null)
            return MemberPasswordResetResult.InvalidOrExpired;

        if (!string.IsNullOrWhiteSpace(tenantSlug))
        {
            var business = await authDb.Businesses.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == reset.BusinessId && x.IsActive,
                cancellationToken);
            if (business is null ||
                !string.Equals(business.Slug, tenantSlug.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return MemberPasswordResetResult.InvalidOrExpired;
            }
        }

        var member = await loyaltyDb.Members.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == reset.MemberId && x.BusinessId == reset.BusinessId,
            cancellationToken);
        if (member is null)
            return MemberPasswordResetResult.InvalidOrExpired;

        string? emailForIdentity = member.Email?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(emailForIdentity))
        {
            var existingOwner = await LoadIdentityMemberIdByEmailAsync(
                reset.BusinessId,
                emailForIdentity,
                cancellationToken);
            if (existingOwner is not null &&
                !string.Equals(existingOwner, reset.MemberId, StringComparison.Ordinal))
            {
                emailForIdentity = null;
            }
        }

        await using var transaction = await loyaltyDb.Database.BeginTransactionAsync(cancellationToken);

        var consumed = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberPasswordReset"
            SET "usedAt" = {now}
            WHERE "id" = {reset.Id}
              AND "usedAt" IS NULL
              AND "expiresAt" > {now}
            """, cancellationToken);

        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MemberPasswordResetResult.InvalidOrExpired;
        }

        var newHash = HashPassword(newPassword);
        var updated = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberIdentity"
            SET "passwordHash" = {newHash},
                "email" = {emailForIdentity},
                "updatedAt" = {now},
                "failedLoginCount" = {0},
                "lockedAt" = NULL
            WHERE "businessId" = {reset.BusinessId}
              AND "memberId" = {reset.MemberId}
            """, cancellationToken);

        if (updated == 0)
        {
            await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MemberIdentity"
                    ("id", "businessId", "memberId", "email", "passwordHash", "verifiedAt",
                     "lastLoginAt", "failedLoginCount", "lockedAt", "createdAt", "updatedAt")
                VALUES
                    ({Guid.NewGuid().ToString()}, {reset.BusinessId}, {reset.MemberId}, {emailForIdentity},
                     {newHash}, NULL, NULL, {0}, NULL, {now}, {now})
                """, cancellationToken);
        }

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberSession"
            SET "revokedAt" = {now},
                "updatedAt" = {now}
            WHERE "businessId" = {reset.BusinessId}
              AND "memberId" = {reset.MemberId}
              AND "revokedAt" IS NULL
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return MemberPasswordResetResult.Success;
    }

    private async Task<BackendLoyalty.Domain.Entities.Business> ResolveBusinessAsync(
        string? businessId,
        string? businessSlug,
        CancellationToken cancellationToken)
    {
        var normalizedId = businessId?.Trim();
        var normalizedSlug = businessSlug?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalizedId) && string.IsNullOrWhiteSpace(normalizedSlug))
        {
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.BusinessContextRequired,
                "Business context is required (provide businessId or businessSlug)");
        }

        var query = authDb.Businesses.AsNoTracking();
        var business = !string.IsNullOrWhiteSpace(normalizedId)
            ? await query.SingleOrDefaultAsync(x => x.Id == normalizedId, cancellationToken)
            : await query.SingleOrDefaultAsync(x => x.Slug.ToLower() == normalizedSlug, cancellationToken);

        if (business is null ||
            (!string.IsNullOrWhiteSpace(normalizedSlug) &&
             !string.Equals(business.Slug, normalizedSlug, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.BusinessNotFound,
                "Business not found");
        }

        if (!business.IsActive)
        {
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.BusinessInactive,
                "Business is inactive");
        }

        return business;
    }

    private async Task<string?> LoadIdentityMemberIdByEmailAsync(
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
                SELECT "memberId"
                FROM "MemberIdentity"
                WHERE "businessId" = @businessId
                  AND lower("email") = @email
                LIMIT 1
                """;
            AddParameter(command, "@businessId", businessId);
            AddParameter(command, "@email", normalizedEmail);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is null or DBNull ? null : Convert.ToString(result);
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private async Task<int?> LoadMaxMembersAsync(
        string businessId,
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
                SELECT "maxMembers"
                FROM "Business"
                WHERE "id" = @businessId
                LIMIT 1
                """;
            AddParameter(command, "@businessId", businessId);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is null or DBNull ? null : Convert.ToInt32(result);
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private async Task<ResetRow?> LoadResetAsync(
        string tokenHash,
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
            command.CommandText = """
                SELECT "id", "memberId", "businessId"
                FROM "MemberPasswordReset"
                WHERE "tokenHash" = @tokenHash
                  AND "usedAt" IS NULL
                  AND "expiresAt" > @now
                LIMIT 1
                """;
            AddParameter(command, "@tokenHash", tokenHash);
            AddParameter(command, "@now", now);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new ResetRow(
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

    private static string CreateMemberBarcode(string businessId)
    {
        var prefix = businessId.Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        prefix = prefix[..Math.Min(6, prefix.Length)];

        var random = Convert.ToBase64String(RandomNumberGenerator.GetBytes(6))
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .Take(7)
            .ToArray();

        return $"{prefix}-{new string(random)}";
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

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record ResetRow(string Id, string MemberId, string BusinessId);
}
