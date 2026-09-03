using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/member/outlets")]
public sealed class MemberOutletsController(
    IMemberSessionResolver memberSessionResolver,
    StandaloneAuthDbContext authDb) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var session = await memberSessionResolver.ResolveAsync(
            Request.Cookies[MemberSessionCookie],
            cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var outlets = await authDb.Outlets.AsNoTracking()
            .Where(x => x.BusinessId == session.BusinessId && x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new
            {
                outletId = x.Id,
                name = x.Name,
            })
            .ToListAsync(cancellationToken);

        return Ok(ApiResponse<object>.Ok(new { outlets }));
    }
}
