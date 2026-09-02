using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/admin/auth")]
public sealed class AdminAuthController(
    IStandaloneCredentialService credentialService,
    ILoyaltyJwtTokenIssuer tokenIssuer,
    IRefreshTokenSessionService refreshSessions) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await credentialService.AuthenticateAdminAsync(
                request.Email,
                request.Password,
                cancellationToken);

            var context = new LoyaltyTokenContext(
                profile.UserId,
                "admin",
                profile.Role,
                profile.BusinessId,
                profile.OutletId);
            var tokens = tokenIssuer.Issue(context);

            await refreshSessions.RegisterAsync(
                tokens.RefreshToken,
                tokens.RefreshExpiresAt,
                ToRefreshContext(context),
                cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                adminUserId = profile.AdminUserId,
                outletId = profile.OutletId,
                outletName = profile.OutletName,
                businessId = profile.BusinessId,
                businessName = profile.BusinessName,
                role = profile.Role.ToUpperInvariant(),
                accessToken = tokens.AccessToken,
                refreshToken = tokens.RefreshToken,
            }));
        }
        catch (CredentialException exception)
        {
            return MapCredentialFailure(exception);
        }
    }

    private static RefreshSessionContext ToRefreshContext(LoyaltyTokenContext context) =>
        new(context.UserId, context.AuthKind, context.Role, context.BusinessId, context.OutletId);

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
            CredentialFailureReason.InactiveBusiness or
            CredentialFailureReason.OutletRequired or
            CredentialFailureReason.InactiveOutlet => StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", exception.Message)),
            _ => StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<object>.Fail("INTERNAL_ERROR", "Internal server error")),
        };
    }
}
