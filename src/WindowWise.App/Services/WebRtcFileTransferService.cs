using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace WindowWise.Services;

public sealed record RemoteFileOffer(Guid Id, string Name, long Size);
public enum RemoteTransferState { AwaitingApproval, Sending, Sent, Receiving, Received, Canceled, Failed }
public sealed record RemoteTransferProgress(Guid Id, string Name, bool Receiving, long Bytes, long Total, RemoteTransferState State);
public sealed class RemoteFileTransferException(string code) : IOException(code)
{
    public string Code { get; } = code;
}
public sealed record RemoteFileReceived(Guid Id, string Name, string Path, long Size);

public sealed class WebRtcFileTransferService : IAsyncDisposable
{
    private const string Protocol = "windowwise-file-v1";
    private const int HeaderSize = 24;
    private const int ChunkSize = 12 * 1024;
    private readonly WebRtcDataChannelService _transport;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<Frame> _frames = Channel.CreateBounded<Frame>(64);
    private readonly Task _pump;
    private readonly object _disposeLock = new();
    private Outgoing? _outgoing;
    private Incoming? _incoming;
    private Task? _disposeTask;

    public event EventHandler<RemoteFileOffer>? FileOffered;
    public event EventHandler<RemoteTransferProgress>? ProgressChanged;
    public event EventHandler<RemoteFileReceived>? FileReceived;
    public event EventHandler<Exception>? ErrorOccurred;

    // Subscribe to the transport and serialize incoming protocol frames.
    public WebRtcFileTransferService(WebRtcDataChannelService transport)
    {
        _transport = transport;
        _transport.TextReceived += OnText;
        _transport.BinaryReceived += OnBinary;
        _transport.Closed += OnClosed;
        _pump = ProcessFramesAsync();
    }

    // Send one file with receiver approval, per-chunk acknowledgments, and a final hash check.
    public async Task SendFileAsync(string path, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var transfer = new Outgoing(new RemoteFileOffer(Guid.NewGuid(), System.IO.Path.GetFileName(path), file.Length));
        await _stateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_incoming is not null || _outgoing is not null)
                throw new RemoteFileTransferException("transfer-busy");
            _outgoing = transfer;
        }
        finally { _stateGate.Release(); }

        bool completed = false;
        try
        {
            Send(new Message("offer", transfer.Offer.Id, transfer.Offer.Name, transfer.Offer.Size));
            Report(transfer.Offer, false, 0, RemoteTransferState.AwaitingApproval);
            await ReadReplyAsync(transfer, "accepted", 0, TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            var buffer = new byte[ChunkSize];
            int count;
            while ((count = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                var frame = new byte[HeaderSize + count];
                transfer.Offer.Id.TryWriteBytes(frame);
                BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(16), offset);
                buffer.AsSpan(0, count).CopyTo(frame.AsSpan(HeaderSize));
                hash.AppendData(buffer, 0, count);
                _transport.SendBinary(frame);
                offset += count;
                await ReadReplyAsync(transfer, "ack", offset, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                Report(transfer.Offer, false, offset, RemoteTransferState.Sending);
            }
            Send(new Message("finish", transfer.Offer.Id, Hash: Convert.ToHexString(hash.GetHashAndReset())));
            await ReadReplyAsync(transfer, "completed", offset, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            completed = true;
            Report(transfer.Offer, false, offset, RemoteTransferState.Sent);
        }
        finally
        {
            if (!completed) TrySend(new Message("cancel", transfer.Offer.Id));
            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { if (ReferenceEquals(_outgoing, transfer)) _outgoing = null; }
            finally { _stateGate.Release(); }
        }
    }

    // Create a temporary file only after the user chooses a destination and approves receipt.
    public async Task AcceptAsync(Guid id, string destinationPath, CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            var incoming = _incoming;
            if (incoming is null || incoming.Offer.Id != id || incoming.Stream is not null)
                throw new RemoteFileTransferException("offer-expired");
            string destination = System.IO.Path.GetFullPath(destinationPath);
            if (File.Exists(destination)) throw new RemoteFileTransferException("destination-exists");
            incoming.Destination = destination;
            incoming.Temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, $".windowwise-{Guid.NewGuid():N}.partial");
            try
            {
                incoming.Stream = new FileStream(incoming.Temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, ChunkSize, FileOptions.Asynchronous);
                incoming.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Send(new Message("accepted", id));
                Report(incoming.Offer, true, 0, RemoteTransferState.Receiving);
            }
            catch
            {
                await ClearIncomingAsync().ConfigureAwait(false);
                TrySend(new Message("error", id, Reason: "receive-open-failed"));
                throw;
            }
        }
        finally { _stateGate.Release(); }
    }

    // Reject a pending offer or cancel an active incoming file and remove its temporary data.
    public async Task CancelIncomingAsync(Guid id)
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_incoming?.Offer.Id != id) return;
            var offer = _incoming.Offer;
            TrySend(new Message(_incoming.Stream is null ? "rejected" : "cancel", id, Reason: "receiver-canceled"));
            await ClearIncomingAsync().ConfigureAwait(false);
            Report(offer, true, 0, RemoteTransferState.Canceled);
        }
        finally { _stateGate.Release(); }
    }

    // Queue text controls without doing file I/O on the WebRTC callback thread.
    private void OnText(object? sender, string text) => Enqueue(new Frame(text, null));

    // Queue binary chunks in the same ordered stream as their control messages.
    private void OnBinary(object? sender, byte[] data) => Enqueue(new Frame(null, data));

    // Bound memory consumption if a peer sends frames faster than the disk can accept them.
    private void Enqueue(Frame frame)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (!_frames.Writer.TryWrite(frame))
        {
            _lifetime.Cancel();
            ErrorOccurred?.Invoke(this, new IOException("수신 대기열이 가득 찼습니다. 다시 연결하세요."));
        }
    }

    // Process incoming controls and chunks sequentially, cleaning partial files on failure.
    private async Task ProcessFramesAsync()
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await _stateGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (frame.Text is not null) await HandleControlAsync(frame.Text).ConfigureAwait(false);
                    else await WriteChunkAsync(frame.Data!).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (_incoming is not null)
                    {
                        var offer = _incoming.Offer;
                        TrySend(new Message("error", offer.Id, Reason: "receive-failed"));
                        await ClearIncomingAsync().ConfigureAwait(false);
                        Report(offer, true, 0, RemoteTransferState.Failed);
                    }
                    _outgoing?.Replies.Writer.TryWrite(new Message("error", _outgoing.Offer.Id, Reason: "receive-failed"));
                    ErrorOccurred?.Invoke(this, exception);
                }
                finally { _stateGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            await _stateGate.WaitAsync().ConfigureAwait(false);
            try { await ClearIncomingAsync().ConfigureAwait(false); }
            finally { _stateGate.Release(); }
        }
    }

    // Route replies to the sender and handle incoming offers, completion, or cancellation.
    private async Task HandleControlAsync(string json)
    {
        var message = JsonSerializer.Deserialize<Message>(json) ?? throw new JsonException("Empty control message.");
        if (message.Protocol != Protocol) return;
        if (_outgoing?.Offer.Id == message.Id && message.Type is "accepted" or "ack" or "completed" or "rejected" or "error" or "cancel")
        {
            if (!_outgoing.Replies.Writer.TryWrite(message)) throw new IOException("Too many transfer replies.");
            return;
        }
        if (message.Type == "offer")
        {
            if (_incoming is not null || _outgoing is not null)
            {
                Send(new Message("rejected", message.Id, Reason: "peer-busy"));
                return;
            }
            if (message.Id == Guid.Empty || message.Size < 0 || string.IsNullOrWhiteSpace(message.Name) ||
                message.Name.Length > 255 || message.Name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                message.Name is "." or "..")
                throw new IOException("Invalid file offer.");
            var offer = new RemoteFileOffer(message.Id, message.Name, message.Size);
            _incoming = new Incoming(offer);
            FileOffered?.Invoke(this, offer);
        }
        else if (_incoming?.Offer.Id == message.Id)
        {
            if (message.Type == "finish") await FinishIncomingAsync(message).ConfigureAwait(false);
            else if (message.Type is "cancel" or "error")
            {
                var offer = _incoming.Offer;
                await ClearIncomingAsync().ConfigureAwait(false);
                Report(offer, true, 0, RemoteTransferState.Canceled);
            }
        }
    }

    // Validate a chunk's transfer ID and offset, write it, and acknowledge written bytes.
    private async Task WriteChunkAsync(byte[] data)
    {
        var incoming = _incoming;
        if (incoming?.Stream is null || data.Length <= HeaderSize)
            throw new IOException("Unexpected file chunk.");
        int count = data.Length - HeaderSize;
        if (count > ChunkSize || new Guid(data.AsSpan(0, 16)) != incoming.Offer.Id ||
            BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16)) != incoming.Bytes ||
            count > incoming.Offer.Size - incoming.Bytes)
            throw new IOException("Invalid file chunk or length.");
        await incoming.Stream.WriteAsync(data.AsMemory(HeaderSize), _lifetime.Token).ConfigureAwait(false);
        incoming.Hash!.AppendData(data, HeaderSize, count);
        incoming.Bytes += count;
        Send(new Message("ack", incoming.Offer.Id, Offset: incoming.Bytes));
        Report(incoming.Offer, true, incoming.Bytes, RemoteTransferState.Receiving);
    }

    // Verify length and SHA-256 before atomically publishing the received file without overwriting.
    private async Task FinishIncomingAsync(Message message)
    {
        var incoming = _incoming!;
        if (incoming.Stream is null || incoming.Bytes != incoming.Offer.Size ||
            !string.Equals(Convert.ToHexString(incoming.Hash!.GetHashAndReset()), message.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("File length or SHA-256 mismatch.");
        await incoming.Stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        await incoming.Stream.DisposeAsync().ConfigureAwait(false);
        incoming.Stream = null;
        File.Move(incoming.Temporary!, incoming.Destination!, overwrite: false);
        var received = new RemoteFileReceived(incoming.Offer.Id, incoming.Offer.Name, incoming.Destination!, incoming.Bytes);
        incoming.Temporary = null;
        await ClearIncomingAsync().ConfigureAwait(false);
        Send(new Message("completed", message.Id, Offset: received.Size));
        Report(incoming.Offer, true, received.Size, RemoteTransferState.Received);
        FileReceived?.Invoke(this, received);
    }

    // Wait for the expected acknowledgment and surface rejection or remote cancellation immediately.
    private static async Task ReadReplyAsync(Outgoing transfer, string expected, long offset, TimeSpan timeout, CancellationToken token)
    {
        var reply = await transfer.Replies.Reader.ReadAsync(token).AsTask().WaitAsync(timeout, token).ConfigureAwait(false);
        if (reply.Type is "rejected" or "error" or "cancel")
            throw new RemoteFileTransferException(reply.Reason ?? "remote-canceled");
        if (reply.Type != expected || (expected is "ack" or "completed" && reply.Offset != offset))
            throw new IOException("Unexpected transfer acknowledgment.");
    }

    // Serialize protocol controls as DataChannel text messages.
    private void Send(Message message) => _transport.SendText(JsonSerializer.Serialize(message));

    // Attempt cancellation delivery even when the transport may already be closed.
    private void TrySend(Message message)
    {
        try { if (_transport.IsOpen) Send(message); }
        catch (InvalidOperationException) { }
    }

    // Report the acknowledged byte count and current transfer phase.
    private void Report(RemoteFileOffer offer, bool receiving, long bytes, RemoteTransferState state) =>
        ProgressChanged?.Invoke(this, new RemoteTransferProgress(offer.Id, offer.Name, receiving, bytes, offer.Size, state));

    // Dispose the receiver's stream and hash and remove an incomplete temporary file.
    private async Task ClearIncomingAsync()
    {
        var incoming = _incoming;
        _incoming = null;
        if (incoming is null) return;
        try { if (incoming.Stream is not null) await incoming.Stream.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            incoming.Hash?.Dispose();
            if (incoming.Temporary is not null) File.Delete(incoming.Temporary);
        }
    }

    // Cancel active waits and disk processing when the transport closes.
    private void OnClosed(object? sender, EventArgs e) => _lifetime.Cancel();

    // Detach callbacks and wait for partial-file cleanup without owning the transport itself.
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= Task.Run(async () =>
            {
                _transport.TextReceived -= OnText;
                _transport.BinaryReceived -= OnBinary;
                _transport.Closed -= OnClosed;
                _lifetime.Cancel();
                _frames.Writer.TryComplete();
                await _pump.ConfigureAwait(false);
            });
            return new ValueTask(_disposeTask);
        }
    }

    private sealed record Message(string Type, Guid Id, string? Name = null, long Size = 0,
        long Offset = 0, string? Hash = null, string? Reason = null)
    {
        public string Protocol { get; init; } = WebRtcFileTransferService.Protocol;
    }
    private sealed record Frame(string? Text, byte[]? Data);
    private sealed class Outgoing(RemoteFileOffer offer)
    {
        public RemoteFileOffer Offer { get; } = offer;
        public Channel<Message> Replies { get; } = Channel.CreateBounded<Message>(8);
    }
    private sealed class Incoming(RemoteFileOffer offer)
    {
        public RemoteFileOffer Offer { get; } = offer;
        public FileStream? Stream { get; set; }
        public IncrementalHash? Hash { get; set; }
        public string? Temporary { get; set; }
        public string? Destination { get; set; }
        public long Bytes { get; set; }
    }
}
