using System.Data;
using System.Security.Cryptography;
using System.Text;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Auth;

public sealed class RefreshTokenSessionService(StandaloneAuthDbContext db) : IRefreshTokenSessionService
{
    public async Task RegisterAsync(
        string refreshToken,
        DateTime expiresAt,
        RefreshSessionContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        db.AuthRefreshSessions.Add(new AuthRefreshSession
        {
            Id = Guid.NewGuid(),
            UserId = context.UserId,
            AuthKind = context.AuthKind,
            TokenHash = Hash(refreshToken),
            FamilyId = Guid.NewGuid(),
            ParentSessionId = null,
            Role = context.Role,
            BusinessId = context.BusinessId,
            OutletId = context.OutletId,
            ExpiresAt = expiresAt,
            RevokedAt = null,
            RevokeReason = null,
            ReplacedBySessionId = null,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RotateAsync(
        string currentRefreshToken,
        string nextRefreshToken,
        DateTime nextExpiresAt,
        RefreshSessionContext nextContext,
        CancellationToken cancellationToken)
    {
        var currentHash = Hash(currentRefreshToken);
        var nextHash = Hash(nextRefreshToken);
        var now = DateTime.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var current = await db.AuthRefreshSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TokenHash == currentHash, cancellationToken);

        if (current is null || current.ExpiresAt <= now)
            return false;

        if (current.RevokedAt is not null)
        {
            if (current.ReplacedBySessionId is not null)
            {
                await db.AuthRefreshSessions
                    .Where(x =>
                        x.FamilyId == current.FamilyId &&
                        x.RevokedAt == null &&
                        x.ExpiresAt > now)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(x => x.RevokedAt, now)
                            .SetProperty(x => x.RevokeReason, "reuse_detected"),
                        cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            return false;
        }

        if (!string.Equals(current.UserId, nextContext.UserId, StringComparison.Ordinal) ||
            !string.Equals(current.AuthKind, nextContext.AuthKind, StringComparison.Ordinal))
        {
            return false;
        }

        var nextId = Guid.NewGuid();

        db.AuthRefreshSessions.Add(new AuthRefreshSession
        {
            Id = nextId,
            UserId = nextContext.UserId,
            AuthKind = nextContext.AuthKind,
            TokenHash = nextHash,
            FamilyId = current.FamilyId,
            ParentSessionId = current.Id,
            Role = nextContext.Role,
            BusinessId = nextContext.BusinessId,
            OutletId = nextContext.OutletId,
            ExpiresAt = nextExpiresAt,
            RevokedAt = null,
            RevokeReason = null,
            ReplacedBySessionId = null,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        var consumed = await db.AuthRefreshSessions
            .Where(x => x.Id == current.Id && x.RevokedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.RevokedAt, now)
                    .SetProperty(x => x.RevokeReason, "rotated")
                    .SetProperty(x => x.ReplacedBySessionId, nextId),
                cancellationToken);

        if (consumed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeAsync(
        string refreshToken,
        string reason,
        CancellationToken cancellationToken)
    {
        var hash = Hash(refreshToken);
        var now = DateTime.UtcNow;
        var affected = await db.AuthRefreshSessions
            .Where(x => x.TokenHash == hash && x.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.RevokedAt, now)
                    .SetProperty(x => x.RevokeReason, reason),
                cancellationToken);
        return affected == 1;
    }

    public Task<int> RevokeAllAsync(
        string userId,
        string authKind,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        return db.AuthRefreshSessions
            .Where(x =>
                x.UserId == userId &&
                x.AuthKind == authKind &&
                x.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.RevokedAt, now)
                    .SetProperty(x => x.RevokeReason, reason),
                cancellationToken);
    }

    private static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
