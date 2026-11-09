using System.Text.Json;
using WindowWise.Models;

namespace WindowWise.Services;

/// <summary>Coordinate room pairing before the WebRTC transport starts negotiation.</summary>
public sealed class RoomPairingSession : IDisposable
{
    private readonly RemoteSignalingClient _signaling;
    private readonly TaskCompletionSource<RoomEntry> _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RoomMatch> _matched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _start = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RoomPairingException> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Subscribe before connecting so an immediate server response is not missed.
    public RoomPairingSession(RemoteSignalingClient signaling)
    {
        _signaling = signaling;
        signaling.MessageReceived += OnMessage;
        signaling.Disconnected += OnDisconnected;
    }

    // Normalize pasted codes by trimming whitespace, removing hyphens, and converting to uppercase.
    public static string NormalizeCode(string text) =>
        text.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();

    // Check the code format before sending a join request.
    public static bool IsValidCode(string code) =>
        code.Length == 20 && code.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    // Create a room when no code is provided, or join the room identified by the code.
    public async Task<RoomEntry> JoinAsync(string? code, CancellationToken token)
    {
        await _signaling.SendAsync(new SignalingMessage
        {
            Type = code is null ? "create-room" : "join-room",
            From = "",
            RoomId = code
        }, token);
        return await WaitAsync(_joined.Task, token);
    }

    // Wait for the server to assign the other peer and the offerer role.
    public Task<RoomMatch> WaitForPeerAsync(CancellationToken token) => WaitAsync(_matched.Task, token);

    // Report that the local transport is ready and wait for the server to release both peers.
    public async Task ReadyAsync(string sessionId, CancellationToken token)
    {
        await _signaling.SendAsync(new SignalingMessage
        {
            Type = "room-ready",
            From = "",
            RoomId = sessionId
        }, token);
        await WaitAsync(_start.Task, token);
    }

    // Stop waiting if the server reports a failure or the caller cancels.
    private async Task<T> WaitAsync<T>(Task<T> task, CancellationToken token)
    {
        if (await Task.WhenAny(task, _failure.Task).WaitAsync(token) == _failure.Task)
            throw await _failure.Task;
        return await task;
    }

    // Handle server control messages and ignore pairing updates for a different session.
    private void OnMessage(object? sender, SignalingMessage message)
    {
        if (message.From != "server") return;
        try
        {
            if (message.Type == "error")
            {
                string? code = (message.Payload as JsonElement?)?.GetProperty("code").GetString();
                Fail(code is "room-not-found" or "room-full" or "room-capacity" or "room-forbidden" or "invalid-room-request"
                    ? code : "room-protocol-error");
            }
            else if (message.Type == "room-joined" && !string.IsNullOrWhiteSpace(message.RoomId) &&
                     message.Payload is JsonElement joined && joined.GetProperty("code").GetString() is string code && IsValidCode(code))
            {
                _joined.TrySetResult(new RoomEntry(message.RoomId, code));
            }
            else if (_joined.Task.IsCompletedSuccessfully && message.RoomId == _joined.Task.Result.SessionId)
            {
                if (message.Type == "room-matched" && message.Payload is JsonElement match)
                {
                    string peerId = match.GetProperty("peerId").GetString()
                        ?? throw new JsonException("Missing peer ID.");
                    _matched.TrySetResult(new RoomMatch(peerId, match.GetProperty("initiate").GetBoolean()));
                }
                else if (message.Type == "room-start")
                {
                    _start.TrySetResult(true);
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Fail("room-protocol-error");
        }
    }

    // Fail pending pairing operations when the signaling connection closes.
    private void OnDisconnected(object? sender, EventArgs e) => Fail("room-closed");
    // Preserve the first failure so a later disconnect does not hide the original error.
    private void Fail(string code) => _failure.TrySetResult(new RoomPairingException(code));

    // Unsubscribe from pairing events without closing the socket used by WebRTC.
    public void Dispose()
    {
        _signaling.MessageReceived -= OnMessage;
        _signaling.Disconnected -= OnDisconnected;
    }
}

public sealed record RoomEntry(string SessionId, string Code);
public sealed record RoomMatch(string PeerId, bool Initiate);
public sealed class RoomPairingException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
