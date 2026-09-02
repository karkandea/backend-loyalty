using System.ComponentModel.DataAnnotations;
using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BackendLoyalty.Api.Controllers;

public sealed record RevokeBusinessInvitationRequest(
    [Required] string InviteId);

[ApiController]
[Route("api/business/team/invitations")]
public sealed class BusinessInvitationManagementController(
    IBusinessInvitationManagementService management) : ControllerBase
{
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        var businessId = LoyaltyClaims.BusinessId(User);
        var role = LoyaltyClaims.Role(User);

        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(businessId) ||
            string.IsNullOrWhiteSpace(role) ||
            authKind != "business")
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "Unauthorized"));
        }

        var outcome = await management.ListActiveAsync(
            userId,
            businessId,
            role,
            cancellationToken);

        if (outcome.Result == InvitationManagementResult.Forbidden)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "Insufficient permissions."));
        }

        return Ok(ApiResponse<object>.Ok(outcome.Items));
    }

    [Authorize]
    [EnableRateLimiting("team-mutation")]
    [HttpPost("revoke")]
    public async Task<IActionResult> Revoke(
        [FromBody] RevokeBusinessInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        var businessId = LoyaltyClaims.BusinessId(User);
        var role = LoyaltyClaims.Role(User);

        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(businessId) ||
            string.IsNullOrWhiteSpace(role) ||
            authKind != "business")
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "Unauthorized"));
        }

        var result = await management.RevokeAsync(
            userId,
            businessId,
            role,
            request.InviteId,
            cancellationToken);

        return result switch
        {
            InvitationManagementResult.Success => Ok(ApiResponse<object>.Ok(new { revoked = true })),
            InvitationManagementResult.Forbidden => StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "Insufficient permissions.")),
            _ => NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Invitation not found")),
        };
    }
}
