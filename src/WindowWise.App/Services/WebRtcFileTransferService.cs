using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using WindowWise.Models;

namespace WindowWise.Services;

public sealed record RemoteFileOffer(Guid Id, string Name, long Size);
public enum RemoteTransferState { AwaitingApproval, Sending, Sent, Receiving, Received, Canceled, Failed }
public sealed record RemoteTransferProgress(Guid Id, string Name, bool Receiving, long Bytes, long Total, RemoteTransferState State);
public sealed class RemoteFileTransferException(string code) : IOException(code)
{
    public string Code { get; } = code;
}
public sealed record RemoteFileReceived(Guid Id, string Name, string Path, long Size);

/// <summary>
/// Manage approval, chunk transfer, and completion acknowledgments for one file over a DataChannel.
/// Write to a temporary file and publish it at the destination only after length and SHA-256 verification
/// succeed. A peer can also list the files this peer shares and download one without an approval prompt,
/// because adding a file to the share list is the sharer's consent.
/// </summary>
public sealed class WebRtcFileTransferService : IAsyncDisposable
{
    private const string Protocol = "windowwise-file-v1";
    // Chunk header: a 16-byte transfer ID (Guid), followed by an 8-byte little-endian file offset (Int64).
    private const int HeaderSize = 24;
    private const int ChunkSize = 12 * 1024;
    // Keep each list page well under the 16 KiB DataChannel message limit, even for long non-ASCII names.
    private const int ListPageBytes = 12 * 1024;
    private readonly WebRtcDataChannelService _transport;
    private readonly RemoteShareList? _shares;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<Frame> _frames = Channel.CreateBounded<Frame>(64);
    private readonly ConcurrentDictionary<Guid, ListRequest> _listRequests = new();
    private readonly Task _pump;
    private readonly object _disposeLock = new();
    private Outgoing? _outgoing;
    private Incoming? _incoming;
    private Requested? _requested;
    private Task? _disposeTask;

    public event EventHandler<RemoteFileOffer>? FileOffered;
    public event EventHandler<RemoteTransferProgress>? ProgressChanged;
    public event EventHandler<RemoteFileReceived>? FileReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    // Raised when the peer adds or removes shared files, so a visible list can refresh itself.
    public event EventHandler? RemoteFilesChanged;

    // Place text controls and binary chunks into the same processing queue. Without a share list, list requests
    // return an empty list and download requests are refused.
    public WebRtcFileTransferService(WebRtcDataChannelService transport, RemoteShareList? shares = null)
    {
        _transport = transport;
        _shares = shares;
        _transport.TextReceived += OnText;
        _transport.BinaryReceived += OnBinary;
        _transport.Closed += OnClosed;
        if (_shares is not null) _shares.Changed += OnSharesChanged;
        _pump = ProcessFramesAsync();
    }

    // Transfer one file per session at a time: receiver approval, per-chunk acknowledgments, then final hash
    // verification.
    public Task SendFileAsync(string path, CancellationToken cancellationToken = default) =>
        SendCoreAsync(path, null, cancellationToken);

    // Ask the peer for its shared files. Large lists arrive over several pages.
    public async Task<IReadOnlyList<SharedFileInfo>> RequestFilesAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var request = new ListRequest();
        var id = Guid.NewGuid();
        _listRequests[id] = request;
        try
        {
            Send(new Message("list-request", id));
            return await request.Result.Task.WaitAsync(TimeSpan.FromSeconds(30), linked.Token).ConfigureAwait(false);
        }
        finally { _listRequests.TryRemove(id, out _); }
    }

    // Download one file from the peer's share list into destinationPath. The returned task completes after the
    // hash check passes and the file is published, or fails with a RemoteFileTransferException code.
    public async Task<RemoteFileReceived> DownloadAsync(SharedFileInfo file, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        string destination = System.IO.Path.GetFullPath(destinationPath);
        if (File.Exists(destination)) throw new RemoteFileTransferException("destination-exists");
        var request = new Requested(file.Id, destination);
        await _stateGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_incoming is not null || _outgoing is not null || _requested is not null)
                throw new RemoteFileTransferException("transfer-busy");
            _requested = request;
            Send(new Message("download", file.Id));
        }
        finally { _stateGate.Release(); }

        try { return await request.Result.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Stop an active receive, or remember the cancellation so a late offer is refused instead of shown.
            await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_requested, request))
                {
                    request.Canceled = true;
                    if (request.OfferId is Guid offerId && _incoming?.Offer.Id == offerId)
                    {
                        var offer = _incoming.Offer;
                        TrySend(new Message("cancel", offerId, Reason: "receiver-canceled"));
                        await ClearIncomingAsync().ConfigureAwait(false);
                        Report(offer, true, 0, RemoteTransferState.Canceled);
                        _requested = null;
                    }
                }
            }
            finally { _stateGate.Release(); }
            throw;
        }
    }

    // Create the temporary file only after approval and destination selection. Returning means receipt has
    // started, not finished.
    public async Task AcceptAsync(Guid id, string destinationPath, CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            var incoming = _incoming;
            if (incoming is null || incoming.Offer.Id != id || incoming.Stream is not null)
                throw new RemoteFileTransferException("offer-expired");
            await OpenIncomingAsync(incoming, destinationPath).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); }
    }

    // Reject an unapproved offer or cancel an active receive, then remove incomplete data.
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
            FailRequested(id, new RemoteFileTransferException("receiver-canceled"));
        }
        finally { _stateGate.Release(); }
    }

    // Send a file either after the receiver approves it, or immediately for a download the receiver requested.
    private async Task SendCoreAsync(string path, Guid? request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var transfer = new Outgoing(new RemoteFileOffer(Guid.NewGuid(), System.IO.Path.GetFileName(path), file.Length));
        await _stateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Allow either an active send or an active receive. Concurrent transfers do not have a queue.
            if (_incoming is not null || _outgoing is not null || _requested is not null)
                throw new RemoteFileTransferException("transfer-busy");
            _outgoing = transfer;
        }
        finally { _stateGate.Release(); }

        bool completed = false;
        try
        {
            Send(new Message("offer", transfer.Offer.Id, transfer.Offer.Name, transfer.Offer.Size, Request: request));
            Report(transfer.Offer, false, 0, RemoteTransferState.AwaitingApproval);
            // A requested download is accepted automatically, so it needs no time for a person to respond.
            await ReadReplyAsync(transfer, "accepted", 0, request is null ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(30),
                token).ConfigureAwait(false);
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

    // Serve a download the peer requested. Failures are reported to the peer through the transfer itself, and to
    // this peer's UI through ErrorOccurred and progress.
    private async Task ServeDownloadAsync(string path, Guid shareId)
    {
        try { await SendCoreAsync(path, shareId, CancellationToken.None).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            TrySend(new Message("error", shareId, Reason: exception is RemoteFileTransferException { Code: "transfer-busy" }
                ? "peer-busy" : "send-failed"));
            ErrorOccurred?.Invoke(this, exception);
        }
    }

    // Open the temporary file for an accepted offer and tell the sender to start. Called with _stateGate held.
    private async Task OpenIncomingAsync(Incoming incoming, string destinationPath)
    {
        string destination = System.IO.Path.GetFullPath(destinationPath);
        if (File.Exists(destination)) throw new RemoteFileTransferException("destination-exists");
        incoming.Destination = destination;
        incoming.Temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, $".windowwise-{Guid.NewGuid():N}.partial");
        try
        {
            incoming.Stream = new FileStream(incoming.Temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, ChunkSize, FileOptions.Asynchronous);
            incoming.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Send(new Message("accepted", incoming.Offer.Id));
            Report(incoming.Offer, true, 0, RemoteTransferState.Receiving);
        }
        catch
        {
            await ClearIncomingAsync().ConfigureAwait(false);
            TrySend(new Message("error", incoming.Offer.Id, Reason: "receive-open-failed"));
            throw;
        }
    }

    // Queue control messages instead of performing disk I/O on the WebRTC callback thread.
    private void OnText(object? sender, string text) => Enqueue(new Frame(text, null));

    // Queue chunks with control messages so completion cannot be processed ahead of pending writes.
    private void OnBinary(object? sender, byte[] data) => Enqueue(new Frame(null, data));

    // Tell the peer its view of this share list is stale. Ignore a closed channel; there is nobody to tell.
    private void OnSharesChanged(object? sender, EventArgs e)
    {
        if (!_lifetime.IsCancellationRequested) TrySend(new Message("list-changed", Guid.Empty));
    }

    // Bound the queue so incoming data cannot exhaust memory when disk processing falls behind.
    private void Enqueue(Frame frame)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (!_frames.Writer.TryWrite(frame))
        {
            _lifetime.Cancel();
            ErrorOccurred?.Invoke(this, new IOException("The receive queue is full. Reconnect and try again."));
        }
    }

    // Process frames sequentially. On receive failure, remove partial files and report the failed transfer.
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
                        FailRequested(offer.Id, new RemoteFileTransferException("receive-failed"));
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
            try
            {
                await ClearIncomingAsync().ConfigureAwait(false);
                _requested?.Result.TrySetCanceled();
                _requested = null;
            }
            finally { _stateGate.Release(); }
            foreach (var request in _listRequests.Values) request.Result.TrySetCanceled();
        }
    }

    // Route ACK, approval, and cancellation replies to the sender; apply offers and completion to receiver
    // state; answer list and download requests from the share list.
    private async Task HandleControlAsync(string json)
    {
        var message = JsonSerializer.Deserialize<Message>(json) ?? throw new JsonException("Empty control message.");
        if (message.Protocol != Protocol) return;
        if (_outgoing?.Offer.Id == message.Id && message.Type is "accepted" or "ack" or "completed" or "rejected" or "error" or "cancel")
        {
            if (!_outgoing.Replies.Writer.TryWrite(message)) throw new IOException("Too many transfer replies.");
            return;
        }
        switch (message.Type)
        {
            case "list-request":
                SendList(message.Id);
                return;
            case "list":
                ReceiveListPage(message);
                return;
            case "list-changed":
                RemoteFilesChanged?.Invoke(this, EventArgs.Empty);
                return;
            case "download":
                if (_shares is null || !_shares.TryGetPath(message.Id, out string path))
                    Send(new Message("error", message.Id, Reason: "not-shared"));
                else if (_incoming is not null || _outgoing is not null || _requested is not null)
                    Send(new Message("error", message.Id, Reason: "peer-busy"));
                // Do not await: the transfer's own replies arrive through this same queue.
                else _ = ServeDownloadAsync(path, message.Id);
                return;
            case "error" when _requested is { OfferId: null } pending && pending.ShareId == message.Id:
                // The sharer refused the request before starting a transfer.
                _requested = null;
                pending.Result.TrySetException(new RemoteFileTransferException(message.Reason ?? "remote-canceled"));
                return;
        }
        if (message.Type == "offer")
        {
            bool requested = message.Request is Guid shareId && _requested?.ShareId == shareId && _requested.OfferId is null;
            if (_incoming is not null || _outgoing is not null || (_requested is not null && !requested))
            {
                Send(new Message("rejected", message.Id, Reason: "peer-busy"));
                return;
            }
            if (message.Id == Guid.Empty || message.Size < 0 || !IsValidFileName(message.Name))
                throw new IOException("Invalid file offer.");
            var offer = new RemoteFileOffer(message.Id, message.Name!, message.Size);
            if (!requested)
            {
                _incoming = new Incoming(offer);
                FileOffered?.Invoke(this, offer);
                return;
            }
            var pending = _requested!;
            if (pending.Canceled)
            {
                _requested = null;
                Send(new Message("rejected", message.Id, Reason: "receiver-canceled"));
                return;
            }
            // The receiver already chose where to save this file when it asked for it.
            pending.OfferId = offer.Id;
            _incoming = new Incoming(offer);
            try { await OpenIncomingAsync(_incoming, pending.Destination).ConfigureAwait(false); }
            catch (Exception exception)
            {
                _requested = null;
                pending.Result.TrySetException(exception);
            }
        }
        else if (_incoming?.Offer.Id == message.Id)
        {
            if (message.Type == "finish") await FinishIncomingAsync(message).ConfigureAwait(false);
            else if (message.Type is "cancel" or "error")
            {
                var offer = _incoming.Offer;
                await ClearIncomingAsync().ConfigureAwait(false);
                Report(offer, true, 0, RemoteTransferState.Canceled);
                FailRequested(offer.Id, new RemoteFileTransferException(message.Reason ?? "remote-canceled"));
            }
        }
    }

    // Answer a list request in pages that each fit within one DataChannel message.
    private void SendList(Guid requestId)
    {
        var files = _shares?.Files ?? [];
        var pages = new List<List<SharedFileInfo>> { new() };
        int pageBytes = 0;
        foreach (var file in files)
        {
            int bytes = JsonSerializer.SerializeToUtf8Bytes(file).Length + 1;
            if (pages[^1].Count > 0 && pageBytes + bytes > ListPageBytes)
            {
                pages.Add(new());
                pageBytes = 0;
            }
            pages[^1].Add(file);
            pageBytes += bytes;
        }
        for (int page = 0; page < pages.Count; page++)
            Send(new Message("list", requestId, Files: pages[page].ToArray(), Page: page, Pages: pages.Count));
    }

    // Collect list pages for a pending request. Names are validated here because they become local file names.
    private void ReceiveListPage(Message message)
    {
        if (!_listRequests.TryGetValue(message.Id, out var request)) return;
        if (message.Pages is < 1 or > 10_000 || message.Page < 0 || message.Page >= message.Pages ||
            message.Files is null || message.Files.Any(file => file.Id == Guid.Empty || file.Size < 0 || !IsValidFileName(file.Name)))
        {
            request.Result.TrySetException(new RemoteFileTransferException("invalid-list"));
            return;
        }
        request.Pages[message.Page] = message.Files;
        if (request.Pages.Count == message.Pages)
            request.Result.TrySetResult(request.Pages.OrderBy(page => page.Key).SelectMany(page => page.Value).ToArray());
    }

    // Validate transfer ID, offset, and chunk length. Send ACK and progress only after the write completes.
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

    // Verify total length and SHA-256 before moving the temporary file to its final name in the same directory.
    // Never overwrite an existing file.
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
        if (_requested?.OfferId == received.Id)
        {
            _requested.Result.TrySetResult(received);
            _requested = null;
        }
    }

    // End a pending download when its transfer stops before completion. Called with _stateGate held.
    private void FailRequested(Guid offerId, Exception exception)
    {
        if (_requested?.OfferId != offerId) return;
        _requested.Result.TrySetException(exception);
        _requested = null;
    }

    // Reject names that could escape the save folder or are not valid on Windows.
    private static bool IsValidFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 255 &&
        name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    // Validate acknowledgment type and offset; surface peer rejection/cancellation immediately instead of
    // waiting for timeout.
    private static async Task ReadReplyAsync(Outgoing transfer, string expected, long offset, TimeSpan timeout, CancellationToken token)
    {
        var reply = await transfer.Replies.Reader.ReadAsync(token).AsTask().WaitAsync(timeout, token).ConfigureAwait(false);
        if (reply.Type is "rejected" or "error" or "cancel")
            throw new RemoteFileTransferException(reply.Reason ?? "remote-canceled");
        if (reply.Type != expected || (expected is "ack" or "completed" && reply.Offset != offset))
            throw new IOException("Unexpected transfer acknowledgment.");
    }

    // Serialize versioned protocol controls as DataChannel text messages.
    private void Send(Message message) => _transport.SendText(JsonSerializer.Serialize(message));

    // If the channel has already closed during cleanup, do not let cancellation-delivery failure mask the
    // original error.
    private void TrySend(Message message)
    {
        try { if (_transport.IsOpen) Send(message); }
        catch (InvalidOperationException) { }
    }

    // Report bytes acknowledged by the peer on sends, or written to disk on receives.
    private void Report(RemoteFileOffer offer, bool receiving, long bytes, RemoteTransferState state) =>
        ProgressChanged?.Invoke(this, new RemoteTransferProgress(offer.Id, offer.Name, receiving, bytes, offer.Size, state));

    // Dispose the receive stream/hash and delete only incomplete temporary files. Preserve verified final
    // files.
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

    // Cancel approval/ACK waits and disk processing when the channel closes.
    private void OnClosed(object? sender, EventArgs e) => _lifetime.Cancel();

    // Unsubscribe callbacks and wait for temporary-file cleanup. The caller owns the underlying channel
    // lifetime.
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= Task.Run(async () =>
            {
                _transport.TextReceived -= OnText;
                _transport.BinaryReceived -= OnBinary;
                _transport.Closed -= OnClosed;
                if (_shares is not null) _shares.Changed -= OnSharesChanged;
                _lifetime.Cancel();
                _frames.Writer.TryComplete();
                await _pump.ConfigureAwait(false);
            });
            return new ValueTask(_disposeTask);
        }
    }

    // Request links a sender's offer to the share ID the receiver asked for. Files, Page, and Pages carry a list page.
    private sealed record Message(string Type, Guid Id, string? Name = null, long Size = 0,
        long Offset = 0, string? Hash = null, string? Reason = null, Guid? Request = null,
        SharedFileInfo[]? Files = null, int Page = 0, int Pages = 0)
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
    private sealed class ListRequest
    {
        public TaskCompletionSource<IReadOnlyList<SharedFileInfo>> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<int, SharedFileInfo[]> Pages { get; } = new();
    }
    // A download this peer asked for. OfferId is set once the sharer starts the transfer.
    private sealed class Requested(Guid shareId, string destination)
    {
        public Guid ShareId { get; } = shareId;
        public string Destination { get; } = destination;
        public Guid? OfferId { get; set; }
        public bool Canceled { get; set; }
        public TaskCompletionSource<RemoteFileReceived> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
