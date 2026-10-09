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











}
