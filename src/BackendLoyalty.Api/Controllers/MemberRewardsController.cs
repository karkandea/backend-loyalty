using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Application.Rewards;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/member/rewards")]
public sealed class MemberRewardsController(
    IMemberSessionResolver memberSessionResolver,
    IMemberRewardTokenService memberRewardTokenService) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";

    [HttpPost("{memberRewardId}/token")]
    [ProducesResponseType(typeof(ApiResponse<MemberRewardTokenResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> IssueToken(
        string memberRewardId,
        CancellationToken cancellationToken)
    {
        var session = await memberSessionResolver.ResolveAsync(
            Request.Cookies[MemberSessionCookie],
            cancellationToken);

        if (session is null)
        {
            return Unauthorized(ApiResponse<MemberRewardTokenResult>.Fail(
                "UNAUTHORIZED",
                "Unauthorized"));
        }

        // Legacy route only accepts UUID-shaped MemberReward ids.
        if (!Guid.TryParse(memberRewardId, out _))
        {
            return NotFound(ApiResponse<MemberRewardTokenResult>.Fail(
                "NOT_FOUND",
                "Reward not found"));
        }

        try
        {
            var result = await memberRewardTokenService.IssueAsync(
                new IssueMemberRewardTokenCommand(
                    session.BusinessId,
                    session.MemberId,
                    memberRewardId),
                cancellationToken);

            return Ok(ApiResponse<MemberRewardTokenResult>.Ok(result));
        }
        catch (MemberRewardTokenException exception) when (exception.Code == MemberRewardTokenErrorCode.NotFound)
        {
            return NotFound(ApiResponse<MemberRewardTokenResult>.Fail(
                "NOT_FOUND",
                "Reward not found"));
        }
        catch (MemberRewardTokenException exception) when (exception.Code == MemberRewardTokenErrorCode.Unavailable)
        {
            return BadRequest(ApiResponse<MemberRewardTokenResult>.Fail(
                "VALIDATION_ERROR",
                "Reward not available for redemption"));
        }
        catch (MemberRewardTokenException)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResponse<MemberRewardTokenResult>.Fail(
                    "INTERNAL_ERROR",
                    "Unable to generate token"));
        }
    }
}
