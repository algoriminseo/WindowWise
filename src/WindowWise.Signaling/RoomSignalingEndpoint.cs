using System.Net.WebSockets;
using System.Text.Json;

namespace WindowWise.Signaling;

public static class RoomSignalingEndpoint
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Share one room registry across signaling requests.
    public static IServiceCollection AddRoomSignaling(this IServiceCollection services) =>
        services.AddSingleton<RoomRegistry>();

    // Require an authenticated user with a subject claim before accepting a signaling connection.
    public static void MapRoomSignaling(this WebApplication app) =>
        app.MapGet("/signal", HandleAsync)
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireClaim("sub"));

    // Create or join a room, coordinate peer readiness, and route signaling within the pairing.
    private static async Task HandleAsync(HttpContext context, RoomRegistry rooms)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        await using var peer = new RoomPeer(socket, deadline);
        ShareRoom? room = null;
        try
        {
            var first = await ReceiveAsync(socket, deadline.Token);
            if (first is null) return;
            room = first.Type switch
            {
                "create-room" => rooms.Create(peer),
                "join-room" when IsRoomCode(first.RoomId) => rooms.Join(first.RoomId!, peer),
                _ => throw new RoomFailure("invalid-room-request")
            };
            await SendControlAsync(peer, room, "room-joined", new { code = room.Code }, deadline.Token);

            if (ReferenceEquals(room.Guest, peer))
            {
                await Task.WhenAll(
                    SendControlAsync(room.Host, room, "room-matched", new { peerId = peer.Id, initiate = true }, deadline.Token),
                    SendControlAsync(peer, room, "room-matched", new { peerId = room.Host.Id, initiate = false }, deadline.Token));
            }

            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveAsync(socket, deadline.Token);
                if (message is null) return;
                if (message.Type == "room-ready")
                {
                    if (rooms.Ready(room, peer, message.RoomId))
                    {
                        await Task.WhenAll(
                            SendControlAsync(room.Host, room, "room-start", null, deadline.Token),
                            SendControlAsync(room.Guest!, room, "room-start", null, deadline.Token));
                    }
                    continue;
                }
                if (message.Type is not ("offer" or "answer" or "ice-candidate"))
                    throw new RoomFailure("invalid-room-request");

                RoomPeer target;
                try { target = rooms.ResolveTarget(room, peer, message); }
                catch (RoomFailure) { continue; }
                // Derive the sender from the authenticated socket so clients cannot spoof another peer.
                await target.SendAsync(message with { From = peer.Id, To = target.Id }, deadline.Token);
            }
        }
        catch (RoomFailure failure)
        {
            try
            {
                await SendControlAsync(peer, room, "error", new { code = failure.Code }, deadline.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidOperationException) { }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidDataException) { }
        finally
        {
            // Leaving ends this pairing; an old room code cannot attach to a new session.
            if (room is not null) rooms.Close(room, peer)?.Abort();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await peer.CloseAsync(timeout.Token); }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or InvalidOperationException) { }
            }
        }
    }

    // Accept only the 20-character uppercase hexadecimal format issued by the server.
    private static bool IsRoomCode(string? code) =>
        code?.Length == 20 && code.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    // Send a server control message with the session ID when a room has been assigned.
    private static Task SendControlAsync(RoomPeer peer, ShareRoom? room, string type, object? payload, CancellationToken token) =>
        peer.SendAsync(new RoomSignal(type, "server", peer.Id, room?.SessionId,
            payload is null ? null : JsonSerializer.SerializeToElement(payload, Json)), token);

    // Reassemble WebSocket text fragments into one bounded JSON message.
    private static async Task<RoomSignal?> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        using var bytes = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || bytes.Length + result.Count > 64 * 1024)
                throw new InvalidDataException("Invalid signaling message.");
            bytes.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return JsonSerializer.Deserialize<RoomSignal>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), Json)
                    ?? throw new InvalidDataException("Missing signaling message.");
        }
    }
}

internal sealed record RoomSignal(string Type, string? From, string? To, string? RoomId, JsonElement? Payload);

internal sealed class RoomPeer(WebSocket socket, CancellationTokenSource pairingDeadline) : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _lifetimeLock = new();
    private int _pendingWrites;
    private TaskCompletionSource? _disposal;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public CancellationTokenSource PairingDeadline { get; } = pairingDeadline;

    // Serialize socket writes and bound the wait so a slow peer cannot stall the sender indefinitely.
    public async Task SendAsync(RoomSignal message, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, RoomSignalingEndpoint.Json);
        await EnterWriteAsync(timeout.Token);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token); }
        finally { ExitWrite(); }
    }

    // Send a normal close frame without overlapping an in-flight socket write.
    public async Task CloseAsync(CancellationToken token)
    {
        await EnterWriteAsync(token);
        try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Room closed", token); }
        finally { ExitWrite(); }
    }

    // Stop the remaining peer immediately when the pairing ends.
    public void Abort() => socket.Abort();

    // Count writers before they wait so disposal also accounts for queued operations.
    private async Task EnterWriteAsync(CancellationToken token)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _pendingWrites++;
        }

        try { await _sendGate.WaitAsync(token); }
        catch
        {
            CompleteWrite();
            throw;
        }
    }

    private void ExitWrite()
    {
        _sendGate.Release();
        CompleteWrite();
    }

    private void CompleteWrite()
    {
        lock (_lifetimeLock)
        {
            _pendingWrites--;
            DisposeGateWhenIdle();
        }
    }

    // Reject new writers and wait for admitted sends and close frames to finish.
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeLock)
        {
            if (_disposal is null)
            {
                _disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DisposeGateWhenIdle();
            }
            return new ValueTask(_disposal.Task);
        }
    }

    // Call only while holding the lifetime lock.
    private void DisposeGateWhenIdle()
    {
        if (_disposal is null || _pendingWrites != 0) return;
        _sendGate.Dispose();
        _disposal.SetResult();
    }
}
