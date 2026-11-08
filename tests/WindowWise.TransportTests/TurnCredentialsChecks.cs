using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using WindowWise.Services;
using WindowWise.Signaling;

internal static class TurnCredentialsChecks
{
    public static async Task RunAsync(CancellationToken token)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CLOUDFLARE_TURN_KEY_ID"] = "fake-key-id",
            ["CLOUDFLARE_TURN_API_TOKEN"] = "fake-cloudflare-secret"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTurnCredentials();
        AddTestAuthentication(builder.Services);
        using var upstream = new FakeCloudflare();
        builder.Services.AddSingleton<IHttpClientFactory>(new FakeFactory(upstream));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapTurnCredentials();
        await app.StartAsync(token);
        var endpoint = new Uri(app.Urls.Single() + "/turn-credentials");
        using var http = new HttpClient();
        var client = new TurnCredentialsClient(http);
        using (var denied = await http.PostAsync(endpoint, null, token))
            Check(denied.StatusCode == HttpStatusCode.Unauthorized && upstream.Calls == 0,
                "TURN issuance rejects requests without sign-in before calling Cloudflare");
        using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ValidToken);
            using var issued = await http.SendAsync(request, token);
            Check(issued.IsSuccessStatusCode && issued.Headers.CacheControl?.NoStore == true, "TURN responses cannot be cached");
        }
        var result = await client.FetchAsync(endpoint, ValidToken, true, token);
        Check(result.Configuration.iceServers.Count == 2 &&
            result.Configuration.iceTransportPolicy == RTCIceTransportPolicy.relay &&
            result.Configuration.iceServers.All(server => !server.urls.StartsWith("stun:")) &&
            result.Secrets.Contains("fake-password-2"),
            "Production issuance endpoint maps TURN credentials and excludes STUN entries");
        Check(upstream.CorrectRequest, "Cloudflare request uses the server token, key ID and one-hour TTL");
        Check(result.ExpiresAtUtc > DateTimeOffset.UtcNow, "Issued credentials include an expiry");
        var fresh = await client.FetchAsync(endpoint, ValidToken, false, token);
        Check(upstream.Calls == 3 && fresh.Secrets.Contains("fake-password-3") &&
            fresh.Configuration.iceTransportPolicy == RTCIceTransportPolicy.all,
            "Each attempt gets fresh credentials and preserves the selected ICE policy");
        await ExpectCodeAsync("turn-unauthorized", () => client.FetchAsync(endpoint, "wrong", true, token));
        upstream.Status = HttpStatusCode.Unauthorized;
        await ExpectCodeAsync("turn-cloudflare-auth", () => client.FetchAsync(endpoint, ValidToken, true, token));
        upstream.Status = HttpStatusCode.NotFound;
        await ExpectCodeAsync("turn-cloudflare-key-not-found", () => client.FetchAsync(endpoint, ValidToken, true, token));
        upstream.Status = HttpStatusCode.TooManyRequests;
        await ExpectCodeAsync("turn-cloudflare-rate-limited", () => client.FetchAsync(endpoint, ValidToken, true, token));
        upstream.Status = HttpStatusCode.OK;
        upstream.Malformed = true;
        await ExpectCodeAsync("turn-cloudflare-invalid-response", () => client.FetchAsync(endpoint, ValidToken, true, token));
        builder.Configuration["CLOUDFLARE_TURN_API_TOKEN"] = "";
        await ExpectCodeAsync("turn-not-configured", () => client.FetchAsync(endpoint, ValidToken, true, token));
        await ExpectCodeAsync("turn-https-required", () => client.FetchAsync(new Uri("http://example.com/turn-credentials"), "key", true, token));
        Check(TurnCredentialsClient.ResolveEndpoint("", "wss://example.com:7443/signal?deviceId=x").AbsoluteUri ==
            "https://example.com:7443/turn-credentials", "Credential endpoint follows the signaling host and port");
        Check(TurnCredentialsClient.ResolveEndpoint("https://example.com", "").AbsoluteUri ==
            "https://example.com/turn-credentials", "A bare credential host automatically gets the issuance path");
        Check(TurnCredentialsClient.ResolveEndpoint("https://example.com/custom/issue", "").AbsolutePath ==
            "/custom/issue", "Explicit custom issuance paths are preserved");
        using var invalidResponseClient = new HttpClient(new InvalidResponseHandler());
        await ExpectCodeAsync("turn-expired", () => new TurnCredentialsClient(invalidResponseClient)
            .FetchAsync(endpoint, "key", true, token));
        using var offlineTunnelClient = new HttpClient(new OfflineTunnelHandler());
        await ExpectCodeAsync("turn-server-unavailable", () => new TurnCredentialsClient(offlineTunnelClient)
            .FetchAsync(endpoint, "key", true, token));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await client.FetchAsync(endpoint, "key", true, canceled.Token);
            throw new InvalidOperationException("Cancellation was ignored.");
        }
        catch (OperationCanceledException) { Check(true, "Credential fetch respects session cancellation"); }
        await app.StopAsync(token);
        await RunCoturnAsync(token);
    }

    // Issue self-hosted TURN credentials locally, without calling Cloudflare.
    private static async Task RunCoturnAsync(CancellationToken token)
    {
        const string secret = "fake-shared-secret";
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TURN_PROVIDER"] = "coturn",
            ["TURN_SHARED_SECRET"] = secret,
            ["TURN_URLS"] = "turn:turn.example.com:3478?transport=udp, turn:turn.example.com:3478?transport=tcp",
            ["TURN_TTL_SECONDS"] = "600"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTurnCredentials();
        AddTestAuthentication(builder.Services);
        using var upstream = new FakeCloudflare();
        builder.Services.AddSingleton<IHttpClientFactory>(new FakeFactory(upstream));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapTurnCredentials();
        await app.StartAsync(token);
        var endpoint = new Uri(app.Urls.Single() + "/turn-credentials");
        using var http = new HttpClient();
        var client = new TurnCredentialsClient(http);

        await ExpectCodeAsync("turn-unauthorized", () => client.FetchAsync(endpoint, "wrong", true, token));
        var first = await client.FetchAsync(endpoint, ValidToken, true, token);
        var second = await client.FetchAsync(endpoint, ValidToken, false, token);
        var server = first.Configuration.iceServers[0];
        Check(first.Configuration.iceServers.Count == 2 && upstream.Calls == 0 &&
            first.Configuration.iceTransportPolicy == RTCIceTransportPolicy.relay,
            "coturn issuance maps every configured URL without calling Cloudflare");
        Check(first.Configuration.iceServers.All(item => item.username == server.username && item.credential == server.credential),
            "coturn URLs from one issue share credentials");
        string[] parts = server.username.Split(':', 2);
        long expiry = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        long remaining = expiry - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Check(remaining is > 590 and <= 600 && first.ExpiresAtUtc?.ToUnixTimeSeconds() == expiry,
            "coturn username carries the configured expiry");
#pragma warning disable CA5350 // The TURN REST format that coturn verifies requires HMAC-SHA1.
        Check(server.credential == Convert.ToBase64String(System.Security.Cryptography.HMACSHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(server.username))),
            "coturn password is the HMAC-SHA1 of the username");
#pragma warning restore CA5350
        Check(second.Configuration.iceServers[0].username.Split(':', 2)[1] != parts[1],
            "Each coturn issue gets its own quota name");
        using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ValidToken);
            using var response = await http.SendAsync(request, token);
            string body = await response.Content.ReadAsStringAsync(token);
            Check(!body.Contains(secret, StringComparison.Ordinal), "coturn responses never contain the shared secret");
        }
        builder.Configuration["TURN_SHARED_SECRET"] = "";
        await ExpectCodeAsync("turn-not-configured", () => client.FetchAsync(endpoint, ValidToken, true, token));
        builder.Configuration["TURN_SHARED_SECRET"] = secret;
        builder.Configuration["TURN_URLS"] = "";
        await ExpectCodeAsync("turn-not-configured", () => client.FetchAsync(endpoint, ValidToken, true, token));
        await app.StopAsync(token);
    }

    // Stands in for Auth0 so the checks exercise the real authorization requirement without network sign-in.
    private const string ValidToken = "valid-test-token";

    private static void AddTestAuthentication(IServiceCollection services)
    {
        services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        services.AddAuthorization();
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization.ToString() != $"Bearer {ValidToken}")
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("sub", "test-user")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeCloudflare : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool CorrectRequest { get; private set; }
        public bool Malformed { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            CorrectRequest = request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsoluteUri == "https://rtc.live.cloudflare.com/v1/turn/keys/fake-key-id/credentials/generate-ice-servers" &&
                request.Headers.Authorization?.Parameter == "fake-cloudflare-secret" && body.GetProperty("ttl").GetInt32() == 3600;
            return new HttpResponseMessage(Status)
            {
                Content = Malformed ? new StringContent("not json") : JsonContent.Create(new
                {
                    iceServers = new object[]
                    {
                        new { urls = new[] { "stun:stun.example.com:3478" } },
                        new { urls = new[] { "turn:turn.example.com:3478?transport=udp", "turns:turn.example.com:443?transport=tcp" },
                            username = $"fake-user-{Calls}", credential = $"fake-password-{Calls}" }
                    }
                })
            };
        }
    }

    private sealed class InvalidResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { iceServers = Array.Empty<object>(), expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) })
            });
    }

    private sealed class OfflineTunnelHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)530)
            {
                Content = new StringContent("error code: 1033")
            });
    }

    private static async Task ExpectCodeAsync(string code, Func<Task<TurnCredentialsResult>> action)
    {
        try { await action(); throw new InvalidOperationException($"Expected {code}."); }
        catch (TurnCredentialsException exception) when (exception.Code == code)
        { Check(true, $"Credential error is sanitized: {code}"); }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {description}");
        Console.WriteLine($"PASS: {description}");
    }
}
