using System.Text.Json;

namespace BackendLoyalty.Domain.Entities;

public sealed class BusinessUser
{
    public string Id { get; set; } = string.Empty;
    public string BusinessId { get; set; } = string.Empty;
    public string? AuthUserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public JsonDocument? Permissions { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
