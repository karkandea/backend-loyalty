namespace BackendLoyalty.Domain.Entities;

public sealed class AuditLog
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }
    public Guid? UserId { get; set; }
    public string? Role { get; set; }
    public Guid? OutletId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? Summary { get; set; }
    public string? MetadataJson { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}
