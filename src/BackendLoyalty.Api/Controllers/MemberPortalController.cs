using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/member")]
public sealed class MemberPortalController(
    IMemberSessionResolver memberSessionResolver,
    LoyaltyDbContext loyaltyDb,
    StandaloneAuthDbContext authDb) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        var session = await ResolveSessionAsync(cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var member = await loyaltyDb.Members.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == session.MemberId && x.BusinessId == session.BusinessId,
                cancellationToken);
        if (member is null)
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Member not found"));

        var business = await authDb.Businesses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == session.BusinessId, cancellationToken);

        var activeCard = await (
            from memberCard in loyaltyDb.MemberCards.AsNoTracking()
            join card in loyaltyDb.Cards.AsNoTracking() on memberCard.CardId equals card.Id
            where memberCard.BusinessId == session.BusinessId
                  && memberCard.MemberId == session.MemberId
                  && memberCard.IsActive
                  && card.BusinessId == session.BusinessId
                  && card.Status == "ACTIVE"
                  && !card.IsDeleted
            orderby memberCard.StartedAt
            select new
            {
                memberCard.Id,
                memberCard.CardId,
                memberCard.CurrentStamps,
                card.Name,
                card.RequiredStamps,
                card.Level,
            })
            .FirstOrDefaultAsync(cancellationToken);

        object? activeCardPayload = null;
        if (activeCard is not null)
        {
            var milestones = await (
                from milestone in loyaltyDb.CardMilestones.AsNoTracking()
                join reward in loyaltyDb.Rewards.AsNoTracking()
                    on milestone.RewardId equals reward.Id into rewardJoin
                from reward in rewardJoin.DefaultIfEmpty()
                where milestone.BusinessId == session.BusinessId
                      && milestone.CardId == activeCard.CardId
                orderby milestone.StampCount
                select new
                {
                    stampCount = milestone.StampCount,
                    title = milestone.Title,
                    rewardId = milestone.RewardId,
                    rewardName = reward == null ? null : reward.Name,
                })
                .ToListAsync(cancellationToken);

            var rewardMilestone = milestones.LastOrDefault();
            activeCardPayload = new
            {
                cardId = activeCard.CardId,
                memberCardId = activeCard.Id,
                cardName = activeCard.Name,
                currentStamps = activeCard.CurrentStamps,
                requiredStamps = activeCard.RequiredStamps,
                level = new
                {
                    value = activeCard.Level,
                    label = activeCard.Level.HasValue ? $"Level {activeCard.Level.Value}" : null,
                },
                rewardStamp = rewardMilestone is null
                    ? null
                    : new { label = rewardMilestone.rewardName ?? rewardMilestone.title, position = rewardMilestone.stampCount },
                milestones,
            };
        }

        var stampTransactions = await loyaltyDb.Transactions.AsNoTracking()
            .Where(x => x.BusinessId == session.BusinessId
                        && x.MemberId == session.MemberId
                        && x.Type == "STAMP_ADDED")
            .Select(x => x.StampsAdded)
            .ToListAsync(cancellationToken);
        var totalStampsEarned = stampTransactions.Sum(value => Math.Max(value, 0));

        var totalRewardsRedeemed = await loyaltyDb.MemberRewards.AsNoTracking()
            .CountAsync(
                x => x.BusinessId == session.BusinessId
                     && x.MemberId == session.MemberId
                     && x.Status == "REDEEMED",
                cancellationToken);

        var totalVouchersAvailable = await loyaltyDb.MemberRewards.AsNoTracking()
            .CountAsync(
                x => x.BusinessId == session.BusinessId
                     && x.MemberId == session.MemberId
                     && x.Status == "AVAILABLE",
                cancellationToken);

        return Ok(ApiResponse<object>.Ok(new
        {
            member = new
            {
                memberId = member.Id,
                memberName = member.Name,
                memberEmail = member.Email,
                memberBarcode = member.MemberBarcode,
            },
            business = business is null
                ? null
                : new { id = business.Id, name = business.Name, slug = business.Slug },
            activeCard = activeCardPayload,
            stats = new
            {
                totalStampsEarned,
                totalRewardsRedeemed,
                totalVouchersAvailable,
            },
        }));
    }

    [HttpGet("transactions")]
    public async Task<IActionResult> Transactions(
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var session = await ResolveSessionAsync(cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        limit = Math.Clamp(limit, 1, 50);
        var rows = await loyaltyDb.Transactions.AsNoTracking()
            .Where(x => x.BusinessId == session.BusinessId
                        && x.MemberId == session.MemberId
                        && (x.Type == "STAMP_ADDED" || x.Type == "REWARD_REDEEMED"))
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var cardIds = rows.Select(x => x.CardId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var rewardIds = rows.Select(x => x.RewardId).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToArray();

        var cards = await loyaltyDb.Cards.AsNoTracking()
            .Where(x => cardIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var rewards = await loyaltyDb.Rewards.AsNoTracking()
            .Where(x => rewardIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var milestones = await loyaltyDb.CardMilestones.AsNoTracking()
            .Where(x => x.BusinessId == session.BusinessId
                        && x.RewardId != null
                        && cardIds.Contains(x.CardId)
                        && rewardIds.Contains(x.RewardId))
            .ToListAsync(cancellationToken);

        var transactions = rows.Select(row =>
        {
            cards.TryGetValue(row.CardId, out var card);
            var reward = row.RewardId is not null && rewards.TryGetValue(row.RewardId, out var foundReward)
                ? foundReward
                : null;
            var redeemed = row.Type == "REWARD_REDEEMED";
            var milestoneStampCount = redeemed && row.RewardId is not null
                ? milestones.FirstOrDefault(x => x.CardId == row.CardId && x.RewardId == row.RewardId)?.StampCount
                : null;
            var stampDelta = redeemed
                ? -Math.Max(milestoneStampCount ?? card?.RequiredStamps ?? 0, 0)
                : Math.Max(row.StampsAdded, 1);

            return new
            {
                transactionId = row.Id,
                type = redeemed ? "REDEEM_REWARD" : "EARN_STAMP",
                displayName = redeemed ? reward?.Name ?? "Reward" : "Stamp Earned",
                stampDelta,
                createdAt = row.CreatedAt,
            };
        });

        return Ok(ApiResponse<object>.Ok(new { transactions }));
    }

    [HttpGet("rewards")]
    public async Task<IActionResult> Rewards(
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var session = await ResolveSessionAsync(cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var query = loyaltyDb.MemberRewards.AsNoTracking()
            .Where(x => x.BusinessId == session.BusinessId && x.MemberId == session.MemberId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(x => x.Status == status.Trim().ToUpperInvariant());

        var memberRewards = await query
            .OrderByDescending(x => x.IssuedAt)
            .ToListAsync(cancellationToken);
        var rewardIds = memberRewards.Select(x => x.RewardId).Distinct().ToArray();
        var rewards = await loyaltyDb.Rewards.AsNoTracking()
            .Where(x => rewardIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var payload = memberRewards.Select(item =>
        {
            rewards.TryGetValue(item.RewardId, out var reward);
            return new
            {
                id = item.Id,
                memberId = item.MemberId,
                rewardId = item.RewardId,
                memberCardId = item.MemberCardId,
                businessId = item.BusinessId,
                status = item.Status,
                issuedAt = item.IssuedAt,
                expiresAt = item.ExpiresAt,
                redeemedAt = item.RedeemedAt,
                title = item.Title ?? reward?.Name,
                description = item.Description ?? reward?.Description,
                sourceType = item.SourceType,
                reward = reward is null
                    ? null
                    : new
                    {
                        id = reward.Id,
                        name = reward.Name,
                        description = reward.Description,
                        sourceType = reward.SourceType,
                        defaultExpiryDays = reward.DefaultExpiryDays,
                    },
            };
        });

        return Ok(ApiResponse<object>.Ok(new { rewards = payload }));
    }

    private Task<MemberSessionContext?> ResolveSessionAsync(CancellationToken cancellationToken) =>
        memberSessionResolver.ResolveAsync(Request.Cookies[MemberSessionCookie], cancellationToken);
}
