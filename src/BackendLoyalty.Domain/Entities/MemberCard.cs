namespace BackendLoyalty.Domain.Entities;

public sealed class MemberCard
{
    public string Id { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string CardId { get; set; } = string.Empty;
    public int CurrentStamps { get; set; }
    public bool IsActive { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
