using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace BackendLoyalty.Api.Auth;

internal sealed record LoyaltyTokenContext(
    string UserId,
    string AuthKind,
    string? Role,
    string? BusinessId,
    string? OutletId);

internal sealed record LoyaltyTokenPair(
    string AccessToken,
    string RefreshToken,
    DateTime AccessExpiresAt,
    DateTime RefreshExpiresAt);

internal interface ILoyaltyJwtTokenIssuer
{
    LoyaltyTokenPair Issue(LoyaltyTokenContext context);
    ClaimsPrincipal? ValidateRefreshToken(string refreshToken);
}

internal sealed class LoyaltyJwtTokenIssuer(IConfiguration configuration) : ILoyaltyJwtTokenIssuer
{
    private readonly string _issuer = Required(configuration, "Jwt:Issuer");
    private readonly string _audience = Required(configuration, "Jwt:Audience");
    private readonly string _signingKey = Required(configuration, "Jwt:SigningKey");
    private readonly int _accessTokenMinutes = ReadPositiveInt(configuration, "Jwt:AccessTokenMinutes", 15);
    private readonly int _refreshTokenDays = ReadPositiveInt(configuration, "Jwt:RefreshTokenDays", 7);

    public LoyaltyTokenPair Issue(LoyaltyTokenContext context)
    {
        var now = DateTime.UtcNow;
        var accessExpiresAt = now.AddMinutes(_accessTokenMinutes);
        var refreshExpiresAt = now.AddDays(_refreshTokenDays);

        var commonClaims = BuildContextClaims(context);
        var accessClaims = new List<Claim>(commonClaims)
        {
            new(LoyaltyClaims.TokenTypeClaim, "access"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        var refreshClaims = new List<Claim>(commonClaims)
        {
            new(LoyaltyClaims.TokenTypeClaim, "refresh"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        return new LoyaltyTokenPair(
            WriteToken(accessClaims, now, accessExpiresAt),
            WriteToken(refreshClaims, now, refreshExpiresAt),
            accessExpiresAt,
            refreshExpiresAt);
    }

    public ClaimsPrincipal? ValidateRefreshToken(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return null;

        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(
                refreshToken,
                ValidationParameters(validateLifetime: true),
                out var validatedToken);

            if (validatedToken is not JwtSecurityToken jwt ||
                !string.Equals(jwt.Header.Alg, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal))
                return null;

            return string.Equals(LoyaltyClaims.TokenType(principal), "refresh", StringComparison.Ordinal)
                ? principal
                : null;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string WriteToken(IEnumerable<Claim> claims, DateTime notBefore, DateTime expires)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private TokenValidationParameters ValidationParameters(bool validateLifetime) => new()
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey)),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuer = true,
        ValidIssuer = _issuer,
        ValidateAudience = true,
        ValidAudience = _audience,
        ValidateLifetime = validateLifetime,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub",
        RoleClaimType = "role",
    };

    private static List<Claim> BuildContextClaims(LoyaltyTokenContext context)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, context.UserId),
            new(LoyaltyClaims.AuthKindClaim, context.AuthKind),
        };

        if (!string.IsNullOrWhiteSpace(context.Role))
            claims.Add(new Claim("role", context.Role));
        if (!string.IsNullOrWhiteSpace(context.BusinessId))
            claims.Add(new Claim(LoyaltyClaims.BusinessIdClaim, context.BusinessId));
        if (!string.IsNullOrWhiteSpace(context.OutletId))
            claims.Add(new Claim(LoyaltyClaims.OutletIdClaim, context.OutletId));

        return claims;
    }

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key]?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{key} is required.");
        return value;
    }

    private static int ReadPositiveInt(IConfiguration configuration, string key, int fallback)
    {
        return int.TryParse(configuration[key], out var value) && value > 0 ? value : fallback;
    }
}
