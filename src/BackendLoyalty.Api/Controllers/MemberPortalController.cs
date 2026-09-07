using System.Data;
using System.Data.Common;
using System.Text.Json;
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
        var memberVisual = await ReadMemberVisualAsync(session.BusinessId, session.MemberId, cancellationToken);
        var businessVisual = await ReadBusinessVisualAsync(session.BusinessId, cancellationToken);

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
            var cardVisual = await ReadCardVisualAsync(session.BusinessId, activeCard.CardId, cancellationToken);
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
                backgroundColor = cardVisual?.BackgroundColorHex,
                backgroundImageUrl = cardVisual?.BackgroundImageUrl,
                backgroundCss = cardVisual?.BackgroundCss,
                backgroundCssSize = cardVisual?.BackgroundCssSize,
                overlayEnabled = cardVisual?.OverlayEnabled ?? false,
                overlayOpacity = cardVisual?.OverlayOpacity,
                overlayColor = cardVisual?.OverlayColor,
                cornerRadius = cardVisual?.CornerRadius,
                stampColor = cardVisual?.StampFillColorHex,
                inactiveStampColor = cardVisual?.InactiveStampColorHex,
                iconUrl = cardVisual?.StampIconUrl,
                logoUrl = cardVisual?.LogoUrl,
                titleColorHex = cardVisual?.TitleColorHex,
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
                phone = memberVisual?.Phone ?? member.Phone,
                avatarUrl = memberVisual?.AvatarUrl ?? member.AvatarUrl,
                emailVerifiedAt = memberVisual?.EmailVerifiedAt,
                dateOfBirth = member.DateOfBirth,
                hasCompletedProfile = member.HasCompletedProfile,
                isActive = member.IsActive,
            },
            business = business is null
                ? null
                : new
                {
                    id = business.Id,
                    name = business.Name,
                    slug = business.Slug,
                    logoUrl = businessVisual?.LogoUrl,
                    brandPrimaryColor = businessVisual?.BrandPrimaryColor,
                },
            activeCard = activeCardPayload,
            program = activeCard is null
                ? new { howItWorks = Array.Empty<object>(), termsAndConditions = (string?)null }
                : await BuildProgramAsync(session.BusinessId, activeCard.CardId, cancellationToken),
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

    private async Task<object> BuildProgramAsync(
        string businessId,
        string cardId,
        CancellationToken cancellationToken)
    {
        var visual = await ReadCardVisualAsync(businessId, cardId, cancellationToken);
        var steps = (visual?.HowItWorksSteps ?? Array.Empty<string>())
            .Select((text, index) => new { number = index + 1, text })
            .ToArray();
        return new
        {
            howItWorks = steps,
            termsAndConditions = visual?.TermsAndConditions,
        };
    }

    private async Task<MemberVisual?> ReadMemberVisualAsync(
        string businessId,
        string memberId,
        CancellationToken cancellationToken)
    {
        var connection = loyaltyDb.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.phone,
                       m."AvatarUrl",
                       mi."verifiedAt"
                FROM "Member" m
                LEFT JOIN "MemberIdentity" mi
                  ON mi."businessId" = m."businessId"
                 AND mi."memberId" = m.id
                WHERE m.id = @memberId
                  AND m."businessId" = @businessId
                LIMIT 1
                """;
            AddParameter(command, "memberId", memberId);
            AddParameter(command, "businessId", businessId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new MemberVisual(
                NullableString(reader, 0),
                NullableString(reader, 1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2));
        }
        catch (DbException)
        {
            return null;
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<BusinessVisual?> ReadBusinessVisualAsync(
        string businessId,
        CancellationToken cancellationToken)
    {
        var connection = authDb.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "logoUrl", "brandPrimaryColor"
                FROM "Business"
                WHERE id = @businessId
                LIMIT 1
                """;
            AddParameter(command, "businessId", businessId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new BusinessVisual(
                NullableString(reader, 0),
                NullableString(reader, 1));
        }
        catch (DbException)
        {
            return null;
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<CardVisual?> ReadCardVisualAsync(
        string businessId,
        string cardId,
        CancellationToken cancellationToken)
    {
        var connection = loyaltyDb.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "termsAndConditions",
                       "backgroundColorHex",
                       "backgroundImageUrl",
                       "backgroundCss",
                       "backgroundCssSize",
                       "overlayEnabled",
                       "overlayOpacity",
                       "overlayColor",
                       "cornerRadius",
                       "stampFillColorHex",
                       "inactiveStampColorHex",
                       "stampIconUrl",
                       "logoUrl",
                       "titleColorHex",
                       "howItWorksSteps"
                FROM "Card"
                WHERE id = @cardId
                  AND "businessId" = @businessId
                LIMIT 1
                """;
            AddParameter(command, "cardId", cardId);
            AddParameter(command, "businessId", businessId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new CardVisual(
                NullableString(reader, 0),
                NullableString(reader, 1),
                NullableString(reader, 2),
                NullableString(reader, 3),
                NullableString(reader, 4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<bool>(5),
                reader.IsDBNull(6) ? null : Convert.ToDecimal(reader.GetValue(6)),
                NullableString(reader, 7),
                reader.IsDBNull(8) ? null : Convert.ToInt32(reader.GetValue(8)),
                NullableString(reader, 9),
                NullableString(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12),
                NullableString(reader, 13),
                ReadHowItWorksSteps(reader, 14));
        }
        catch (DbException)
        {
            return null;
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }


    private static string[] ReadHowItWorksSteps(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return Array.Empty<string>();

        var value = reader.GetValue(ordinal);
        if (value is string[] array)
            return array;

        if (value is string json)
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                return string.IsNullOrWhiteSpace(json) ? Array.Empty<string>() : new[] { json };
            }
        }

        if (value is Array values)
        {
            return values
                .Cast<object?>()
                .Select(item => Convert.ToString(item))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static string? NullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record MemberVisual(string? Phone, string? AvatarUrl, DateTime? EmailVerifiedAt);
    private sealed record BusinessVisual(string? LogoUrl, string? BrandPrimaryColor);
    private sealed record CardVisual(
        string? TermsAndConditions,
        string? BackgroundColorHex,
        string? BackgroundImageUrl,
        string? BackgroundCss,
        string? BackgroundCssSize,
        bool? OverlayEnabled,
        decimal? OverlayOpacity,
        string? OverlayColor,
        int? CornerRadius,
        string? StampFillColorHex,
        string? InactiveStampColorHex,
        string? StampIconUrl,
        string? LogoUrl,
        string? TitleColorHex,
        string[] HowItWorksSteps);

    private Task<MemberSessionContext?> ResolveSessionAsync(CancellationToken cancellationToken) =>
        memberSessionResolver.ResolveAsync(Request.Cookies[MemberSessionCookie], cancellationToken);
}
