namespace WindowWise.Services;

using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WindowWise.Models;
/// <summary>
///  remote socket client implementation for signaling server
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

    public async Task ConnectAsync(
        Uri signalingServerUri,
        string deviceId,
        CancellationToken cancellationToken = default)
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

        Uri connectUri = AppendDeviceId(signalingServerUri,_deviceId);

        await _socket.ConnectAsync(connectUri, cancellationToken);

        _receiveCancellation = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCancellation.Token), cancellationToken);

        Connected?.Invoke(this, EventArgs.Empty);
    }

    //send byte array to the siganling server and prevent concurrent sends using a semaphore Client -> Servber
    public async Task SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("Not connected to the signaling server.");
        }

        string json = JsonSerializer.Serialize(message, _jsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

        }
        finally
        {
            _sendLock.Release();

        }

    }
    // send offer message, offer: client A sends an offer to client B to initiate a WebRTC connection
    public Task SendOfferAsync(string targetDeviceId, string roomId, object offer, CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateMessage("offer", targetDeviceId, roomId, offer), cancellationToken);
    }

    //send answer messsage, answer: client B gets and offer and responds with an answer to client A to establish the WebRTC connection
    public Task SendAnswerAsync(string targetDeviceId, string roomId, object answer, CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateMessage("answer", targetDeviceId, roomId, answer), cancellationToken);
    }
    // network candidate message, ice-candidate: both clients exchange network candidates to establish a direct peer-to-peer connection
    public Task SendIceCandidateAsync(string targetDeviceId, string roomId, object iceCandidate, CancellationToken cancellationToken = default)
    {
        return SendAsync(CreateMessage("ice-candidate", targetDeviceId, roomId, iceCandidate), cancellationToken);
    }


    // Disonnects the client from the signaling server and cleans up resources
    public async Task DisconnectAsync()
    {
        if (_socket is null)
        {
            return;
        }

        _receiveCancellation?.Cancel();

        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None);
        }


        _socket.Dispose();
        _socket = null;

        Disconnected?.Invoke(this, EventArgs.Empty);
    }



    // Receives messages from the signaling server in a loop and raises the MessageReceived event when a message is received Server -> Client
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if(_socket is  null)
        {
            return;
        }
        byte[] buffer = new byte[64 * 1024];
        var messageBuffer = new ArrayBufferWriter<byte>();

        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;


                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await DisconnectAsync();
                        return;
                    }

                    messageBuffer.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);


                SignalingMessage? message = JsonSerializer.Deserialize<SignalingMessage>(messageBuffer.WrittenSpan, _jsonOptions);

                messageBuffer.Clear();

                if (message is not null)
                {
                    MessageReceived?.Invoke(this, message);
                }
            }
        }
        catch (OperationCanceledException)
        {

        }
        catch (WebSocketException)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
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
            _socket.Dispose();
            _socket = null;
        }

        _receiveCancellation?.Dispose();
        _sendLock.Dispose();
    }


}
