using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/business/team/invitations")]
public sealed class BusinessInvitationsController(
    IBusinessInvitationService invitations,
    IBusinessInvitationManagementService management,
    IBusinessPasswordService passwords,
    IStandaloneInvitationIdentityService standaloneIdentities,
    ITransactionalEmailSender emailSender,
    IConfiguration configuration) : ControllerBase
{
    [Authorize]
    [EnableRateLimiting("team-mutation")]
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateTeamInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        var businessId = LoyaltyClaims.BusinessId(User);
        var role = LoyaltyClaims.Role(User)?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(businessId) ||
            authKind != "business" ||
            role is not ("owner" or "admin"))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "Unauthorized"));
        }

        if (!await management.CanManageTeamAsync(userId, businessId, role, cancellationToken))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "Insufficient permissions."));
        }

        var policy = await passwords.GetPasswordPolicyAsync(userId, cancellationToken);
        if (policy.IsExpired)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail(
                    "FORBIDDEN",
                    "For security, please set a password before inviting new team members."));
        }

        var issue = await invitations.CreateTeamInvitationAsync(
            new TeamInvitationRequest(
                businessId,
                userId,
                request.Email,
                request.Role,
                request.Permissions),
            cancellationToken);

        if (issue is not null)
        {
            // Legacy Supabase treated any existing auth identity as an existing user,
            // even when that identity had zero BusinessUser memberships.
            await standaloneIdentities.NormalizeInvitationForExistingIdentityAsync(
                issue.InvitationId,
                issue.Email,
                cancellationToken);

            await emailSender.SendBusinessInvitationAsync(
                issue.Email,
                issue.BusinessName,
                issue.Role,
                BuildInvitationUrl(issue.RawToken),
                cancellationToken);
        }

        // Match legacy anti-enumeration behavior for existing/self members.
        return Ok(ApiResponse<object>.Ok(new { message = "Invite sent." }));
    }

    [AllowAnonymous]
    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve(
        [FromQuery] string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 8)
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid token"));

        var details = await invitations.ResolveInvitationAsync(token, cancellationToken);
        if (details is null)
        {
            return StatusCode(
                StatusCodes.Status410Gone,
                ApiResponse<object>.Fail("INVALID_INVITE", "Invite expired or already used."));
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            id = details.InvitationId,
            businessId = details.BusinessId,
            businessName = details.BusinessName,
            email = details.Email,
            role = details.Role,
            permissions = details.Permissions,
            expiresAt = details.ExpiresAt,
            requiresPassword = details.RequiresPassword,
        }));
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromBody] RegisterBusinessInvitationRequest request,
        CancellationToken cancellationToken)
    {
        if (await standaloneIdentities.InvitationTargetsExistingIdentityAsync(
                request.Token,
                cancellationToken))
        {
            return Conflict(
                ApiResponse<object>.Fail("USER_EXISTS", "Account already exists. Please login."));
        }

        var outcome = await invitations.RegisterAsync(
            request.Token,
            request.FullName,
            request.Password,
            cancellationToken);

        return outcome.Status switch
        {
            InvitationRegistrationStatus.Success => Ok(ApiResponse<object>.Ok(new
            {
                userId = outcome.UserId,
                businessId = outcome.BusinessId,
                email = outcome.Email,
            })),
            InvitationRegistrationStatus.UserExists => Conflict(
                ApiResponse<object>.Fail("USER_EXISTS", "Account already exists. Please login.")),
            InvitationRegistrationStatus.InvalidOrExpired => StatusCode(
                StatusCodes.Status410Gone,
                ApiResponse<object>.Fail("INVALID_INVITE", "Invite expired or already used.")),
            _ => Conflict(ApiResponse<object>.Fail("CONFLICT", "Unable to accept invitation.")),
        };
    }

    [Authorize]
    [EnableRateLimiting("team-mutation")]
    [HttpPost("accept")]
    public async Task<IActionResult> Accept(
        [FromBody] AcceptBusinessInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        if (string.IsNullOrWhiteSpace(userId) || authKind != "business")
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var outcome = await invitations.AcceptExistingAsync(
            userId,
            request.Token,
            cancellationToken);

        if (outcome.Status == InvitationAcceptanceStatus.IdentityNotFound)
        {
            var zeroMembershipOutcome = await standaloneIdentities.AcceptZeroMembershipAsync(
                userId,
                request.Token,
                cancellationToken);
            if (zeroMembershipOutcome is not null)
                outcome = zeroMembershipOutcome;
        }

        return outcome.Status switch
        {
            InvitationAcceptanceStatus.Success => Ok(ApiResponse<object>.Ok(new
            {
                businessId = outcome.BusinessId,
                membershipCount = outcome.MembershipCount,
                requiresRelogin = outcome.RequiresRelogin,
            })),
            InvitationAcceptanceStatus.EmailMismatch => StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "Invite does not match this account.")),
            InvitationAcceptanceStatus.IdentityNotFound => Unauthorized(
                ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized")),
            InvitationAcceptanceStatus.InvalidOrExpired => StatusCode(
                StatusCodes.Status410Gone,
                ApiResponse<object>.Fail("INVALID_INVITE", "Invite expired or already used.")),
            _ => Conflict(ApiResponse<object>.Fail("CONFLICT", "Unable to accept invitation.")),
        };
    }

    private string BuildInvitationUrl(string rawToken)
    {
        var configured = configuration["EMAIL_CONFIRM_BASE_DOMAIN"]?.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(configured)
            ? "https://dualangka.com"
            : configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? configured
                : $"https://{configured}";

        return $"{baseUrl.TrimEnd('/')}/cms/invite/accept?token={Uri.EscapeDataString(rawToken)}";
    }
}
