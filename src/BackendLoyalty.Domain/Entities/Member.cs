namespace BackendLoyalty.Domain.Entities;

public sealed class Member
{
    public string Id { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string MemberBarcode { get; set; } = string.Empty;
    public int TotalStamps { get; set; }
    public DateTime DateJoined { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
