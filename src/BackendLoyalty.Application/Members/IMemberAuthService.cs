namespace BackendLoyalty.Application.Members;

public sealed record MemberLoginResult(
    string MemberId,
    string BusinessId,
    string BusinessName,
    string BusinessSlug,
    string? Email,
    string MemberName,
    string MemberBarcode,
    string SessionToken,
    DateTime ExpiresAt);

public enum MemberAuthErrorCode
{
    InvalidCredentials,
    BusinessContextRequired,
    BusinessNotFound,
    BusinessInactive,
    AccountLocked,
}

public sealed class MemberAuthException(MemberAuthErrorCode code, string message) : Exception(message)
{
    public MemberAuthErrorCode Code { get; } = code;
}

public interface IMemberAuthService
{
    Task<MemberLoginResult> LoginAsync(
        string? email,
        string? password,
        string? businessId,
        string? businessSlug,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);

    Task<bool> LogoutAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default);
}
