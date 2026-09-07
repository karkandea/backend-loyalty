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
[Route("api/member/cards")]
public sealed class MemberCardsController(
    IMemberSessionResolver memberSessionResolver,
    LoyaltyDbContext loyaltyDb,
    StandaloneAuthDbContext authDb) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var session = await ResolveSessionAsync(cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var rows = await (
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
                    MemberCard = memberCard,
                    Card = card,
                })
            .ToListAsync(cancellationToken);

        var business = await authDb.Businesses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == session.BusinessId, cancellationToken);
        var businessVisual = await ReadBusinessVisualAsync(session.BusinessId, cancellationToken);

        var cards = new List<object>(rows.Count);
        foreach (var row in rows)
        {
            var visual = await ReadCardVisualAsync(
                session.BusinessId,
                row.Card.Id,
                cancellationToken);

            cards.Add(new
            {
                memberCardId = row.MemberCard.Id,
                cardId = row.Card.Id,
                name = row.Card.Name,
                currentStamps = Math.Max(row.MemberCard.CurrentStamps, 0),
                requiredStamps = Math.Max(row.Card.RequiredStamps, 1),
                backgroundColor = visual?.BackgroundColorHex ?? businessVisual?.BrandPrimaryColor ?? "#111827",
                backgroundImageUrl = visual?.BackgroundImageUrl,
                stampFillColor = visual?.StampFillColorHex ?? "#f97316",
                stampIconUrl = visual?.StampIconUrl,
                logoUrl = visual?.LogoUrl ?? businessVisual?.LogoUrl,
                titleColorHex = visual?.TitleColorHex,
                level = row.Card.Level,
                cardStatus = row.Card.Status,
                businessTier = business?.Tier,
                updatedAt = row.MemberCard.UpdatedAt,
            });
        }

        return Ok(ApiResponse<object>.Ok(new { cards }));
    }

    [HttpGet("{memberCardId}")]
    public async Task<IActionResult> Detail(
        string memberCardId,
        CancellationToken cancellationToken)
    {
        var session = await ResolveSessionAsync(cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        if (string.IsNullOrWhiteSpace(memberCardId))
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Card not found"));

        var row = await (
                from memberCard in loyaltyDb.MemberCards.AsNoTracking()
                join card in loyaltyDb.Cards.AsNoTracking() on memberCard.CardId equals card.Id
                where memberCard.Id == memberCardId
                      && memberCard.BusinessId == session.BusinessId
                      && memberCard.MemberId == session.MemberId
                      && card.BusinessId == session.BusinessId
                select new
                {
                    MemberCard = memberCard,
                    Card = card,
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Card not found"));

        var business = await authDb.Businesses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == session.BusinessId, cancellationToken);
        var businessVisual = await ReadBusinessVisualAsync(session.BusinessId, cancellationToken);
        var visual = await ReadCardVisualAsync(session.BusinessId, row.Card.Id, cancellationToken);

        var milestones = await loyaltyDb.CardMilestones.AsNoTracking()
            .Where(x => x.BusinessId == session.BusinessId && x.CardId == row.Card.Id)
            .OrderBy(x => x.StampCount)
            .ThenBy(x => x.SortOrder)
            .Select(x => new
            {
                id = x.Id,
                cardId = x.CardId,
                stampCount = x.StampCount,
                sortOrder = x.SortOrder,
                title = x.Title,
                description = x.Description,
                rewardId = x.RewardId,
                rewardType = x.RewardType,
                rewardValue = x.RewardValue,
            })
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse<object>.Ok(new
        {
            memberCard = new
            {
                id = row.MemberCard.Id,
                cardId = row.MemberCard.CardId,
                memberId = row.MemberCard.MemberId,
                businessId = row.MemberCard.BusinessId,
                currentStamps = Math.Max(row.MemberCard.CurrentStamps, 0),
                isActive = row.MemberCard.IsActive,
                startedAt = row.MemberCard.StartedAt,
                completedAt = row.MemberCard.CompletedAt,
                updatedAt = row.MemberCard.UpdatedAt,
                card = new
                {
                    id = row.Card.Id,
                    name = row.Card.Name,
                    requiredStamps = Math.Max(row.Card.RequiredStamps, 1),
                    level = row.Card.Level,
                    status = row.Card.Status,
                    backgroundColorHex = visual?.BackgroundColorHex,
                    backgroundImageUrl = visual?.BackgroundImageUrl,
                    backgroundCss = visual?.BackgroundCss,
                    backgroundCssSize = visual?.BackgroundCssSize,
                    overlayEnabled = visual?.OverlayEnabled ?? false,
                    overlayOpacity = visual?.OverlayOpacity,
                    overlayColor = visual?.OverlayColor,
                    cornerRadius = visual?.CornerRadius,
                    stampFillColorHex = visual?.StampFillColorHex,
                    inactiveStampColorHex = visual?.InactiveStampColorHex,
                    stampIconUrl = visual?.StampIconUrl,
                    logoUrl = visual?.LogoUrl,
                    titleColorHex = visual?.TitleColorHex,
                    termsAndConditions = visual?.TermsAndConditions,
                    howItWorks = (visual?.HowItWorksSteps ?? Array.Empty<string>())
                        .Select((text, index) => new { number = index + 1, text }),
                },
                business = business is null
                    ? null
                    : new
                    {
                        id = business.Id,
                        name = business.Name,
                        tier = business.Tier,
                        logoUrl = businessVisual?.LogoUrl,
                        brandPrimaryColor = businessVisual?.BrandPrimaryColor,
                    },
            },
            milestones,
        }));
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

    private Task<MemberSessionContext?> ResolveSessionAsync(CancellationToken cancellationToken) =>
        memberSessionResolver.ResolveAsync(Request.Cookies[MemberSessionCookie], cancellationToken);

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
}
