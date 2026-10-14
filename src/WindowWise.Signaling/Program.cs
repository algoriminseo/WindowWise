using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();


var peers = new ConcurrentDictionary<string, SignalingPeer>();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.UseWebSockets();




sealed record SignalingMessage(
    String Type, String From, String? To, String? RoomId, JsonElement? Payload);



//send byte array from signaling server to client and prevent concurrent sends using a semaphore Server -> Client
sealed class SignalingPeer(WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);


 
    public async Task SendAsync(SignalingMessage message, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        byte [] bytes = JsonSerializer.SerializeToUtf8Bytes(message, options);

        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            if(socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("Peer is disconnected");
            }

            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);


        }
        finally
        {
            _sendLock.Release();
        }   

    }



}
