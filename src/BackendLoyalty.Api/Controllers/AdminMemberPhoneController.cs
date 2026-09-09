using BackendLoyalty.Api.Auth;
using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Members;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/member")]
public sealed class AdminMemberPhoneController(
    IMemberPhoneOtpService phoneOtp) : ControllerBase
{
    [HttpPatch("{memberId}/phone")]
    public async Task<IActionResult> UpdatePhone(
        string memberId,
        [FromBody] AdminMemberPhoneRequest request,
        CancellationToken cancellationToken)
    {
        var userId = LoyaltyClaims.UserId(User);
        var authKind = LoyaltyClaims.AuthKind(User);
        var businessId = LoyaltyClaims.BusinessId(User);
        var role = LoyaltyClaims.Role(User)?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(userId)
            || string.IsNullOrWhiteSpace(businessId)
            || authKind != "business")
        {
            return Unauthorized(ApiResponse<object>.Fail("UNAUTHORIZED", "Unauthorized"));
        }

        if (role is not ("owner" or "admin"))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                ApiResponse<object>.Fail("FORBIDDEN", "Insufficient permissions."));
        }

        var deviceId = ResolveDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Device ID is required"));

        if (string.IsNullOrWhiteSpace(request.Phone))
            return BadRequest(ApiResponse<object>.Fail("VALIDATION_ERROR", "Phone is required"));

        try
        {
            if (string.IsNullOrWhiteSpace(request.Otp))
            {
                var issue = await phoneOtp.SendAdminMemberPhoneOtpAsync(
                    memberId,
                    businessId,
                    request.Phone,
                    deviceId,
                    GetClientIp(),
                    Request.Headers.UserAgent.ToString(),
                    cancellationToken);

                if (issue is null)
                {
                    return Ok(ApiResponse<object>.Ok(new
                    {
                        updated = true,
                        message = "Nomor HP sudah sesuai.",
                    }));
                }

                return Ok(ApiResponse<object>.Ok(new
                {
                    otpSessionId = issue.OtpSessionId,
                    expiresAt = issue.ExpiresAt,
                }));
            }

            if (string.IsNullOrWhiteSpace(request.OtpSessionId))
            {
                return BadRequest(ApiResponse<object>.Fail(
                    "VALIDATION_ERROR",
                    "otpSessionId is required"));
            }

            await phoneOtp.VerifyAdminMemberPhoneOtpAsync(
                memberId,
                businessId,
                request.Phone,
                request.Otp,
                request.OtpSessionId,
                deviceId,
                GetClientIp(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                updated = true,
                message = "Nomor HP berhasil diperbarui.",
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
            MemberPhoneOtpErrorCode.NotFound or
            MemberPhoneOtpErrorCode.BusinessNotFound =>
                NotFound(ApiResponse<object>.Fail("NOT_FOUND", exception.Message)),
            MemberPhoneOtpErrorCode.BusinessInactive or
            MemberPhoneOtpErrorCode.FeatureDisabled =>
                StatusCode(
                    StatusCodes.Status403Forbidden,
                    ApiResponse<object>.Fail("FORBIDDEN", exception.Message)),
            MemberPhoneOtpErrorCode.Conflict or
            MemberPhoneOtpErrorCode.DeviceMismatch or
            MemberPhoneOtpErrorCode.AlreadyUsed =>
                Conflict(ApiResponse<object>.Fail("CONFLICT", exception.Message)),
            MemberPhoneOtpErrorCode.TooManyRequests =>
                StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    ApiResponse<object>.Fail("TOO_MANY_REQUESTS", exception.Message)),
            MemberPhoneOtpErrorCode.ProviderUnavailable =>
                StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    ApiResponse<object>.Fail("PROVIDER_UNAVAILABLE", exception.Message)),
            _ => StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<object>.Fail("INTERNAL_ERROR", "Internal server error")),
        };
    }

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

public sealed class AdminMemberPhoneRequest
{
    public string? Phone { get; init; }
    public string? Otp { get; init; }
    public string? OtpSessionId { get; init; }
}
