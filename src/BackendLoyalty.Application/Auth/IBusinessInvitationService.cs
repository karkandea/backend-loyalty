namespace BackendLoyalty.Application.Auth;

public enum OwnerSignupStatus
{
    Success,
    SlugTaken,
    BusinessLimitReached,
    DataConflict,
}

public sealed record OwnerSignupRequest(
    string Email,
    string FullName,
    string BusinessName,
    string Slug,
    int MaxBusinesses = 10);

public sealed record OwnerSignupOutcome(
    OwnerSignupStatus Status,
    string? BusinessId,
    string? Slug,
    bool RequiresEmailConfirmation,
    InvitationIssue? Invitation);

public sealed record TeamInvitationRequest(
    string BusinessId,
    string InvitedBy,
    string Email,
    string Role,
    IReadOnlyList<string>? Permissions = null);

public sealed record InvitationIssue(
    string InvitationId,
    string BusinessId,
    string BusinessName,
    string Email,
    string Role,
    string RawToken,
    DateTime ExpiresAt,
    bool RequiresPassword);

public sealed record InvitationDetails(
    string InvitationId,
    string BusinessId,
    string BusinessName,
    string Email,
    string Role,
    IReadOnlyList<string> Permissions,
    DateTime ExpiresAt,
    bool RequiresPassword);

public enum InvitationRegistrationStatus
{
    Success,
    InvalidOrExpired,
    UserExists,
    Conflict,
}

public sealed record InvitationRegistrationOutcome(
    InvitationRegistrationStatus Status,
    string? UserId,
    string? BusinessId,
    string? Email);

public enum InvitationAcceptanceStatus
{
    Success,
    InvalidOrExpired,
    EmailMismatch,
    IdentityNotFound,
    Conflict,
}

public sealed record InvitationAcceptanceOutcome(
    InvitationAcceptanceStatus Status,
    string? BusinessId,
    int MembershipCount,
    bool RequiresRelogin);

public interface IBusinessInvitationService
{
    Task<OwnerSignupOutcome> SignupOwnerAsync(
        OwnerSignupRequest request,
        CancellationToken cancellationToken);

    Task<InvitationIssue?> ReissuePendingOwnerInvitationAsync(
        string email,
        string slug,
        CancellationToken cancellationToken);

    Task<InvitationIssue?> CreateTeamInvitationAsync(
        TeamInvitationRequest request,
        CancellationToken cancellationToken);

    Task<InvitationDetails?> ResolveInvitationAsync(
        string rawToken,
        CancellationToken cancellationToken);

    Task<InvitationRegistrationOutcome> RegisterAsync(
        string rawToken,
        string fullName,
        string password,
        CancellationToken cancellationToken);

    Task<InvitationAcceptanceOutcome> AcceptExistingAsync(
        string userId,
        string rawToken,
        CancellationToken cancellationToken);
}
