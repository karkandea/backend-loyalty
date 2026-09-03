using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/admin/auth")]
public sealed class AdminPasswordController(
    IAdminPasswordService passwords,
    ITransactionalEmailSender emailSender,
    StandaloneAuthDbContext db,
    IConfiguration configuration) : ControllerBase
{
    private const string GenericForgotMessage = "Jika email terdaftar, tautan reset sudah dikirim.";

    [AllowAnonymous]
    [EnableRateLimiting("email-action")]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] AdminForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var context = await ResolveBusinessAsync(request.BusinessId, request.BusinessSlug, cancellationToken);
        if (context.Status == BusinessContextStatus.Missing)
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Business context is required (provide businessId or businessSlug)"));
        if (context.Status == BusinessContextStatus.NotFound)
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Business not found"));
        if (context.Status == BusinessContextStatus.Inactive)
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail("FORBIDDEN", "Business is inactive"));

        var business = context.Business!;
        var issue = await passwords.CreateResetAsync(
            business.Id,
            request.Email,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            cancellationToken);

        if (issue is not null)
        {
            await emailSender.SendAdminPasswordResetAsync(
                issue.Email,
                BuildResetUrl(business.Slug, issue.RawToken),
                cancellationToken);
        }

        return Ok(ApiResponse<object>.Ok(new { message = GenericForgotMessage }));
    }

    [AllowAnonymous]
    [EnableRateLimiting("email-action")]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await passwords.ResetPasswordAsync(
            request.Token,
            request.NewPassword,
            ResolveTenantSlug(),
            cancellationToken);

        if (result == PasswordResetResult.InvalidOrExpired)
        {
            return BadRequest(ApiResponse<object>.Fail(
                "FORBIDDEN",
                "Token tidak valid atau sudah kadaluarsa"));
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            message = "Kata sandi berhasil diubah. Silakan login kembali.",
        }));
    }

    [Authorize]
    [EnableRateLimiting("email-action")]
    [HttpPost("update-password")]
    public async Task<IActionResult> UpdatePassword(
        [FromBody] UpdatePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        var role = LoyaltyClaims.Role(User)?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(userId) ||
            authKind != "admin" ||
            role is not ("staff" or "manager"))
        {
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));
        }

        var result = await passwords.UpdatePasswordAsync(
            userId,
            request.CurrentPassword,
            request.NewPassword,
            cancellationToken);

        return result switch
        {
            PasswordUpdateResult.Success => Ok(ApiResponse<object>.Ok(new { message = "Kata sandi berhasil diubah." })),
            PasswordUpdateResult.InvalidCurrentPassword => Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Password lama salah")),
            PasswordUpdateResult.UserNotFound => Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized")),
            _ => StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("INTERNAL_ERROR", "Internal server error")),
        };
    }

    private async Task<BusinessContextResult> ResolveBusinessAsync(
        string? requestedId,
        string? requestedSlug,
        CancellationToken cancellationToken)
    {
        var businessId = FirstNonBlank(
            Request.Headers["x-tenant-business-id"].FirstOrDefault(),
            Request.Headers["x-business-id"].FirstOrDefault(),
            Request.Headers["x-auth-business-id"].FirstOrDefault(),
            requestedId);
        var businessSlug = FirstNonBlank(
            Request.Headers["x-tenant-slug"].FirstOrDefault(),
            Request.Headers["x-business-slug"].FirstOrDefault(),
            requestedSlug,
            ResolveTenantSlug());

        if (string.IsNullOrWhiteSpace(businessId) && string.IsNullOrWhiteSpace(businessSlug))
            return new(BusinessContextStatus.Missing, null);

        Business? business;
        if (!string.IsNullOrWhiteSpace(businessId))
        {
            business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(
                x => x.Id == businessId.Trim(),
                cancellationToken);
            if (business is not null && !string.IsNullOrWhiteSpace(businessSlug) &&
                !string.Equals(business.Slug, businessSlug.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                business = null;
            }
        }
        else
        {
            var slug = businessSlug!.Trim().ToLowerInvariant();
            business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(
                x => x.Slug.ToLower() == slug,
                cancellationToken);
        }

        if (business is null)
            return new(BusinessContextStatus.NotFound, null);
        if (!business.IsActive)
            return new(BusinessContextStatus.Inactive, business);
        return new(BusinessContextStatus.Ok, business);
    }

    private string? ResolveTenantSlug()
    {
        var explicitSlug = FirstNonBlank(
            Request.Headers["x-tenant-slug"].FirstOrDefault(),
            Request.Headers["x-business-slug"].FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(explicitSlug))
            return explicitSlug.Trim().ToLowerInvariant();

        var host = Request.Host.Host?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host) || host == "localhost" || host == "127.0.0.1")
            return null;

        var baseDomain = configuration["EMAIL_CONFIRM_BASE_DOMAIN"]?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(baseDomain))
        {
            if (baseDomain.StartsWith("https://")) baseDomain = baseDomain[8..];
            if (baseDomain.StartsWith("http://")) baseDomain = baseDomain[7..];
            baseDomain = baseDomain.TrimEnd('/');
            if (host.EndsWith($".{baseDomain}", StringComparison.OrdinalIgnoreCase))
                return host[..^(baseDomain.Length + 1)].Split('.')[0];
        }

        return null;
    }

    private string BuildResetUrl(string slug, string rawToken)
    {
        var configured = configuration["EMAIL_CONFIRM_BASE_DOMAIN"]?.Trim();
        var baseDomain = string.IsNullOrWhiteSpace(configured) ? "dualangka.com" : configured;
        if (baseDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) baseDomain = baseDomain[8..];
        if (baseDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) baseDomain = baseDomain[7..];
        baseDomain = baseDomain.TrimEnd('/');
        return $"https://{slug}.{baseDomain}/admin/reset-password?token={Uri.EscapeDataString(rawToken)}";
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private enum BusinessContextStatus { Ok, Missing, NotFound, Inactive }
    private sealed record BusinessContextResult(BusinessContextStatus Status, Business? Business);
}
