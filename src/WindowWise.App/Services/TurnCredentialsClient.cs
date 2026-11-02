using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using SIPSorcery.Net;

namespace WindowWise.Services;

public sealed class TurnCredentialsException(string code, int? httpStatus = null, int? upstreamStatus = null)
    : Exception(code + (httpStatus is null ? "" : $" (HTTP {httpStatus}" +
        (upstreamStatus is null ? "" : $", Cloudflare {upstreamStatus}") + ")"))
{
    public string Code { get; } = code;
}

public sealed record TurnCredentialsResult(RTCConfiguration Configuration, string[] Secrets, DateTimeOffset? ExpiresAtUtc);

public sealed class TurnCredentialsClient
{
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _client;

    public TurnCredentialsClient(HttpClient? client = null) => _client = client ?? DefaultClient;

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
        if (endpoint.Scheme != "https" && !endpoint.IsLoopback)
            throw new TurnCredentialsException("turn-https-required");
        return endpoint;
    }

    public async Task<TurnCredentialsResult> FetchAsync(Uri endpoint, string accessKey, bool relayOnly,
        CancellationToken cancellationToken)
    {
        endpoint = ResolveEndpoint(endpoint.AbsoluteUri, "");
        if (string.IsNullOrWhiteSpace(accessKey)) throw new TurnCredentialsException("turn-access-key-missing");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("X-Turn-Access-Key", accessKey);
        try
        {
            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                FailurePayload? failure = null;
                try { failure = await response.Content.ReadFromJsonAsync<FailurePayload>(cancellationToken); }
                catch (JsonException) { }
                catch (NotSupportedException) { }
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
            if (payload.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow.AddSeconds(10))
                throw new TurnCredentialsException("turn-expired");
            var servers = new List<RTCIceServer>();
            var secrets = new List<string> { accessKey };
            foreach (var server in payload.IceServers ?? [])
            {
                // STUN is intentionally still UI-only; use only TURN entries from the response.
                string[] urls = (server.Urls ?? []).Where(url => url is not null &&
                    (url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))).ToArray();
                if (urls.Length == 0) continue;
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
