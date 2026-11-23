using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;

namespace WindowWise.Mobile.Services;

/// <summary>
/// Sign in with the same Auth0 application as the desktop app. The access token authorizes the signaling
/// server and TURN credentials. It lives in memory only, so each app start signs in again.
/// </summary>
public sealed class MobileAuth
{
    private const string Audience = "https://windowwise-api";
    private readonly OidcClient _client = new(new OidcClientOptions
    {
        Authority = "https://dev-g47rqjm3r21jo4re.us.auth0.com/",
        ClientId = "pO4TM6DE6g3GlQtTGK0wqUklsF6A7xrW",
        RedirectUri = LoopbackBrowser.CallbackUrl,
        PostLogoutRedirectUri = LoopbackBrowser.CallbackUrl,
        Scope = "openid profile email",
        Browser = new LoopbackBrowser()
    });
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public static MobileAuth Current { get; } = new();

    public string? DisplayName { get; private set; }
    public bool IsSignedIn => _accessToken is not null && _expiresAt > DateTimeOffset.UtcNow.AddSeconds(30);

    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        var result = await _client.LoginAsync(new LoginRequest
        {
            FrontChannelExtraParameters = new Parameters { { "audience", Audience } }
        }, cancellationToken);
        if (result.IsError || string.IsNullOrWhiteSpace(result.AccessToken))
            throw new InvalidOperationException(result.Error == "UserCancel" ? "Sign-in was canceled." : "Sign-in could not be completed.");
        _accessToken = result.AccessToken;
        _expiresAt = result.AccessTokenExpiration;
        DisplayName = result.User?.FindFirst("name")?.Value ?? result.User?.FindFirst("email")?.Value;
    }

    public string GetAccessToken() => IsSignedIn ? _accessToken!
        : throw new InvalidOperationException("Your sign-in has expired. Sign in again.");
}
