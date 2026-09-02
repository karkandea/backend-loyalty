using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IStandaloneCredentialService credentialService,
    ILoyaltyJwtTokenIssuer tokenIssuer,
    IRefreshTokenSessionService refreshSessions) : ControllerBase
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

        var context = new LoyaltyTokenContext(
            userId,
            authKind,
            LoyaltyClaims.Role(principal),
            LoyaltyClaims.BusinessId(principal),
            LoyaltyClaims.OutletId(principal));
        var tokens = tokenIssuer.Issue(context);

        var rotated = await refreshSessions.RotateAsync(
            request.RefreshToken,
            tokens.RefreshToken,
            tokens.RefreshExpiresAt,
            ToRefreshContext(context),
            cancellationToken);

        if (!rotated)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Invalid or already used refresh token"));

        return Ok(ApiResponse<object>.Ok(new
        {
            accessToken = tokens.AccessToken,
            refreshToken = tokens.RefreshToken,
            expiresAt = new DateTimeOffset(tokens.AccessExpiresAt).ToUnixTimeSeconds(),
        }));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var principal = tokenIssuer.ValidateRefreshToken(request.RefreshToken);
        if (principal is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Invalid refresh token"));

        var revoked = await refreshSessions.RevokeAsync(
            request.RefreshToken,
            "logout",
            cancellationToken);

        if (!revoked)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Invalid or already revoked refresh token"));

        return Ok(ApiResponse<object>.Ok(new { loggedOut = true }));
    }

    private static RefreshSessionContext ToRefreshContext(LoyaltyTokenContext context) =>
        new(context.UserId, context.AuthKind, context.Role, context.BusinessId, context.OutletId);
}
