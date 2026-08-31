namespace BackendLoyalty.Application.Auth;

public sealed record AdminLoginProfile(
    string UserId,
    string AdminUserId,
    string BusinessId,
    string BusinessName,
    string OutletId,
    string OutletName,
    string Role);

public sealed record BusinessMembership(
    string BusinessUserId,
    string BusinessId,
    string BusinessName,
    string BusinessSlug,
    string Role);

public sealed record BusinessLoginProfile(
    string UserId,
    IReadOnlyList<BusinessMembership> Memberships);

public enum CredentialFailureReason
{
    InvalidCredentials,
    PasswordMigrationRequired,
    InactiveAccount,
    InactiveBusiness,
    OutletRequired,
    InactiveOutlet,
}

public sealed class CredentialException(CredentialFailureReason reason, string message) : Exception(message)
{
    public CredentialFailureReason Reason { get; } = reason;
}
