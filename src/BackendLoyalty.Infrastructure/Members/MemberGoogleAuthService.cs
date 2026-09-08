using System.Data;
using System.Security.Cryptography;
using System.Text;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Members;

public sealed class MemberGoogleAuthService(
    LoyaltyDbContext loyaltyDb,
    StandaloneAuthDbContext authDb) : IMemberGoogleAuthService
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(7);

    public async Task<MemberGoogleLoginResult> SignInAsync(
        MemberGoogleProfile profile,
        string businessSlug,
        string? next,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var slug = businessSlug.Trim().ToLowerInvariant();
        var redirect = NormalizeRedirect(next);

        var business = await authDb.Businesses.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Slug.ToLower() == slug,
                cancellationToken);

        if (business is null)
            throw new MemberGoogleAuthException(
                MemberGoogleAuthErrorCode.BusinessNotFound,
                "Business not found");

        if (!business.IsActive)
            throw new MemberGoogleAuthException(
                MemberGoogleAuthErrorCode.BusinessInactive,
                "Business is inactive");

        var normalizedEmail = profile.Email.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        var identity = await LoadIdentityByEmailAsync(
            business.Id,
            normalizedEmail,
            cancellationToken);

        Member member;
        var isNewMember = false;

        if (identity is not null)
        {
            member = await loyaltyDb.Members.SingleOrDefaultAsync(
                x => x.Id == identity.MemberId && x.BusinessId == business.Id,
                cancellationToken)
                ?? throw new MemberGoogleAuthException(
                    MemberGoogleAuthErrorCode.MemberInactive,
                    "Member not found");

            if (!member.IsActive)
                throw new MemberGoogleAuthException(
                    MemberGoogleAuthErrorCode.MemberInactive,
                    "Account is inactive");

            member.Name = profile.Name.Trim();
            if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
                member.AvatarUrl = profile.AvatarUrl.Trim();
            member.UpdatedAt = now;

            await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "MemberIdentity"
                SET "verifiedAt" = COALESCE("verifiedAt", {now}),
                    "lastLoginAt" = {now},
                    "failedLoginCount" = 0,
                    "lockedAt" = NULL,
                    "updatedAt" = {now}
                WHERE "id" = {identity.Id}
                  AND "businessId" = {business.Id}
                  AND "memberId" = {member.Id}
                """, cancellationToken);
        }
        else
        {
            var existingMember = await loyaltyDb.Members
                .Where(x => x.BusinessId == business.Id
                            && x.Email != null
                            && x.Email.ToLower() == normalizedEmail)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingMember is not null)
            {
                member = existingMember;

                if (!member.IsActive)
                    throw new MemberGoogleAuthException(
                        MemberGoogleAuthErrorCode.MemberInactive,
                        "Account is inactive");

                await CreateIdentityAsync(
                    member.Id,
                    business.Id,
                    normalizedEmail,
                    now,
                    cancellationToken);

                member.Name = profile.Name.Trim();
                if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
                    member.AvatarUrl = profile.AvatarUrl.Trim();
                member.UpdatedAt = now;
            }
            else
            {
                isNewMember = true;
                member = new Member
                {
                    Id = Guid.NewGuid().ToString(),
                    BusinessId = business.Id,
                    Name = profile.Name.Trim(),
                    Email = normalizedEmail,
                    Phone = null,
                    MemberBarcode = CreateMemberBarcode(),
                    TotalStamps = 0,
                    DateJoined = now,
                    DateOfBirth = null,
                    AvatarUrl = string.IsNullOrWhiteSpace(profile.AvatarUrl)
                        ? null
                        : profile.AvatarUrl.Trim(),
                    HasCompletedProfile = false,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                loyaltyDb.Members.Add(member);
                await loyaltyDb.SaveChangesAsync(cancellationToken);

                try
                {
                    await CreateIdentityAsync(
                        member.Id,
                        business.Id,
                        normalizedEmail,
                        now,
                        cancellationToken);
                }
                catch
                {
                    loyaltyDb.Members.Remove(member);
                    await loyaltyDb.SaveChangesAsync(cancellationToken);
                    throw;
                }
            }
        }

        await loyaltyDb.SaveChangesAsync(cancellationToken);

        var sessionToken = CreateOpaqueToken();
        var expiresAt = now + SessionTtl;

        loyaltyDb.MemberSessions.Add(new MemberSession
        {
            Id = Guid.NewGuid().ToString(),
            BusinessId = business.Id,
            MemberId = member.Id,
            SessionTokenHash = HashToken(sessionToken),
            ExpiresAt = expiresAt,
            RevokedAt = null,
            Ip = string.IsNullOrWhiteSpace(ip) ? null : ip,
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await loyaltyDb.SaveChangesAsync(cancellationToken);

        return new MemberGoogleLoginResult(
            member.Id,
            business.Id,
            business.Slug,
            sessionToken,
            expiresAt,
            isNewMember,
            isNewMember || !member.HasCompletedProfile
                ? "/member/welcome"
                : redirect);
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
                SELECT "id", "memberId"
                FROM "MemberIdentity"
                WHERE "businessId" = @businessId
                  AND lower("email") = @email
                ORDER BY "createdAt" DESC
                LIMIT 1
                """;
            AddParameter(command, "@businessId", businessId);
            AddParameter(command, "@email", normalizedEmail);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new IdentityRow(reader.GetString(0), reader.GetString(1));
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private async Task CreateIdentityAsync(
        string memberId,
        string businessId,
        string email,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var placeholderHash = $"oauth:google:{Guid.NewGuid():N}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        await loyaltyDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MemberIdentity"
                ("id", "businessId", "memberId", "email", "passwordHash",
                 "verifiedAt", "lastLoginAt", "failedLoginCount", "lockedAt",
                 "createdAt", "updatedAt")
            VALUES
                ({Guid.NewGuid().ToString()}, {businessId}, {memberId}, {email}, {placeholderHash},
                 {now}, {now}, {0}, NULL, {now}, {now})
            """, cancellationToken);
    }

    private static string NormalizeRedirect(string? next)
    {
        var value = string.IsNullOrWhiteSpace(next) ? "/member" : next.Trim();

        if (!value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains("://", StringComparison.Ordinal))
        {
            throw new MemberGoogleAuthException(
                MemberGoogleAuthErrorCode.InvalidRedirect,
                "Invalid redirect");
        }

        return value;
    }

    private static string CreateMemberBarcode() =>
        Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private static string CreateOpaqueToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record IdentityRow(string Id, string MemberId);
}
