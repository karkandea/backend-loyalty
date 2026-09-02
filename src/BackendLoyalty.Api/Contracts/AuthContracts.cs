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
