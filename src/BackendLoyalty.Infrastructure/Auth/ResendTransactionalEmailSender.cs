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

    public async Task<bool> SendPasswordResetAsync(
        string recipient,
        string resetUrl,
        CancellationToken cancellationToken)
    {
        if (recipient.EndsWith("@example.invalid", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Skipping password-reset email for reserved smoke-test domain.");
            return false;
        }

        var apiKey = Environment.GetEnvironmentVariable("RESEND_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("RESEND_API_KEY is missing; password-reset email was not sent.");
            return false;
        }

        var from = Environment.GetEnvironmentVariable("RESEND_FROM")?.Trim();
        if (string.IsNullOrWhiteSpace(from))
            from = "Dualangka <noreply@dualangka.com>";

        var safeUrl = WebUtility.HtmlEncode(resetUrl);
        var payload = new
        {
            from,
            to = recipient,
            subject = "Dualangka – Reset Password",
            html = $"<p>Halo,</p><p>Kami menerima permintaan reset kata sandi untuk akun owner/admin bisnis Anda.</p><p><a href=\"{safeUrl}\">Klik di sini untuk reset kata sandi</a></p><p>Jika Anda tidak meminta ini, abaikan email ini.</p>",
            text = $"Halo,\n\nKami menerima permintaan reset kata sandi untuk akun owner/admin bisnis Anda.\nReset link: {resetUrl}\n\nJika Anda tidak meminta ini, abaikan email ini.",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ResendApiUrl)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return true;

            logger.LogWarning(
                "Resend rejected password-reset email with status {StatusCode}.",
                (int)response.StatusCode);
            return false;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Password-reset email request failed.");
            return false;
        }
    }
}
