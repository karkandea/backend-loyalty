namespace BackendLoyalty.Domain.Entities;

public sealed class CardMilestone
{
    public string Id { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string CardId { get; set; } = string.Empty;
    public int StampCount { get; set; }
    public int SortOrder { get; set; }
    public string? RewardId { get; set; }
    public string? RewardType { get; set; }
    public string? RewardValue { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
