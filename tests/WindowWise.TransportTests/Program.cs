using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using WindowWise.Signaling;
using System.Net;
using System.Threading.Channels;
using SIPSorcery.Net;
using WindowWise.Models;
using WindowWise.Services;

// A dependency-free integration runner using the production WebSocket server and
// two real ICE/DTLS/SCTP peers. Any failed assertion exits with a non-zero code.
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
RemoteLocalizationChecks.Run();
var iceConfig = IceServerSettings.Create(true,
    "turn:turn.example.com:3478?transport=udp\nturns:turn.example.com:443?transport=tcp",
    "test-user", "test-credential", true);
Check(iceConfig.iceServers.Count == 2 && iceConfig.iceTransportPolicy == RTCIceTransportPolicy.relay &&
    iceConfig.iceServers[1].username == "test-user" && iceConfig.iceServers[1].credential == "test-credential",
    "TURN endpoints, credentials and relay-only policy reach RTCConfiguration");
Check(IceServerSettings.Create(false, "invalid ignored", "", "", false).iceServers.Count == 0,
    "Disabled TURN leaves direct connectivity available");
Expect<ArgumentException>(() => IceServerSettings.Create(false, "", "", "", true));
Expect<ArgumentException>(() => IceServerSettings.Create(true, "turn:turn.example.com:3478", "", "", false));
Expect<ArgumentException>(() => IceServerSettings.Create(true, "https://example.com", "u", "p", false));
Expect<ArgumentException>(() => IceServerSettings.Create(true, "turn:host:99999", "u", "p", false));
var stunConfig = IceServerSettings.CreateStun("stun:stun.example.com:3478\nstuns:stun.example.com:5349");
Check(stunConfig.iceServers.Count == 2 && stunConfig.iceTransportPolicy == RTCIceTransportPolicy.all &&
    stunConfig.iceServers.All(server => server.username is null && server.credential is null),
    "STUN endpoints reach RTCConfiguration without credentials and allow direct paths");
Expect<ArgumentException>(() => IceServerSettings.CreateStun(""));
Expect<ArgumentException>(() => IceServerSettings.CreateStun("turn:turn.example.com:3478"));
Expect<ArgumentException>(() => IceServerSettings.CreateStun("stun:user@stun.example.com:3478"));
Console.WriteLine("Starting WebRTC transport integration checks...");
var token = timeout.Token;
await TurnCredentialsChecks.RunAsync(token);
await using (var gatherSignaling = new RemoteSignalingClient())
await using (var gatherPeer = new WebRtcDataChannelService(gatherSignaling, "unused", "gather-test",
    new RTCConfiguration { X_BindAddress = IPAddress.Loopback }))
{
    await gatherPeer.GatherLocalCandidatesAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);
    string diagnostics = gatherPeer.GetDiagnostics();
    Check(diagnostics.Contains("Gathering: complete") && diagnostics.Contains(" typ host"),
        "Standalone ICE gathering reports real host candidates without signaling");
    Check(!gatherPeer.IsOpen && !gatherSignaling.IsConnected,
        "Gathering alone does not claim a peer connection");
}
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
TurnCredentialsChecks.AddTestAuthentication(builder.Services);
builder.Services.AddRoomSignaling();
await using var server = builder.Build();
server.UseAuthentication();
server.UseAuthorization();
server.UseWebSockets();
server.MapRoomSignaling();
await server.StartAsync(token);

try
{
    var endpoint = new Uri(server.Urls.Single() + "/signal");
    var uri = new UriBuilder(endpoint) { Scheme = "ws" }.Uri;
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
    using var anonymous = await http.GetAsync(endpoint, token);
    Check(anonymous.StatusCode == HttpStatusCode.Unauthorized, "Signaling rejects anonymous requests");

    await using var clientA = new RemoteSignalingClient();
    await using var clientB = new RemoteSignalingClient();
    using var roomA = new RoomPairingSession(clientA);
    using var roomB = new RoomPairingSession(clientB);
    await clientA.ConnectAsync(uri, "test-a", token, TurnCredentialsChecks.ValidToken);
    RoomEntry entryA = await roomA.JoinAsync(null, token);
    var waitingForPeer = roomA.WaitForPeerAsync(token);
    Check(RoomPairingSession.IsValidCode(entryA.Code) && !waitingForPeer.IsCompleted,
        "Creating a room returns an invitation and waits for the second device");

    await clientB.ConnectAsync(uri, "test-b", token, TurnCredentialsChecks.ValidToken);
    RoomEntry entryB = await roomB.JoinAsync(entryA.Code, token);
    RoomMatch matchA = await waitingForPeer;
    RoomMatch matchB = await roomB.WaitForPeerAsync(token);
    Check(entryA == entryB && matchA.PeerId != matchB.PeerId && matchA.Initiate && !matchB.Initiate,
        "Room code matches two devices and designates only the host to initiate");

    await using var peerA = new WebRtcDataChannelService(clientA, matchA.PeerId, entryA.SessionId,
        new RTCConfiguration { X_BindAddress = IPAddress.Loopback });
    await using var peerB = new WebRtcDataChannelService(clientB, matchB.PeerId, entryB.SessionId,
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

    await CheckJoinRejectedAsync(entryA.Code, "room-full", uri, token);
    var hostReady = roomA.ReadyAsync(entryA.SessionId, token);
    var readyProbe = Task.Delay(150, token);
    Check(await Task.WhenAny(hostReady, readyProbe) == readyProbe, "Host does not start while the guest is unready");
    await Task.WhenAll(hostReady, roomB.ReadyAsync(entryB.SessionId, token));

    // A message addressed to a different session cannot reach the room's peer.
    var firstOffer = new TaskCompletionSource<SignalingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    clientB.MessageReceived += (_, message) =>
    {
        if (message.Type == "offer") firstOffer.TrySetResult(message);
    };
    await clientA.SendOfferAsync(matchA.PeerId, "foreign-session", new { type = "offer", sdp = "invalid" }, token);

    Console.WriteLine("Negotiating DataChannel...");
    await peerA.StartAsync(token);
    var offer = await firstOffer.Task.WaitAsync(token);
    Check(offer.RoomId == entryB.SessionId && offer.From == matchB.PeerId,
        "Server drops cross-room offers and supplies the sender ID");
    await Task.WhenAll(peerA.WaitUntilOpenAsync(token), peerB.WaitUntilOpenAsync(token))
        .WaitAsync(TimeSpan.FromSeconds(15), token);
    Check(peerA.IsOpen && peerB.IsOpen, "Both DataChannels open");
    string connectedDiagnostics = peerA.GetDiagnostics();
    Check(connectedDiagnostics.Contains("DataChannel: open") &&
        !connectedDiagnostics.Contains("Selected local: \n") &&
        !connectedDiagnostics.Contains("Selected remote: \n") &&
        !connectedDiagnostics.Contains("Remote candidates (0)"),
        "Connected diagnostics contain the selected pair and remote candidates");
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
    peerA.Closed += (_, _) => closed.TrySetResult();
    await clientB.DisconnectAsync();
    await closed.Task.WaitAsync(token);
    Check(!peerA.IsOpen, "Leaving the room closes the remaining peer");

    await peerA.DisposeAsync();
    await peerA.DisposeAsync();
    Check(!peerA.IsOpen, "Disposed peer is closed");
    Expect<ObjectDisposedException>(() => peerA.SendText("after close"));

    await CheckJoinRejectedAsync(entryA.Code, "room-not-found", uri, token);

    Console.WriteLine("PASS: WebRTC transport integration checks completed.");
}
finally
{
    await server.StopAsync();
}

// Verify that a join request is rejected with the expected room error.
static async Task CheckJoinRejectedAsync(string code, string expectedError, Uri endpoint, CancellationToken token)
{
    await using var client = new RemoteSignalingClient();
    using var pairing = new RoomPairingSession(client);
    await client.ConnectAsync(endpoint, "extra-device", token, TurnCredentialsChecks.ValidToken);
    try { await pairing.JoinAsync(code, token); }
    catch (RoomPairingException exception) when (exception.Code == expectedError)
    {
        Console.WriteLine($"PASS: Join rejected with {expectedError}");
        return;
    }
    throw new InvalidOperationException($"Expected join rejection: {expectedError}");
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
