using System.Text.Json;
using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auditing;
using BackendLoyalty.Application.Loyalty;
using BackendLoyalty.Application.Members;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/admin/members")]
public sealed class AdminMembersController(
    IMemberScanService memberScanService,
    ILoyaltyStampService loyaltyStampService,
    IAuditLogWriter auditLogWriter) : ControllerBase
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

    [Authorize]
    [HttpPost("{memberId}/cards/{memberCardId}/add-stamp")]
    [ProducesResponseType(typeof(ApiResponse<AddStampResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> AddStamp(
        string memberId,
        string memberCardId,
        [FromBody] AddStampRequest request,
        CancellationToken cancellationToken)
    {
        if (request.StampCount <= 0 || (request.TransactionNotes?.Trim().Length ?? 0) > 500)
        {
            return BadRequest(ApiResponse<AddStampResult>.Fail(
                "VALIDATION_ERROR",
                "Invalid payload",
                new
                {
                    stampCount = request.StampCount <= 0 ? "stampCount must be a positive integer" : null,
                    transactionNotes = (request.TransactionNotes?.Trim().Length ?? 0) > 500
                        ? "transactionNotes must be at most 500 characters"
                        : null,
                }));
        }

        var role = SupabaseClaims.AppRole(User)?.ToLowerInvariant();
        if (role is not ("staff" or "manager"))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<AddStampResult>.Fail("FORBIDDEN", "Role is not allowed for this operation"));
        }

        var userId = SupabaseClaims.UserId(User);
        var businessId = SupabaseClaims.BusinessId(User);
        var outletId = SupabaseClaims.OutletId(User);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(ApiResponse<AddStampResult>.Fail("UNAUTHORIZED", "Unauthorized"));
        }

        if (string.IsNullOrWhiteSpace(businessId))
        {
            return Unauthorized(ApiResponse<AddStampResult>.Fail(
                "UNAUTHORIZED",
                "No business context (business_id missing)"));
        }

        if (string.IsNullOrWhiteSpace(outletId))
        {
            return Unauthorized(ApiResponse<AddStampResult>.Fail(
                "UNAUTHORIZED",
                "No outlet context (outlet_id missing)"));
        }

        try
        {
            var result = await loyaltyStampService.AddStampsAsync(
                new AddStampCommand(
                    businessId,
                    userId,
                    outletId,
                    memberId,
                    memberCardId,
                    request.StampCount,
                    request.TransactionNotes?.Trim()),
                cancellationToken);

            await auditLogWriter.WriteAsync(
                new AuditEvent(
                    businessId,
                    userId,
                    role,
                    outletId,
                    "pos_add_stamp",
                    "member_card",
                    memberCardId,
                    "Tambah stamp",
                    JsonSerializer.Serialize(new
                    {
                        memberId,
                        memberCardId,
                        stampCount = request.StampCount,
                        outletId,
                        transactionIds = result.Transactions,
                        didAdvanceCard = result.DidAdvanceCard,
                    }),
                    GetClientIp(),
                    Request.Headers["User-Agent"].ToString()),
                cancellationToken);

            return Ok(ApiResponse<AddStampResult>.Ok(result));
        }
        catch (LoyaltyOperationException exception) when (exception.Code == LoyaltyOperationErrorCode.NotFound)
        {
            return NotFound(ApiResponse<AddStampResult>.Fail("NOT_FOUND", exception.Message));
        }
        catch (LoyaltyOperationException exception) when (exception.Code == LoyaltyOperationErrorCode.Validation)
        {
            return BadRequest(ApiResponse<AddStampResult>.Fail("VALIDATION_ERROR", exception.Message));
        }
        catch (LoyaltyOperationException)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResponse<AddStampResult>.Fail("INTERNAL_ERROR", "Failed to update stamp count"));
        }
    }

    private string? GetClientIp()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].ToString();
        var firstForwarded = forwarded
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(firstForwarded))
        {
            return firstForwarded;
        }

        var realIp = Request.Headers["X-Real-IP"].ToString();
        return string.IsNullOrWhiteSpace(realIp) ? null : realIp;
    }
}
