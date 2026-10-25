using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SIPSorcery.Net;
using WindowWise.Services;

// A dependency-free integration runner using the production WebSocket server and
// two real ICE/DTLS/SCTP peers. Any failed assertion exits with a non-zero code.
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
RemoteLocalizationChecks.Run();
Console.WriteLine("Starting WebRTC transport integration checks...");
var token = timeout.Token;
var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "WindowWise.slnx"))) root = root.Parent;
if (root is null) throw new InvalidOperationException("Repository root not found.");
string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
string serverDll = Path.Combine(root.FullName, "artifacts", "bin", "WindowWise.Signaling", configuration, "net10.0", "WindowWise.Signaling.dll");

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var startInfo = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
startInfo.ArgumentList.Add(serverDll);
startInfo.ArgumentList.Add("--urls");
startInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
startInfo.Environment["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning";
using var server = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not launch signaling server.");
var serverReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
server.OutputDataReceived += (_, e) =>
{
    if (e.Data?.Contains("Now listening on:", StringComparison.Ordinal) == true) serverReady.TrySetResult();
};
server.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
server.BeginOutputReadLine();
server.BeginErrorReadLine();

try
{
    await serverReady.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
    var uri = new Uri($"ws://127.0.0.1:{port}/signal");
    await using var clientA = new RemoteSignalingClient();
    await using var clientB = new RemoteSignalingClient();
    await clientA.ConnectAsync(uri, "test-a", token);
    await clientB.ConnectAsync(uri, "test-b", token);
    // The WebSocket accept can complete just before server registration.
    await Task.Delay(100, token);

    await using var peerA = new WebRtcDataChannelService(clientA, "test-b", "test-room",
        new RTCConfiguration { X_BindAddress = IPAddress.Loopback });
    await using var peerB = new WebRtcDataChannelService(clientB, "test-a", "test-room",
        new RTCConfiguration { X_BindAddress = IPAddress.Loopback });
    var textsA = Channel.CreateUnbounded<string>();
    var textsB = Channel.CreateUnbounded<string>();
    var binariesB = Channel.CreateUnbounded<byte[]>();
    peerA.TextReceived += (_, text) => textsA.Writer.TryWrite(text);
    peerB.TextReceived += (_, text) => textsB.Writer.TryWrite(text);
    peerB.BinaryReceived += (_, data) => binariesB.Writer.TryWrite(data);
    peerA.ErrorOccurred += (_, error) => Console.Error.WriteLine($"Peer A: {error}");
    peerB.ErrorOccurred += (_, error) => Console.Error.WriteLine($"Peer B: {error}");

    Expect<InvalidOperationException>(() => peerA.SendText("too early"));
    using (var waitCancellation = new CancellationTokenSource())
    {
        waitCancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => peerA.WaitUntilOpenAsync(waitCancellation.Token));
    }

    // Foreign rooms and senders must not poison this session.
    await clientA.SendOfferAsync("test-b", "other-room", new { type = "offer", sdp = "invalid" }, token);
    await using (var stranger = new RemoteSignalingClient())
    {
        await stranger.ConnectAsync(uri, "stranger", token);
        await stranger.SendOfferAsync("test-b", "test-room", new { type = "offer", sdp = "invalid" }, token);
    }

    Console.WriteLine("Negotiating DataChannel...");
    await peerA.StartAsync(token);
    await Task.WhenAll(peerA.WaitUntilOpenAsync(token), peerB.WaitUntilOpenAsync(token))
        .WaitAsync(TimeSpan.FromSeconds(15), token);
    Check(peerA.IsOpen && peerB.IsOpen, "Both DataChannels open");
    Check(peerA.ConnectionRoute == WebRtcConnectionRoute.Direct && peerB.ConnectionRoute == WebRtcConnectionRoute.Direct,
        "Nominated ICE pairs identify a direct connection");
    await ExpectAsync<InvalidOperationException>(() => peerA.StartAsync(token));

    peerA.SendText("안녕하세요 👋");
    Check(await textsB.Reader.ReadAsync(token) == "안녕하세요 👋", "UTF-8 text A -> B");
    peerB.SendText("reply");
    Check(await textsA.Reader.ReadAsync(token) == "reply", "Text B -> A");
    peerA.SendText("");
    Check(await textsB.Reader.ReadAsync(token) == "", "Empty text");
    peerA.SendBinary([]);
    Check((await binariesB.Reader.ReadAsync(token)).Length == 0, "Empty binary");

    byte[] payload = new byte[WebRtcDataChannelService.MaxMessageSize];
    Random.Shared.NextBytes(payload);
    peerA.SendBinary(payload);
    Check((await binariesB.Reader.ReadAsync(token)).AsSpan().SequenceEqual(payload), "16 KiB binary integrity");
    Expect<ArgumentOutOfRangeException>(() => peerA.SendBinary(new byte[WebRtcDataChannelService.MaxMessageSize + 1]));
    Expect<ArgumentOutOfRangeException>(() => peerA.SendText(new string('가', 6000)));

    for (int i = 0; i < 30; i++) peerA.SendText($"ordered-{i}");
    for (int i = 0; i < 30; i++)
        Check(await textsB.Reader.ReadAsync(token) == $"ordered-{i}", $"Ordered message {i}");

    await FileTransferChecks.RunAsync(peerA, peerB, token);

    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    peerB.Closed += (_, _) => closed.TrySetResult();
    await clientB.DisconnectAsync();
    await closed.Task.WaitAsync(token);
    Check(!peerB.IsOpen, "Signaling disconnect closes the session");

    await peerA.DisposeAsync();
    await peerA.DisposeAsync();
    Check(!peerA.IsOpen, "Disposed peer is closed");
    Expect<ObjectDisposedException>(() => peerA.SendText("after close"));

    await using (var unavailable = new WebRtcDataChannelService(clientA, "missing-peer", "missing-room"))
    {
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        unavailable.ErrorOccurred += (_, error) => failure.TrySetResult(error);
        await unavailable.StartAsync(token);
        Check((await failure.Task.WaitAsync(token)).Message.Contains("peer-not-found", StringComparison.Ordinal),
            "Server errors reported without hanging");
        await ExpectAsync<OperationCanceledException>(() => unavailable.WaitUntilOpenAsync(token));
    }

    Console.WriteLine("PASS: WebRTC transport integration checks completed.");
}
finally
{
    if (!server.HasExited) server.Kill(entireProcessTree: true);
    await server.WaitForExitAsync();
}

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {description}");
    Console.WriteLine($"PASS: {description}");
}

static void Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
