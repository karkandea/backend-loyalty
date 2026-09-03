namespace BackendLoyalty.Domain.Entities;

public sealed class LoyaltyTransaction
{
    public string Id { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string MemberCardId { get; set; } = string.Empty;
    public string CardId { get; set; } = string.Empty;
    public string? RewardId { get; set; }
    public string OutletId { get; set; } = string.Empty;
    public string StaffId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int StampsAdded { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
