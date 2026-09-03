using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/member/auth")]
public sealed class MemberAuthController(IMemberAuthService memberAuth) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";

    [EnableRateLimiting("auth-login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] MemberLoginRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await memberAuth.LoginAsync(
                request.Email,
                request.Password,
                FirstNonBlank(
                    Request.Headers["x-tenant-business-id"].FirstOrDefault(),
                    Request.Headers["x-business-id"].FirstOrDefault(),
                    request.BusinessId),
                FirstNonBlank(
                    Request.Headers["x-tenant-slug"].FirstOrDefault(),
                    Request.Headers["x-business-slug"].FirstOrDefault(),
                    request.BusinessSlug),
                GetClientIp(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            Response.Cookies.Append(
                MemberSessionCookie,
                result.SessionToken,
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = Request.IsHttps,
                    Path = "/",
                    Expires = result.ExpiresAt,
                    IsEssential = true,
                });

            return Ok(ApiResponse<object>.Ok(new
            {
                memberId = result.MemberId,
                businessId = result.BusinessId,
                businessName = result.BusinessName,
                businessSlug = result.BusinessSlug,
                email = result.Email,
                memberName = result.MemberName,
                memberBarcode = result.MemberBarcode,
                message = "Login berhasil",
                requiresVerification = false,
            }));
        }
        catch (MemberAuthException exception)
        {
            return exception.Code switch
            {
                MemberAuthErrorCode.InvalidCredentials => Unauthorized(
                    ApiResponse<object>.Fail("UNAUTHORIZED", exception.Message)),
                MemberAuthErrorCode.BusinessContextRequired or MemberAuthErrorCode.BusinessNotFound => BadRequest(
                    ApiResponse<object>.Fail("VALIDATION_ERROR", exception.Message)),
                MemberAuthErrorCode.BusinessInactive or MemberAuthErrorCode.AccountLocked => StatusCode(
                    StatusCodes.Status403Forbidden,
                    ApiResponse<object>.Fail("FORBIDDEN", exception.Message)),
                _ => StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<object>.Fail("INTERNAL_ERROR", "Internal server error")),
            };
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var token = Request.Cookies[MemberSessionCookie];
        await memberAuth.LogoutAsync(token, cancellationToken);
        Response.Cookies.Delete(
            MemberSessionCookie,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                Path = "/",
            });

        return Ok(ApiResponse<object>.Ok(new { loggedOut = true }));
    }

    private string? GetClientIp()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].ToString();
        var firstForwarded = forwarded
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstForwarded))
            return firstForwarded;

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed class MemberLoginRequest
{
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string? BusinessId { get; init; }
    public string? BusinessSlug { get; init; }
}
