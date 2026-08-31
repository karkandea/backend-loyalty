using System.Data;
using BackendLoyalty.Application.Rewards;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Rewards;

public sealed class RewardRedemptionService(LoyaltyDbContext dbContext) : IRewardRedemptionService
{
    public async Task<RewardRedemptionExecutionResult> RedeemAsync(
        RedeemRewardCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var now = DateTime.UtcNow;

        var token = await dbContext.RewardTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Token == command.RewardToken, cancellationToken);

        if (token is null)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.NotFound,
                "Reward token not found");
        }

        if (token.BusinessId != command.BusinessId)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.Forbidden,
                "Token not valid for this business");
        }

        if (!string.Equals(token.Status, "ACTIVE", StringComparison.Ordinal))
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.Expired,
                "Reward token has expired");
        }

        if (token.UsedAt is not null)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.Used,
                "Reward token already redeemed");
        }

        if (token.ExpiresAt is not null && token.ExpiresAt < now)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.Expired,
                "Reward token has expired");
        }

        if (string.IsNullOrWhiteSpace(token.MemberRewardId))
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.NotFound,
                "Reward token not found");
        }

        var memberReward = await dbContext.MemberRewards
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == token.MemberRewardId, cancellationToken);

        if (memberReward is null)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.NotFound,
                "Reward token not found");
        }

        if (memberReward.BusinessId != command.BusinessId)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.Forbidden,
                "Token not valid for this business");
        }

        if (!string.Equals(memberReward.Status, "AVAILABLE", StringComparison.Ordinal))
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.Unavailable,
                "Reward is not available for redemption");
        }

        if (string.IsNullOrWhiteSpace(memberReward.MemberCardId))
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.MissingMemberCard,
                "Reward is not linked to a member card");
        }

        var memberCard = await dbContext.MemberCards
            .AsNoTracking()
            .Where(x => x.Id == memberReward.MemberCardId)
            .Select(x => new { x.Id, x.CardId, x.BusinessId })
            .SingleOrDefaultAsync(cancellationToken);

        if (memberCard is null)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.NotFound,
                "Reward token not found");
        }

        if (memberCard.BusinessId != command.BusinessId)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.Forbidden,
                "Token not valid for this business");
        }

        var tokenRows = await dbContext.RewardTokens
            .Where(x =>
                x.Id == token.Id &&
                x.BusinessId == command.BusinessId &&
                x.Status == "ACTIVE" &&
                x.UsedAt == null &&
                (x.ExpiresAt == null || x.ExpiresAt >= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.UsedAt, now)
                    .SetProperty(x => x.UsedByStaffId, command.StaffId)
                    .SetProperty(x => x.UsedAtOutletId, command.OutletId)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        if (tokenRows == 0)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.Used,
                "Reward token already redeemed");
        }

        var rewardRows = await dbContext.MemberRewards
            .Where(x =>
                x.Id == memberReward.Id &&
                x.BusinessId == command.BusinessId &&
                x.Status == "AVAILABLE")
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, "REDEEMED")
                    .SetProperty(x => x.RedeemedAt, now)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        if (rewardRows == 0)
        {
            throw new RewardRedemptionException(
                RewardRedemptionErrorCode.Unavailable,
                "Reward is not available for redemption");
        }

        var transactionId = Guid.NewGuid().ToString();
        dbContext.Transactions.Add(new LoyaltyTransaction
        {
            Id = transactionId,
            BusinessId = command.BusinessId,
            MemberId = memberReward.MemberId,
            MemberCardId = memberCard.Id,
            CardId = memberCard.CardId,
            RewardId = memberReward.RewardId,
            OutletId = command.OutletId,
            StaffId = command.StaffId,
            Type = "REWARD_REDEEMED",
            StampsAdded = 0,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RewardRedemptionExecutionResult(
            new RewardRedemptionResult(
                token.Id,
                memberReward.Id,
                "REDEEMED",
                now,
                transactionId),
            memberReward.MemberId,
            memberCard.Id,
            memberReward.RewardId);
    }
}
