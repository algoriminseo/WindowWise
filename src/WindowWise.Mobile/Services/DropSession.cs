using SIPSorcery.Net;
using WindowWise.Services;

namespace WindowWise.Mobile.Services;

/// <summary>
/// One room connection with the PC. Follows the desktop app's sequence: connect to signaling, join or create
/// a room, try a direct connection for 8 seconds, and switch to a TURN relay if either side could not open it.
/// </summary>
public sealed class DropSession : IAsyncDisposable
{
    public static readonly Uri SignalingServer = new("wss://drop.datbl.com/signal");
    private static readonly TimeSpan DirectAttempt = TimeSpan.FromSeconds(8);
    private RemoteSignalingClient? _signaling;

    public static DropSession? Current { get; set; }

    public RemoteShareList Shares { get; } = new();
    public WebRtcDataChannelService? Transport { get; private set; }
    public WebRtcFileTransferService? Files { get; private set; }
    public bool Relayed { get; private set; }

    // Raised when the connection to the PC closes after it was open.
    public event EventHandler? Closed;

    // create: start a new room on this phone; otherwise join the room with this code.
    // roomCreated reports the code to show while waiting for the PC.
    public async Task ConnectAsync(string? code, Action<string> stage, Action<string>? roomCreated, CancellationToken token)
    {
        string accessToken = MobileAuth.Current.GetAccessToken();
        var signaling = _signaling = new RemoteSignalingClient();
        using var pairing = new RoomPairingSession(signaling);
        stage("Connecting to the server…");
        await signaling.ConnectAsync(SignalingServer, Guid.NewGuid().ToString("N"), token, accessToken);

        RoomEntry entry = await pairing.JoinAsync(code, token);
        if (code is null) roomCreated?.Invoke(entry.Code);
        stage(code is null ? "Waiting for your PC to join…" : "Waiting for your PC…");
        RoomMatch match = await pairing.WaitForPeerAsync(token);

        stage("Connecting directly…");
        // Same public STUN server as the desktop app's default, so a direct path can cross home routers.
        var direct = Prepare(signaling, match, entry, IceServerSettings.CreateStun("stun:stun.cloudflare.com:3478"), attempt: 1);
        await pairing.ReadyAsync(entry.SessionId, token);
        bool opened = await TryOpenAsync(direct, match.Initiate, token);
        bool useTurn = await pairing.ResolveDirectAttemptAsync(entry.SessionId, opened, token);
        if (useTurn)
        {
            stage("Direct path blocked. Connecting through the relay…");
            await DisposeTransportAsync();
            var endpoint = TurnCredentialsClient.ResolveEndpoint("", SignalingServer.ToString());
            var turn = await new TurnCredentialsClient().FetchAsync(endpoint, accessToken, relayOnly: true, token);
            var relay = Prepare(signaling, match, entry, turn.Configuration, attempt: 2);
            await pairing.ReadyAsync(entry.SessionId, token, attempt: 2);
            await OpenAsync(relay, match.Initiate, token);
            Relayed = true;
        }
        if (Transport?.IsOpen != true) throw new WebRtcConnectionAttemptException();
        Transport.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
    }

    private WebRtcDataChannelService Prepare(RemoteSignalingClient signaling, RoomMatch match, RoomEntry entry,
        RTCConfiguration config, int attempt)
    {
        var transport = Transport = new WebRtcDataChannelService(signaling, match.PeerId, entry.SessionId, config, attempt);
        var files = Files = new WebRtcFileTransferService(transport, Shares);
        // The phone never accepts unrequested pushes; it downloads what the PC shares.
        files.FileOffered += (_, offer) => _ = files.CancelIncomingAsync(offer.Id);
        return transport;
    }

    private static async Task<bool> TryOpenAsync(WebRtcDataChannelService transport, bool initiate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(DirectAttempt);
        try
        {
            await OpenAsync(transport, initiate, deadline.Token);
            return true;
        }
        catch (WebRtcConnectionAttemptException) when (!token.IsCancellationRequested) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested) { return false; }
    }

    private static async Task OpenAsync(WebRtcDataChannelService transport, bool initiate, CancellationToken token)
    {
        if (initiate)
        {
            try { await Task.Run(() => transport.StartAsync(token), token); }
            catch (Exception exception) when (!token.IsCancellationRequested &&
                exception is OperationCanceledException or ObjectDisposedException)
            {
                await transport.WaitUntilOpenAsync(token);
                throw;
            }
        }
        await transport.WaitUntilOpenAsync(token);
    }

    private async Task DisposeTransportAsync()
    {
        var files = Files;
        var transport = Transport;
        Files = null;
        Transport = null;
        try { if (files is not null) await files.DisposeAsync(); }
        finally { if (transport is not null) await transport.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisposeTransportAsync(); }
        finally { if (_signaling is not null) await _signaling.DisposeAsync(); }
    }
}
