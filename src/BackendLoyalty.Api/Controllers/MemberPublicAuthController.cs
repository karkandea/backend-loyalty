using System.ComponentModel.DataAnnotations;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using BackendLoyalty.Application.Members;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/member/auth")]
public sealed class MemberPublicAuthController(
    IMemberPublicAuthService memberAuth,
    ITransactionalEmailSender emailSender,
    IConfiguration configuration,
    ILogger<MemberPublicAuthController> logger) : ControllerBase
{
    private const string GenericForgotMessage =
        "If the email exists, a reset link has been sent.";

    [EnableRateLimiting("signup")]
    [HttpPost("signup")]
    public async Task<IActionResult> Register(
        [FromBody] MemberRegisterRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        var dateOfBirth = NormalizeDateOfBirth(request.DateOfBirth);

        if (name.Length is < 1 or > 120 ||
            !new EmailAddressAttribute().IsValid(email) ||
            password.Length is < 8 or > 128 ||
            dateOfBirth is null)
        {
            return BadRequest(ApiResponse<object>.Fail(
                "VALIDATION_ERROR",
                "Nama, email, password 8-128 karakter, dan tanggal lahir yang valid wajib diisi."));
        }

        try
        {
            var result = await memberAuth.RegisterAsync(
                new MemberRegistrationRequest(
                    name,
                    email,
                    password,
                    dateOfBirth,
                    request.BusinessId,
                    request.BusinessSlug),
                cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                memberId = result.MemberId,
                email = result.Email,
                memberBarcode = result.MemberBarcode,
                businessId = result.BusinessId,
                businessSlug = result.BusinessSlug,
                requiresEmailConfirmation = result.RequiresEmailConfirmation,
                message = result.Message,
            }));
        }
        catch (MemberPublicAuthException exception)
        {
            return MapPublicAuthException(exception);
        }
    }

    [EnableRateLimiting("email-action")]
    [HttpPost("send-otp")]
    public async Task<IActionResult> SendEmailOtp(
        [FromBody] MemberEmailOtpRequest request,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var businessSlug = FirstNonBlank(
            Request.Headers["x-tenant-slug"].FirstOrDefault(),
            Request.Headers["x-business-slug"].FirstOrDefault(),
            request.BusinessSlug);
        var deviceId = ResolveDeviceId();

        if (!new EmailAddressAttribute().IsValid(email) ||
            string.IsNullOrWhiteSpace(deviceId))
        {
            return BadRequest(ApiResponse<object>.Fail(
                "VALIDATION_ERROR",
                string.IsNullOrWhiteSpace(deviceId)
                    ? "Device ID is required"
                    : "Invalid email address"));
        }

        try
        {
            var result = await memberAuth.SendEmailVerificationOtpAsync(
                email,
                businessSlug,
                deviceId,
                cancellationToken);

            if (result.Status == MemberEmailOtpSendStatus.AlreadyVerified)
            {
                return Ok(ApiResponse<object>.Ok(new
                {
                    message = "Email sudah terverifikasi. Silakan login.",
                }));
            }

            var issue = result.Issue!;
            await emailSender.SendMemberVerificationOtpAsync(
                issue.Email,
                issue.RawOtp,
                15,
                cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                message = "Kode OTP telah dikirim ke email Anda.",
                otpSessionId = issue.OtpSessionId,
                expiresAt = issue.ExpiresAt,
            }));
        }
        catch (MemberPublicAuthException exception)
        {
            return MapPublicAuthException(exception);
        }
    }

    [EnableRateLimiting("email-action")]
    [HttpPost("verify-email-otp")]
    public async Task<IActionResult> VerifyEmailOtp(
        [FromBody] MemberVerifyEmailOtpRequest request,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        var otp = request.Otp?.Trim() ?? string.Empty;
        var sessionId = request.OtpSessionId?.Trim() ?? string.Empty;
        var businessSlug = FirstNonBlank(
            Request.Headers["x-tenant-slug"].FirstOrDefault(),
            Request.Headers["x-business-slug"].FirstOrDefault(),
            request.BusinessSlug);
        var deviceId = ResolveDeviceId();

        if (!new EmailAddressAttribute().IsValid(email) ||
            otp.Length != 6 ||
            !otp.All(char.IsDigit) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(deviceId))
        {
            return BadRequest(ApiResponse<object>.Fail(
                "VALIDATION_ERROR",
                string.IsNullOrWhiteSpace(deviceId)
                    ? "Device ID is required"
                    : "Invalid OTP payload"));
        }

        try
        {
            var result = await memberAuth.VerifyEmailVerificationOtpAsync(
                email,
                otp,
                sessionId,
                businessSlug,
                deviceId,
                cancellationToken);

            return result switch
            {
                MemberEmailOtpVerifyResult.Success =>
                    Ok(ApiResponse<object>.Ok(new { message = "Email berhasil diverifikasi!" })),
                MemberEmailOtpVerifyResult.NotFound =>
                    NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Session OTP tidak ditemukan.")),
                MemberEmailOtpVerifyResult.AlreadyUsed =>
                    Conflict(ApiResponse<object>.Fail("CONFLICT", "OTP sudah digunakan.")),
                MemberEmailOtpVerifyResult.Expired =>
                    BadRequest(ApiResponse<object>.Fail(
                        "VALIDATION_ERROR",
                        "OTP sudah kedaluwarsa. Silakan minta kode baru.")),
                _ =>
                    BadRequest(ApiResponse<object>.Fail(
                        "VALIDATION_ERROR",
                        "OTP tidak valid.")),
            };
        }
        catch (MemberPublicAuthException exception)
        {
            return MapPublicAuthException(exception);
        }
    }

    [EnableRateLimiting("email-action")]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] MemberForgotPasswordRequest request,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!new EmailAddressAttribute().IsValid(email))
        {
            return BadRequest(ApiResponse<object>.Fail(
                "VALIDATION_ERROR",
                "Invalid payload"));
        }

        try
        {
            var issue = await memberAuth.CreatePasswordResetAsync(
                email,
                request.BusinessId,
                request.BusinessSlug,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            if (issue is not null)
            {
                await emailSender.SendMemberPasswordResetAsync(
                    issue.Email,
                    BuildResetUrl(issue.BusinessSlug, issue.RawToken),
                    cancellationToken);
            }

            return Ok(ApiResponse<object>.Ok(new { message = GenericForgotMessage }));
        }
        catch (MemberPublicAuthException exception)
        {
            return MapPublicAuthException(exception);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Member forgot-password flow failed.");
            return Ok(ApiResponse<object>.Ok(new { message = GenericForgotMessage }));
        }
    }

    [EnableRateLimiting("email-action")]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(
        [FromBody] MemberResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            request.Password.Length is < 8 or > 128)
        {
            return BadRequest(ApiResponse<object>.Fail(
                "VALIDATION_ERROR",
                "Token dan password baru 8-128 karakter wajib diisi."));
        }

        var result = await memberAuth.ResetPasswordAsync(
            request.Token,
            request.Password,
            ResolveTenantSlug(),
            cancellationToken);

        if (result == MemberPasswordResetResult.InvalidOrExpired)
        {
            return BadRequest(ApiResponse<object>.Fail(
                "FORBIDDEN",
                "Token tidak valid atau sudah kadaluarsa"));
        }

        return Ok(ApiResponse<object>.Ok(new
        {
            message = "Password berhasil diperbarui. Silakan login kembali.",
        }));
    }

    private IActionResult MapPublicAuthException(MemberPublicAuthException exception) =>
        exception.Code switch
        {
            MemberPublicAuthErrorCode.BusinessContextRequired or
            MemberPublicAuthErrorCode.BusinessNotFound =>
                BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", exception.Message)),
            MemberPublicAuthErrorCode.BusinessInactive =>
                StatusCode(StatusCodes.Status403Forbidden,
                    ApiResponse<object>.Fail("FORBIDDEN", exception.Message)),
            MemberPublicAuthErrorCode.MemberNotFound =>
                NotFound(ApiResponse<object>.Fail("NOT_FOUND", exception.Message)),
            MemberPublicAuthErrorCode.TooManyRequests =>
                StatusCode(StatusCodes.Status429TooManyRequests,
                    ApiResponse<object>.Fail("TOO_MANY_REQUESTS", exception.Message)),
            MemberPublicAuthErrorCode.Conflict or
            MemberPublicAuthErrorCode.CardNotReady or
            MemberPublicAuthErrorCode.MemberLimitReached =>
                Conflict(ApiResponse<object>.Fail("CONFLICT", exception.Message)),
            _ => StatusCode(StatusCodes.Status500InternalServerError,
                ApiResponse<object>.Fail("INTERNAL_ERROR", "Internal server error")),
        };

    private string BuildResetUrl(string slug, string rawToken)
    {
        var configured = configuration["EMAIL_CONFIRM_BASE_DOMAIN"]?.Trim();
        var baseDomain = string.IsNullOrWhiteSpace(configured) ? "dualangka.com" : configured;
        if (baseDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) baseDomain = baseDomain[8..];
        if (baseDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) baseDomain = baseDomain[7..];
        baseDomain = baseDomain.TrimEnd('/');

        return $"https://{slug}.{baseDomain}/member/reset-password?token={Uri.EscapeDataString(rawToken)}";
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
        if (string.IsNullOrWhiteSpace(baseDomain))
            return null;

        if (baseDomain.StartsWith("https://")) baseDomain = baseDomain[8..];
        if (baseDomain.StartsWith("http://")) baseDomain = baseDomain[7..];
        baseDomain = baseDomain.TrimEnd('/');

        return host.EndsWith($".{baseDomain}", StringComparison.OrdinalIgnoreCase)
            ? host[..^(baseDomain.Length + 1)].Split('.')[0]
            : null;
    }

    private string? ResolveDeviceId() =>
        FirstNonBlank(
            Request.Headers["x-device-id"].FirstOrDefault(),
            Request.Headers["x-device-fingerprint"].FirstOrDefault(),
            Request.Headers["x-device"].FirstOrDefault())?.Trim();

    private static string? NormalizeDateOfBirth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var formats = new[] { "yyyy-MM-dd", "dd-MM-yyyy" };
        return DateOnly.TryParseExact(
            value.Trim(),
            formats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var parsed)
            ? parsed.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
}

public sealed class MemberRegisterRequest
{
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string? DateOfBirth { get; init; }
    public string? BusinessId { get; init; }
    public string? BusinessSlug { get; init; }
}

public sealed class MemberForgotPasswordRequest
{
    public string? Email { get; init; }
    public string? BusinessId { get; init; }
    public string? BusinessSlug { get; init; }
}

public sealed class MemberResetPasswordRequest
{
    public string? Token { get; init; }
    public string? Password { get; init; }
}

public sealed class MemberEmailOtpRequest
{
    public string? Email { get; init; }
    public string? BusinessSlug { get; init; }
}

public sealed class MemberVerifyEmailOtpRequest
{
    public string? Email { get; init; }
    public string? Otp { get; init; }
    public string? OtpSessionId { get; init; }
    public string? BusinessSlug { get; init; }
}
