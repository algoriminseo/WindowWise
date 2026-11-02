using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;

namespace WindowWise.Signaling;

public static class TurnCredentialsEndpoint
{
    public static IServiceCollection AddTurnCredentials(this IServiceCollection services)
    {
        services.AddHttpClient("CloudflareTurn", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddFixedWindowLimiter("turn-credentials", limiter =>
            {
                limiter.PermitLimit = 30;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });
        return services;
    }

    public static void MapTurnCredentials(this WebApplication app) =>
        app.MapPost("/turn-credentials", IssueAsync).RequireRateLimiting("turn-credentials");

    private static async Task<IResult> IssueAsync(HttpContext context, IConfiguration settings,
        IHttpClientFactory clients, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger("WindowWise.TurnCredentials");
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps &&
            (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
            return Results.Json(new { code = "turn-https-required" }, statusCode: 403);

        string? keyId = settings["CLOUDFLARE_TURN_KEY_ID"];
        string? apiToken = settings["CLOUDFLARE_TURN_API_TOKEN"];
        string? accessKey = settings["WINDOWWISE_TURN_ACCESS_KEY"];
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(apiToken) ||
            string.IsNullOrWhiteSpace(accessKey))
            return Results.Json(new { code = "turn-not-configured" }, statusCode: 503);

        string suppliedKey = context.Request.Headers["X-Turn-Access-Key"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(accessKey)),
            SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey))))
            return Results.Unauthorized();

        int ttl = int.TryParse(settings["CLOUDFLARE_TURN_TTL_SECONDS"], out int configuredTtl)
            ? Math.Clamp(configuredTtl, 60, 86400) : 3600;
        DateTimeOffset expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(ttl);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://rtc.live.cloudflare.com/v1/turn/keys/{Uri.EscapeDataString(keyId)}/credentials/generate-ice-servers");
        request.Content = JsonContent.Create(new { ttl });
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
            using var client = clients.CreateClient("CloudflareTurn");
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Cloudflare TURN credential issuance failed: HTTP {Status}; request {RequestId}",
                    (int)response.StatusCode, context.TraceIdentifier);
                string code = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "turn-cloudflare-auth",
                    HttpStatusCode.NotFound => "turn-cloudflare-key-not-found",
                    HttpStatusCode.TooManyRequests => "turn-cloudflare-rate-limited",
                    _ => "turn-upstream-failed"
                };
                return Results.Json(new { code, upstreamStatus = (int)response.StatusCode }, statusCode: 502);
            }
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("iceServers", out var servers) || servers.ValueKind != JsonValueKind.Array)
                return UpstreamFailure("turn-cloudflare-invalid-response");
            return Results.Json(new { iceServers = servers, expiresAtUtc });
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Cloudflare TURN network request failed; request {RequestId}", context.TraceIdentifier);
            return UpstreamFailure("turn-cloudflare-unreachable");
        }
        catch (JsonException) { return UpstreamFailure("turn-cloudflare-invalid-response"); }
        catch (FormatException) { return Results.Json(new { code = "turn-cloudflare-auth" }, statusCode: 503); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Results.Json(new { code = "turn-cloudflare-timeout" }, statusCode: 504); }
    }

    private static IResult UpstreamFailure(string code) => Results.Json(new { code }, statusCode: 502);
}
