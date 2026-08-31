using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/business/auth")]
public sealed class BusinessAuthController(
    IStandaloneCredentialService credentialService,
    ILoyaltyJwtTokenIssuer tokenIssuer) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await credentialService.AuthenticateBusinessAsync(
                request.Email,
                request.Password,
                cancellationToken);

            var membershipCount = profile.Memberships.Count;
            var selected = membershipCount == 1 ? profile.Memberships[0] : null;
            var role = selected?.Role ?? profile.Memberships[0].Role;

            var tokens = tokenIssuer.Issue(new LoyaltyTokenContext(
                profile.UserId,
                "business",
                role,
                selected?.BusinessId,
                null));

            SetAuthCookies(tokens, membershipCount, selected?.BusinessId);

            return Ok(ApiResponse<object>.Ok(new
            {
                businessUserId = selected?.BusinessUserId,
                businessId = selected?.BusinessId,
                businessName = selected?.BusinessName,
                role = role.ToUpperInvariant(),
                membershipCount,
                accessToken = tokens.AccessToken,
                refreshToken = tokens.RefreshToken,
            }));
        }
        catch (CredentialException exception)
        {
            return MapCredentialFailure(exception);
        }
    }

    [Authorize]
    [HttpPost("active-business")]
    public async Task<IActionResult> SetActiveBusiness(
        [FromBody] ActiveBusinessRequest request,
        CancellationToken cancellationToken)
    {
        var accessUserId = LoyaltyClaims.UserId(User);
        var accessAuthKind = LoyaltyClaims.AuthKind(User);
        if (string.IsNullOrWhiteSpace(accessUserId) || accessAuthKind != "business")
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var refreshPrincipal = tokenIssuer.ValidateRefreshToken(request.RefreshToken);
        if (refreshPrincipal is null ||
            LoyaltyClaims.UserId(refreshPrincipal) != accessUserId ||
            LoyaltyClaims.AuthKind(refreshPrincipal) != "business")
        {
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Invalid refresh token"));
        }

        var membership = await credentialService.ResolveBusinessMembershipAsync(
            accessUserId,
            request.BusinessId,
            cancellationToken);

        if (membership is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "No access to this business"));

        var tokens = tokenIssuer.Issue(new LoyaltyTokenContext(
            accessUserId,
            "business",
            membership.Role,
            membership.BusinessId,
            null));

        SetAuthCookies(tokens, null, membership.BusinessId);

        return Ok(ApiResponse<object>.Ok(new
        {
            businessId = membership.BusinessId,
            businessName = membership.BusinessName,
            slug = membership.BusinessSlug,
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            expiresAt = new DateTimeOffset(tokens.AccessExpiresAt).ToUnixTimeSeconds(),
        }));
    }

    private void SetAuthCookies(
        LoyaltyTokenPair tokens,
        int? membershipCount,
        string? activeBusinessId)
    {
        var secure = !HttpContext.RequestServices
            .GetRequiredService<IWebHostEnvironment>()
            .IsDevelopment();

        var common = new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        };

        Response.Cookies.Append("loyalty-access-token", tokens.AccessToken, new CookieOptions
        {
            HttpOnly = common.HttpOnly,
            Secure = common.Secure,
            SameSite = common.SameSite,
            Path = common.Path,
            Expires = tokens.AccessExpiresAt,
        });

        Response.Cookies.Append("loyalty-refresh-token", tokens.RefreshToken, new CookieOptions
        {
            HttpOnly = common.HttpOnly,
            Secure = common.Secure,
            SameSite = common.SameSite,
            Path = common.Path,
            Expires = tokens.RefreshExpiresAt,
        });

        if (membershipCount.HasValue)
        {
            Response.Cookies.Append("cms-membership-count", membershipCount.Value.ToString(), new CookieOptions
            {
                HttpOnly = common.HttpOnly,
                Secure = common.Secure,
                SameSite = common.SameSite,
                Path = common.Path,
                Expires = tokens.RefreshExpiresAt,
            });
        }

        if (!string.IsNullOrWhiteSpace(activeBusinessId))
        {
            Response.Cookies.Append("cms-active-business-id", activeBusinessId, new CookieOptions
            {
                HttpOnly = common.HttpOnly,
                Secure = common.Secure,
                SameSite = common.SameSite,
                Path = common.Path,
                Expires = tokens.RefreshExpiresAt,
            });
        }
        else if (membershipCount.HasValue)
        {
            Response.Cookies.Delete("cms-active-business-id", common);
        }
    }

    private IActionResult MapCredentialFailure(CredentialException exception)
    {
        return exception.Reason switch
        {
            CredentialFailureReason.InvalidCredentials => Unauthorized(
                ApiResponse<object>.Fail("UNAUTHORIZED", "Invalid email or password")),
            CredentialFailureReason.PasswordMigrationRequired => StatusCode(
                StatusCodes.Status409Conflict,
                ApiResponse<object>.Fail(
                    "PASSWORD_MIGRATION_REQUIRED",
                    "Password migration is required for this account")),
            CredentialFailureReason.InactiveAccount or
            CredentialFailureReason.InactiveBusiness => StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", exception.Message)),
            _ => StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<object>.Fail("INTERNAL_ERROR", "Internal server error")),
        };
    }
}
