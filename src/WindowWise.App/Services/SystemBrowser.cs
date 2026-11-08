using System.Diagnostics;
using System.Net;
using Duende.IdentityModel.OidcClient.Browser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace WindowWise.Services;

/// <summary>Open the system browser and receive the authorization response on loopback.</summary>
public sealed class SystemBrowser : IBrowser
{
    public const string CallbackUrl = "http://127.0.0.1:7890/callback/";
    private readonly Action<string> _launchBrowser;

    public SystemBrowser(Action<string>? launchBrowser = null) =>
        _launchBrowser = launchBrowser ?? OpenBrowser;

    private static void OpenBrowser(string address)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = address,
            UseShellExecute = true
        });
    }

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options,
        CancellationToken cancellationToken = default)
    {
        var startUri = new Uri(options.StartUrl);
        var parameters = QueryHelpers.ParseQuery(startUri.Query);
        bool signingOut = parameters.TryGetValue("post_logout_redirect_uri", out var logoutRedirect) &&
            logoutRedirect.ToString() == CallbackUrl && startUri.AbsolutePath.EndsWith("/logout", StringComparison.Ordinal);
        string expectedState = parameters["state"].ToString();
        if (string.IsNullOrWhiteSpace(expectedState))
            throw new InvalidOperationException("The sign-in request is missing its state parameter.");

        var callback = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // Authorization codes must not appear in request logs.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 7890));
        await using var app = builder.Build();
        app.MapGet("/callback/", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";
            if ((!signingOut && !context.Request.Query.ContainsKey("code") && !context.Request.Query.ContainsKey("error")) ||
                context.Request.Query["state"].Count != 1 ||
                !string.Equals(context.Request.Query["state"].ToString(), expectedState, StringComparison.Ordinal))
            {
                // Ignore unrelated callbacks and continue waiting for this login attempt.
                context.Response.StatusCode = 400;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("""
                <!doctype html>
                <html lang="en">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <title>WindowWise</title>
                  <style>
                    body { margin:0; padding:32px; background:#f4f6f8; color:#18212f; font-family:"Segoe UI",sans-serif; }
                    main { max-width:400px; margin:12vh auto; padding:28px; background:white; border:1px solid #e2e7ee; border-radius:10px; }
                    h1 { margin:0 0 12px; font-size:24px; }
                    p { margin:0; color:#687386; line-height:1.6; }
                  </style>
                </head>
                <body><main>
                  <h1>Return to WindowWise</h1>
                  <p>You can close this tab and check your sign-in status in the app.</p>
                </main></body>
                </html>
                """, context.RequestAborted);
            // The OIDC client also validates state, nonce, PKCE, and token signatures.
            callback.TrySetResult(CallbackUrl + context.Request.QueryString.Value);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        bool started = false;
        try
        {
            await app.StartAsync(timeout.Token);
            started = true;
            _launchBrowser(options.StartUrl);
            string response = await callback.Task.WaitAsync(timeout.Token);
            if (signingOut && QueryHelpers.ParseQuery(new Uri(response).Query).ContainsKey("error"))
                throw new InvalidOperationException("Browser sign-out could not be completed.");
            return new BrowserResult
            {
                ResultType = BrowserResultType.Success,
                Response = response
            };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new BrowserResult
            {
                ResultType = BrowserResultType.UserCancel,
                Error = signingOut ? "Sign-out timed out. Please try again." : "Sign-in timed out. Please try again."
            };
        }
        finally
        {
            if (started)
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(stopTimeout.Token);
            }
        }
    }
}
