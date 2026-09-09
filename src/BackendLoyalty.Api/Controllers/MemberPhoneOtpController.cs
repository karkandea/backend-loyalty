using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
public sealed class MemberPhoneOtpController(
    IMemberPhoneOtpService phoneOtp,
    IMemberSessionResolver memberSessionResolver) : ControllerBase
{
    private const string MemberSessionCookie = "member_session";
    private const string MemberResetCookie = "member_reset_token";

    [HttpGet("api/member/auth/phone-capability")]
    public async Task<IActionResult> GetWhatsAppCapability(
        [FromQuery] string? businessId,
        [FromQuery] string? businessSlug,
        CancellationToken cancellationToken)
    {
        try
        {
            var enabled = await phoneOtp.IsWhatsAppRegistrationEnabledAsync(
                ResolveBusinessId(businessId),
                ResolveBusinessSlug(businessSlug),
                cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                whatsappRegisterEnabled = enabled,
            }));
        }
        catch (MemberPhoneOtpException exception)
        {
            return MapException(exception);
        }
    }

    [HttpPost("api/otp/send")]
    public async Task<IActionResult> SendPublicOtp(
        [FromBody] MemberPhoneOtpSendRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = ResolveDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Device ID is required"));

        var purpose = (request.Purpose ?? "signup").Trim().ToLowerInvariant() switch
        {
            "signup" => MemberPhoneOtpPurpose.Signup,
            "forgot-password" => MemberPhoneOtpPurpose.ForgotPassword,
            _ => (MemberPhoneOtpPurpose?)null,
        };

        if (purpose is null || string.IsNullOrWhiteSpace(request.Phone))
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid OTP payload"));

        try
        {
            var issue = await phoneOtp.SendPublicOtpAsync(
                request.Phone,
                ResolveBusinessId(request.BusinessId),
                ResolveBusinessSlug(request.BusinessSlug),
                purpose.Value,
                deviceId,
                GetClientIp(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                otpSessionId = issue.OtpSessionId,
                expiresAt = issue.ExpiresAt,
            }));
        }
        catch (MemberPhoneOtpException exception)
        {
            return MapException(exception);
        }
    }

    [HttpPost("api/otp/verify")]
    public async Task<IActionResult> VerifySignupOtp(
        [FromBody] MemberPhoneOtpVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = ResolveDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Device ID is required"));

        if (string.IsNullOrWhiteSpace(request.Phone)
            || string.IsNullOrWhiteSpace(request.Otp)
            || string.IsNullOrWhiteSpace(request.OtpSessionId))
        {
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid OTP payload"));
        }

        try
        {
            var verification = await phoneOtp.VerifySignupOtpAsync(
                request.Phone,
                request.Otp,
                request.OtpSessionId,
                ResolveBusinessId(request.BusinessId),
                ResolveBusinessSlug(request.BusinessSlug),
                deviceId,
                GetClientIp(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                verificationToken = verification.VerificationToken,
                expiresAt = verification.ExpiresAt,
            }));
        }
        catch (MemberPhoneOtpException exception)
        {
            return MapException(exception);
        }
    }

    [HttpPost("api/member/auth/forgot-password/verify-otp")]
    public async Task<IActionResult> VerifyForgotPasswordOtp(
        [FromBody] MemberPhoneOtpVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = ResolveDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Device ID is required"));

        if (string.IsNullOrWhiteSpace(request.Phone)
            || string.IsNullOrWhiteSpace(request.Otp)
            || string.IsNullOrWhiteSpace(request.OtpSessionId))
        {
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid OTP payload"));
        }

        try
        {
            var reset = await phoneOtp.VerifyForgotPasswordOtpAsync(
                request.Phone,
                request.Otp,
                request.OtpSessionId,
                ResolveBusinessId(request.BusinessId),
                ResolveBusinessSlug(request.BusinessSlug),
                deviceId,
                GetClientIp(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            Response.Cookies.Append(
                MemberResetCookie,
                reset.RawResetToken,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    Expires = reset.ExpiresAt,
                    MaxAge = TimeSpan.FromMinutes(15),
                    IsEssential = true,
                });

            return Ok(ApiResponse<object>.Ok(new
            {
                message = "OTP verified. Redirecting...",
                redirectUrl = "/member/reset-password",
            }));
        }
        catch (MemberPhoneOtpException exception)
        {
            return MapException(exception);
        }
    }

    [HttpPost("api/member/identity/phone/send")]
    public async Task<IActionResult> SendProfilePhoneOtp(
        [FromBody] MemberProfilePhoneOtpSendRequest request,
        CancellationToken cancellationToken)
    {
        var session = await memberSessionResolver.ResolveAsync(
            Request.Cookies[MemberSessionCookie],
            cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var deviceId = ResolveDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Device ID is required"));

        if (string.IsNullOrWhiteSpace(request.Phone))
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Phone is required"));

        try
        {
            var issue = await phoneOtp.SendProfilePhoneOtpAsync(
                session.MemberId,
                session.BusinessId,
                request.Phone,
                deviceId,
                GetClientIp(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                otpSessionId = issue.OtpSessionId,
                expiresAt = issue.ExpiresAt,
            }));
        }
        catch (MemberPhoneOtpException exception)
        {
            return MapException(exception);
        }
    }

    [HttpPost("api/member/identity/phone/verify")]
    public async Task<IActionResult> VerifyProfilePhoneOtp(
        [FromBody] MemberProfilePhoneOtpVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var session = await memberSessionResolver.ResolveAsync(
            Request.Cookies[MemberSessionCookie],
            cancellationToken);
        if (session is null)
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));

        var deviceId = ResolveDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Device ID is required"));

        if (string.IsNullOrWhiteSpace(request.Phone)
            || string.IsNullOrWhiteSpace(request.Otp)
            || string.IsNullOrWhiteSpace(request.OtpSessionId))
        {
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Invalid OTP payload"));
        }

        try
        {
            await phoneOtp.VerifyProfilePhoneOtpAsync(
                session.MemberId,
                session.BusinessId,
                request.Phone,
                request.Otp,
                request.OtpSessionId,
                deviceId,
                GetClientIp(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                phone = request.Phone,
                verified = true,
                message = "Nomor HP berhasil diverifikasi.",
            }));
        }
        catch (MemberPhoneOtpException exception)
        {
            return MapException(exception);
        }
    }

    private IActionResult MapException(MemberPhoneOtpException exception)
    {
        if (exception.RetryAfterSeconds is > 0)
            Response.Headers["Retry-After"] = exception.RetryAfterSeconds.Value.ToString();

        return exception.Code switch
        {
            MemberPhoneOtpErrorCode.Validation or
            MemberPhoneOtpErrorCode.InvalidOtp or
            MemberPhoneOtpErrorCode.Expired =>
                BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", exception.Message)),
            MemberPhoneOtpErrorCode.BusinessNotFound or
            MemberPhoneOtpErrorCode.NotFound =>
                NotFound(ApiResponse<object>.Fail("NOT_FOUND", exception.Message)),
            MemberPhoneOtpErrorCode.BusinessInactive or
            MemberPhoneOtpErrorCode.FeatureDisabled =>
                StatusCode(StatusCodes.Status403Forbidden,
                    ApiResponse<object>.Fail("FORBIDDEN", exception.Message)),
            MemberPhoneOtpErrorCode.Conflict or
            MemberPhoneOtpErrorCode.DeviceMismatch or
            MemberPhoneOtpErrorCode.AlreadyUsed =>
                Conflict(ApiResponse<object>.Fail("CONFLICT", exception.Message)),
            MemberPhoneOtpErrorCode.TooManyRequests =>
                StatusCode(StatusCodes.Status429TooManyRequests,
                    ApiResponse<object>.Fail("TOO_MANY_REQUESTS", exception.Message)),
            MemberPhoneOtpErrorCode.ProviderUnavailable =>
                StatusCode(StatusCodes.Status503ServiceUnavailable,
                    ApiResponse<object>.Fail("PROVIDER_UNAVAILABLE", exception.Message)),
            _ => StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<object>.Fail("INTERNAL_ERROR", "Internal server error")),
        };
    }

    private string? ResolveBusinessId(string? bodyValue) =>
        FirstNonBlank(
            Request.Headers["x-tenant-business-id"].FirstOrDefault(),
            Request.Headers["x-business-id"].FirstOrDefault(),
            bodyValue);

    private string? ResolveBusinessSlug(string? bodyValue) =>
        FirstNonBlank(
            Request.Headers["x-tenant-slug"].FirstOrDefault(),
            Request.Headers["x-business-slug"].FirstOrDefault(),
            bodyValue)?.Trim().ToLowerInvariant();

    private string? ResolveDeviceId() =>
        FirstNonBlank(
            Request.Headers["x-device-id"].FirstOrDefault(),
            Request.Headers["x-device-fingerprint"].FirstOrDefault(),
            Request.Headers["x-device"].FirstOrDefault())?.Trim();

    private string? GetClientIp()
    {
        var forwarded = Request.Headers["X-Forwarded-For"].ToString();
        var firstForwarded = forwarded
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstForwarded))
            return firstForwarded;

        var realIp = Request.Headers["X-Real-IP"].FirstOrDefault();
        return !string.IsNullOrWhiteSpace(realIp)
            ? realIp
            : HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

public sealed class MemberPhoneOtpSendRequest
{
    public string? Phone { get; init; }
    public string? BusinessId { get; init; }
    public string? BusinessSlug { get; init; }
    public string? Purpose { get; init; }
}

public sealed class MemberPhoneOtpVerifyRequest
{
    public string? Phone { get; init; }
    public string? Otp { get; init; }
    public string? OtpSessionId { get; init; }
    public string? BusinessId { get; init; }
    public string? BusinessSlug { get; init; }
}

public sealed class MemberProfilePhoneOtpSendRequest
{
    public string? Phone { get; init; }
}

public sealed class MemberProfilePhoneOtpVerifyRequest
{
    public string? Phone { get; init; }
    public string? Otp { get; init; }
    public string? OtpSessionId { get; init; }
}
