using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/member/account")]
public sealed class MemberAccountController(IMemberAuthService memberAuth) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";

    [HttpPost("delete")]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken)
    {
        try
        {
            await memberAuth.DeleteAccountAsync(
                Request.Cookies[MemberSessionCookie],
                cancellationToken);
            DeleteMemberSessionCookie();

            return Ok(ApiResponse<object>.Ok(new { deleted = true }));
        }
        catch (MemberAuthException exception)
        {
            return exception.Code == MemberAuthErrorCode.InvalidCredentials
                ? Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", exception.Message))
                : StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<object>.Fail("INTERNAL_ERROR", "Failed to delete account"));
        }
    }

    private void DeleteMemberSessionCookie()
    {
        Response.Cookies.Delete(
            MemberSessionCookie,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                Path = "/",
            });
    }
}
