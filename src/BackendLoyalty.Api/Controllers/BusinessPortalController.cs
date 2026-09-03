using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/business/portal")]
public sealed class BusinessPortalController(IBusinessPortalService portal) : ControllerBase
{
    [HttpGet("memberships")]
    public async Task<IActionResult> Memberships(CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        if (string.IsNullOrWhiteSpace(userId) || authKind != "business")
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var memberships = await portal.ListMembershipsAsync(userId, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { memberships }));
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        var businessId = LoyaltyClaims.BusinessId(User);
        if (string.IsNullOrWhiteSpace(userId) || authKind != "business")
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));
        if (string.IsNullOrWhiteSpace(businessId))
            return BadRequest(ApiResponse<object>.Fail("BUSINESS_SELECTION_REQUIRED", "Select a business first."));

        var summary = await portal.GetSummaryAsync(userId, businessId, cancellationToken);
        if (summary is null)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "No access to this business."));
        }

        return Ok(ApiResponse<BusinessPortalSummary>.Ok(summary));
    }

    [EnableRateLimiting("team-mutation")]
    [HttpPost("onboarding/complete")]
    public async Task<IActionResult> CompleteOnboarding(CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        var businessId = LoyaltyClaims.BusinessId(User);
        if (string.IsNullOrWhiteSpace(userId) || authKind != "business")
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));
        if (string.IsNullOrWhiteSpace(businessId))
            return BadRequest(ApiResponse<object>.Fail("BUSINESS_SELECTION_REQUIRED", "Select a business first."));

        var completed = await portal.CompleteOnboardingAsync(userId, businessId, cancellationToken);
        if (!completed)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "Only owners or admins can complete onboarding."));
        }

        return Ok(ApiResponse<object>.Ok(new { hasCompletedOnboarding = true }));
    }
}
