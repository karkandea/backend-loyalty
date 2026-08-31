using System.Data;
using BackendLoyalty.Application.Loyalty;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Loyalty;

public sealed class LoyaltyStampService(LoyaltyDbContext dbContext) : ILoyaltyStampService
{
    private const int MaxOverflowIterations = 25;
    private static readonly TimeSpan LegacyMemberRewardTtl = TimeSpan.FromMinutes(15);

    public async Task<AddStampResult> AddStampsAsync(
        AddStampCommand command,
        CancellationToken cancellationToken = default)
    {
        _ = command.TransactionNotes; // Accepted for API parity; legacy Transaction has no notes column.

        await ValidateInitialStateAsync(command, cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        var operationNow = DateTime.UtcNow;
        var remaining = command.StampCount;
        var safety = 0;
        var transactionIds = new List<string>();
        var milestoneHits = new List<MilestoneHitResult>();
        var transitions = new List<CardTransitionResult>();
        var issuedTokens = new List<IssuedRewardTokenResult>();
        var didAdvanceCard = false;

        ActiveMemberCardSnapshot? activeMemberCard = null;
        CardSnapshot? activeCard = null;

        while (remaining > 0 && safety < MaxOverflowIterations)
        {
            safety += 1;

            var fresh = await LoadActiveMemberCardAsync(
                command.MemberId,
                command.BusinessId,
                cancellationToken);

            if (fresh is null)
            {
                throw new LoyaltyOperationException(
                    LoyaltyOperationErrorCode.Internal,
                    "Active member card not found");
            }

            activeMemberCard = fresh.MemberCard;
            activeCard = fresh.Card;

            if (!string.Equals(activeCard.Status, "ACTIVE", StringComparison.Ordinal) || activeCard.IsDeleted)
            {
                throw new LoyaltyOperationException(
                    LoyaltyOperationErrorCode.Internal,
                    "Active card is not valid");
            }

            var previousStamps = activeMemberCard.CurrentStamps;
            var requiredStamps = Math.Max(activeCard.RequiredStamps, 1);
            var available = Math.Max(requiredStamps - previousStamps, 0);
            var applied = Math.Min(remaining, available);
            var newStamps = previousStamps + applied;

            var updatedRows = await dbContext.MemberCards
                .Where(x =>
                    x.Id == activeMemberCard.Id &&
                    x.BusinessId == command.BusinessId &&
                    x.MemberId == command.MemberId &&
                    x.IsActive &&
                    x.CurrentStamps == previousStamps)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.CurrentStamps, newStamps)
                        .SetProperty(x => x.UpdatedAt, operationNow),
                    cancellationToken);

            // Another request changed this card first. Reload and retry against the fresh value.
            if (updatedRows == 0)
            {
                continue;
            }

            if (applied > 0)
            {
                var transactionId = Guid.NewGuid().ToString();
                dbContext.Transactions.Add(new LoyaltyTransaction
                {
                    Id = transactionId,
                    BusinessId = command.BusinessId,
                    MemberId = command.MemberId,
                    MemberCardId = activeMemberCard.Id,
                    CardId = activeMemberCard.CardId,
                    RewardId = null,
                    OutletId = command.OutletId,
                    StaffId = command.StaffId,
                    Type = "STAMP_ADDED",
                    StampsAdded = applied,
                    CreatedAt = operationNow,
                    UpdatedAt = operationNow,
                });

                await dbContext.SaveChangesAsync(cancellationToken);
                transactionIds.Add(transactionId);
            }

            var milestones = await dbContext.CardMilestones
                .Where(x =>
                    x.CardId == activeMemberCard.CardId &&
                    x.StampCount > previousStamps &&
                    x.StampCount <= newStamps)
                .OrderBy(x => x.StampCount)
                .ToListAsync(cancellationToken);

            var originalMilestoneHits = milestones
                .Select(x => new MilestoneHitResult(x.StampCount, x.RewardId))
                .ToList();

            await GenerateMilestoneRewardsAsync(
                command.BusinessId,
                command.MemberId,
                activeMemberCard.Id,
                milestones,
                cancellationToken);

            milestoneHits.AddRange(originalMilestoneHits);
            remaining -= applied;

            if (remaining <= 0)
            {
                activeMemberCard = activeMemberCard with { CurrentStamps = newStamps };
                break;
            }

            if (newStamps < requiredStamps)
            {
                activeMemberCard = activeMemberCard with { CurrentStamps = newStamps };
                break;
            }

            didAdvanceCard = true;

            var deactivatedRows = await dbContext.MemberCards
                .Where(x => x.Id == activeMemberCard.Id && x.IsActive)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.IsActive, false)
                        .SetProperty(x => x.CompletedAt, operationNow)
                        .SetProperty(x => x.UpdatedAt, operationNow),
                    cancellationToken);

            if (deactivatedRows == 0)
            {
                continue;
            }

            var nextCard = await SelectNextCardAsync(
                command.BusinessId,
                activeCard,
                cancellationToken);

            if (nextCard is null)
            {
                throw new LoyaltyOperationException(
                    LoyaltyOperationErrorCode.Internal,
                    "Next card not found");
            }

            var newMemberCard = new MemberCard
            {
                Id = Guid.NewGuid().ToString(),
                BusinessId = command.BusinessId,
                MemberId = command.MemberId,
                CardId = nextCard.Id,
                CurrentStamps = 0,
                IsActive = true,
                StartedAt = operationNow,
                CompletedAt = null,
                CreatedAt = operationNow,
                UpdatedAt = operationNow,
            };

            dbContext.MemberCards.Add(newMemberCard);
            await dbContext.SaveChangesAsync(cancellationToken);

            transitions.Add(new CardTransitionResult(
                activeMemberCard.CardId,
                nextCard.Id,
                activeCard.Level,
                nextCard.Level,
                nextCard.Level == activeCard.Level ? "CYCLE" : "LEVEL_UP"));

            activeMemberCard = new ActiveMemberCardSnapshot(
                newMemberCard.Id,
                newMemberCard.MemberId,
                newMemberCard.CardId,
                newMemberCard.CurrentStamps);
            activeCard = nextCard;
        }

        if (remaining > 0 || activeMemberCard is null || activeCard is null)
        {
            throw new LoyaltyOperationException(
                LoyaltyOperationErrorCode.Internal,
                "Failed to apply all stamps");
        }

        await transaction.CommitAsync(cancellationToken);

        return new AddStampResult(
            transactionIds.LastOrDefault() ?? string.Empty,
            transactionIds,
            activeMemberCard.Id,
            activeMemberCard.CurrentStamps,
            activeCard.RequiredStamps,
            didAdvanceCard,
            activeCard.Level,
            transitions,
            milestoneHits.FirstOrDefault(),
            issuedTokens);
    }

    private async Task ValidateInitialStateAsync(
        AddStampCommand command,
        CancellationToken cancellationToken)
    {
        var memberCard = await dbContext.MemberCards
            .AsNoTracking()
            .Where(x => x.Id == command.MemberCardId)
            .Select(x => new
            {
                x.Id,
                x.MemberId,
                x.CardId,
                x.IsActive,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (memberCard is null || memberCard.MemberId != command.MemberId)
        {
            throw new LoyaltyOperationException(
                LoyaltyOperationErrorCode.NotFound,
                "Member card not found");
        }

        if (!memberCard.IsActive)
        {
            throw new LoyaltyOperationException(
                LoyaltyOperationErrorCode.Validation,
                "Card is not active");
        }

        var memberExists = await dbContext.Members
            .AsNoTracking()
            .AnyAsync(
                x => x.Id == command.MemberId && x.BusinessId == command.BusinessId,
                cancellationToken);

        if (!memberExists)
        {
            throw new LoyaltyOperationException(
                LoyaltyOperationErrorCode.NotFound,
                "Member not found");
        }

        var card = await dbContext.Cards
            .AsNoTracking()
            .Where(x => x.Id == memberCard.CardId)
            .Select(x => new
            {
                x.BusinessId,
                x.Status,
                x.IsDeleted,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (card is null || card.BusinessId != command.BusinessId)
        {
            throw new LoyaltyOperationException(
                LoyaltyOperationErrorCode.NotFound,
                "Card not found for this business");
        }

        if (card.Status != "ACTIVE" || card.IsDeleted)
        {
            throw new LoyaltyOperationException(
                LoyaltyOperationErrorCode.Validation,
                "Card is not active");
        }
    }

    private async Task<ActiveCardState?> LoadActiveMemberCardAsync(
        string memberId,
        string businessId,
        CancellationToken cancellationToken)
    {
        return await (
            from memberCard in dbContext.MemberCards.AsNoTracking()
            join card in dbContext.Cards.AsNoTracking() on memberCard.CardId equals card.Id
            where memberCard.MemberId == memberId
                  && memberCard.BusinessId == businessId
                  && memberCard.IsActive
            select new ActiveCardState(
                new ActiveMemberCardSnapshot(
                    memberCard.Id,
                    memberCard.MemberId,
                    memberCard.CardId,
                    memberCard.CurrentStamps),
                new CardSnapshot(
                    card.Id,
                    card.BusinessId,
                    card.RequiredStamps,
                    card.Status,
                    card.Level,
                    card.IsDeleted)))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<CardSnapshot?> SelectNextCardAsync(
        string businessId,
        CardSnapshot currentCard,
        CancellationToken cancellationToken)
    {
        var currentLevel = currentCard.Level ?? 1;

        var nextLevelCard = await dbContext.Cards
            .AsNoTracking()
            .Where(x =>
                x.BusinessId == businessId &&
                x.Status == "ACTIVE" &&
                !x.IsDeleted &&
                x.Level > currentLevel)
            .OrderBy(x => x.Level)
            .ThenBy(x => x.CreatedAt)
            .Select(x => new CardSnapshot(
                x.Id,
                x.BusinessId,
                x.RequiredStamps,
                x.Status,
                x.Level,
                x.IsDeleted))
            .FirstOrDefaultAsync(cancellationToken);

        if (nextLevelCard is not null)
        {
            return nextLevelCard;
        }

        return await dbContext.Cards
            .AsNoTracking()
            .Where(x =>
                x.BusinessId == businessId &&
                x.Status == "ACTIVE" &&
                !x.IsDeleted &&
                x.Level == currentLevel)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new CardSnapshot(
                x.Id,
                x.BusinessId,
                x.RequiredStamps,
                x.Status,
                x.Level,
                x.IsDeleted))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task GenerateMilestoneRewardsAsync(
        string businessId,
        string memberId,
        string memberCardId,
        IReadOnlyList<CardMilestone> milestones,
        CancellationToken cancellationToken)
    {
        if (milestones.Count == 0)
        {
            return;
        }

        var validRewardIds = milestones
            .Where(x => !string.IsNullOrWhiteSpace(x.RewardId))
            .Select(x => x.RewardId!)
            .Distinct()
            .ToArray();

        var alreadyIssuedRewardIds = validRewardIds.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await dbContext.MemberRewards
                .AsNoTracking()
                .Where(x =>
                    x.MemberCardId == memberCardId &&
                    x.MemberId == memberId &&
                    x.SourceType == "MILESTONE" &&
                    validRewardIds.Contains(x.RewardId))
                .Select(x => x.RewardId)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var memberRewardExpiresAt = now.Add(LegacyMemberRewardTtl);
        var processedRewards = new List<string>();

        foreach (var milestone in milestones)
        {
            var rewardId = milestone.RewardId;

            if (string.IsNullOrWhiteSpace(rewardId) && !string.IsNullOrWhiteSpace(milestone.Title))
            {
                rewardId = Guid.NewGuid().ToString();
                dbContext.Rewards.Add(new Reward
                {
                    Id = rewardId,
                    BusinessId = businessId,
                    Name = milestone.Title,
                    Description = $"Auto-created reward for milestone at {milestone.StampCount} stamps",
                    SourceType = "MILESTONE",
                    DefaultExpiryDays = 30,
                    CreatedAt = now,
                    UpdatedAt = now,
                });

                milestone.RewardId = rewardId;
                milestone.UpdatedAt = now;
            }

            if (!string.IsNullOrWhiteSpace(rewardId))
            {
                processedRewards.Add(rewardId);
            }
        }

        foreach (var rewardId in processedRewards.Distinct(StringComparer.Ordinal))
        {
            if (alreadyIssuedRewardIds.Contains(rewardId))
            {
                continue;
            }

            dbContext.MemberRewards.Add(new MemberReward
            {
                Id = Guid.NewGuid().ToString(),
                BusinessId = businessId,
                MemberId = memberId,
                RewardId = rewardId,
                MemberCardId = memberCardId,
                SourceType = "MILESTONE",
                Status = "AVAILABLE",
                IssuedAt = now,
                ExpiresAt = memberRewardExpiresAt,
                RedeemedAt = null,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private sealed record ActiveCardState(
        ActiveMemberCardSnapshot MemberCard,
        CardSnapshot Card);

    private sealed record ActiveMemberCardSnapshot(
        string Id,
        string MemberId,
        string CardId,
        int CurrentStamps);

    private sealed record CardSnapshot(
        string Id,
        string BusinessId,
        int RequiredStamps,
        string Status,
        int? Level,
        bool IsDeleted);
}
