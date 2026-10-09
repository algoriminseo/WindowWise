namespace WindowWise.Services;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WindowWise.Models;
/// <summary>
///  remote socket client implementation for signaling server
/// </summary>
public sealed class RemoteSignalingClient : IAsyncDisposable
{

    private readonly JsonSerializationOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private string? _deviceId;
    private bool _disposed;

    public eventHandler<SignalingMessage>? MessageReceived;
    public event EventHanlder? Connected;

    public event EventHanlder? Disconnected;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(
        Uri signalingServerUri,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if(string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("Device ID is required.", nameof(deviceId));
        }


        if(isConnected)
        {
            return;
        }


        _deviceId = deviceId;
        _socket = new ClientWebSocket();

        Uri connectUri = AppendDeviceId(signalingServerUri, deviceid);

        await _soceket.CcnnectAsync(connectUri, cancellationToken);

        _reciveCancellation = new CancellationTokenSource();
        _reciveTask = Task.Run(() => ReceiveLoopAsync(_reciveCancellation.Token), cancellationToken);

        Connected?.Invoke(this, EventArgs.Empty);
    }









}
