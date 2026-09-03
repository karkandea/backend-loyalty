namespace BackendLoyalty.Application.Auth;

public sealed record RefreshSessionContext(
    string UserId,
    string AuthKind,
    string? Role,
    string? BusinessId,
    string? OutletId);

public interface IRefreshTokenSessionService
{
    Task RegisterAsync(
        string refreshToken,
        DateTime expiresAt,
        RefreshSessionContext context,
        CancellationToken cancellationToken);

    Task<bool> RotateAsync(
        string currentRefreshToken,
        string nextRefreshToken,
        DateTime nextExpiresAt,
        RefreshSessionContext nextContext,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(
        string refreshToken,
        string reason,
        CancellationToken cancellationToken);

    Task<int> RevokeAllAsync(
        string userId,
        string authKind,
        string reason,
        CancellationToken cancellationToken);
}
