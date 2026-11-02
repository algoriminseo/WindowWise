using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SIPSorcery.Net;
using WindowWise.Models;

namespace WindowWise.Services;

public enum WebRtcConnectionRoute { Unknown, Direct, Relayed }

/// <summary>
/// Manage one reliable, ordered DataChannel for the configured peer and room.
/// Events run on background threads; WPF callers must marshal UI updates through their Dispatcher.
/// The caller owns the signaling connection and must connect it before negotiation begins.
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
    private readonly object _diagnosticLock = new();
    private readonly HashSet<string> _localCandidates = new();
    private readonly HashSet<string> _remoteCandidates = new();
    private string _gatheringState = "new", _iceState = "new", _peerState = "new";
    private readonly TaskCompletionSource _gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Record candidates from both SDP and trickled ICE messages. Candidate presence alone does not prove
    // connectivity.
    public string GetDiagnostics()
    {
        lock (_diagnosticLock)
        {
            var pair = (_peer.GetRtpChannel() as RtpIceChannel)?.NominatedEntry;
            return $"Gathering: {_gatheringState}\nICE: {_iceState}\nPeer: {_peerState}\nDataChannel: {_channel?.readyState.ToString() ?? "none"}\n" +
                $"Selected local: {pair?.LocalCandidate}\nSelected remote: {pair?.RemoteCandidate}\n" +
                $"\nLocal candidates ({_localCandidates.Count}):\n{string.Join('\n', _localCandidates)}\n" +
                $"\nRemote candidates ({_remoteCandidates.Count}):\n{string.Join('\n', _remoteCandidates)}";
        }
    }

    private void RecordCandidates(string sdp, bool local)
    {
        lock (_diagnosticLock)
            foreach (string line in sdp.Split('\n'))
                if (line.Trim().StartsWith("a=candidate:", StringComparison.Ordinal))
                    (local ? _localCandidates : _remoteCandidates).Add(line.Trim()[2..]);
    }

    // Gather real ICE candidates without signaling or a remote peer. Gathering completion does not prove peer
    // connectivity.
    public async Task GatherLocalCandidatesAsync(CancellationToken cancellationToken)
    {
        if (_negotiationStarted) throw new InvalidOperationException("Negotiation has already started.");
        _negotiationStarted = true;
        AttachChannel(await _peer.createDataChannel("windowwise", new RTCDataChannelInit { ordered = true }));
        var offer = _peer.createOffer();
        await _peer.setLocalDescription(offer);
        RecordCandidates(offer.sdp, true);
        await _gathered.Task.WaitAsync(cancellationToken);
    }

    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<string>? TextReceived;
    public event EventHandler<byte[]>? BinaryReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    public bool IsOpen => !_stopped && _channel?.readyState == RTCDataChannelState.open;

    // Determine the route from the nominated pair, rather than assuming TURN is used because it was configured.
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

    // Create the peer connection and register negotiation, ICE-state, and channel handlers.
    public WebRtcDataChannelService(RemoteSignalingClient signaling, string targetDeviceId,
        string roomId, RTCConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(signaling);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        _signaling = signaling;
        _targetDeviceId = targetDeviceId;
        _roomId = roomId;
        // Use no external ICE servers by default. STUN input remains unimplemented; the caller supplies TURN
        // configuration.
        _peer = new RTCPeerConnection(configuration ?? new RTCConfiguration());
        _peer.onicegatheringstatechange += state =>
        {
            lock (_diagnosticLock) _gatheringState = state.ToString();
            if (state == RTCIceGatheringState.complete) _gathered.TrySetResult();
        };
        _peer.oniceconnectionstatechange += state => { lock (_diagnosticLock) _iceState = state.ToString(); };
        _peer.ondatachannel += AttachChannel;
        _peer.onicecandidate += candidate =>
        {
            if (candidate is not null)
                lock (_diagnosticLock) _localCandidates.Add(candidate.candidate);
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
            lock (_diagnosticLock) _peerState = state.ToString();
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

    // Only the initiating peer creates the channel and offer. Starting both peers would trigger duplicate
    // negotiation.
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
                RecordCandidates(offer.sdp, true);
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

    // Canceling this wait does not end the session. Dispose the service when the connection itself must stop.
    public async Task WaitUntilOpenAsync(CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _opened.Task.WaitAsync(cancellation.Token);
        ThrowIfStopped();
    }

    // Validate the UTF-8 byte count, rather than character count, before sending text through an open channel.
    public void SendText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateMessageSize(Encoding.UTF8.GetByteCount(text));
        lock (_channelLock) { GetOpenChannel().send(text); }
    }

    // Check message size and buffer capacity before sending binary data.
    public void SendBinary(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateMessageSize(data.Length);
        lock (_channelLock) { GetOpenChannel().send(data); }
    }

    // Queue negotiation messages only from the configured peer and room, plus server errors for that room.
    private void OnSignalingMessageReceived(object? sender, SignalingMessage message)
    {
        if (_stopped || message.RoomId != _roomId) return;
        bool isPeerMessage = message.From == _targetDeviceId && message.Type is "offer" or "answer" or "ice-candidate";

        bool isServerError = message.From == "server" && message.Type == "error";
        if ((isPeerMessage || isServerError) && !_incoming.Writer.TryWrite(message))
            Fail(new InvalidOperationException("Too many pending signaling messages."));
    }

    // Process negotiation messages sequentially so SDP application cannot race with adding ICE candidates.
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

    // Apply offers/answers and buffer remote candidates until their remote SDP has been applied.
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
                RecordCandidates(answer.sdp, true);

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
                lock (_diagnosticLock) _remoteCandidates.Add(candidate.candidate);
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

    // Apply remote SDP successfully before adding buffered candidates; reversing this order may reject
    // candidates.
    private void ApplyRemoteDescription(RTCSdpType type, string sdp)
    {
        var result = _peer.setRemoteDescription(new RTCSessionDescriptionInit { type = type, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"Remote SDP was rejected: {result}");
        _remoteDescriptionSet = true;
        RecordCandidates(sdp, false);
        while (_pendingCandidates.TryDequeue(out var candidate)) _peer.addIceCandidate(candidate);
    }

    // Drain queued local candidates only after sending the local SDP, so the peer receives their description
    // first.
    private async Task SendCandidatesAsync()
    {
        try
        {
            // Candidate events can fire during setLocalDescription. Queue them until SDP transmission
            // completes.
            await _localDescriptionSent.Task.WaitAsync(_lifetime.Token);
            await foreach (var candidate in _outgoingCandidates.Reader.ReadAllAsync(_lifetime.Token))
                await _signaling.SendIceCandidateAsync(_targetDeviceId, _roomId, candidate, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }

        catch (Exception exception) { Fail(exception); }
    }

    // Accept one channel labeled windowwise per session and attach the same handlers to incoming channels.
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
        // An incoming channel may already be open before handlers are attached, so inspect its current state as
        // well.
        if (channel.readyState == RTCDataChannelState.open) NotifyOpened();
    }

    // Complete the connection wait and raise Opened only once, even if duplicate open notifications arrive.
    private void NotifyOpened()
    {
        if (!_stopped && _opened.TrySetResult()) Opened?.Invoke(this, EventArgs.Empty);
    }

    // Require an open channel and reject sends when its buffer is full so callers can retry later.
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

    // Apply the smaller of the local limit and the remote SCTP limit.
    private void ValidateMessageSize(int size)
    {
        // Retain the conservative local message limit even when the peer reports zero or no limit.
        long remoteLimit = _peer.sctp?.maxMessageSize ?? 0;
        long limit = remoteLimit > 0 ? Math.Min(MaxMessageSize, remoteLimit) : MaxMessageSize;
        if (size > limit)
            throw new ArgumentOutOfRangeException(nameof(size), $"Messages must be at most {limit} bytes.");
    }

    // Validate the expected SDP type and require a nonempty SDP string in the payload.
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

    // End this session when signaling is lost. Reconnecting requires a new service instance.
    private void OnSignalingDisconnected(object? sender, EventArgs e) =>
        Fail(new InvalidOperationException("The signaling connection was closed."));

    // Report only the first failure so overlapping close events do not display duplicate errors.
    private void Fail(Exception exception)
    {
        if (Stop()) ErrorOccurred?.Invoke(this, exception);
    }

    // Stop queues and signaling subscriptions. Dispose subsequently releases the channel and peer resources.
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

    // Reject new operations once the session is stopping or has stopped.
    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    // Return the same cleanup task when multiple owners request disposal.
    public ValueTask DisposeAsync()
    {
        lock (_channelLock) { return new ValueTask(_disposeTask ??= DisposeCoreAsync()); }
    }

    // Wait for message-processing and candidate-sending tasks before closing the channel and peer to reduce
    // shutdown races.
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
