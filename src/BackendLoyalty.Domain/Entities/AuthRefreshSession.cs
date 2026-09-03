namespace BackendLoyalty.Domain.Entities;

public sealed class AuthRefreshSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string AuthKind { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }
    public Guid? ParentSessionId { get; set; }
    public string? Role { get; set; }
    public string? BusinessId { get; set; }
    public string? OutletId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public Guid? ReplacedBySessionId { get; set; }
    public DateTime CreatedAt { get; set; }
}
