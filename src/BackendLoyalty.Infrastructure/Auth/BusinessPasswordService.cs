using System.Data;
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

        // Supabase auth had globally unique emails. If migrated data violates that
        // assumption across multiple auth identities, do not guess which account to reset.
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
        await refreshSessions.RevokeAllAsync(
            userId,
            "business",
            "password_changed",
            cancellationToken);

        return PasswordUpdateResult.Success;
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
