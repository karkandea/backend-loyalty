namespace BackendLoyalty.Application.Auth;

public sealed record TeamInvitationSummary(
    string InvitationId,
    string Email,
    string Role,
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
