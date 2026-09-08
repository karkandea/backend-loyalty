namespace BackendLoyalty.Application.Members;

public enum MemberPhoneOtpPurpose
{
    Signup,
    ForgotPassword,
    ProfilePhone,
}

public enum MemberPhoneOtpErrorCode
{
    Validation,
    BusinessNotFound,
    BusinessInactive,
    FeatureDisabled,
    Conflict,
    NotFound,
    TooManyRequests,
    DeviceMismatch,
    Expired,
    AlreadyUsed,
    InvalidOtp,
    ProviderUnavailable,
}

public sealed class MemberPhoneOtpException(
    MemberPhoneOtpErrorCode code,
    string message,
    int? retryAfterSeconds = null) : Exception(message)
{
    public MemberPhoneOtpErrorCode Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed record MemberPhoneOtpIssue(
    string OtpSessionId,
    DateTime ExpiresAt);

public sealed record MemberPhoneOtpVerification(
    string VerificationToken,
    DateTime ExpiresAt);

public sealed record MemberPhonePasswordResetIssue(
    string RawResetToken,
    DateTime ExpiresAt);

public interface IWhatsAppOtpSender
{
    bool IsConfigured { get; }

    Task SendOtpAsync(
        string phoneE164,
        string otp,
        int expiresMinutes,
        CancellationToken cancellationToken = default);
}

public interface IMemberPhoneOtpService
{
    Task<MemberPhoneOtpIssue> SendPublicOtpAsync(
        string phone,
        string? businessId,
        string? businessSlug,
        MemberPhoneOtpPurpose purpose,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);

    Task<MemberPhoneOtpVerification> VerifySignupOtpAsync(
        string phone,
        string otp,
        string otpSessionId,
        string? businessId,
        string? businessSlug,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);

    Task ConsumeSignupVerificationAsync(
        string phone,
        string otpSessionId,
        string verificationToken,
        string? businessId,
        string? businessSlug,
        CancellationToken cancellationToken = default);

    Task<MemberPhonePasswordResetIssue> VerifyForgotPasswordOtpAsync(
        string phone,
        string otp,
        string otpSessionId,
        string? businessId,
        string? businessSlug,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);

    Task<MemberPhoneOtpIssue> SendProfilePhoneOtpAsync(
        string memberId,
        string businessId,
        string phone,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);

    Task VerifyProfilePhoneOtpAsync(
        string memberId,
        string businessId,
        string phone,
        string otp,
        string otpSessionId,
        string deviceId,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken = default);
}
