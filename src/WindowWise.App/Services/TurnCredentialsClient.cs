using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SIPSorcery.Net;


namespace WindowWise.Services;

/// <summary>Keep UI error codes separate from issuance-server and Cloudflare HTTP statuses. Exclude response bodies.</summary>
public sealed class TurnCredentialsException(string code, int? httpStatus = null, int? upstreamStatus = null)
    : Exception(code + (httpStatus is null ? "" : $" (HTTP {httpStatus}" +
        (upstreamStatus is null ? "" : $", Cloudflare {upstreamStatus}") + ")"))
{
    public string Code { get; } = code;
}

/// <summary>
/// ICE configuration and expiry for the channel. Secrets contains values to redact from diagnostics; never log
/// them.
/// </summary>
public sealed record TurnCredentialsResult(RTCConfiguration Configuration, string[] Secrets, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// Request temporary TURN credentials using the signed-in user's access token.
/// Fetch on every connection or environment check. Do not reuse credentials or refresh an active channel's
/// expiry.
/// </summary>
public sealed class TurnCredentialsClient
{
    // Disable automatic redirects on requests that carry the user's access token.
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _client;

    public TurnCredentialsClient(HttpClient? client = null) => _client = client ?? DefaultClient;

    /// <summary>
    /// When no endpoint is supplied, derive an HTTP(S) /turn-credentials URL from the WS(S) signaling address.
    /// Append the default path to a bare host, while preserving explicitly supplied custom API paths.
    /// </summary>
    public static Uri ResolveEndpoint(string explicitUrl, string signalingUrl)
    {
        Uri endpoint;
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            if (!Uri.TryCreate(explicitUrl.Trim(), UriKind.Absolute, out endpoint!))
                throw new TurnCredentialsException("turn-invalid-endpoint");
        }
        else
        {
            if (!Uri.TryCreate(signalingUrl.Trim(), UriKind.Absolute, out var signaling) ||
                signaling.Scheme is not ("ws" or "wss"))
                throw new TurnCredentialsException("turn-invalid-endpoint");
            endpoint = new UriBuilder(signaling)
            {
                Scheme = signaling.Scheme == "wss" ? "https" : "http",
                Path = "/turn-credentials", Query = "", Fragment = ""
            }.Uri;
        }
        if (endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0)
            throw new TurnCredentialsException("turn-invalid-endpoint");
        if (endpoint.AbsolutePath == "/")
            endpoint = new UriBuilder(endpoint) { Path = "/turn-credentials" }.Uri;
        // Access keys and temporary passwords cross this connection, so disallow external plain HTTP endpoints.
        if (endpoint.Scheme != "https" && !endpoint.IsLoopback)
            throw new TurnCredentialsException("turn-https-required");
        return endpoint;
    }

    // Apply the caller's cancellation token to both the HTTP request and JSON read so disconnect also cancels
    // issuance.
    public async Task<TurnCredentialsResult> FetchAsync(Uri endpoint, string accessToken, bool relayOnly,
        CancellationToken cancellationToken)
    {
        endpoint = ResolveEndpoint(endpoint.AbsoluteUri, "");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new TurnCredentialsException("turn-sign-in-required");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Tunnel failures may return HTML instead of JSON. Classify them by HTTP status without
                // displaying the body.
                FailurePayload? failure = null;
                try { failure = await response.Content.ReadFromJsonAsync<FailurePayload>(cancellationToken); }
                catch (JsonException) { }
                catch (NotSupportedException) { }
                // Use only allowlisted server error codes as UI translation keys, never arbitrary response
                // strings.
                string code = failure?.Code is "turn-https-required" or "turn-not-configured" or
                    "turn-cloudflare-auth" or "turn-cloudflare-key-not-found" or "turn-cloudflare-rate-limited" or
                    "turn-cloudflare-invalid-response" or "turn-cloudflare-unreachable" or "turn-cloudflare-timeout" or "turn-upstream-failed"
                    ? failure.Code : response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "turn-unauthorized",
                    HttpStatusCode.ServiceUnavailable => "turn-server-unavailable",
                    HttpStatusCode.TooManyRequests => "turn-rate-limited",
                    HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "turn-endpoint-not-found",
                    HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => "turn-server-unavailable",
                    _ when (int)response.StatusCode is 521 or 522 or 523 or 524 or 530 => "turn-server-unavailable",
                    _ => "turn-upstream-failed"
                };
                throw new TurnCredentialsException(code, (int)response.StatusCode, failure?.UpstreamStatus);
            }
            var payload = await response.Content.ReadFromJsonAsync<Payload>(cancellationToken)
                ?? throw new TurnCredentialsException("turn-invalid-response");
            // Reject credentials that would expire as soon as negotiation begins, before constructing the
            // channel.
            if (payload.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow.AddSeconds(10))
                throw new TurnCredentialsException("turn-expired");
            var servers = new List<RTCIceServer>();
            var secrets = new List<string> { accessToken };
            foreach (var server in payload.IceServers ?? [])
            {
                // STUN integration belongs to a separate implementation. Ignore STUN entries even when
                // Cloudflare returns them.
                string[] urls = (server.Urls ?? []).Where(url => url is not null &&
                    (url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))).ToArray();
                if (urls.Length == 0) continue;
                // URLs within one response entry share credentials. Keep credentials from different entries
                // separate.
                var config = IceServerSettings.Create(true, string.Join('\n', urls),
                    server.Username ?? "", server.Credential ?? "", relayOnly);
                servers.AddRange(config.iceServers);
                secrets.Add(server.Username!);
                secrets.Add(server.Credential!);
            }
            if (servers.Count == 0) throw new TurnCredentialsException("turn-invalid-response");
            return new(new RTCConfiguration
            {
                iceServers = servers,
                iceTransportPolicy = relayOnly ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all
            }, secrets.ToArray(), payload.ExpiresAtUtc);
        }
        catch (JsonException) { throw new TurnCredentialsException("turn-invalid-response"); }
        catch (ArgumentException) { throw new TurnCredentialsException("turn-invalid-response"); }
        catch (HttpRequestException) { throw new TurnCredentialsException("turn-unreachable"); }
    }

    public sealed record Payload(Server[]? IceServers, DateTimeOffset? ExpiresAtUtc);
    public sealed record FailurePayload(string? Code, int? UpstreamStatus);
    public sealed record Server(string[]? Urls, string? Username, string? Credential);
}
