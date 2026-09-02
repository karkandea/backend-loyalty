using BackendLoyalty.Api.Contracts;
using BackendLoyalty.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackendLoyalty.Api.Controllers;

[ApiController]
[Route("api/business/auth")]
public sealed class BusinessSignupController(
    IBusinessInvitationService invitations,
    ITransactionalEmailSender emailSender,
    IConfiguration configuration,
    ILogger<BusinessSignupController> logger) : ControllerBase
{
    private const string GenericResendMessage =
        "If the signup is still pending, you’ll receive a new verification link within a few minutes.";

    [AllowAnonymous]
    [HttpPost("signup")]
    public async Task<IActionResult> Signup(
        [FromBody] OwnerSignupApiRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var maxBusinesses = int.TryParse(
                configuration["OWNER_MAX_BUSINESSES"] ?? Environment.GetEnvironmentVariable("OWNER_MAX_BUSINESSES"),
                out var configuredMax)
                ? configuredMax
                : 10;

            var outcome = await invitations.SignupOwnerAsync(
                new OwnerSignupRequest(
                    request.Email,
                    request.FullName,
                    request.BusinessName,
                    request.Slug,
                    maxBusinesses),
                cancellationToken);

            if (outcome.Status == OwnerSignupStatus.SlugTaken)
                return Conflict(ApiResponse<object>.Fail("CONFLICT", "Slug is already taken"));

            if (outcome.Status == OwnerSignupStatus.BusinessLimitReached)
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    ApiResponse<object>.Fail("VALIDATION_ERROR", "Batas maksimal bisnis untuk akun ini sudah tercapai."));

            if (outcome.Status == OwnerSignupStatus.DataConflict)
                return Conflict(ApiResponse<object>.Fail(
                    "CONFLICT",
                    "Data akun tidak konsisten. Silakan hubungi support."));

            if (outcome.Invitation is not null)
                await SendInvitationAsync(outcome.Invitation, cancellationToken);

            return Ok(ApiResponse<object>.Ok(new
            {
                businessId = outcome.BusinessId,
                slug = outcome.Slug,
                requiresEmailConfirmation = outcome.RequiresEmailConfirmation,
                reason = outcome.RequiresEmailConfirmation ? "UNVERIFIED" : null,
            }));
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Standalone owner signup hit a database conflict.");
            return Conflict(ApiResponse<object>.Fail("CONFLICT", "Signup conflict. Please retry."));
        }
    }

    [AllowAnonymous]
    [HttpPost("resend-verification")]
    public async Task<IActionResult> ResendVerification(
        [FromBody] ResendOwnerVerificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var issue = await invitations.ReissuePendingOwnerInvitationAsync(
                request.Email,
                request.Slug,
                cancellationToken);
            if (issue is not null)
                await SendInvitationAsync(issue, cancellationToken);
        }
        catch (Exception exception)
        {
            // Preserve anti-enumeration behavior for pending signup verification.
            logger.LogError(exception, "Standalone owner verification resend failed.");
        }

        return Ok(ApiResponse<object>.Ok(new { message = GenericResendMessage }));
    }

    private Task<bool> SendInvitationAsync(
        InvitationIssue issue,
        CancellationToken cancellationToken) =>
        emailSender.SendBusinessInvitationAsync(
            issue.Email,
            issue.BusinessName,
            issue.Role,
            BuildInvitationUrl(issue.RawToken),
            cancellationToken);

    private string BuildInvitationUrl(string rawToken)
    {
        var configured = configuration["EMAIL_CONFIRM_BASE_DOMAIN"]?.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(configured)
            ? "https://dualangka.com"
            : configured.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              configured.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? configured
                : $"https://{configured}";

        return $"{baseUrl.TrimEnd('/')}/cms/invite/accept?token={Uri.EscapeDataString(rawToken)}";
    }
}
