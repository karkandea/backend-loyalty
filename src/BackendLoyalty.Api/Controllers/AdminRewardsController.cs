using System.Text.Json;
using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auditing;
using BackendLoyalty.Application.Rewards;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/admin/rewards")]
public sealed class AdminRewardsController(
    IRewardRedemptionService rewardRedemptionService,
    IAuditLogWriter auditLogWriter) : ControllerBase
{
    [Authorize]
    [HttpPost("scan")]
    [ProducesResponseType(typeof(ApiResponse<RewardRedemptionResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Scan(
        [FromBody] RewardScanRequest request,
        CancellationToken cancellationToken)
    {
        var tokenValue = request.RewardToken?.Trim();
        if (string.IsNullOrWhiteSpace(tokenValue) || tokenValue.Length > 255)
        {
            return BadRequest(ApiResponse<RewardRedemptionResult>.Fail(
                "VALIDATION_ERROR",
                "Invalid payload",
                new { rewardToken = "rewardToken is required and must be at most 255 characters" }));
        }

        var role = LoyaltyClaims.Role(User)?.ToLowerInvariant();
        if (role is not ("staff" or "manager"))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<RewardRedemptionResult>.Fail(
                    "FORBIDDEN",
                    "Role is not allowed for this operation"));
        }

        var userId = LoyaltyClaims.UserId(User);
        var businessId = LoyaltyClaims.BusinessId(User);
        var outletId = LoyaltyClaims.OutletId(User);

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(ApiResponse<RewardRedemptionResult>.Fail("UNAUTHORIZED", "Unauthorized"));
        }

        if (string.IsNullOrWhiteSpace(businessId))
        {
            return Unauthorized(ApiResponse<RewardRedemptionResult>.Fail(
                "UNAUTHORIZED",
                "No business context (business_id missing)"));
        }

        if (string.IsNullOrWhiteSpace(outletId))
        {
            return Unauthorized(ApiResponse<RewardRedemptionResult>.Fail(
                "UNAUTHORIZED",
                "No outlet context (outlet_id missing)"));
        }

        try
        {
            var execution = await rewardRedemptionService.RedeemAsync(
                new RedeemRewardCommand(
                    businessId,
                    userId,
                    outletId,
                    tokenValue),
                cancellationToken);

            await auditLogWriter.WriteAsync(
                new AuditEvent(
                    businessId,
                    userId,
                    role,
                    outletId,
                    "pos_redeem_reward",
                    "member_reward",
                    execution.Response.MemberRewardId,
                    "Redeem reward",
                    JsonSerializer.Serialize(new
                    {
                        memberId = execution.MemberId,
                        memberCardId = execution.MemberCardId,
                        rewardTokenId = execution.Response.RewardTokenId,
                        rewardId = execution.RewardId,
                        outletId,
                        transactionId = execution.Response.TransactionId,
                    }),
                    GetClientIp(),
                    Request.Headers["User-Agent"].ToString()),
                cancellationToken);

            return Ok(ApiResponse<RewardRedemptionResult>.Ok(execution.Response));
        }
        catch (RewardRedemptionException exception) when (exception.Code == RewardRedemptionErrorCode.NotFound)
        {
            return NotFound(ApiResponse<RewardRedemptionResult>.Fail("NOT_FOUND", "Reward token not found"));
        }
        catch (RewardRedemptionException exception) when (exception.Code == RewardRedemptionErrorCode.Forbidden)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                ApiResponse<RewardRedemptionResult>.Fail("FORBIDDEN", "Token not valid for this business"));
        }
        catch (RewardRedemptionException exception) when (exception.Code == RewardRedemptionErrorCode.Used)
        {
            return BadRequest(ApiResponse<RewardRedemptionResult>.Fail("CONFLICT", "Reward token already redeemed"));
        }
        catch (RewardRedemptionException exception) when (exception.Code == RewardRedemptionErrorCode.Expired)
        {
            return BadRequest(ApiResponse<RewardRedemptionResult>.Fail("CONFLICT", "Reward token has expired"));
        }
        catch (RewardRedemptionException exception) when (exception.Code == RewardRedemptionErrorCode.Unavailable)
        {
            return BadRequest(ApiResponse<RewardRedemptionResult>.Fail(
                "CONFLICT",
                "Reward is not available for redemption"));
        }
        catch (RewardRedemptionException exception) when (exception.Code == RewardRedemptionErrorCode.MissingMemberCard)
        {
            return BadRequest(ApiResponse<RewardRedemptionResult>.Fail(
                "VALIDATION_ERROR",
                "Reward is not linked to a member card"));
        }
        catch (RewardRedemptionException)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResponse<RewardRedemptionResult>.Fail("INTERNAL_ERROR", "Failed to load reward token"));
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