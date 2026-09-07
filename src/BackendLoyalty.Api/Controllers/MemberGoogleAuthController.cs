using System.ComponentModel.DataAnnotations;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/member/auth/google")]
public sealed class MemberGoogleAuthController(
    IMemberGoogleAuthService googleAuth,
    IConfiguration configuration,
    ILogger<MemberGoogleAuthController> logger) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";

    [EnableRateLimiting("auth-login")]
    [HttpPost]
    public async Task<IActionResult> SignIn(
        [FromBody] MemberGoogleSignInRequest request,
        CancellationToken cancellationToken)
    {
        var credential = request.Credential?.Trim() ?? string.Empty;
        var businessSlug = request.BusinessSlug?.Trim().ToLowerInvariant() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(credential)
            || string.IsNullOrWhiteSpace(businessSlug)
            || businessSlug.Length > 64
            || businessSlug.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '-')))
        {
            return BadRequest(ApiResponse<object>.Fail(
                "VALIDATION_ERROR",
                "Google credential and valid business slug are required."));
        }

        var clientId = configuration["GoogleOAuth:ClientId"]?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                ApiResponse<object>.Fail(
                    "OAUTH_NOT_CONFIGURED",
                    "Google sign-in is not configured."));
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(
                credential,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { clientId },
                });
        }
        catch (InvalidJwtException exception)
        {
            logger.LogWarning(exception, "Rejected invalid Google ID token.");
            return Unauthorized(ApiResponse<object>.Fail(
                "INVALID_GOOGLE_CREDENTIAL",
                "Google credential is invalid or expired."));
        }

        if (!payload.EmailVerified
            || string.IsNullOrWhiteSpace(payload.Email)
            || !new EmailAddressAttribute().IsValid(payload.Email))
        {
            return Unauthorized(ApiResponse<object>.Fail(
                "GOOGLE_EMAIL_NOT_VERIFIED",
                "Google email is not verified."));
        }

        var name = string.IsNullOrWhiteSpace(payload.Name)
            ? payload.Email.Split('@')[0]
            : payload.Name.Trim();

        try
        {
            var result = await googleAuth.SignInAsync(
                new MemberGoogleProfile(
                    payload.Email,
                    name,
                    string.IsNullOrWhiteSpace(payload.Picture) ? null : payload.Picture,
                    payload.Subject ?? string.Empty),
                businessSlug,
                request.Next,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            SetMemberSessionCookie(result.SessionToken, result.ExpiresAt);

            return Ok(ApiResponse<object>.Ok(new
            {
                memberId = result.MemberId,
                businessId = result.BusinessId,
                businessSlug = result.BusinessSlug,
                isNewMember = result.IsNewMember,
                redirectTo = result.RedirectTo,
            }));
        }
        catch (MemberGoogleAuthException exception)
        {
            return exception.Code switch
            {
                MemberGoogleAuthErrorCode.BusinessNotFound =>
                    NotFound(ApiResponse<object>.Fail("NOT_FOUND", exception.Message)),
                MemberGoogleAuthErrorCode.BusinessInactive or
                MemberGoogleAuthErrorCode.MemberInactive =>
                    StatusCode(
                        StatusCodes.Status403Forbidden,
                        ApiResponse<object>.Fail("FORBIDDEN", exception.Message)),
                MemberGoogleAuthErrorCode.InvalidRedirect =>
                    BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", exception.Message)),
                _ => StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<object>.Fail("INTERNAL_ERROR", "Google sign-in failed")),
            };
        }
    }

    private void SetMemberSessionCookie(string token, DateTime expiresAt)
    {
        Response.Cookies.Append(
            MemberSessionCookie,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)),
                MaxAge = TimeSpan.FromDays(7),
            });
    }
}

public sealed class MemberGoogleSignInRequest
{
    public string? Credential { get; init; }
    public string? BusinessSlug { get; init; }
    public string? Next { get; init; }
}
