using System.Text.Json;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Infrastructure.Auth;

public sealed class BusinessInvitationManagementService(StandaloneAuthDbContext db)
    : IBusinessInvitationManagementService
{
    public async Task<bool> CanManageTeamAsync(
        string userId,
        string businessId,
        string role,
        CancellationToken cancellationToken)
    {
        var normalizedRole = role.Trim().ToLowerInvariant();
        if (normalizedRole == "owner")
            return true;
        if (normalizedRole != "admin")
            return false;

        var rows = await ResolveRowsAsync(userId, businessId, cancellationToken);
        if (rows.Count != 1)
            return false;

        var permissions = rows[0].Permissions;
        if (permissions is null || permissions.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;
        if (permissions.RootElement.ValueKind != JsonValueKind.Array)
            return false;

        return permissions.RootElement.EnumerateArray().Any(x =>
            x.ValueKind == JsonValueKind.String &&
            string.Equals(x.GetString(), "can_manage_team", StringComparison.Ordinal));
    }

    public async Task<(InvitationManagementResult Result, IReadOnlyList<TeamInvitationSummary> Items)> ListActiveAsync(
        string userId,
        string businessId,
        string role,
        CancellationToken cancellationToken)
    {
        if (!await CanManageTeamAsync(userId, businessId, role, cancellationToken))
            return (InvitationManagementResult.Forbidden, Array.Empty<TeamInvitationSummary>());

        var now = DateTime.UtcNow;
        var rows = await db.BusinessInvitations
            .AsNoTracking()
            .Where(x => x.BusinessId == businessId &&
                        x.UsedAt == null &&
                        x.RevokedAt == null &&
                        x.ExpiresAt > now)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(x => new TeamInvitationSummary(
                x.Id,
                x.Email,
                x.Role,
                ReadPermissions(x.Permissions),
                x.ExpiresAt,
                x.CreatedAt,
                x.RequiresPassword))
            .ToList();

        return (InvitationManagementResult.Success, items);
    }

    public async Task<InvitationManagementResult> RevokeAsync(
        string userId,
        string businessId,
        string role,
        string invitationId,
        CancellationToken cancellationToken)
    {
        if (!await CanManageTeamAsync(userId, businessId, role, cancellationToken))
            return InvitationManagementResult.Forbidden;

        if (!Guid.TryParse(invitationId, out var inviteGuid))
            return InvitationManagementResult.NotFound;

        var now = DateTime.UtcNow;
        var normalizedId = inviteGuid.ToString();
        var affected = await db.BusinessInvitations
            .Where(x => x.Id == normalizedId &&
                        x.BusinessId == businessId &&
                        x.UsedAt == null &&
                        x.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.RevokedAt, now)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        return affected == 1
            ? InvitationManagementResult.Success
            : InvitationManagementResult.NotFound;
    }

    private Task<List<BusinessUser>> ResolveRowsAsync(
        string userId,
        string businessId,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(userId, out var guid))
        {
            var normalized = guid.ToString();
            return db.BusinessUsers
                .Where(x => x.IsActive &&
                            x.BusinessId == businessId &&
                            (x.Id == userId || x.AuthUserId == normalized))
                .ToListAsync(cancellationToken);
        }

        return db.BusinessUsers
            .Where(x => x.IsActive && x.BusinessId == businessId && x.Id == userId)
            .ToListAsync(cancellationToken);
    }

    private static IReadOnlyList<string> ReadPermissions(JsonDocument? permissions)
    {
        if (permissions is null || permissions.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return permissions.RootElement
            .EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
    }
}
