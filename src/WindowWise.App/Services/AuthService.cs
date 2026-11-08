using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;

namespace WindowWise.Services;

public interface IAuthService
{
    bool IsAuthenticated { get; }
    string? UserId { get; }
    string? Email { get; }
    string? DisplayName { get; }
    Task SignInAsync(CancellationToken cancellationToken);
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
}

/// <summary>Sign in with Auth0 and verify the token with the WindowWise API.</summary>
public sealed class AuthService : IAuthService
{
    private const string Audience = "https://windowwise-api";
    // Override this address with the production HTTPS endpoint before release.
    private static readonly Uri MeEndpoint = ResolveMeEndpoint();
    private static readonly HttpClient Http = new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private readonly OidcClient _client = new(new OidcClientOptions
    {
        Authority = "https://dev-g47rqjm3r21jo4re.us.auth0.com/",
        ClientId = "pO4TM6DE6g3GlQtTGK0wqUklsF6A7xrW",
        RedirectUri = SystemBrowser.CallbackUrl,
        Scope = "openid profile email",
        Browser = new SystemBrowser()
    });
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public string? UserId { get; private set; }
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }
    public bool IsAuthenticated =>
        !string.IsNullOrWhiteSpace(_accessToken) &&
        !string.IsNullOrWhiteSpace(UserId) &&
        _expiresAt > DateTimeOffset.UtcNow.AddSeconds(30);

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            if (IsAuthenticated) return;
            ClearSession();
            var result = await _client.LoginAsync(new LoginRequest
            {
                FrontChannelExtraParameters = new Parameters { { "audience", Audience } }
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsError)
                throw new InvalidOperationException("Sign-in could not be completed. Please try again.");
            if (string.IsNullOrWhiteSpace(result.AccessToken))
                throw new InvalidOperationException("No access token was returned. Check the Auth0 application settings.");

            using var request = new HttpRequestMessage(HttpMethod.Get, MeEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
            using var response = await Http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException(
                    "The server could not verify your sign-in. Check the Authority, Audience, and API access settings.");
            response.EnsureSuccessStatusCode();
            var profile = await response.Content.ReadFromJsonAsync<MeResponse>(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(profile?.UserId))
                throw new InvalidOperationException("The server did not return a user ID.");
            if (result.AccessTokenExpiration <= DateTimeOffset.UtcNow.AddSeconds(30))
                throw new InvalidOperationException("Your session has expired. Please sign in again.");

            // Accept the session only after API verification. Never log or persist tokens.
            _accessToken = result.AccessToken;
            _expiresAt = result.AccessTokenExpiration;
            UserId = profile.UserId;
            // Use the validated OIDC profile for display only, never for authorization.
            Email = result.User.FindFirst("email")?.Value;
            DisplayName = result.User.FindFirst("name")?.Value;
        }
        finally { _loginGate.Release(); }
    }

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAuthenticated)
            throw new InvalidOperationException("Your session has expired. Please sign in again.");
        return Task.FromResult(_accessToken!);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            // Clear the app session only; this does not end the Auth0 browser session.
            ClearSession();
        }
        finally { _loginGate.Release(); }
    }

    private void ClearSession()
    {
        _accessToken = null;
        _expiresAt = default;
        UserId = null;
        Email = null;
        DisplayName = null;
    }

    private static Uri ResolveMeEndpoint()
    {
        string address = Environment.GetEnvironmentVariable("WINDOWWISE_API_URL") ?? "http://localhost:5047";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("WINDOWWISE_API_URL must be an HTTPS address or a local HTTP address.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/me");
    }

    private sealed record MeResponse(string? UserId);
}
