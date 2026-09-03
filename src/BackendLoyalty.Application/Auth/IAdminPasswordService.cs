namespace BackendLoyalty.Application.Auth;

public sealed record AdminPasswordResetIssue(
    string UserId,
    string Email,
    string RawToken,
    DateTime ExpiresAt);

public interface IAdminPasswordService
{
    Task<AdminPasswordResetIssue?> CreateResetAsync(
        string businessId,
        string email,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken);

    Task<PasswordResetResult> ResetPasswordAsync(
        string rawToken,
        string newPassword,
        string? tenantSlug,
        CancellationToken cancellationToken);

    Task<PasswordUpdateResult> UpdatePasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);
}
