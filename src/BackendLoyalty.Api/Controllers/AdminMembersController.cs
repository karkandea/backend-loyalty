using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/admin/members")]
public sealed class AdminMembersController(IMemberScanService memberScanService) : ControllerBase
{
    [Authorize]
    [HttpPost("scan")]
    [ProducesResponseType(typeof(ApiResponse<MemberScanResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Scan(
        [FromBody] MemberScanRequest request,
        CancellationToken cancellationToken)
    {
        var barcode = request.MemberBarcode?.Trim();
        if (string.IsNullOrWhiteSpace(barcode) || barcode.Length > 64)
        {
            return BadRequest(ApiResponse<MemberScanResult>.Fail(
                "VALIDATION_ERROR",
                "Invalid payload",
                new { memberBarcode = "memberBarcode is required and must be at most 64 characters" }));
        }

        var role = SupabaseClaims.AppRole(User)?.ToLowerInvariant();
        if (role is not ("staff" or "manager"))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<MemberScanResult>.Fail("FORBIDDEN", "Role is not allowed for this operation"));
        }

        var businessId = SupabaseClaims.BusinessId(User);
        if (string.IsNullOrWhiteSpace(businessId))
        {
            return Unauthorized(ApiResponse<MemberScanResult>.Fail(
                "UNAUTHORIZED",
                "No business context (business_id missing)"));
        }

        var result = await memberScanService.ScanAsync(businessId, barcode, cancellationToken);
        if (result is null)
        {
            return NotFound(ApiResponse<MemberScanResult>.Fail("NOT_FOUND", "Member not found"));
        }

        return Ok(ApiResponse<MemberScanResult>.Ok(result));
    }
}
