namespace BackendLoyalty.Application.Auth;

public sealed record TeamInvitationSummary(
    string InvitationId,
    string Email,
    string Role,
    IReadOnlyList<string> Permissions,
    DateTime ExpiresAt,
    DateTime CreatedAt,
    bool RequiresPassword);

public enum InvitationManagementResult
{
    Success,
    Forbidden,
    NotFound,
}

public interface IBusinessInvitationManagementService
{
    Task<bool> CanManageTeamAsync(
        string userId,
        string businessId,
        string role,
        CancellationToken cancellationToken);

    Task<(InvitationManagementResult Result, IReadOnlyList<TeamInvitationSummary> Items)> ListActiveAsync(
        string userId,
        string businessId,
        string role,
        CancellationToken cancellationToken);

    Task<InvitationManagementResult> RevokeAsync(
        string userId,
        string businessId,
        string role,
        string invitationId,
        CancellationToken cancellationToken);
}
