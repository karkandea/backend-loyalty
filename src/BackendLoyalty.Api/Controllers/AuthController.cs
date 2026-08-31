using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IStandaloneCredentialService credentialService,
    ILoyaltyJwtTokenIssuer tokenIssuer) : ControllerBase
{
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var principal = tokenIssuer.ValidateRefreshToken(request.RefreshToken);
        if (principal is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Invalid refresh token"));

        var userId = LoyaltyClaims.UserId(principal);
        var authKind = LoyaltyClaims.AuthKind(principal);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(authKind))
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Invalid refresh token"));

        var isActive = await credentialService.IsUserActiveAsync(userId, authKind, cancellationToken);
        if (!isActive)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Account is inactive"));

        var tokens = tokenIssuer.Issue(new LoyaltyTokenContext(
            userId,
            authKind,
            LoyaltyClaims.Role(principal),
            LoyaltyClaims.BusinessId(principal),
            LoyaltyClaims.OutletId(principal)));

        return Ok(ApiResponse<object>.Ok(new
        {
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            expiresAt = new DateTimeOffset(tokens.AccessExpiresAt).ToUnixTimeSeconds(),
        }));
    }
}
