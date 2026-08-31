using System.Text;
using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auditing;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Application.Loyalty;
using BackendLoyalty.Application.Members;
using BackendLoyalty.Application.Rewards;
using BackendLoyalty.Infrastructure.Auditing;
using BackendLoyalty.Infrastructure.Auth;
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

var jwtIssuer = builder.Configuration["Jwt:Issuer"]?.Trim();
var jwtAudience = builder.Configuration["Jwt:Audience"]?.Trim();
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"];

if (string.IsNullOrWhiteSpace(jwtIssuer))
    throw new InvalidOperationException("Jwt:Issuer is required.");
if (string.IsNullOrWhiteSpace(jwtAudience))
    throw new InvalidOperationException("Jwt:Audience is required.");
if (string.IsNullOrWhiteSpace(jwtSigningKey) || Encoding.UTF8.GetByteCount(jwtSigningKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey is required and must be at least 32 bytes.");

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
builder.Services.AddDbContext<StandaloneAuthDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<IMemberScanService, MemberScanService>();
builder.Services.AddScoped<IMemberSessionResolver, MemberSessionResolver>();
builder.Services.AddScoped<ILoyaltyStampService, LoyaltyStampService>();
builder.Services.AddScoped<IRewardRedemptionService, RewardRedemptionService>();
builder.Services.AddScoped<IMemberRewardTokenService, MemberRewardTokenService>();
builder.Services.AddScoped<IAuditLogWriter, AuditLogWriter>();
builder.Services.AddScoped<IStandaloneCredentialService, StandaloneCredentialService>();
builder.Services.AddSingleton<ILoyaltyJwtTokenIssuer, LoyaltyJwtTokenIssuer>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var tokenType = context.Principal?.FindFirst(LoyaltyClaims.TokenTypeClaim)?.Value;
                if (!string.Equals(tokenType, "access", StringComparison.Ordinal))
                    context.Fail("Only access tokens may call protected APIs.");

                return Task.CompletedTask;
            },
        };
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
