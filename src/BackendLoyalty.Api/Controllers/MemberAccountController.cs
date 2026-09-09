using BackendLoyalty.Api.Contracts;
using System.Text.Json;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/member/account")]
public sealed class MemberAccountController(
    IMemberAuthService memberAuth,
    IMemberSessionResolver memberSessionResolver,
    LoyaltyDbContext loyaltyDb) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";


    [HttpPatch]
    public async Task<IActionResult> Update(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        var session = await memberSessionResolver.ResolveAsync(
            Request.Cookies[MemberSessionCookie],
            cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Not authenticated"));

        var member = await loyaltyDb.Members.SingleOrDefaultAsync(
            x => x.Id == session.MemberId
                 && x.BusinessId == session.BusinessId
                 && x.IsActive,
            cancellationToken);
        if (member is null)
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Member not found"));

        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid payload"));

        if (body.TryGetProperty("name", out var nameNode))
        {
            if (nameNode.ValueKind != JsonValueKind.String)
                return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid name"));

            var name = nameNode.GetString()?.Trim() ?? string.Empty;
            if (name.Length is < 1 or > 100)
                return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid name"));

            member.Name = name;
        }

        if (body.TryGetProperty("phone", out _))
        {
            return Conflict(ApiResponse<object>.Fail(
                "CONFLICT",
                "Phone changes require WhatsApp verification. Existing verified numbers can only be replaced by an admin."));
        }

        if (body.TryGetProperty("dateOfBirth", out var dobNode))
        {
            if (dobNode.ValueKind == JsonValueKind.Null)
            {
                member.DateOfBirth = null;
            }
            else if (dobNode.ValueKind == JsonValueKind.String)
            {
                var rawDob = dobNode.GetString();
                if (string.IsNullOrWhiteSpace(rawDob))
                {
                    member.DateOfBirth = null;
                }
                else if (DateOnly.TryParse(
                    rawDob,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsedDob))
                {
                    member.DateOfBirth = parsedDob.ToDateTime(TimeOnly.MinValue);
                }
                else
                {
                    return BadRequest(ApiResponse<object>.Fail(
                        "VALIDATION_ERROR",
                        "Invalid date of birth"));
                }
            }
            else
            {
                return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid date of birth"));
            }
        }

        member.UpdatedAt = DateTime.UtcNow;
        await loyaltyDb.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse<object>.Ok(new
        {
            memberId = member.Id,
            updated = true,
        }));
    }

    [HttpPost("complete-profile")]
    public async Task<IActionResult> CompleteProfile(CancellationToken cancellationToken)
    {
        var session = await memberSessionResolver.ResolveAsync(
            Request.Cookies[MemberSessionCookie],
            cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Not authenticated"));

        var member = await loyaltyDb.Members.SingleOrDefaultAsync(
            x => x.Id == session.MemberId
                 && x.BusinessId == session.BusinessId
                 && x.IsActive,
            cancellationToken);
        if (member is null)
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Member not found"));

        member.HasCompletedProfile = true;
        member.UpdatedAt = DateTime.UtcNow;
        await loyaltyDb.SaveChangesAsync(cancellationToken);

        return Ok(ApiResponse<object>.Ok(new
        {
            memberId = member.Id,
            profileCompleted = true,
        }));
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken)
    {
        try
        {
            await memberAuth.DeleteAccountAsync(
                Request.Cookies[MemberSessionCookie],
                cancellationToken);
            DeleteMemberSessionCookie();

            return Ok(ApiResponse<object>.Ok(new { deleted = true }));
        }
        catch (MemberAuthException exception)
        {
            return exception.Code == MemberAuthErrorCode.InvalidCredentials
                ? Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", exception.Message))
                : StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<object>.Fail("INTERNAL_ERROR", "Failed to delete account"));
        }
    }

    private static string? NormalizePhoneE164(string input)
    {
        var trimmed = string.Concat(input.Trim().Where(ch => !char.IsWhiteSpace(ch)));
        if (trimmed.Length == 0)
            return null;

        var digits = trimmed.StartsWith('+') ? trimmed[1..] : trimmed;
        if (digits.Length is < 8 or > 15 || digits.Any(ch => !char.IsDigit(ch)))
            return null;

        return trimmed.StartsWith('+') ? trimmed : $"+{trimmed}";
    }

    private void DeleteMemberSessionCookie()
    {
        Response.Cookies.Delete(
            MemberSessionCookie,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                Path = "/",
            });
    }
}
