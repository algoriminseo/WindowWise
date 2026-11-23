using System.Net;
using System.Net.Sockets;
using System.Text;
using Duende.IdentityModel.OidcClient.Browser;

namespace WindowWise.Mobile.Services;

/// <summary>
/// Open the sign-in page in the phone's browser and receive the result on the phone's own loopback address.
/// This reuses the desktop app's registered callback (http://127.0.0.1:7890/callback/), so Auth0 needs no
/// extra setting. 127.0.0.1 always means "this device", so the browser on the phone reaches this listener.
/// </summary>
public sealed class LoopbackBrowser : Duende.IdentityModel.OidcClient.Browser.IBrowser
{
    public const string CallbackUrl = "http://127.0.0.1:7890/callback/";
    private const int Port = 7890;

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        string? expectedState = ReadQuery(new Uri(options.StartUrl).Query, "state");
        if (string.IsNullOrEmpty(expectedState))
            throw new InvalidOperationException("The sign-in request is missing its state parameter.");

        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                Browser.Default.OpenAsync(options.StartUrl, BrowserLaunchMode.SystemPreferred));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                string? target = await ReadRequestTargetAsync(client, timeout.Token);
                string query = target is null ? "" : new Uri(new Uri(CallbackUrl), target).Query;
                bool ours = target?.StartsWith("/callback/", StringComparison.Ordinal) == true &&
                    ReadQuery(query, "state") == expectedState &&
                    (ReadQuery(query, "code") is not null || ReadQuery(query, "error") is not null);
                await WriteResponseAsync(client, ours, timeout.Token);
                // Ignore unrelated requests (favicon, other states) and keep waiting for this sign-in.
                if (!ours) continue;
                BringAppToFront();
                // The OIDC client also validates state, nonce, PKCE, and the token signature.
                return new BrowserResult { ResultType = BrowserResultType.Success, Response = CallbackUrl + query };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BrowserResult { ResultType = BrowserResultType.Timeout, Error = "Sign-in timed out." };
        }
        catch (OperationCanceledException)
        {
            return new BrowserResult { ResultType = BrowserResultType.UserCancel };
        }
        finally { listener.Stop(); }
    }

    // Read only the request line ("GET /callback/?code=... HTTP/1.1").
    private static async Task<string?> ReadRequestTargetAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) break;
            length += read;
            int end = Array.IndexOf(buffer, (byte)'\n', 0, length);
            if (end < 0) continue;
            string[] parts = Encoding.ASCII.GetString(buffer, 0, end).Trim().Split(' ');
            return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
        }
        return null;
    }

    private static async Task WriteResponseAsync(TcpClient client, bool ours, CancellationToken token)
    {
        string body = ours
            ? "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
              "<title>WindowWise</title></head><body style=\"margin:0;background:#0B1020;color:#EEF2FA;font-family:sans-serif;" +
              "display:flex;align-items:center;justify-content:center;height:100vh;text-align:center\"><div>" +
              "<h2>Signed in</h2><p style=\"color:#8F9BB8\">Return to WindowWise Drop.</p></div></body></html>"
            : "";
        string head = ours
            ? $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(head + body), token);
    }

    private static string? ReadQuery(string query, string name)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            if (key == name) return equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return null;
    }

    // Return to the app after the browser shows the result.
    private static void BringAppToFront()
    {
#if ANDROID
        var context = Android.App.Application.Context;
        var intent = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        if (intent is null) return;
        intent.AddFlags(Android.Content.ActivityFlags.ReorderToFront | Android.Content.ActivityFlags.NewTask);
        context.StartActivity(intent);
#endif
    }
}
