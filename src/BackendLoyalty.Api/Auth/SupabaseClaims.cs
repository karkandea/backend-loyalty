using System.Security.Claims;
using System.Text.Json;

namespace BackendLoyalty.Api.Auth;

internal static class SupabaseClaims
{
    public static string? AppRole(ClaimsPrincipal user) => Metadata(user, "role") ?? user.FindFirst("role")?.Value;
    public static string? BusinessId(ClaimsPrincipal user) => Metadata(user, "business_id") ?? user.FindFirst("business_id")?.Value;

    private static string? Metadata(ClaimsPrincipal user, string key)
    {
        foreach (var claimName in new[] { "app_metadata", "user_metadata" })
        {
            var raw = user.FindFirst(claimName)?.Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                using var json = JsonDocument.Parse(raw);
                if (json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
            catch (JsonException)
            {
                // Ignore malformed optional metadata; token signature validation is handled separately.
            }
        }
        return null;
    }
}
