using System.Security.Claims;

namespace BackendLoyalty.Api.Auth;

internal static class LoyaltyClaims
{
    public const string AuthKindClaim = "auth_kind";
    public const string BusinessIdClaim = "business_id";
    public const string OutletIdClaim = "outlet_id";
    public const string TokenTypeClaim = "token_type";

    public static string? UserId(ClaimsPrincipal user) => user.FindFirst("sub")?.Value;
    public static string? Role(ClaimsPrincipal user) => user.FindFirst("role")?.Value;
    public static string? BusinessId(ClaimsPrincipal user) => user.FindFirst(BusinessIdClaim)?.Value;
    public static string? OutletId(ClaimsPrincipal user) => user.FindFirst(OutletIdClaim)?.Value;
    public static string? AuthKind(ClaimsPrincipal user) => user.FindFirst(AuthKindClaim)?.Value;
    public static string? TokenType(ClaimsPrincipal user) => user.FindFirst(TokenTypeClaim)?.Value;
}
