using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BackendLoyalty.Application.Auth;
using Microsoft.Extensions.Logging;

namespace BackendLoyalty.Infrastructure.Auth;

public sealed class ResendTransactionalEmailSender(
    HttpClient httpClient,
    ILogger<ResendTransactionalEmailSender> logger) : ITransactionalEmailSender
{
    private const string ResendApiUrl = "https://api.resend.com/emails";

    public Task<bool> SendPasswordResetAsync(
        string recipient,
        string resetUrl,
        CancellationToken cancellationToken)
    {
        var safeUrl = WebUtility.HtmlEncode(resetUrl);
        return SendAsync(
            recipient,
            "Dualangka – Reset Password",
            $"<p>Halo,</p><p>Kami menerima permintaan reset kata sandi untuk akun owner/admin bisnis Anda.</p><p><a href=\"{safeUrl}\">Klik di sini untuk reset kata sandi</a></p><p>Jika Anda tidak meminta ini, abaikan email ini.</p>",
            $"Halo,\n\nKami menerima permintaan reset kata sandi untuk akun owner/admin bisnis Anda.\nReset link: {resetUrl}\n\nJika Anda tidak meminta ini, abaikan email ini.",
            "password-reset",
            cancellationToken);
    }

    public Task<bool> SendBusinessInvitationAsync(
        string recipient,
        string businessName,
        string role,
        string invitationUrl,
        CancellationToken cancellationToken)
    {
        var safeBusiness = WebUtility.HtmlEncode(businessName);
        var safeRole = WebUtility.HtmlEncode(role.ToLowerInvariant());
        var safeUrl = WebUtility.HtmlEncode(invitationUrl);
        var isOwner = string.Equals(role, "OWNER", StringComparison.OrdinalIgnoreCase);
        var subject = isOwner
            ? $"Confirm your Dualangka business – {businessName}"
            : $"You’ve been invited to join {businessName}";
        var intro = isOwner
            ? $"Complete your account setup for <strong>{safeBusiness}</strong>."
            : $"You’ve been invited to join <strong>{safeBusiness}</strong> as {safeRole}.";
        var textIntro = isOwner
            ? $"Complete your account setup for {businessName}."
            : $"You've been invited to join {businessName} as {role.ToLowerInvariant()}.";

        return SendAsync(
            recipient,
            subject,
            $"<p>{intro}</p><p><a href=\"{safeUrl}\">Continue</a></p><p>This link expires in 7 days. If you did not expect this email, you can ignore it.</p>",
            $"{textIntro}\n\nContinue: {invitationUrl}\n\nThis link expires in 7 days. If you did not expect this email, ignore it.",
            "business-invitation",
            cancellationToken);
    }

    private async Task<bool> SendAsync(
        string recipient,
        string subject,
        string html,
        string text,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (recipient.EndsWith("@example.invalid", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Skipping {Purpose} email for reserved smoke-test domain.", purpose);
            return false;
        }

        var apiKey = Environment.GetEnvironmentVariable("RESEND_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("RESEND_API_KEY is missing; {Purpose} email was not sent.", purpose);
            return false;
        }

        var from = Environment.GetEnvironmentVariable("RESEND_FROM")?.Trim();
        if (string.IsNullOrWhiteSpace(from))
            from = "Dualangka <noreply@dualangka.com>";

        using var request = new HttpRequestMessage(HttpMethod.Post, ResendApiUrl)
        {
            Content = JsonContent.Create(new { from, to = recipient, subject, html, text }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return true;

            logger.LogWarning(
                "Resend rejected {Purpose} email with status {StatusCode}.",
                purpose,
                (int)response.StatusCode);
            return false;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "{Purpose} email request failed.", purpose);
            return false;
        }
    }
}
