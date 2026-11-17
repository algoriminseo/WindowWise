using System.Security.Cryptography;

namespace WindowWise.Signaling;

internal sealed class RoomRegistry
{
    private const int MaxRooms = 256;
    private readonly object _gate = new();
    private readonly Dictionary<string, ShareRoom> _rooms = new(StringComparer.Ordinal);

    // Enforce the room limit and register a new room with a unique invitation code.
    public ShareRoom Create(RoomPeer host)
    {
        lock (_gate)
        {
            if (_rooms.Count >= MaxRooms)
                throw new RoomFailure("room-capacity");
            string code;
            do { code = Convert.ToHexString(RandomNumberGenerator.GetBytes(10)); }
            while (_rooms.ContainsKey(code));
            var room = new ShareRoom(code, host);
            _rooms.Add(code, room);
            return room;
        }
    }

    // Reserve the guest slot under the lock so concurrent requests cannot claim it twice.
    public ShareRoom Join(string code, RoomPeer guest)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(code, out var room))
                throw new RoomFailure("room-not-found");
            if (room.Guest is not null)
                throw new RoomFailure("room-full");
            room.Guest = guest;
            return room;
        }
    }

    // Start negotiation only after both peers have prepared the current attempt.
    public bool Ready(ShareRoom room, RoomPeer peer, RoomSignal message)
    {
        lock (_gate)
        {
            EnsureActive(room, message.RoomId);
            if (message.Attempt != room.Attempt || room.Started)
                return false;

            if (ReferenceEquals(room.Host, peer))
                room.HostReady = true;
            else if (ReferenceEquals(room.Guest, peer))
                room.GuestReady = true;
            else
                throw new RoomFailure("room-forbidden");

            if (!room.HostReady || !room.GuestReady)
                return false;

            room.Host.PairingDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            room.Guest!.PairingDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            room.Started = true;
            return true;
        }
    }

    // Keep the direct connection only when both peers report an open channel.
    public bool? ReportDirectResult(ShareRoom room, RoomPeer peer, RoomSignal message, bool opened)
    {
        lock (_gate)
        {
            EnsureActive(room, message.RoomId);
            if (message.Attempt != 1 || room.Attempt != 1 || !room.Started || room.DirectDecisionSent)
                return null;

            if (ReferenceEquals(room.Host, peer)) room.HostDirectOpened ??= opened;
            else if (ReferenceEquals(room.Guest, peer)) room.GuestDirectOpened ??= opened;
            else throw new RoomFailure("room-forbidden");

            if (room.HostDirectOpened is null || room.GuestDirectOpened is null) return null;
            room.DirectDecisionSent = true;
            bool useTurn = room.HostDirectOpened == false || room.GuestDirectOpened == false;
            if (useTurn)
            {
                room.Attempt = 2;
                room.HostReady = room.GuestReady = room.Started = false;
            }
            return useTurn;
        }
    }

    // Verify room membership, the intended recipient, and which peer may send an offer or answer.
    public RoomPeer ResolveTarget(ShareRoom room, RoomPeer sender, RoomSignal message)
    {
        lock (_gate)
        {
            EnsureActive(room, message.RoomId);
            bool host = ReferenceEquals(room.Host, sender);
            if (!host && !ReferenceEquals(room.Guest, sender))
                throw new RoomFailure("room-forbidden");
            var target = host ? room.Guest : room.Host;
            if (!room.Started || message.Attempt != room.Attempt || target is null || target.Id != message.To)
                throw new RoomFailure("room-forbidden");
            if ((message.Type == "offer" && !host) || (message.Type == "answer" && host))
                throw new RoomFailure("room-forbidden");
            return target;
        }
    }

    // Remove this pairing and return the remaining peer so its connection can be closed.
    public RoomPeer? Close(ShareRoom room, RoomPeer leaving)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(room.Code, out var current) || !ReferenceEquals(current, room))
                return null;
            _rooms.Remove(room.Code);
            return ReferenceEquals(room.Host, leaving) ? room.Guest : room.Host;
        }
    }

    // Reject messages for stale sessions or rooms that have already been closed.
    private void EnsureActive(ShareRoom room, string? sessionId)
    {
        if (room.SessionId != sessionId || !_rooms.TryGetValue(room.Code, out var current) ||
            !ReferenceEquals(current, room))
            throw new RoomFailure("room-forbidden");
    }
}

internal sealed class ShareRoom(string code, RoomPeer host)
{
    public string Code { get; } = code;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public RoomPeer Host { get; } = host;
    public RoomPeer? Guest { get; set; }
    public bool HostReady { get; set; }
    public bool GuestReady { get; set; }
    public bool Started { get; set; }

    public int Attempt { get; set; } = 1;
    public bool? HostDirectOpened { get; set; }
    public bool? GuestDirectOpened { get; set; }
    public bool DirectDecisionSent { get; set; }
}

internal sealed class RoomFailure(string code) : Exception(code)
{
    public string Code { get; } = code;
}
