using System.ComponentModel.DataAnnotations;
using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/business/auth")]
public sealed class BusinessPasswordController(
    IBusinessPasswordService passwords,
    ITransactionalEmailSender emailSender,
    IConfiguration configuration,
    ILogger<BusinessPasswordController> logger) : ControllerBase
{
    private const string GenericForgotMessage =
        "If the email exists, you’ll receive a password reset link within a few minutes.";

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!new EmailAddressAttribute().IsValid(email))
            return Ok(ApiResponse<object>.Ok(new { message = GenericForgotMessage }));

        try
        {
            var issue = await passwords.CreateResetAsync(
                email,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            if (issue is not null)
            {
                var resetUrl = BuildResetUrl(issue.RawToken);
                await emailSender.SendPasswordResetAsync(
                    issue.Email,
                    resetUrl,
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            // Deliberately preserve anti-enumeration behavior: callers always receive
            // the same generic success response whether the account exists or not.
            logger.LogError(exception, "Standalone forgot-password flow failed.");
        }

        return Ok(ApiResponse<object>.Ok(new { message = GenericForgotMessage }));
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var result = await passwords.ResetPasswordAsync(
            request.Token,
            request.NewPassword,
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
    [HttpPost("update-password")]
    public async Task<IActionResult> UpdatePassword(
        [FromBody] UpdatePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        var role = LoyaltyClaims.Role(User)?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(userId) ||
            authKind != "business" ||
            role is not ("owner" or "admin"))
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
            PasswordUpdateResult.Success => Ok(ApiResponse<object>.Ok(new
            {
                message = "Kata sandi berhasil diubah.",
            })),
            PasswordUpdateResult.InvalidCurrentPassword => Unauthorized(
                ApiResponse<object>.Fail("UNAUTHORIZED", "Password lama salah")),
            PasswordUpdateResult.UserNotFound => Unauthorized(
                ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized")),
            _ => StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<object>.Fail("INTERNAL_ERROR", "Internal server error")),
        };
    }

    private string BuildResetUrl(string rawToken)
    {
        var configured = configuration["EMAIL_CONFIRM_BASE_DOMAIN"]?.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(configured)
            ? "https://dualangka.com"
            : configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? configured
                : $"https://{configured}";

        return $"{baseUrl.TrimEnd('/')}/cms/reset-password?token={Uri.EscapeDataString(rawToken)}";
    }
}
