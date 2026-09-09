using System.Data;
using System.Security.Cryptography;
using System.Text;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Members;

public sealed class MemberPublicAuthService(
    LoyaltyDbContext loyaltyDb,
    StandaloneAuthDbContext authDb,
    IMemberPhoneOtpService phoneOtpService) : IMemberPublicAuthService
{
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan EmailOtpLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan EmailOtpResendCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EmailOtpExpiryGrace = TimeSpan.FromSeconds(30);

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
                x => x.BusinessId == business.Id && x.IsActive,
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

    public async Task<MemberPhoneRegistrationResult> RegisterPhoneAsync(
        MemberPhoneRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(
            request.BusinessId,
            request.BusinessSlug,
            cancellationToken);

        var normalizedPhone = NormalizePhoneE164(request.Phone);

        var existingPhoneMember = await loyaltyDb.Members.AsNoTracking()
            .AnyAsync(
                x => x.BusinessId == business.Id && x.Phone == normalizedPhone,
                cancellationToken);
        if (existingPhoneMember)
        {
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.Conflict,
                "Nomor HP sudah terdaftar di brand ini. Silakan login.");
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
                x => x.BusinessId == business.Id && x.IsActive,
                cancellationToken);
            if (memberCount >= maxMembers.Value)
            {
                throw new MemberPublicAuthException(
                    MemberPublicAuthErrorCode.MemberLimitReached,
                    "Member limit reached for this business tier");
            }
        }

        await using var transaction = await loyaltyDb.Database.BeginTransactionAsync(cancellationToken);

        await phoneOtpService.ConsumeSignupVerificationAsync(
            normalizedPhone,
            request.OtpSessionId,
            request.OtpVerificationToken,
            business.Id,
            business.Slug,
            cancellationToken);

        var now = DateTime.UtcNow;
        var memberId = Guid.NewGuid().ToString();
        var identityId = Guid.NewGuid().ToString();
        var memberCardId = Guid.NewGuid().ToString();
        var barcode = CreateMemberBarcode(business.Id);
        var passwordHash = HashPassword(request.Password);
        var phoneHash = HashToken(normalizedPhone);
        var phoneLast4 = new string(normalizedPhone.Where(char.IsDigit).TakeLast(4).ToArray());

        var reserved = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PhoneChangeAudit"
                ("id", "businessId", "memberId", "phoneHash", "phoneLast4", "reason", "createdAt")
            VALUES
                ({Guid.NewGuid().ToString()}, {business.Id}, {memberId}, {phoneHash},
                 {phoneLast4}, {"signup"}, {now})
            ON CONFLICT ("businessId", "phoneHash") DO NOTHING
            """, cancellationToken);

        if (reserved != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.Conflict,
                "Nomor HP sudah terdaftar di brand ini. Silakan login.");
        }

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Member"
                ("id", "businessId", "name", "email", "phone", "memberBarcode",
                 "totalStamps", "dateJoined", "DateOfBirth", "hasCompletedProfile",
                 "isActive", "createdAt", "updatedAt")
            VALUES
                ({memberId}, {business.Id}, {request.Name.Trim()}, NULL, {normalizedPhone}, {barcode},
                 {0}, {now}, CAST({request.DateOfBirth} AS date), {true},
                 {true}, {now}, {now})
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemberIdentity"
                ("id", "businessId", "memberId", "email", "passwordHash", "verifiedAt",
                 "lastLoginAt", "failedLoginCount", "lockedAt", "createdAt", "updatedAt")
            VALUES
                ({identityId}, {business.Id}, {memberId}, NULL, {passwordHash}, {now},
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

        return new MemberPhoneRegistrationResult(
            memberId,
            business.Id,
            business.Slug,
            normalizedPhone,
            barcode,
            "Registrasi berhasil. Silakan login menggunakan nomor HP Anda.");
    }

    public async Task<MemberEmailOtpSendResult> SendEmailVerificationOtpAsync(
        string email,
        string? businessSlug,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(null, businessSlug, cancellationToken);
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedDeviceId = deviceId.Trim();
        var identity = await LoadIdentityEmailStateAsync(
            business.Id,
            normalizedEmail,
            cancellationToken);

        if (identity is null)
        {
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.MemberNotFound,
                "Akun tidak ditemukan. Silakan daftar terlebih dahulu.");
        }

        if (identity.VerifiedAt.HasValue)
            return new MemberEmailOtpSendResult(MemberEmailOtpSendStatus.AlreadyVerified, null);

        var now = DateTime.UtcNow;
        var latest = await LoadLatestEmailVerificationAsync(
            business.Id,
            identity.MemberId,
            cancellationToken);

        if (latest is not null &&
            latest.UsedAt is null &&
            now - latest.CreatedAt < EmailOtpResendCooldown)
        {
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.TooManyRequests,
                "Tunggu sebentar sebelum meminta OTP lagi.");
        }

        var otp = GenerateOtpCode();
        var sessionId = Guid.NewGuid().ToString();
        var expiresAt = now + EmailOtpLifetime;
        var otpHash = HashEmailOtp(
            business.Id,
            identity.MemberId,
            normalizedEmail,
            normalizedDeviceId,
            otp);

        await using var transaction = await loyaltyDb.Database.BeginTransactionAsync(cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "MemberEmailVerification"
            WHERE "businessId" = {business.Id}
              AND "memberId" = {identity.MemberId}
              AND "usedAt" IS NULL
            """, cancellationToken);

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemberEmailVerification"
                ("id", "businessId", "memberId", "tokenHash", "expiresAt", "usedAt", "createdAt")
            VALUES
                ({sessionId}, {business.Id}, {identity.MemberId}, {otpHash}, {expiresAt}, NULL, {now})
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new MemberEmailOtpSendResult(
            MemberEmailOtpSendStatus.Issued,
            new MemberEmailOtpIssue(
                identity.MemberId,
                business.Id,
                business.Slug,
                normalizedEmail,
                sessionId,
                otp,
                expiresAt));
    }

    public async Task<MemberEmailOtpVerifyResult> VerifyEmailVerificationOtpAsync(
        string email,
        string otp,
        string otpSessionId,
        string? businessSlug,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(null, businessSlug, cancellationToken);
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedDeviceId = deviceId.Trim();
        var identity = await LoadIdentityEmailStateAsync(
            business.Id,
            normalizedEmail,
            cancellationToken);

        if (identity is null)
            return MemberEmailOtpVerifyResult.NotFound;

        var session = await LoadEmailVerificationAsync(
            otpSessionId,
            business.Id,
            identity.MemberId,
            cancellationToken);

        if (session is null)
            return MemberEmailOtpVerifyResult.NotFound;
        if (session.UsedAt.HasValue)
            return MemberEmailOtpVerifyResult.AlreadyUsed;

        var now = DateTime.UtcNow;
        if (session.ExpiresAt + EmailOtpExpiryGrace < now)
            return MemberEmailOtpVerifyResult.Expired;

        var expectedHash = HashEmailOtp(
            business.Id,
            identity.MemberId,
            normalizedEmail,
            normalizedDeviceId,
            otp);

        if (!FixedTimeHexEquals(expectedHash, session.TokenHash))
            return MemberEmailOtpVerifyResult.Invalid;

        await using var transaction = await loyaltyDb.Database.BeginTransactionAsync(cancellationToken);

        var consumed = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberEmailVerification"
            SET "usedAt" = {now}
            WHERE "id" = {session.Id}
              AND "businessId" = {business.Id}
              AND "memberId" = {identity.MemberId}
              AND "tokenHash" = {expectedHash}
              AND "usedAt" IS NULL
              AND "expiresAt" > {now - EmailOtpExpiryGrace}
            """, cancellationToken);

        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MemberEmailOtpVerifyResult.AlreadyUsed;
        }

        var verified = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MemberIdentity"
            SET "verifiedAt" = COALESCE("verifiedAt", {now}),
                "updatedAt" = {now}
            WHERE "businessId" = {business.Id}
              AND "memberId" = {identity.MemberId}
              AND lower("email") = {normalizedEmail}
            """, cancellationToken);

        if (verified != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MemberEmailOtpVerifyResult.NotFound;
        }

        await transaction.CommitAsync(cancellationToken);
        return MemberEmailOtpVerifyResult.Success;
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

    private async Task<IdentityEmailState?> LoadIdentityEmailStateAsync(
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
                SELECT "memberId", "verifiedAt"
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

            return new IdentityEmailState(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1));
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private async Task<EmailVerificationRow?> LoadLatestEmailVerificationAsync(
        string businessId,
        string memberId,
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
                SELECT "id", "tokenHash", "expiresAt", "usedAt", "createdAt"
                FROM "MemberEmailVerification"
                WHERE "businessId" = @businessId
                  AND "memberId" = @memberId
                ORDER BY "createdAt" DESC
                LIMIT 1
                """;
            AddParameter(command, "@businessId", businessId);
            AddParameter(command, "@memberId", memberId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return ReadEmailVerification(reader);
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private async Task<EmailVerificationRow?> LoadEmailVerificationAsync(
        string id,
        string businessId,
        string memberId,
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
                SELECT "id", "tokenHash", "expiresAt", "usedAt", "createdAt"
                FROM "MemberEmailVerification"
                WHERE "id" = @id
                  AND "businessId" = @businessId
                  AND "memberId" = @memberId
                LIMIT 1
                """;
            AddParameter(command, "@id", id);
            AddParameter(command, "@businessId", businessId);
            AddParameter(command, "@memberId", memberId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return ReadEmailVerification(reader);
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private static EmailVerificationRow ReadEmailVerification(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.GetDateTime(4));

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

    private static string NormalizePhoneE164(string input)
    {
        var trimmed = string.Concat((input ?? string.Empty).Trim().Where(ch => !char.IsWhiteSpace(ch)));
        var digits = trimmed.StartsWith('+') ? trimmed[1..] : trimmed;
        if (digits.Length is < 8 or > 15 || digits.Any(ch => !char.IsDigit(ch)))
        {
            throw new MemberPublicAuthException(
                MemberPublicAuthErrorCode.Conflict,
                "Phone must contain 8-15 digits and may start with +");
        }

        return trimmed.StartsWith('+') ? trimmed : $"+{trimmed}";
    }

    private static string GenerateOtpCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string HashEmailOtp(
        string businessId,
        string memberId,
        string email,
        string deviceId,
        string otp) =>
        HashToken($"{businessId}|{memberId}|{email}|{deviceId}|{otp}");

    private static bool FixedTimeHexEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
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

    private sealed record IdentityEmailState(string MemberId, DateTime? VerifiedAt);
    private sealed record EmailVerificationRow(
        string Id,
        string TokenHash,
        DateTime ExpiresAt,
        DateTime? UsedAt,
        DateTime CreatedAt);
    private sealed record ResetRow(string Id, string MemberId, string BusinessId);
}
