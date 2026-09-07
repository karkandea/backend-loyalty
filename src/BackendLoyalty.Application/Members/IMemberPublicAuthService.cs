namespace BackendLoyalty.Application.Members;

public sealed record MemberRegistrationRequest(
    string Name,
    string Email,
    string Password,
    string DateOfBirth,
    string? BusinessId,
    string? BusinessSlug);

public sealed record MemberRegistrationResult(
    string MemberId,
    string BusinessId,
    string BusinessSlug,
    string Email,
    string MemberBarcode,
    bool RequiresEmailConfirmation,
    string Message);

public sealed record MemberPasswordResetIssue(
    string MemberId,
    string BusinessId,
    string BusinessSlug,
    string Email,
    string RawToken,
    DateTime ExpiresAt);

public sealed record MemberEmailOtpIssue(
    string MemberId,
    string BusinessId,
    string BusinessSlug,
    string Email,
    string OtpSessionId,
    string RawOtp,
    DateTime ExpiresAt);

public enum MemberEmailOtpSendStatus
{
    Issued,
    AlreadyVerified,
}

public sealed record MemberEmailOtpSendResult(
    MemberEmailOtpSendStatus Status,
    MemberEmailOtpIssue? Issue);

public enum MemberEmailOtpVerifyResult
{
    Success,
    NotFound,
    Invalid,
    Expired,
    AlreadyUsed,
}

public enum MemberPasswordResetResult
{
    Success,
    InvalidOrExpired,
}

public enum MemberPublicAuthErrorCode
{
    BusinessContextRequired,
    BusinessNotFound,
    BusinessInactive,
    Conflict,
    CardNotReady,
    MemberLimitReached,
    MemberNotFound,
    TooManyRequests,
}

public sealed class MemberPublicAuthException(MemberPublicAuthErrorCode code, string message) : Exception(message)
{
    public MemberPublicAuthErrorCode Code { get; } = code;
}

public interface IMemberPublicAuthService
{
    Task<MemberRegistrationResult> RegisterAsync(
        MemberRegistrationRequest request,
        CancellationToken cancellationToken = default);

    Task<MemberEmailOtpSendResult> SendEmailVerificationOtpAsync(
        string email,
        string? businessSlug,
        string deviceId,
        CancellationToken cancellationToken = default);

    Task<MemberEmailOtpVerifyResult> VerifyEmailVerificationOtpAsync(
        string email,
        string otp,
        string otpSessionId,
        string? businessSlug,
        string deviceId,
        CancellationToken cancellationToken = default);

    Task<MemberPasswordResetIssue?> CreatePasswordResetAsync(
        string email,
        string? businessId,
        string? businessSlug,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);

    Task<MemberPasswordResetResult> ResetPasswordAsync(
        string rawToken,
        string newPassword,
        string? tenantSlug,
        CancellationToken cancellationToken = default);
}
