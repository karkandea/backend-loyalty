namespace BackendLoyalty.Application.Auth;

public sealed record PasswordResetIssue(
    string UserId,
    string Email,
    string RawToken,
    DateTime ExpiresAt);

public sealed record PasswordPolicyStatus(
    bool RequiresPassword,
    DateTime? GraceExpiresAt,
    DateTime? PasswordSetAt,
    bool IsExpired);

public enum PasswordResetResult
{
    Success,
    InvalidOrExpired,
}

public enum PasswordUpdateResult
{
    Success,
    InvalidCurrentPassword,
    UserNotFound,
}

public enum RequiredPasswordSetResult
{
    Success,
    AlreadySet,
    UserNotFound,
}

public interface IBusinessPasswordService
{
    Task<PasswordResetIssue?> CreateResetAsync(
        string email,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken);

    Task<PasswordResetResult> ResetPasswordAsync(
        string rawToken,
        string newPassword,
        CancellationToken cancellationToken);

    Task<PasswordUpdateResult> UpdatePasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);

    Task<PasswordPolicyStatus> GetPasswordPolicyAsync(
        string userId,
        CancellationToken cancellationToken);

    Task<RequiredPasswordSetResult> SetRequiredPasswordAsync(
        string userId,
        string newPassword,
        CancellationToken cancellationToken);
}

public interface ITransactionalEmailSender
{
    Task<bool> SendPasswordResetAsync(
        string recipient,
        string resetUrl,
        CancellationToken cancellationToken);

    Task<bool> SendAdminPasswordResetAsync(
        string recipient,
        string resetUrl,
        CancellationToken cancellationToken);

    Task<bool> SendBusinessInvitationAsync(
        string recipient,
        string businessName,
        string role,
        string invitationUrl,
        CancellationToken cancellationToken);
}
