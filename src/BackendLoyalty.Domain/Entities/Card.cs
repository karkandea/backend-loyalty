namespace BackendLoyalty.Domain.Entities;

public sealed class Card
{
    public string Id { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int RequiredStamps { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public int? Level { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
