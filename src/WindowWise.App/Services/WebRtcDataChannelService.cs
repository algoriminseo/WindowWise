using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SIPSorcery.Net;
using WindowWise.Models;

namespace WindowWise.Services;

public enum WebRtcConnectionRoute { Unknown, Direct, Relayed }

/// <summary>
/// One reliable, ordered DataChannel for a configured peer and room.
/// Events run on background threads; WPF callers must dispatch UI updates.
/// The caller owns the signaling connection and must connect it first.
/// </summary>
public sealed class WebRtcDataChannelService : IAsyncDisposable
{
    public const int MaxMessageSize = 16 * 1024;
    private const ulong MaxBufferedAmount = 1024 * 1024;
    private readonly RemoteSignalingClient _signaling;
    private readonly string _targetDeviceId;
    private readonly string _roomId;
    private readonly RTCPeerConnection _peer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<RTCIceCandidateInit> _pendingCandidates = new();
    private readonly object _channelLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _localDescriptionSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<SignalingMessage> _incoming = Channel.CreateBounded<SignalingMessage>(256);
    private readonly Channel<RTCIceCandidateInit> _outgoingCandidates = Channel.CreateBounded<RTCIceCandidateInit>(256);
    private readonly Task _incomingTask;
    private readonly Task _candidateTask;
    private RTCDataChannel? _channel;
    private bool _remoteDescriptionSet;
    private bool _negotiationStarted;
    private volatile bool _stopped;
    private Task? _disposeTask;

    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<string>? TextReceived;
    public event EventHandler<byte[]>? BinaryReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    public bool IsOpen => !_stopped && _channel?.readyState == RTCDataChannelState.open;

    public WebRtcConnectionRoute ConnectionRoute
    {
        get
        {
            if (!IsOpen) return WebRtcConnectionRoute.Unknown;
            var pair = (_peer.GetRtpChannel() as RtpIceChannel)?.NominatedEntry;
            if (pair is null) return WebRtcConnectionRoute.Unknown;
            return pair.LocalCandidate.type == RTCIceCandidateType.relay || pair.RemoteCandidate.type == RTCIceCandidateType.relay
                ? WebRtcConnectionRoute.Relayed : WebRtcConnectionRoute.Direct;
        }
    }

    // Set up the peer connection and subscribe to signaling events.
    public WebRtcDataChannelService(RemoteSignalingClient signaling, string targetDeviceId,
        string roomId, RTCConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(signaling);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        _signaling = signaling;
        _targetDeviceId = targetDeviceId;
        _roomId = roomId;
        // No public STUN dependency by default. Supply STUN/TURN for other networks.
        _peer = new RTCPeerConnection(configuration ?? new RTCConfiguration());
        _peer.ondatachannel += AttachChannel;
        _peer.onicecandidate += candidate =>
        {
            if (!_stopped && candidate is not null &&
                !_outgoingCandidates.Writer.TryWrite(new RTCIceCandidateInit
                {
                    candidate = candidate.candidate,
                    sdpMid = candidate.sdpMid,
                    sdpMLineIndex = candidate.sdpMLineIndex
                }))
                Fail(new InvalidOperationException("Too many local ICE candidates."));
        };
        _peer.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.failed)
                Fail(new InvalidOperationException("WebRTC connection failed. Check ICE/STUN/TURN settings."));
            else if (state == RTCPeerConnectionState.closed)
                Stop();
        };
        _incomingTask = ProcessIncomingAsync();
        _candidateTask = SendCandidatesAsync();
        _signaling.MessageReceived += OnSignalingMessageReceived;
        _signaling.Disconnected += OnSignalingDisconnected;
    }

    // Create the DataChannel and send an offer from the initiating peer.
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(cancellation.Token);
        try
        {
            ThrowIfStopped();
            if (_negotiationStarted)
                throw new InvalidOperationException("Negotiation has already started. Create a new service to reconnect.");
            if (!_signaling.IsConnected)
                throw new InvalidOperationException("Connect to the signaling server first.");
            _negotiationStarted = true;
            try
            {
                AttachChannel(await _peer.createDataChannel("windowwise", new RTCDataChannelInit { ordered = true }));
                var offer = _peer.createOffer();
                await _peer.setLocalDescription(offer);
                await _signaling.SendOfferAsync(_targetDeviceId, _roomId,
                    new { type = "offer", sdp = offer.sdp }, cancellation.Token);
                _localDescriptionSent.TrySetResult();
            }
            catch (Exception exception)
            {
                Fail(exception);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    // Wait for the DataChannel to open without canceling the session if this wait is canceled.
    public async Task WaitUntilOpenAsync(CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _opened.Task.WaitAsync(cancellation.Token);
        ThrowIfStopped();
    }

    // Check the UTF-8 message size and send text through the open channel.
    public void SendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateMessageSize(Encoding.UTF8.GetByteCount(text));
        lock (_channelLock) { GetOpenChannel().send(text); }
    }

    // Check the payload size and send binary data through the open channel.
    public void SendBinary(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateMessageSize(data.Length);
        lock (_channelLock) { GetOpenChannel().send(data); }
    }

    // Queue signaling messages for this peer and room, including server errors.
    private void OnSignalingMessageReceived(object? sender, SignalingMessage message)
    {
        if (_stopped || message.RoomId != _roomId) return;
        bool isPeerMessage = message.From == _targetDeviceId && message.Type is "offer" or "answer" or "ice-candidate";

        bool isServerError = message.From == "server" && message.Type == "error";
        if ((isPeerMessage || isServerError) && !_incoming.Writer.TryWrite(message))
            Fail(new InvalidOperationException("Too many pending signaling messages."));
    }

    // Process queued signaling messages one at a time until the session stops.
    private async Task ProcessIncomingAsync()
    {
        try
        {
            await foreach (var message in _incoming.Reader.ReadAllAsync(_lifetime.Token))
            {
                await _gate.WaitAsync(_lifetime.Token);
                try
                {
                    ThrowIfStopped();
                    await HandleSignalingMessageAsync(message, _lifetime.Token);
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Fail(exception); }
    }

    // Handle offers, answers, ICE candidates, and signaling server errors.
    private async Task HandleSignalingMessageAsync(SignalingMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(message.Payload);
        switch (message.Type)
        {
            case "offer":
                if (_negotiationStarted)
                    throw new InvalidOperationException("Simultaneous or duplicate offer. Only one peer should call StartAsync.");
                _negotiationStarted = true;
                ApplyRemoteDescription(RTCSdpType.offer, ReadSdp(payload, "offer"));
                var answer = _peer.createAnswer();

                await _peer.setLocalDescription(answer);

                await _signaling.SendAnswerAsync(_targetDeviceId, _roomId,
                    new { type = "answer", sdp = answer.sdp }, cancellationToken);
                _localDescriptionSent.TrySetResult();
                break;

            case "answer":
                if (!_localDescriptionSent.Task.IsCompletedSuccessfully || _remoteDescriptionSet)
                    throw new InvalidOperationException("Unexpected answer.");
                ApplyRemoteDescription(RTCSdpType.answer, ReadSdp(payload, "answer"));
                break;

            case "ice-candidate":
                var candidate = payload.Deserialize<RTCIceCandidateInit>()
                    ?? throw new JsonException("Invalid ICE candidate.");
                if (string.IsNullOrWhiteSpace(candidate.candidate)) break; 
                if (_remoteDescriptionSet) _peer.addIceCandidate(candidate);
                else
                {
                    if (_pendingCandidates.Count >= 256)
                        throw new InvalidOperationException("Too many pending ICE candidates.");
                    _pendingCandidates.Enqueue(candidate);
                }
                break;

            case "error":
                throw new InvalidOperationException($"Signaling server error: {payload}");
        }
    }

    // Apply the remote SDP, then add any ICE candidates received before it.
    private void ApplyRemoteDescription(RTCSdpType type, string sdp)
    {
        var result = _peer.setRemoteDescription(new RTCSessionDescriptionInit { type = type, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"Remote SDP was rejected: {result}");
        _remoteDescriptionSet = true;
        while (_pendingCandidates.TryDequeue(out var candidate)) _peer.addIceCandidate(candidate);
    }

    // Wait for the local SDP to be sent before sending queued ICE candidates.
    private async Task SendCandidatesAsync()
    {
        try
        {
            // Gathering may start during setLocalDescription. Always send SDP first.
            await _localDescriptionSent.Task.WaitAsync(_lifetime.Token);
            await foreach (var candidate in _outgoingCandidates.Reader.ReadAllAsync(_lifetime.Token))
                await _signaling.SendIceCandidateAsync(_targetDeviceId, _roomId, candidate, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }

        catch (Exception exception) { Fail(exception); }
    }

    // Register channel lifecycle and message handlers, rejecting extra channels.
    private void AttachChannel(RTCDataChannel channel)
    {
        lock (_channelLock)
        {
            if (_stopped || _channel is not null || channel.label != "windowwise")
            {
                channel.close();
                return;
            }
            _channel = channel;
        }
        channel.onopen += NotifyOpened;
        channel.onclose += () => Stop();
        channel.onerror += error => Fail(new InvalidOperationException(error));
        channel.onmessage += (_, protocol, data) =>
        {
            if (_stopped) return;
            switch (protocol)
            {
                case DataChannelPayloadProtocols.WebRTC_String:
                    TextReceived?.Invoke(this, Encoding.UTF8.GetString(data));
                    break;
                case DataChannelPayloadProtocols.WebRTC_String_Empty:
                    TextReceived?.Invoke(this, string.Empty);
                    break;
                case DataChannelPayloadProtocols.WebRTC_Binary:
                    BinaryReceived?.Invoke(this, data);
                    break;
                case DataChannelPayloadProtocols.WebRTC_Binary_Empty:
                    BinaryReceived?.Invoke(this, Array.Empty<byte>());
                    break;
            }
        };
        // Remote channels may already be open before their event handlers are attached.
        if (channel.readyState == RTCDataChannelState.open) NotifyOpened();
    }

    // Complete connection waiters and raise the Opened event only once.
    private void NotifyOpened()
    {
        if (!_stopped && _opened.TrySetResult()) Opened?.Invoke(this, EventArgs.Empty);
    }

    // Return the open channel after checking the session and send buffer.
    private RTCDataChannel GetOpenChannel()
    {
        ThrowIfStopped();
        var channel = _channel;
        if (channel?.readyState != RTCDataChannelState.open)
            throw new InvalidOperationException("The DataChannel is not open.");
        if (channel.bufferedAmount > MaxBufferedAmount)
            throw new InvalidOperationException("The send buffer is full. Retry later.");
        return channel;
    }

    // Reject payloads that exceed the local or remote message size limit.
    private void ValidateMessageSize(int size)
    {
        // Respect the remote SCTP limit as well as our conservative chunk size.
        long remoteLimit = _peer.sctp?.maxMessageSize ?? 0;
        long limit = remoteLimit > 0 ? Math.Min(MaxMessageSize, remoteLimit) : MaxMessageSize;
        if (size > limit)
            throw new ArgumentOutOfRangeException(nameof(size), $"Messages must be at most {limit} bytes.");
    }

    // Validate the description type and extract the SDP from the JSON payload.
    private static string ReadSdp(JsonElement payload, string expectedType)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() != expectedType ||
            !payload.TryGetProperty("sdp", out var sdp) || sdp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(sdp.GetString()))
            throw new JsonException("Invalid SDP payload.");
        return sdp.GetString()!;
    }

    // Stop the session and report the lost signaling connection.
    private void OnSignalingDisconnected(object? sender, EventArgs e) =>
        Fail(new InvalidOperationException("The signaling connection was closed."));

    // Stop the session and report the first failure.
    private void Fail(Exception exception)
    {
        if (Stop()) ErrorOccurred?.Invoke(this, exception);
    }

    // Stop queue processing, detach signaling handlers, and notify listeners once.
    private bool Stop()
    {
        lock (_channelLock)
        {
            if (_stopped) return false;
            _stopped = true;
        }
        _signaling.MessageReceived -= OnSignalingMessageReceived;
        _signaling.Disconnected -= OnSignalingDisconnected;
        _lifetime.Cancel();
        _incoming.Writer.TryComplete();
        _outgoingCandidates.Writer.TryComplete();
        Closed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // Reject operations after the session has stopped.
    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    // Start cleanup or return the existing cleanup task for repeated calls.
    public ValueTask DisposeAsync()
    {
        lock (_channelLock) { return new ValueTask(_disposeTask ??= DisposeCoreAsync()); }
    }

    // Wait for pending work to finish, then close the channel and peer connection.
    private async Task DisposeCoreAsync()
    {
        Stop();
        await Task.WhenAll(_incomingTask, _candidateTask);
        await _gate.WaitAsync();
        try
        {
            _pendingCandidates.Clear();
            lock (_channelLock)
            {
                _channel?.close();
                _peer.Close("Disposed");
            }
        }
        finally { _gate.Release(); }
    }
}
