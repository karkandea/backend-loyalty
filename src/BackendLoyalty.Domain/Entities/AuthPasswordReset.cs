namespace BackendLoyalty.Domain.Entities;

public sealed class AuthPasswordReset
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string AuthKind { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}
