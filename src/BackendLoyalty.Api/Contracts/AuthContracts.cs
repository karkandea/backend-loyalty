using System.ComponentModel.DataAnnotations;

namespace BackendLoyalty.Api.Contracts;

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(1)] string Password);

public sealed record RefreshTokenRequest(
    [Required, MinLength(10)] string RefreshToken);

public sealed record ActiveBusinessRequest(
    [Required] string BusinessId,
    [Required, MinLength(10)] string RefreshToken);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(
    [Required, MinLength(8)] string Token,
    [Required, MinLength(8), MaxLength(128)] string NewPassword);

public sealed record UpdatePasswordRequest(
    [Required, MinLength(1)] string CurrentPassword,
    [Required, MinLength(8), MaxLength(128)] string NewPassword);

public sealed record SetPasswordRequest(
    [Required, MinLength(8), MaxLength(128)] string NewPassword);
