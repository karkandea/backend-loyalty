using System.Data;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/member/me")]
public sealed class MemberMeController(
    IMemberSessionResolver memberSessionResolver,
    LoyaltyDbContext loyaltyDb,
    StandaloneAuthDbContext authDb) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var session = await memberSessionResolver.ResolveAsync(
            Request.Cookies[MemberSessionCookie],
            cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var member = await loyaltyDb.Members.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == session.MemberId
                     && x.BusinessId == session.BusinessId
                     && x.IsActive,
                cancellationToken);
        if (member is null)
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Member profile not found"));

        var business = await authDb.Businesses.AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.Id == session.BusinessId && x.IsActive,
                cancellationToken);

        var verifiedAt = await ReadVerifiedAtAsync(
            session.BusinessId,
            session.MemberId,
            cancellationToken);
        var businessVisual = await ReadBusinessVisualAsync(
            session.BusinessId,
            cancellationToken);

        return Ok(ApiResponse<object>.Ok(new
        {
            member = new
            {
                id = member.Id,
                name = member.Name,
                email = member.Email,
                phone = member.Phone,
                emailVerifiedAt = verifiedAt,
                memberBarcode = member.MemberBarcode,
                businessId = member.BusinessId,
                avatarUrl = member.AvatarUrl,
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
                    tier = business.Tier,
                    brandPrimaryColor = businessVisual?.BrandPrimaryColor,
                    logoUrl = businessVisual?.LogoUrl,
                },
        }));
    }

    private async Task<DateTime?> ReadVerifiedAtAsync(
        string businessId,
        string memberId,
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
                SELECT "verifiedAt"
                FROM "MemberIdentity"
                WHERE "businessId" = @businessId
                  AND "memberId" = @memberId
                LIMIT 1
                """;
            AddParameter(command, "@businessId", businessId);
            AddParameter(command, "@memberId", memberId);

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToDateTime(value);
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private async Task<BusinessVisual?> ReadBusinessVisualAsync(
        string businessId,
        CancellationToken cancellationToken)
    {
        var connection = authDb.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "brandPrimaryColor", "logoUrl"
                FROM "Business"
                WHERE id = @businessId
                LIMIT 1
                """;
            AddParameter(command, "@businessId", businessId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return new BusinessVisual(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        finally
        {
            if (closeAfter)
                await connection.CloseAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record BusinessVisual(string? BrandPrimaryColor, string? LogoUrl);
}
