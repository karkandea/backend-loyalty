using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BackendLoyalty.Infrastructure.Members;

public sealed class MemberPhoneOtpService(
    LoyaltyDbContext loyaltyDb,
    StandaloneAuthDbContext authDb,
    IWhatsAppOtpSender whatsAppSender,
    IConfiguration configuration) : IMemberPhoneOtpService
{
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BlockLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ForgotResetLifetime = TimeSpan.FromMinutes(15);
    private const int MaxAttempts = 3;

    public async Task<bool> IsWhatsAppRegistrationEnabledAsync(
        string? businessId,
        string? businessSlug,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, businessSlug, cancellationToken);
        return await IsWhatsAppEnabledAsync(business.Id, cancellationToken);
    }

    public async Task<MemberPhoneOtpIssue> SendPublicOtpAsync(
        string phone,
        string? businessId,
        string? businessSlug,
        MemberPhoneOtpPurpose purpose,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, businessSlug, cancellationToken);
        var normalizedPhone = NormalizePhoneE164(phone);
        var normalizedDevice = RequireDeviceId(deviceId);

        if (purpose == MemberPhoneOtpPurpose.Signup)
            await EnsureWhatsAppEnabledAsync(business.Id, cancellationToken);

        if (!whatsAppSender.IsConfigured)
            throw ProviderUnavailable();

        string? memberId = null;
        var phantom = false;

        if (purpose == MemberPhoneOtpPurpose.Signup)
        {
            if (!await IsPhoneAvailableAsync(business.Id, normalizedPhone, null, cancellationToken))
                throw Conflict("Nomor HP sudah terdaftar di bisnis ini.");
        }
        else if (purpose == MemberPhoneOtpPurpose.ForgotPassword)
        {
            memberId = await loyaltyDb.Members.AsNoTracking()
                .Where(x => x.BusinessId == business.Id
                            && x.Phone == normalizedPhone
                            && x.IsActive)
                .Select(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            phantom = string.IsNullOrWhiteSpace(memberId);
        }
        else
        {
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.Validation,
                "Invalid OTP purpose.");
        }

        var ipKey = string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();
        await RequireRateLimitAsync(
            "otp-send",
            $"public-otp:{ipKey}:{normalizedDevice}",
            300,
            5,
            cancellationToken);
        await RequireRateLimitAsync(
            "otp-send",
            $"{normalizedPhone}:{business.Id}:{ipKey}:{normalizedDevice}",
            300,
            5,
            cancellationToken);
        await RequireRateLimitAsync(
            "otp-send-daily",
            $"{normalizedPhone}:{business.Id}",
            86_400,
            10,
            cancellationToken);

        var purposeName = PurposeName(purpose);
        var issue = await IssueOtpSessionAsync(
            business.Id,
            memberId,
            normalizedPhone,
            purposeName,
            normalizedDevice,
            cancellationToken);

        if (!phantom)
        {
            try
            {
                await whatsAppSender.SendOtpAsync(
                    normalizedPhone,
                    issue.RawOtp,
                    15,
                    cancellationToken);
            }
            catch
            {
                await TryAuditAsync(
                    "otp_send",
                    purposeName,
                    business.Id,
                    memberId,
                    issue.OtpSessionId,
                    normalizedPhone,
                    ip,
                    normalizedDevice,
                    userAgent,
                    "fail",
                    "WAHA_SEND_FAILED",
                    cancellationToken);
                throw;
            }
        }

        await TryAuditAsync(
            "otp_send",
            purposeName,
            business.Id,
            memberId,
            issue.OtpSessionId,
            normalizedPhone,
            ip,
            normalizedDevice,
            userAgent,
            phantom ? "suspicious" : "success",
            phantom ? "PHANTOM" : null,
            cancellationToken);

        return new MemberPhoneOtpIssue(issue.OtpSessionId, issue.ExpiresAt);
    }

    public async Task<MemberPhoneOtpVerification> VerifySignupOtpAsync(
        string phone,
        string otp,
        string otpSessionId,
        string? businessId,
        string? businessSlug,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, businessSlug, cancellationToken);
        var normalizedPhone = NormalizePhoneE164(phone);
        var normalizedDevice = RequireDeviceId(deviceId);
        ValidateOtp(otp);

        var ipKey = string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();
        await RequireRateLimitAsync(
            "otp-verify",
            $"public-otp:{ipKey}:{normalizedDevice}",
            300,
            10,
            cancellationToken);
        await RequireRateLimitAsync(
            "otp-verify",
            $"{normalizedPhone}:{business.Id}:{ipKey}:{normalizedDevice}",
            300,
            10,
            cancellationToken);

        var session = await LoadOtpSessionAsync(
            otpSessionId,
            business.Id,
            normalizedPhone,
            "signup",
            cancellationToken);

        await ValidateOtpSessionAsync(
            session,
            otp,
            normalizedDevice,
            "signup",
            business.Id,
            normalizedPhone,
            ip,
            userAgent,
            cancellationToken);

        var rawVerificationToken = CreateOpaqueToken();
        var verificationHash = Hash(rawVerificationToken);
        var now = DateTime.UtcNow;
        var verificationExpiresAt = now + VerificationLifetime;

        var updated = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OtpSession"
            SET "verifiedAt" = {now},
                "verificationTokenHash" = {verificationHash},
                "verificationExpiresAt" = {verificationExpiresAt},
                "updatedAt" = {now}
            WHERE "id" = {session!.Id}
              AND "businessId" = {business.Id}
              AND "phoneHash" = {Hash(normalizedPhone)}
              AND "purpose" = {"signup"}
              AND "deviceId" = {normalizedDevice}
              AND "usedAt" IS NULL
              AND ("blockedUntil" IS NULL OR "blockedUntil" <= {now})
              AND "expiresAt" > {now}
            """, cancellationToken);

        if (updated != 1)
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.AlreadyUsed,
                "OTP sudah digunakan.");

        await TryAuditAsync(
            "otp_verify",
            "signup",
            business.Id,
            session.MemberId,
            session.Id,
            normalizedPhone,
            ip,
            normalizedDevice,
            userAgent,
            "success",
            null,
            cancellationToken);

        return new MemberPhoneOtpVerification(
            rawVerificationToken,
            verificationExpiresAt);
    }

    public async Task ConsumeSignupVerificationAsync(
        string phone,
        string otpSessionId,
        string verificationToken,
        string? businessId,
        string? businessSlug,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, businessSlug, cancellationToken);
        await EnsureWhatsAppEnabledAsync(business.Id, cancellationToken);

        var normalizedPhone = NormalizePhoneE164(phone);
        if (!await IsPhoneAvailableAsync(business.Id, normalizedPhone, null, cancellationToken))
            throw Conflict("Nomor HP sudah terdaftar di brand ini. Silakan login.");

        var session = await LoadOtpSessionAsync(
            otpSessionId,
            business.Id,
            normalizedPhone,
            "signup",
            cancellationToken);

        if (session is null || session.VerifiedAt is null)
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.InvalidOtp,
                "OTP belum diverifikasi.");
        if (session.UsedAt is not null)
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.AlreadyUsed,
                "OTP sudah digunakan.");

        var now = DateTime.UtcNow;
        if (session.VerificationExpiresAt is null || session.VerificationExpiresAt <= now)
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.Expired,
                "OTP sudah kedaluwarsa.");

        if (string.IsNullOrWhiteSpace(session.VerificationTokenHash)
            || !FixedTimeEquals(session.VerificationTokenHash, Hash(verificationToken)))
        {
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.InvalidOtp,
                "OTP tidak valid.");
        }

        var consumed = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OtpSession"
            SET "usedAt" = {now},
                "updatedAt" = {now}
            WHERE "id" = {session.Id}
              AND "businessId" = {business.Id}
              AND "phoneHash" = {Hash(normalizedPhone)}
              AND "purpose" = {"signup"}
              AND "usedAt" IS NULL
              AND "verifiedAt" IS NOT NULL
              AND "verificationExpiresAt" > {now}
              AND "verificationTokenHash" = {Hash(verificationToken)}
            """, cancellationToken);

        if (consumed != 1)
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.AlreadyUsed,
                "OTP sudah digunakan.");
    }

    public async Task<MemberPhonePasswordResetIssue> VerifyForgotPasswordOtpAsync(
        string phone,
        string otp,
        string otpSessionId,
        string? businessId,
        string? businessSlug,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var business = await ResolveBusinessAsync(businessId, businessSlug, cancellationToken);
        var normalizedPhone = NormalizePhoneE164(phone);
        var normalizedDevice = RequireDeviceId(deviceId);
        ValidateOtp(otp);

        var ipKey = string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();
        await RequireRateLimitAsync(
            "otp-verify",
            $"member-forgot-phone:{ipKey}:{normalizedDevice}",
            300,
            10,
            cancellationToken);
        await RequireRateLimitAsync(
            "member-phone-otp-verify",
            $"{normalizedPhone}:{business.Id}:{ipKey}:{normalizedDevice}",
            300,
            10,
            cancellationToken);

        var session = await LoadOtpSessionAsync(
            otpSessionId,
            business.Id,
            normalizedPhone,
            "forgot-password",
            cancellationToken);

        await ValidateOtpSessionAsync(
            session,
            otp,
            normalizedDevice,
            "forgot-password",
            business.Id,
            normalizedPhone,
            ip,
            userAgent,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(session!.MemberId))
        {
            await TryAuditAsync(
                "otp_verify",
                "forgot-password",
                business.Id,
                null,
                session.Id,
                normalizedPhone,
                ip,
                normalizedDevice,
                userAgent,
                "fail",
                "PHANTOM",
                cancellationToken);
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.InvalidOtp,
                "Kode OTP tidak valid.");
        }

        var now = DateTime.UtcNow;
        await using var transaction = await loyaltyDb.Database.BeginTransactionAsync(cancellationToken);

        var consumed = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OtpSession"
            SET "verifiedAt" = {now},
                "usedAt" = {now},
                "updatedAt" = {now}
            WHERE "id" = {session.Id}
              AND "businessId" = {business.Id}
              AND "phoneHash" = {Hash(normalizedPhone)}
              AND "purpose" = {"forgot-password"}
              AND "deviceId" = {normalizedDevice}
              AND "usedAt" IS NULL
              AND "expiresAt" > {now}
            """, cancellationToken);

        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.AlreadyUsed,
                "OTP sudah digunakan.");
        }

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "MemberPasswordReset"
            WHERE "businessId" = {business.Id}
              AND "memberId" = {session.MemberId}
              AND "usedAt" IS NULL
            """, cancellationToken);

        var rawResetToken = CreateOpaqueToken();
        var resetHash = Hash(rawResetToken);
        var resetExpiresAt = now + ForgotResetLifetime;

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemberPasswordReset"
                ("id", "businessId", "memberId", "tokenHash", "expiresAt",
                 "usedAt", "ip", "userAgent", "createdAt")
            VALUES
                ({Guid.NewGuid().ToString()}, {business.Id}, {session.MemberId}, {resetHash},
                 {resetExpiresAt}, NULL, {NullIfBlank(ip)}, {NullIfBlank(userAgent)}, {now})
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        await TryAuditAsync(
            "otp_verify",
            "forgot-password",
            business.Id,
            session.MemberId,
            session.Id,
            normalizedPhone,
            ip,
            normalizedDevice,
            userAgent,
            "success",
            null,
            cancellationToken);

        return new MemberPhonePasswordResetIssue(rawResetToken, resetExpiresAt);
    }

    public async Task<MemberPhoneOtpIssue> SendProfilePhoneOtpAsync(
        string memberId,
        string businessId,
        string phone,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        await EnsureWhatsAppEnabledAsync(businessId, cancellationToken);
        if (!whatsAppSender.IsConfigured)
            throw ProviderUnavailable();

        var normalizedPhone = NormalizePhoneE164(phone);
        var normalizedDevice = RequireDeviceId(deviceId);

        var member = await loyaltyDb.Members.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == memberId
                     && x.BusinessId == businessId
                     && x.IsActive,
                cancellationToken);
        if (member is null)
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.NotFound, "Member not found");

        if (!string.IsNullOrWhiteSpace(member.Phone))
        {
            throw Conflict(
                string.Equals(member.Phone, normalizedPhone, StringComparison.Ordinal)
                    ? "Nomor HP sudah terverifikasi."
                    : "Nomor HP sudah ditetapkan. Hubungi admin untuk mengganti.");
        }

        if (!await IsPhoneAvailableAsync(businessId, normalizedPhone, memberId, cancellationToken))
            throw Conflict("Nomor HP sudah digunakan oleh akun lain di brand ini.");

        var ipKey = string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();
        await RequireRateLimitAsync(
            "otp-send",
            $"member-phone:{ipKey}:{normalizedDevice}",
            300,
            5,
            cancellationToken);
        await RequireRateLimitAsync(
            "member-phone-otp-send",
            $"{normalizedPhone}:{businessId}:{ipKey}:{normalizedDevice}",
            300,
            5,
            cancellationToken);
        await RequireRateLimitAsync(
            "member-phone-otp-send-daily",
            $"{normalizedPhone}:{businessId}",
            86_400,
            10,
            cancellationToken);

        var issue = await IssueOtpSessionAsync(
            businessId,
            memberId,
            normalizedPhone,
            "profile_phone",
            normalizedDevice,
            cancellationToken);

        await whatsAppSender.SendOtpAsync(normalizedPhone, issue.RawOtp, 15, cancellationToken);

        await TryAuditAsync(
            "otp_send",
            "profile_phone",
            businessId,
            memberId,
            issue.OtpSessionId,
            normalizedPhone,
            ip,
            normalizedDevice,
            userAgent,
            "success",
            null,
            cancellationToken);

        return new MemberPhoneOtpIssue(issue.OtpSessionId, issue.ExpiresAt);
    }

    public async Task VerifyProfilePhoneOtpAsync(
        string memberId,
        string businessId,
        string phone,
        string otp,
        string otpSessionId,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        await EnsureWhatsAppEnabledAsync(businessId, cancellationToken);

        var normalizedPhone = NormalizePhoneE164(phone);
        var normalizedDevice = RequireDeviceId(deviceId);
        ValidateOtp(otp);

        var ipKey = string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim();
        await RequireRateLimitAsync(
            "otp-verify",
            $"member-phone:{ipKey}:{normalizedDevice}",
            300,
            10,
            cancellationToken);
        await RequireRateLimitAsync(
            "member-phone-otp-verify",
            $"{normalizedPhone}:{businessId}:{ipKey}:{normalizedDevice}",
            300,
            10,
            cancellationToken);

        var session = await LoadOtpSessionAsync(
            otpSessionId,
            businessId,
            normalizedPhone,
            "profile_phone",
            cancellationToken);

        if (session is not null
            && !string.IsNullOrWhiteSpace(session.MemberId)
            && !string.Equals(session.MemberId, memberId, StringComparison.Ordinal))
        {
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.DeviceMismatch,
                "OTP tidak sesuai dengan akun ini.");
        }

        await ValidateOtpSessionAsync(
            session,
            otp,
            normalizedDevice,
            "profile_phone",
            businessId,
            normalizedPhone,
            ip,
            userAgent,
            cancellationToken);

        if (!await IsPhoneAvailableAsync(businessId, normalizedPhone, memberId, cancellationToken))
            throw Conflict("Nomor HP sudah digunakan oleh akun lain di brand ini.");

        var now = DateTime.UtcNow;
        await using var transaction = await loyaltyDb.Database.BeginTransactionAsync(cancellationToken);

        var consumed = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OtpSession"
            SET "verifiedAt" = {now},
                "usedAt" = {now},
                "updatedAt" = {now}
            WHERE "id" = {session!.Id}
              AND "businessId" = {businessId}
              AND "phoneHash" = {Hash(normalizedPhone)}
              AND "purpose" = {"profile_phone"}
              AND "deviceId" = {normalizedDevice}
              AND "usedAt" IS NULL
              AND "expiresAt" > {now}
            """, cancellationToken);

        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.AlreadyUsed,
                "OTP sudah digunakan.");
        }

        var updatedMember = await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Member"
            SET "phone" = {normalizedPhone},
                "updatedAt" = {now}
            WHERE "id" = {memberId}
              AND "businessId" = {businessId}
              AND "isActive" = true
              AND "phone" IS NULL
            """, cancellationToken);

        if (updatedMember != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw Conflict("Nomor HP sudah ditetapkan. Hubungi admin untuk mengganti.");
        }

        await RecordPhoneUsageAsync(
            businessId,
            memberId,
            normalizedPhone,
            "profile",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        await TryAuditAsync(
            "otp_verify",
            "profile_phone",
            businessId,
            memberId,
            session.Id,
            normalizedPhone,
            ip,
            normalizedDevice,
            userAgent,
            "success",
            null,
            cancellationToken);
    }

    private async Task<(string OtpSessionId, string RawOtp, DateTime ExpiresAt)> IssueOtpSessionAsync(
        string businessId,
        string? memberId,
        string normalizedPhone,
        string purpose,
        string deviceId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var cooldownSeconds = int.TryParse(
            configuration["OTP_RESEND_COOLDOWN_SECONDS"],
            out var configuredCooldown)
            ? Math.Max(1, configuredCooldown)
            : 60;

        var latest = await LoadLatestOtpSessionAsync(
            businessId,
            normalizedPhone,
            purpose,
            cancellationToken);

        if (latest?.BlockedUntil is not null && latest.BlockedUntil > now)
        {
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.TooManyRequests,
                "OTP diblok sementara. Coba lagi nanti.",
                Math.Max(1, (int)Math.Ceiling((latest.BlockedUntil.Value - now).TotalSeconds)));
        }

        if (latest is not null)
        {
            var elapsed = now - latest.LastSentAt;
            if (elapsed.TotalSeconds < cooldownSeconds)
            {
                throw new MemberPhoneOtpException(
                    MemberPhoneOtpErrorCode.TooManyRequests,
                    "Tunggu sebentar sebelum meminta OTP lagi.",
                    Math.Max(1, cooldownSeconds - (int)Math.Floor(elapsed.TotalSeconds)));
            }
        }

        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var otpHash = Hash(otp);
        var expiresAt = now + OtpLifetime;
        var phoneHash = Hash(normalizedPhone);

        if (latest is not null
            && latest.UsedAt is null
            && latest.VerifiedAt is null
            && latest.ExpiresAt > now)
        {
            await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "OtpSession"
                SET "otpHash" = {otpHash},
                    "expiresAt" = {expiresAt},
                    "lastSentAt" = {now},
                    "resendCount" = COALESCE("resendCount", 0) + 1,
                    "memberId" = {memberId},
                    "deviceId" = {deviceId},
                    "updatedAt" = {now}
                WHERE "id" = {latest.Id}
                """, cancellationToken);

            return (latest.Id, otp, expiresAt);
        }

        var sessionId = Guid.NewGuid().ToString();
        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OtpSession"
                ("id", "businessId", "memberId", "purpose", "phoneHash", "phoneLast4",
                 "otpHash", "attemptCount", "resendCount", "lastSentAt", "expiresAt",
                 "blockedUntil", "verifiedAt", "usedAt", "verificationTokenHash",
                 "verificationExpiresAt", "deviceId", "createdAt", "updatedAt")
            VALUES
                ({sessionId}, {businessId}, {memberId}, {purpose}, {phoneHash}, {PhoneLast4(normalizedPhone)},
                 {otpHash}, {0}, {0}, {now}, {expiresAt},
                 NULL, NULL, NULL, NULL, NULL, {deviceId}, {now}, {now})
            """, cancellationToken);

        return (sessionId, otp, expiresAt);
    }

    private async Task ValidateOtpSessionAsync(
        OtpSessionRow? session,
        string otp,
        string deviceId,
        string purpose,
        string businessId,
        string normalizedPhone,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (session is null)
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.NotFound, "OTP tidak ditemukan.");
        if (session.UsedAt is not null)
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.AlreadyUsed, "OTP sudah digunakan.");
        if (string.IsNullOrWhiteSpace(session.DeviceId)
            || !string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal))
        {
            await TryAuditAsync(
                "otp_suspicious",
                purpose,
                businessId,
                session.MemberId,
                session.Id,
                normalizedPhone,
                ip,
                deviceId,
                userAgent,
                "blocked",
                "DEVICE_MISMATCH",
                cancellationToken);
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.DeviceMismatch,
                "Perangkat tidak sesuai. Minta OTP baru.");
        }

        var now = DateTime.UtcNow;
        if (session.BlockedUntil is not null && session.BlockedUntil > now)
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.TooManyRequests,
                "OTP diblok sementara. Coba lagi nanti.",
                Math.Max(1, (int)Math.Ceiling((session.BlockedUntil.Value - now).TotalSeconds)));

        if (session.ExpiresAt <= now)
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.Expired, "OTP sudah kedaluwarsa.");

        var suppliedHash = Hash(otp);
        if (FixedTimeEquals(session.OtpHash, suppliedHash))
            return;

        var nextAttempts = session.AttemptCount + 1;
        DateTime? blockedUntil = nextAttempts >= MaxAttempts ? now + BlockLifetime : null;

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OtpSession"
            SET "attemptCount" = {nextAttempts},
                "blockedUntil" = {blockedUntil},
                "updatedAt" = {now}
            WHERE "id" = {session.Id}
              AND "usedAt" IS NULL
            """, cancellationToken);

        await TryAuditAsync(
            "otp_verify_fail",
            purpose,
            businessId,
            session.MemberId,
            session.Id,
            normalizedPhone,
            ip,
            deviceId,
            userAgent,
            "fail",
            "OTP_INVALID",
            cancellationToken);

        throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.InvalidOtp, "OTP salah.");
    }

    private async Task RequireRateLimitAsync(
        string scope,
        string key,
        int windowSeconds,
        int limit,
        CancellationToken cancellationToken)
    {
        var connection = loyaltyDb.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT allowed, remaining, retry_after_seconds
                FROM increment_rate_limit(@scope, @key, @window, @limit)
                """;
            AddParameter(command, "@scope", scope);
            AddParameter(command, "@key", key);
            AddParameter(command, "@window", windowSeconds);
            AddParameter(command, "@limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return;

            var allowed = reader.GetBoolean(0);
            var retryAfter = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            if (!allowed)
            {
                throw new MemberPhoneOtpException(
                    MemberPhoneOtpErrorCode.TooManyRequests,
                    "Terlalu banyak permintaan OTP. Coba lagi nanti.",
                    Math.Max(1, retryAfter));
            }
        }
        finally
        {
            if (close)
                await connection.CloseAsync();
        }
    }

    private async Task<bool> IsPhoneAvailableAsync(
        string businessId,
        string phone,
        string? excludeMemberId,
        CancellationToken cancellationToken)
    {
        var phoneHash = Hash(phone);
        var audited = await ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM "PhoneChangeAudit"
            WHERE "businessId" = @businessId
              AND "phoneHash" = @phoneHash
              AND "reason" <> 'delete'
            """,
            cancellationToken,
            ("@businessId", businessId),
            ("@phoneHash", phoneHash));

        if (audited > 0)
            return false;

        var connection = loyaltyDb.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM "Member"
                WHERE "businessId" = @businessId
                  AND "phone" = @phone
                  AND (@excludeMemberId IS NULL OR "id" <> @excludeMemberId)
                """;
            AddParameter(command, "@businessId", businessId);
            AddParameter(command, "@phone", phone);
            var excludeMemberParameter = command.CreateParameter();
            excludeMemberParameter.ParameterName = "@excludeMemberId";
            excludeMemberParameter.DbType = DbType.String;
            excludeMemberParameter.Value = (object?)excludeMemberId ?? DBNull.Value;
            command.Parameters.Add(excludeMemberParameter);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(value) == 0;
        }
        finally
        {
            if (close)
                await connection.CloseAsync();
        }
    }

    private async Task RecordPhoneUsageAsync(
        string businessId,
        string? memberId,
        string phone,
        string reason,
        CancellationToken cancellationToken)
    {
        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PhoneChangeAudit"
                ("id", "businessId", "memberId", "phoneHash", "phoneLast4", "reason", "createdAt")
            VALUES
                ({Guid.NewGuid().ToString()}, {businessId}, {memberId}, {Hash(phone)}, {PhoneLast4(phone)}, {reason}, {DateTime.UtcNow})
            ON CONFLICT ("businessId", "phoneHash")
            DO UPDATE SET
                "memberId" = EXCLUDED."memberId",
                "phoneLast4" = EXCLUDED."phoneLast4",
                "reason" = EXCLUDED."reason"
            """, cancellationToken);
    }

    private async Task EnsureWhatsAppEnabledAsync(
        string businessId,
        CancellationToken cancellationToken)
    {
        if (await IsWhatsAppEnabledAsync(businessId, cancellationToken))
            return;

        throw new MemberPhoneOtpException(
            MemberPhoneOtpErrorCode.FeatureDisabled,
            "Fitur WhatsApp tidak tersedia untuk membership ini.");
    }

    private async Task<bool> IsWhatsAppEnabledAsync(
        string businessId,
        CancellationToken cancellationToken)
    {
        var connection = loyaltyDb.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH business AS (
                    SELECT id, UPPER(COALESCE(tier, 'FREE')) AS tier
                    FROM "Business"
                    WHERE id = @businessId
                    LIMIT 1
                ),
                subscription AS (
                    SELECT
                        "planId",
                        status,
                        "currentPeriodEnd",
                        "graceEndsAt"
                    FROM "BusinessSubscription"
                    WHERE "businessId" = @businessId
                    ORDER BY "createdAt" DESC
                    LIMIT 1
                ),
                effective AS (
                    SELECT CASE
                        WHEN s."planId" IS NOT NULL
                             AND (
                                 s.status = 'expired'
                                 OR (
                                     s.status = 'grace_7_days'
                                     AND now() >= COALESCE(
                                         s."graceEndsAt",
                                         s."currentPeriodEnd" + interval '7 days')
                                 )
                                 OR (
                                     s.status IN ('active', 'scheduled_cancel')
                                     AND now() >= s."currentPeriodEnd" + interval '7 days'
                                 )
                             )
                            THEN (SELECT id FROM "Plan" WHERE UPPER(code) = 'FREE' LIMIT 1)
                        WHEN s."planId" IS NOT NULL THEN s."planId"
                        WHEN b.tier = 'PRO'
                            THEN (SELECT id FROM "Plan" WHERE UPPER(code) = 'PRO' LIMIT 1)
                        ELSE (SELECT id FROM "Plan" WHERE UPPER(code) = 'FREE' LIMIT 1)
                    END AS "planId"
                    FROM business b
                    LEFT JOIN subscription s ON true
                )
                SELECT COALESCE(
                    CASE
                        WHEN UPPER(p.code) = 'ENTERPRISE'
                        THEN (bpo."otherFeatureFlags" ->> 'whatsappRegisterEnabled')::boolean
                    END,
                    (pl."otherFeatureFlags" ->> 'whatsappRegisterEnabled')::boolean,
                    false
                )
                FROM effective e
                JOIN "Plan" p ON p.id = e."planId"
                LEFT JOIN "PlanLimit" pl ON pl."planId" = p.id
                LEFT JOIN "BusinessPlanOverride" bpo ON bpo."businessId" = @businessId
                LIMIT 1
                """;
            AddParameter(command, "@businessId", businessId);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is bool enabled && enabled;
        }
        finally
        {
            if (close)
                await connection.CloseAsync();
        }
    }

    private async Task<BusinessRef> ResolveBusinessAsync(
        string? businessId,
        string? businessSlug,
        CancellationToken cancellationToken)
    {
        var normalizedId = businessId?.Trim();
        var normalizedSlug = businessSlug?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalizedId) && string.IsNullOrWhiteSpace(normalizedSlug))
        {
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.Validation,
                "Business context is required.");
        }

        var query = authDb.Businesses.AsNoTracking();
        var business = !string.IsNullOrWhiteSpace(normalizedId)
            ? await query.SingleOrDefaultAsync(x => x.Id == normalizedId, cancellationToken)
            : await query.SingleOrDefaultAsync(
                x => x.Slug.ToLower() == normalizedSlug,
                cancellationToken);

        if (business is null)
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.BusinessNotFound, "Business not found.");
        if (!business.IsActive)
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.BusinessInactive, "Business is inactive.");

        return new BusinessRef(business.Id, business.Slug);
    }

    private async Task<OtpSessionRow?> LoadLatestOtpSessionAsync(
        string businessId,
        string normalizedPhone,
        string purpose,
        CancellationToken cancellationToken) =>
        await LoadOtpSessionByQueryAsync(
            """
            SELECT
                "id", "memberId", "otpHash", "attemptCount", "resendCount",
                "lastSentAt", "expiresAt", "blockedUntil", "verifiedAt", "usedAt",
                "verificationTokenHash", "verificationExpiresAt", "deviceId"
            FROM "OtpSession"
            WHERE "businessId" = @businessId
              AND "phoneHash" = @phoneHash
              AND "purpose" = @purpose
            ORDER BY "createdAt" DESC
            LIMIT 1
            """,
            cancellationToken,
            ("@businessId", businessId),
            ("@phoneHash", Hash(normalizedPhone)),
            ("@purpose", purpose));

    private async Task<OtpSessionRow?> LoadOtpSessionAsync(
        string otpSessionId,
        string businessId,
        string normalizedPhone,
        string purpose,
        CancellationToken cancellationToken) =>
        await LoadOtpSessionByQueryAsync(
            """
            SELECT
                "id", "memberId", "otpHash", "attemptCount", "resendCount",
                "lastSentAt", "expiresAt", "blockedUntil", "verifiedAt", "usedAt",
                "verificationTokenHash", "verificationExpiresAt", "deviceId"
            FROM "OtpSession"
            WHERE "id" = @id
              AND "businessId" = @businessId
              AND "phoneHash" = @phoneHash
              AND "purpose" = @purpose
            LIMIT 1
            """,
            cancellationToken,
            ("@id", otpSessionId),
            ("@businessId", businessId),
            ("@phoneHash", Hash(normalizedPhone)),
            ("@purpose", purpose));

    private async Task<OtpSessionRow?> LoadOtpSessionByQueryAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var connection = loyaltyDb.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                AddParameter(command, name, value ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new OtpSessionRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetDateTime(5),
                reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                reader.IsDBNull(12) ? null : reader.GetString(12));
        }
        finally
        {
            if (close)
                await connection.CloseAsync();
        }
    }

    private async Task<T> ExecuteScalarAsync<T>(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var connection = loyaltyDb.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                AddParameter(command, name, value ?? DBNull.Value);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return (T)Convert.ChangeType(result!, typeof(T));
        }
        finally
        {
            if (close)
                await connection.CloseAsync();
        }
    }

    private async Task TryAuditAsync(
        string eventType,
        string purpose,
        string businessId,
        string? memberId,
        string? otpSessionId,
        string phone,
        string? ip,
        string deviceId,
        string? userAgent,
        string status,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var connection = loyaltyDb.Database.GetDbConnection();
            var close = connection.State != ConnectionState.Open;
            if (close)
                await connection.OpenAsync(cancellationToken);

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO otp_audit_logs
                        (event_type, channel, purpose, business_id, member_id,
                         otp_session_id, phone_hash, ip, device_id, user_agent,
                         is_proxy, fingerprint_match, status, error_code, metadata)
                    VALUES
                        (@eventType, 'wa', @purpose, @businessId,
                         CASE WHEN @memberId IS NULL THEN NULL ELSE CAST(@memberId AS uuid) END,
                         CASE WHEN @otpSessionId IS NULL THEN NULL ELSE CAST(@otpSessionId AS uuid) END,
                         @phoneHash,
                         CASE WHEN @ip IS NULL OR @ip = '' THEN NULL ELSE CAST(@ip AS inet) END,
                         @deviceId, @userAgent, false, NULL, @status, @errorCode, '{}'::jsonb)
                    """;
                AddParameter(command, "@eventType", eventType);
                AddParameter(command, "@purpose", purpose);
                AddParameter(command, "@businessId", businessId);
                AddParameter(command, "@memberId", (object?)memberId ?? DBNull.Value);
                AddParameter(command, "@otpSessionId", (object?)otpSessionId ?? DBNull.Value);
                AddParameter(command, "@phoneHash", Hash(phone));
                AddParameter(command, "@ip", (object?)ip ?? DBNull.Value);
                AddParameter(command, "@deviceId", deviceId);
                AddParameter(command, "@userAgent", (object?)userAgent ?? DBNull.Value);
                AddParameter(command, "@status", status);
                AddParameter(command, "@errorCode", (object?)errorCode ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                if (close)
                    await connection.CloseAsync();
            }
        }
        catch
        {
            // Audit must not break OTP delivery/verification.
        }
    }

    private static string NormalizePhoneE164(string input)
    {
        var trimmed = string.Concat((input ?? string.Empty).Trim().Where(ch => !char.IsWhiteSpace(ch)));
        if (trimmed.Length == 0)
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.Validation, "Phone is required.");

        var digits = trimmed.StartsWith('+') ? trimmed[1..] : trimmed;
        if (digits.Length is < 8 or > 15 || digits.Any(ch => !char.IsDigit(ch)))
        {
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.Validation,
                "Phone must contain 8-15 digits and may start with +.");
        }

        return trimmed.StartsWith('+') ? trimmed : $"+{trimmed}";
    }

    private static string RequireDeviceId(string deviceId)
    {
        var normalized = deviceId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.Validation, "Device ID is required.");
        if (normalized.Length > 256)
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.Validation, "Device ID is invalid.");
        return normalized;
    }

    private static void ValidateOtp(string otp)
    {
        if (string.IsNullOrWhiteSpace(otp)
            || otp.Length != 6
            || otp.Any(ch => !char.IsDigit(ch)))
        {
            throw new MemberPhoneOtpException(MemberPhoneOtpErrorCode.Validation, "Invalid OTP payload.");
        }
    }

    private static string PurposeName(MemberPhoneOtpPurpose purpose) =>
        purpose switch
        {
            MemberPhoneOtpPurpose.Signup => "signup",
            MemberPhoneOtpPurpose.ForgotPassword => "forgot-password",
            MemberPhoneOtpPurpose.ProfilePhone => "profile_phone",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedTimeEquals(string expectedHex, string actualHex)
    {
        try
        {
            var expected = Convert.FromHexString(expectedHex);
            var actual = Convert.FromHexString(actualHex);
            return expected.Length == actual.Length
                   && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string CreateOpaqueToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string PhoneLast4(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : digits;
    }

    private static MemberPhoneOtpException Conflict(string message) =>
        new(MemberPhoneOtpErrorCode.Conflict, message);

    private static MemberPhoneOtpException ProviderUnavailable() =>
        new(
            MemberPhoneOtpErrorCode.ProviderUnavailable,
            "WhatsApp OTP provider is not configured.");

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record BusinessRef(string Id, string Slug);

    private sealed record OtpSessionRow(
        string Id,
        string? MemberId,
        string OtpHash,
        int AttemptCount,
        int ResendCount,
        DateTime LastSentAt,
        DateTime ExpiresAt,
        DateTime? BlockedUntil,
        DateTime? VerifiedAt,
        DateTime? UsedAt,
        string? VerificationTokenHash,
        DateTime? VerificationExpiresAt,
        string? DeviceId);
}
