namespace BackendLoyalty.Application.Auth;

public interface IStandaloneInvitationIdentityService
{
    Task<bool> NormalizeInvitationForExistingIdentityAsync(
        string invitationId,
        string email,
        CancellationToken cancellationToken);

    Task<bool> InvitationTargetsExistingIdentityAsync(
        string rawToken,
        CancellationToken cancellationToken);

    Task<InvitationAcceptanceOutcome?> AcceptZeroMembershipAsync(
        string userId,
        string rawToken,
        CancellationToken cancellationToken);
}
