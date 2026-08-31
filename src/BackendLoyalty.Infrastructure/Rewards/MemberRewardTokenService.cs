using System.Data;
using System.Security.Cryptography;
using BackendLoyalty.Application.Rewards;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Rewards;

public sealed class MemberRewardTokenService(LoyaltyDbContext dbContext) : IMemberRewardTokenService
{
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(5);

    public async Task<MemberRewardTokenResult> IssueAsync(
        IssueMemberRewardTokenCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            var reward = await dbContext.MemberRewards
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.Id == command.MemberRewardId
                         && x.MemberId == command.MemberId
                         && x.BusinessId == command.BusinessId,
                    cancellationToken);

            if (reward is null)
            {
                throw new MemberRewardTokenException(
                    MemberRewardTokenErrorCode.NotFound,
                    "Reward not found");
            }

            if (!string.Equals(reward.Status, "AVAILABLE", StringComparison.Ordinal))
            {
                throw new MemberRewardTokenException(
                    MemberRewardTokenErrorCode.Unavailable,
                    "Reward not available for redemption");
            }

            var now = DateTime.UtcNow;
            var expiresAt = now.Add(TokenTtl);

            await dbContext.RewardTokens
                .Where(x => x.MemberRewardId == reward.Id && x.Status == "ACTIVE")
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.Status, "EXPIRED")
                        .SetProperty(x => x.ExpiresAt, now)
                        .SetProperty(x => x.UpdatedAt, now),
                    cancellationToken);

            var token = new RewardToken
            {
                Id = Guid.NewGuid().ToString(),
                BusinessId = reward.BusinessId,
                MemberRewardId = reward.Id,
                MemberId = null,
                MemberCardId = null,
                Scope = "REWARD",
                Token = GenerateToken(),
                Status = "ACTIVE",
                ExpiresAt = expiresAt,
                UsedAt = null,
                UsedByStaffId = null,
                OutletId = null,
                UsedAtOutletId = null,
                CreatedAt = now,
                UpdatedAt = now,
            };

            dbContext.RewardTokens.Add(token);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new MemberRewardTokenResult(
                new RewardTokenResult(
                    token.Id,
                    token.Token,
                    token.Status,
                    expiresAt),
                new DateTimeOffset(expiresAt).ToUnixTimeMilliseconds());
        }
        catch (MemberRewardTokenException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MemberRewardTokenException(
                MemberRewardTokenErrorCode.Internal,
                "Unable to generate token",
                exception);
        }
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
