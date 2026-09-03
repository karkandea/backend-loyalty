using System.ComponentModel.DataAnnotations;

namespace BackendLoyalty.Api.Contracts;

public sealed record OwnerSignupApiRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(1), MaxLength(200)] string FullName,
    [Required, MinLength(1), MaxLength(200)] string BusinessName,
    [Required, MinLength(3), MaxLength(64), RegularExpression("^[a-z0-9-]+$")] string Slug);

public sealed record ResendOwnerVerificationRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(3), MaxLength(64), RegularExpression("^[a-z0-9-]+$")] string Slug);

public sealed record CreateTeamInvitationRequest(
    [Required, EmailAddress] string Email,
    [Required, RegularExpression("^(?i:ADMIN|STAFF)$")] string Role,
    string[]? Permissions = null);

public sealed record RegisterBusinessInvitationRequest(
    [Required, MinLength(8)] string Token,
    [Required, MinLength(1), MaxLength(200)] string FullName,
    [Required, MinLength(8), MaxLength(128)] string Password);

public sealed record AcceptBusinessInvitationRequest(
    [Required, MinLength(8)] string Token);
