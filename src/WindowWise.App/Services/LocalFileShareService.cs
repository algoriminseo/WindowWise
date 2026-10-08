using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WindowWise.Services;

public sealed class LocalFileShareService : IDisposable
{
    private const int DefaultPort = 48291;
    private const int MaximumPortProbeCount = 20;

    private readonly ConcurrentDictionary<string, ShareSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _serverLock = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _serverCancellation;
    private Task? _serverTask;
    private int _port;
    private bool _disposed;

    public LocalShareResult StartShare(
        string filePath,
        TimeSpan? lifetime = null,
        int maxDownloads = 10)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        string fullPath = Path.GetFullPath(filePath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected file does not exist.", fullPath);
        }

        EnsureServerStarted();

        string token = GenerateToken();
        var session = new ShareSession
        {
            Token = token,
            FilePath = fullPath,
            ContentType = GetContentType(fullPath),
            ExpiresAt = DateTimeOffset.Now.Add(lifetime ?? TimeSpan.FromMinutes(30)),
            MaxDownloads = maxDownloads
        };

        _sessions[token] = session;

        return new LocalShareResult(session, CreateShareUrl(token));
    }

    public bool StopShare(Guid sessionId)
    {
        ShareSession? session = _sessions.Values.FirstOrDefault(session => session.Id == sessionId);

        if (session is null)
        {
            return false;
        }

        session.Stop();
        return _sessions.TryRemove(session.Token, out _);
    }

    public void StopAll()
    {
        foreach (ShareSession session in _sessions.Values)
        {
            session.Stop();
        }

        _sessions.Clear();
    }

    private void EnsureServerStarted()
    {
        lock (_serverLock)
        {
            if (_listener?.IsListening == true)
            {
                return;
            }

            _port = FindAvailablePort();
            _serverCancellation = new CancellationTokenSource();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{_port}/");
            listener.Start();

            _listener = listener;
            _serverTask = Task.Run(() => ListenAsync(listener, _serverCancellation.Token));
        }
    }

    private async Task ListenAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleRequestAsync(context), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";

            if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(context.Response, "OK", "text/plain", HttpStatusCode.OK);
                return;
            }

            if (TryReadToken(path, "/s/", out string pageToken))
            {
                await WriteSharePageAsync(context.Response, pageToken);
                return;
            }

            if (TryReadToken(path, "/download/", out string downloadToken))
            {
                await WriteDownloadAsync(context.Response, downloadToken);
                return;
            }

            await WriteTextAsync(context.Response, "Not found", "text/plain", HttpStatusCode.NotFound);
        }
        catch
        {
            if (context.Response.OutputStream.CanWrite)
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task WriteSharePageAsync(HttpListenerResponse response, string token)
    {
        if (!TryGetDownloadableSession(token, out ShareSession? session, out string unavailableReason))
        {
            await WriteTextAsync(response, CreateUnavailablePage(unavailableReason), "text/html; charset=utf-8", HttpStatusCode.NotFound);
            return;
        }

        string safeFileName = WebUtility.HtmlEncode(session.FileName);
        string safeDetail = WebUtility.HtmlEncode($"{session.MaxDownloads - session.DownloadCount} downloads remaining");
        string safeExpiry = WebUtility.HtmlEncode(session.ExpiresAt.LocalDateTime.ToString("g"));

        string html =
            $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>WindowWise Share</title>
              <style>
                body { font-family: Segoe UI, Arial, sans-serif; margin: 40px; color: #18212f; }
                main { max-width: 560px; margin: 0 auto; }
                a { display: inline-block; margin-top: 18px; padding: 10px 14px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px; }
                p { color: #687386; }
              </style>
            </head>
            <body>
              <main>
                <h1>WindowWise Share</h1>
                <h2>{{safeFileName}}</h2>
                <p>{{safeDetail}}</p>
                <p>Expires {{safeExpiry}}</p>
                <a href="/download/{{WebUtility.UrlEncode(token)}}">Download</a>
              </main>
            </body>
            </html>
            """;

        await WriteTextAsync(response, html, "text/html; charset=utf-8", HttpStatusCode.OK);
    }

    private async Task WriteDownloadAsync(HttpListenerResponse response, string token)
    {
        if (!TryGetDownloadableSession(token, out ShareSession? session, out string unavailableReason))
        {
            await WriteTextAsync(response, unavailableReason, "text/plain", HttpStatusCode.NotFound);
            return;
        }

        var fileInfo = new FileInfo(session.FilePath);

        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = session.ContentType;
        response.ContentLength64 = fileInfo.Length;
        response.AddHeader("Content-Disposition", $"attachment; filename=\"{SanitizeHeaderFileName(session.FileName)}\"");

        await using FileStream fileStream = File.OpenRead(session.FilePath);
        await fileStream.CopyToAsync(response.OutputStream);

        session.RegisterDownload();

        if (session.HasReachedDownloadLimit)
        {
            _sessions.TryRemove(token, out _);
        }
    }

    private bool TryGetDownloadableSession(string token, out ShareSession? session, out string unavailableReason)
    {
        RemoveExpiredSessions();

        if (!_sessions.TryGetValue(token, out session))
        {
            unavailableReason = "This share link was not found or has expired.";
            return false;
        }

        if (session.CanDownload)
        {
            unavailableReason = string.Empty;
            return true;
        }

        unavailableReason = "This file is no longer available.";
        _sessions.TryRemove(token, out _);
        return false;
    }

    private void RemoveExpiredSessions()
    {
        foreach ((string token, ShareSession session) in _sessions)
        {
            if (!session.CanDownload)
            {
                _sessions.TryRemove(token, out _);
            }
        }
    }

    private string CreateShareUrl(string token)
    {
        string host = GetLanIpAddress() ?? "localhost";
        return $"http://{host}:{_port}/s/{WebUtility.UrlEncode(token)}";
    }

    private static bool TryReadToken(string path, string prefix, out string token)
    {
        token = string.Empty;

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = WebUtility.UrlDecode(path[prefix.Length..]);
        return !string.IsNullOrWhiteSpace(token);
    }

    private static int FindAvailablePort()
    {
        for (int offset = 0; offset < MaximumPortProbeCount; offset++)
        {
            int port = DefaultPort + offset;

            if (IsPortAvailable(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("No available local file sharing port was found.");
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            TcpListener listener = new(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string? GetLanIpAddress()
    {
        return Dns.GetHostEntry(Dns.GetHostName())
            .AddressList
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.ToString())
            .FirstOrDefault(address => !address.StartsWith("127.", StringComparison.Ordinal));
    }

    private static string GenerateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string GetContentType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".json" => "application/json",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };
    }

    private static string SanitizeHeaderFileName(string fileName)
    {
        var builder = new StringBuilder(fileName.Length);

        foreach (char character in fileName)
        {
            builder.Append(character is '"' or '\r' or '\n' ? '_' : character);
        }

        return builder.ToString();
    }

    private static string CreateUnavailablePage(string message)
    {
        string safeMessage = WebUtility.HtmlEncode(message);

        return
            $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>WindowWise Share</title>
            </head>
            <body>
              <h1>WindowWise Share</h1>
              <p>{{safeMessage}}</p>
            </body>
            </html>
            """;
    }

    private static async Task WriteTextAsync(
        HttpListenerResponse response,
        string text,
        string contentType,
        HttpStatusCode statusCode)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = (int)statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAll();

        _serverCancellation?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _serverCancellation?.Dispose();
    }
}

public sealed record LocalShareResult(ShareSession Session, string ShareUrl);
