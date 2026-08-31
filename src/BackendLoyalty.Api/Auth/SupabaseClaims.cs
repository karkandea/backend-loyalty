using System.Security.Claims;
using System.Text.Json;

namespace BackendLoyalty.Api.Auth;

internal static class SupabaseClaims
{
    public static string? UserId(ClaimsPrincipal user) => user.FindFirst("sub")?.Value;
    public static string? AppRole(ClaimsPrincipal user) => AppMetadata(user, "role") ?? user.FindFirst("app_role")?.Value;
    public static string? BusinessId(ClaimsPrincipal user) => AppMetadata(user, "business_id") ?? user.FindFirst("business_id")?.Value;
    public static string? OutletId(ClaimsPrincipal user) => AppMetadata(user, "outlet_id") ?? user.FindFirst("outlet_id")?.Value;

    private static string? AppMetadata(ClaimsPrincipal user, string key)
    {
        var raw = user.FindFirst("app_metadata")?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;

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

        return null;
    }
}
