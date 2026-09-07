namespace BackendLoyalty.Application.Members;

public sealed record MemberGoogleProfile(
    string Email,
    string Name,
    string? AvatarUrl,
    string Subject);

public sealed record MemberGoogleLoginResult(
    string MemberId,
    string BusinessId,
    string BusinessSlug,
    string SessionToken,
    DateTime ExpiresAt,
    bool IsNewMember,
    string RedirectTo);

public enum MemberGoogleAuthErrorCode
{
    BusinessNotFound,
    BusinessInactive,
    MemberInactive,
    InvalidRedirect,
}

public sealed class MemberGoogleAuthException(
    MemberGoogleAuthErrorCode code,
    string message) : Exception(message)
{
    public MemberGoogleAuthErrorCode Code { get; } = code;
}

public interface IMemberGoogleAuthService
{
    Task<MemberGoogleLoginResult> SignInAsync(
        MemberGoogleProfile profile,
        string businessSlug,
        string? next,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);
}
