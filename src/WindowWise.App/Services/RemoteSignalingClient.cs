namespace WindowWise.Services;

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WindowWise.Models;
/// <summary>
/// Exchange Offer/Answer/ICE over the signaling WebSocket. File data does not pass through this client.
/// </summary>
public sealed class RemoteSignalingClient : IAsyncDisposable
{

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private string? _deviceId;
    private bool _disposed;
    public event EventHandler<SignalingMessage>? MessageReceived;
    public event EventHandler? Connected;

    public event EventHandler? Disconnected;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    // Attach the access token to the WebSocket handshake and start receiving on the connected socket.
    public async Task ConnectAsync(
        Uri signalingServerUri,
        string deviceId,
        CancellationToken cancellationToken = default,
        string? accessToken = null)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device ID is required.", nameof(deviceId));
        }


        if (IsConnected)
        {
            return;
        }


        _deviceId = deviceId;
        _socket = new ClientWebSocket();
        // Attach the access token before the server upgrades the WebSocket.
        if (!string.IsNullOrWhiteSpace(accessToken))
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");


        Uri connectUri = AppendDeviceId(signalingServerUri,_deviceId);

        await _socket.ConnectAsync(connectUri, cancellationToken);

        _receiveCancellation = new CancellationTokenSource();
        var socket = _socket;
        var receiveToken = _receiveCancellation.Token;
        _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, receiveToken));

        Connected?.Invoke(this, EventArgs.Empty);
    }

    // Serialize sends so room control messages and ICE candidates do not write concurrently.
    public async Task SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to the signaling server.");
        }

        string json = JsonSerializer.Serialize(message, _jsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        }
        finally
        {
            _sendLock.Release();

        }

    }
    // Forward the initiating peer's local SDP offer to its target.
    public Task SendOfferAsync(string targetDeviceId, string roomId, object offer, CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateMessage("offer", targetDeviceId, roomId, offer), cancellationToken);
    }

    // Forward the listening peer's SDP answer in response to the received offer.
    public Task SendAnswerAsync(string targetDeviceId, string roomId, object answer, CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateMessage("answer", targetDeviceId, roomId, answer), cancellationToken);
    }

    // Exchange direct or TURN-relay candidates. ICE connectivity checks determine whether they are reachable.
    public Task SendIceCandidateAsync(string targetDeviceId, string roomId, object iceCandidate, CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateMessage("ice-candidate", targetDeviceId, roomId, iceCandidate), cancellationToken);
    }


    // Detach the current socket and bound shutdown so leaving a room does not wait on the peer.
    public async Task DisconnectAsync()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null) return;
        _receiveCancellation?.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", timeout.Token);
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidOperationException) { }
        finally
        {
            socket.Dispose();
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    // Read bounded messages from this socket and publish disconnect only if it still owns the connection.
    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        var messageBuffer = new ArrayBufferWriter<byte>();
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text ||
                        messageBuffer.WrittenCount + result.Count > 64 * 1024)
                        throw new JsonException("Invalid signaling response.");
                    messageBuffer.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                var message = JsonSerializer.Deserialize<SignalingMessage>(messageBuffer.WrittenSpan, _jsonOptions);
                messageBuffer.Clear();
                if (message is not null) MessageReceived?.Invoke(this, message);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _socket, null, socket), socket))
            {
                socket.Dispose();
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private SignalingMessage CreateMessage(string type, string targetDeviceId, string roomId, object payload)
    {
        if (string.IsNullOrWhiteSpace(_deviceId))
        {
            throw new InvalidOperationException("Device ID is not set.");
        }

        return new SignalingMessage
        {
            Type = type,
            From = _deviceId,
            To = targetDeviceId,
            RoomId = roomId,
            Payload = payload
        };

    }
    private static Uri AppendDeviceId(Uri baseUri, string deviceId)
    {
        string separator = string.IsNullOrEmpty(baseUri.Query) ? "?" : "&";
        string uri = $"{baseUri}{separator}deviceId={Uri.EscapeDataString(deviceId)}";
        return new Uri(uri);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }


    // Wait for the receive loop to finish before releasing its cancellation and send resources.
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_socket is not null)
        {
            await DisconnectAsync();
        }

        if (_receiveTask is not null) await _receiveTask;
        _receiveCancellation?.Dispose();
        _sendLock.Dispose();
    }


}
