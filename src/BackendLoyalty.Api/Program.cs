using System.Text;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auditing;
using BackendLoyalty.Application.Loyalty;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Application.Rewards;
using BackendLoyalty.Infrastructure.Auditing;
using BackendLoyalty.Infrastructure.Loyalty;
using BackendLoyalty.Infrastructure.Members;
using BackendLoyalty.Infrastructure.Persistence;
using BackendLoyalty.Infrastructure.Rewards;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("LoyaltyDb");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:LoyaltyDb is required.");
}

var supabaseUrl = builder.Configuration["Supabase:Url"]?.TrimEnd('/');
if (string.IsNullOrWhiteSpace(supabaseUrl))
{
    throw new InvalidOperationException("Supabase:Url is required.");
}

var supabaseJwtSecret = builder.Configuration["Supabase:JwtSecret"];
var issuer = $"{supabaseUrl}/auth/v1";

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var details = context.ModelState
                .Where(x => x.Value?.Errors.Count > 0)
                .ToDictionary(
                    x => x.Key,
                    x => x.Value!.Errors
                        .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                            ? "Invalid value"
                            : error.ErrorMessage)
                        .ToArray());

            return new BadRequestObjectResult(
                ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid payload", details));
        };
    });
builder.Services.AddOpenApi();

builder.Services.AddDbContext<LoyaltyDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddScoped<IMemberScanService, MemberScanService>();
builder.Services.AddScoped<IMemberSessionResolver, MemberSessionResolver>();
builder.Services.AddScoped<ILoyaltyStampService, LoyaltyStampService>();
builder.Services.AddScoped<IRewardRedemptionService, RewardRedemptionService>();
builder.Services.AddScoped<IMemberRewardTokenService, MemberRewardTokenService>();
builder.Services.AddScoped<IAuditLogWriter, AuditLogWriter>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;

        if (string.IsNullOrWhiteSpace(supabaseJwtSecret))
        {
            // Preferred mode: asymmetric Supabase signing keys discovered through OIDC/JWKS.
            options.Authority = issuer;
            options.MetadataAddress = $"{issuer}/.well-known/openid-configuration";
            options.RequireHttpsMetadata = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = "authenticated",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = "sub",
                RoleClaimType = "role"
            };
        }
        else
        {
            // Compatibility mode for legacy Supabase projects still signing access tokens with HS256.
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(supabaseJwtSecret)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudience = "authenticated",
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = "sub",
                RoleClaimType = "role"
            };
        }
    });

builder.Services.AddAuthorization();

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .GetChildren()
    .Select(x => x.Value)
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .Select(x => x!)
    .ToArray();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(
            ApiResponse<object>.Fail("INTERNAL_ERROR", "Internal server error"));
    });
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "backend-loyalty" }));
app.MapGet("/health/db", async (LoyaltyDbContext db, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        ? Results.Ok(new { status = "ok", database = "connected" })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Database unavailable");
});

app.Run();
