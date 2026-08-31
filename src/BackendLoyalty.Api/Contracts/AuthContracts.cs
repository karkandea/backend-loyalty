using System.ComponentModel.DataAnnotations;

namespace BackendLoyalty.Api.Contracts;

public sealed record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required, MinLength(1)] string Password);

public sealed record RefreshTokenRequest(
    [property: Required, MinLength(10)] string RefreshToken);

public sealed record ActiveBusinessRequest(
    [property: Required] string BusinessId,
    [property: Required, MinLength(10)] string RefreshToken);
