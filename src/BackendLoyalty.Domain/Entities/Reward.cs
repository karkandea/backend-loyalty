namespace BackendLoyalty.Domain.Entities;

public sealed class Reward
{
    public string Id { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SourceType { get; set; } = "MILESTONE";
    public int? DefaultExpiryDays { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
