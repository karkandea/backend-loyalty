using System.Net.Http.Json;
using BackendLoyalty.Application.Members;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackendLoyalty.Infrastructure.Members;

public sealed class WahaWhatsAppOtpSender(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<WahaWhatsAppOtpSender> logger) : IWhatsAppOtpSender
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(configuration["WAHA_BASE_URL"]);

    public async Task SendOtpAsync(
        string phoneE164,
        string otp,
        int expiresMinutes,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = configuration["WAHA_BASE_URL"]?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.ProviderUnavailable,
                "WhatsApp OTP provider is not configured.");
        }

        var sessionBase = configuration["WAHA_SESSION"]?.Trim();
        if (string.IsNullOrWhiteSpace(sessionBase))
            sessionBase = "default";

        var instanceId = configuration["WAHA_SESSION_INSTANCE_ID"]?.Trim();
        var session = string.IsNullOrWhiteSpace(instanceId)
            ? sessionBase
            : $"{sessionBase}-{instanceId}";

        var endpoint = baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/sendText"
            : $"{baseUrl}/api/sendText";

        var digits = new string(phoneE164.Where(char.IsDigit).ToArray());
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                session,
                chatId = $"{digits}@c.us",
                text = $"Kode OTP Anda: {otp}. Berlaku {expiresMinutes} menit.",
            }),
        };

        var apiKey = configuration["WAHA_API_KEY"]?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(exception, "WAHA sendText request failed.");
            throw new MemberPhoneOtpException(
                MemberPhoneOtpErrorCode.ProviderUnavailable,
                "WhatsApp OTP provider is unavailable.");
        }

        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogError(
            "WAHA sendText failed with HTTP {StatusCode}: {Body}",
            (int)response.StatusCode,
            body.Length > 1000 ? body[..1000] : body);

        throw new MemberPhoneOtpException(
            MemberPhoneOtpErrorCode.ProviderUnavailable,
            "WhatsApp OTP provider rejected the message.");
    }
}
