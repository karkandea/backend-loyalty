namespace BackendLoyalty.Application.Auth;

public interface IStandaloneCredentialService
{
    Task<AdminLoginProfile> AuthenticateAdminAsync(
        string email,
        string password,
        CancellationToken cancellationToken);

    Task<BusinessLoginProfile> AuthenticateBusinessAsync(
        string email,
        string password,
        CancellationToken cancellationToken);

    Task<BusinessMembership?> ResolveBusinessMembershipAsync(
        string userId,
        string businessId,
        CancellationToken cancellationToken);

    Task<int> CountBusinessMembershipsAsync(
        string userId,
        CancellationToken cancellationToken);

    Task<bool> IsUserActiveAsync(
        string userId,
        string authKind,
        CancellationToken cancellationToken);
}
