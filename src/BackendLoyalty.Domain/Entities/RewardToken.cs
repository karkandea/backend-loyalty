namespace BackendLoyalty.Domain.Entities;

public sealed class RewardToken
{
    public string Id { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string? MemberRewardId { get; set; }
    public string? MemberId { get; set; }
    public string? MemberCardId { get; set; }
    public string Scope { get; set; } = "REWARD";
    public string Token { get; set; } = string.Empty;
    public string Status { get; set; } = "ACTIVE";
    public DateTime? ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? UsedByStaffId { get; set; }
    public string? OutletId { get; set; }
    public string? UsedAtOutletId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
