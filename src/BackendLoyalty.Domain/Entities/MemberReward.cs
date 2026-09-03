namespace BackendLoyalty.Domain.Entities;

public sealed class MemberReward
{
    public string Id { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string RewardId { get; set; } = string.Empty;
    public string? MemberCardId { get; set; }
    public string SourceType { get; set; } = "MILESTONE";
    public string Status { get; set; } = "AVAILABLE";
    public DateTime IssuedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}
