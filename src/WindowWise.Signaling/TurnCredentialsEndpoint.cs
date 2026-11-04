using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;

namespace WindowWise.Signaling;

/// <summary>
/// Authenticate app requests, then use the server-held Cloudflare API token to issue temporary TURN
/// credentials.
/// Files bypass this API and travel over the direct or TURN-relayed WebRTC DataChannel.
/// </summary>
public static class TurnCredentialsEndpoint


{

    // Cache typed delegates to avoid allocating logging argument arrays.
    private static readonly Action<ILogger, int, string, Exception?>
        LogIssuanceFailed =
            LoggerMessage.Define<int, string>(
                LogLevel.Warning,
                new EventId(1, "TurnIssuanceFailed"),
                "Cloudflare TURN credential issuance failed: HTTP {Status}; request {RequestId}");

    private static readonly Action<ILogger, string, Exception?>
        LogNetworkFailed =
            LoggerMessage.Define<string>(
                LogLevel.Warning,
                new EventId(2, "TurnNetworkFailed"),
                "Cloudflare TURN network request failed; request {RequestId}");

    // Use a dedicated HTTP client with redirects disabled so the long-lived API token cannot be forwarded to
    // another host.
    public static IServiceCollection AddTurnCredentials(this IServiceCollection services)
    {
        services.AddHttpClient("CloudflareTurn", client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        // This is a global development limit for the issuance endpoint, not a per-user limit or a substitute
        // for production authentication.
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

    // The /signal WebSocket negotiation endpoint and the /turn-credentials HTTP issuance endpoint serve
    // different purposes.
    public static void MapTurnCredentials(this WebApplication app) =>
        app.MapPost("/turn-credentials", IssueAsync).RequireAuthorization().RequireRateLimiting("turn-credentials");

    private static async Task<IResult> IssueAsync(HttpContext context, IConfiguration settings,
        IHttpClientFactory clients, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger("WindowWise.TurnCredentials");
        // Responses contain temporary passwords, so browsers and proxies must not cache them.
        context.Response.Headers.CacheControl = "no-store";
        // Require HTTPS externally. Allow HTTP only for local tests and the local origin of an HTTPS tunnel.
        // A loopback origin does not by itself guarantee that the public side of a tunnel is secure.
        if (!context.Request.IsHttps &&
            (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
            return Results.Json(new { code = "turn-https-required" }, statusCode: 403);

        // keyId/apiToken authenticate the server to Cloudflare; accessKey authenticates test apps to this
        // server.
        // Never return the long-lived Cloudflare API token to apps or include it in logs.
        // TURN_PROVIDER=coturn issues credentials locally from the secret shared with a self-hosted server.
        string? keyId = settings["CLOUDFLARE_TURN_KEY_ID"];
        string? apiToken = settings["CLOUDFLARE_TURN_API_TOKEN"];
        string? accessKey = settings["WINDOWWISE_TURN_ACCESS_KEY"];
        bool useCoturn = string.Equals(settings["TURN_PROVIDER"], "coturn", StringComparison.OrdinalIgnoreCase);
        string? sharedSecret = settings["TURN_SHARED_SECRET"];
        string[] turnUrls = (settings["TURN_URLS"] ?? "").Split([',', '\r', '\n'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(accessKey) || (useCoturn
            ? string.IsNullOrWhiteSpace(sharedSecret) || turnUrls.Length == 0
            : string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(apiToken)))
            return Results.Json(new { code = "turn-not-configured" }, statusCode: 503);

        string suppliedKey = context.Request.Headers["X-Turn-Access-Key"].ToString();
        // Compare equal-length hashes in constant time to reduce timing differences based on access-key
        // contents.
        if (!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(accessKey)),
            SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey))))
            return Results.Unauthorized();

        if (useCoturn) return IssueCoturn(sharedSecret!, turnUrls, ReadTtl(settings["TURN_TTL_SECONDS"]));

        // Calculate expiry before the upstream request to avoid counting network latency as usable credential
        // lifetime.
        int ttl = ReadTtl(settings["CLOUDFLARE_TURN_TTL_SECONDS"]);
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
                // Distinguish this API's HTTP 502 from Cloudflare's 401/404, but discard upstream bodies that
              LogIssuanceFailed(logger, (int)response.StatusCode, context.TraceIdentifier, null);
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
            LogNetworkFailed(logger,  context.TraceIdentifier, null);
            return UpstreamFailure("turn-cloudflare-unreachable");
        }
        catch (JsonException) { return UpstreamFailure("turn-cloudflare-invalid-response"); }
        catch (FormatException) { return Results.Json(new { code = "turn-cloudflare-auth" }, statusCode: 503); }
        // Distinguish an app-request cancellation from an upstream response timeout.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Results.Json(new { code = "turn-cloudflare-timeout" }, statusCode: 504); }
    }

    // Credentials last one hour by default. Active channels do not refresh them, so reconnect before expiry.
    private static int ReadTtl(string? configured) =>
        int.TryParse(configured, out int ttl) ? Math.Clamp(ttl, 60, 86400) : 3600;

    // Follow coturn's use-auth-secret format: username "expiry:name", password base64(HMAC-SHA1(secret, username)).
    // coturn applies user-quota to the name, so each issue gets its own name to keep peers from sharing a quota.
    private static IResult IssueCoturn(string sharedSecret, string[] urls, int ttl)
    {
        DateTimeOffset expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(ttl);
        string name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        string username = $"{expiresAtUtc.ToUnixTimeSeconds()}:windowwise-{name}";
#pragma warning disable CA5350 // The TURN REST format that coturn verifies requires HMAC-SHA1.
        string credential = Convert.ToBase64String(
            HMACSHA1.HashData(Encoding.UTF8.GetBytes(sharedSecret), Encoding.UTF8.GetBytes(username)));
#pragma warning restore CA5350
        return Results.Json(new { iceServers = new[] { new { urls, username, credential } }, expiresAtUtc });
    }

    private static IResult UpstreamFailure(string code) => Results.Json(new { code }, statusCode: 502);
}
