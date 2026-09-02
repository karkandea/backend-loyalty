using System.Data;
using System.Security.Cryptography;
using System.Text;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Auth;

public sealed class AdminPasswordService(
    StandaloneAuthDbContext db,
    IStandaloneCredentialService credentials,
    IRefreshTokenSessionService refreshSessions) : IAdminPasswordService
{
    private static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(45);

    public async Task<AdminPasswordResetIssue?> CreateResetAsync(
        string businessId,
        string email,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var admin = await db.AdminUsers.SingleOrDefaultAsync(
            x => x.BusinessId == businessId &&
                 x.IsActive &&
                 x.Email.ToLower() == normalizedEmail,
            cancellationToken);

        if (admin is null)
            return null;

        var now = DateTime.UtcNow;
        await db.AuthPasswordResets
            .Where(x => x.UserId == admin.Id && x.AuthKind == "admin" && x.UsedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var reset = new AuthPasswordReset
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            AuthKind = "admin",
            Email = normalizedEmail,
            TokenHash = Hash(rawToken),
            ExpiresAt = now + ResetLifetime,
            UsedAt = null,
            Ip = NullIfBlank(ip),
            UserAgent = NullIfBlank(userAgent),
            CreatedAt = now,
        };

        db.AuthPasswordResets.Add(reset);
        await db.SaveChangesAsync(cancellationToken);
        return new AdminPasswordResetIssue(admin.Id, normalizedEmail, rawToken, reset.ExpiresAt);
    }

    public async Task<PasswordResetResult> ResetPasswordAsync(
        string rawToken,
        string newPassword,
        string? tenantSlug,
        CancellationToken cancellationToken)
    {
        var tokenHash = Hash(rawToken.Trim());
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var reset = await db.AuthPasswordResets.AsNoTracking().SingleOrDefaultAsync(
            x => x.TokenHash == tokenHash &&
                 x.AuthKind == "admin" &&
                 x.UsedAt == null &&
                 x.ExpiresAt > now,
            cancellationToken);

        if (reset is null)
            return PasswordResetResult.InvalidOrExpired;

        var admin = await db.AdminUsers.SingleOrDefaultAsync(
            x => x.Id == reset.UserId && x.IsActive,
            cancellationToken);
        if (admin is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return PasswordResetResult.InvalidOrExpired;
        }

        var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == admin.BusinessId && x.IsActive,
            cancellationToken);
        if (business is null ||
            (!string.IsNullOrWhiteSpace(tenantSlug) &&
             !string.Equals(business.Slug, tenantSlug.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return PasswordResetResult.InvalidOrExpired;
        }

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

        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        admin.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await DeleteLegacyBridgeAsync(admin.Id, cancellationToken);
        await refreshSessions.RevokeAllAsync(
            admin.Id,
            "admin",
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
        var admin = await db.AdminUsers.SingleOrDefaultAsync(
            x => x.Id == userId && x.IsActive,
            cancellationToken);
        if (admin is null)
            return PasswordUpdateResult.UserNotFound;

        AdminLoginProfile profile;
        try
        {
            profile = await credentials.AuthenticateAdminAsync(
                admin.Email,
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
        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        admin.UpdatedAt = now;

        await db.AuthPasswordResets
            .Where(x => x.UserId == userId && x.AuthKind == "admin" && x.UsedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.UsedAt, now),
                cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await DeleteLegacyBridgeAsync(userId, cancellationToken);
        await refreshSessions.RevokeAllAsync(
            userId,
            "admin",
            "password_changed",
            cancellationToken);

        return PasswordUpdateResult.Success;
    }

    private Task<int> DeleteLegacyBridgeAsync(
        string userId,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM \"LegacyAuthUserPassword\" WHERE \"authUserId\" = {userId}",
            cancellationToken);

    private static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
