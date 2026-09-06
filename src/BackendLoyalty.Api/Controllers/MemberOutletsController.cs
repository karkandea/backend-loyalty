using System.Data;
using System.Data.Common;
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

        var connection = authDb.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        try
        {
            if (shouldClose)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, address, city, phone
                FROM "Outlet"
                WHERE "businessId" = @businessId
                  AND "isActive" = true
                ORDER BY name
                """;
            AddParameter(command, "businessId", session.BusinessId);

            var outlets = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                outlets.Add(new
                {
                    outletId = reader.GetString(0),
                    name = reader.GetString(1),
                    address = reader.GetString(2),
                    city = reader.GetString(3),
                    phone = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }

            return Ok(ApiResponse<object>.Ok(new { outlets }));
        }
        finally
        {
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
