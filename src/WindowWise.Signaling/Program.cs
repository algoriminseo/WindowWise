using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using WindowWise.Signaling;
using Microsoft.AspNetCore.Authentication.JwtBearer;



var builder = WebApplication.CreateBuilder(args);

// /signal forwards connection negotiation; /turn-credentials issues temporary Cloudflare TURN credentials.
// File contents travel over the WebRTC channel, either directly or through TURN, rather than through this
// server.
builder.Services.AddTurnCredentials();


builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Auth:Authority"];
        options.Audience = builder.Configuration["Auth:Audience"];
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;

    });
builder.Services.AddAuthorization();



var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapTurnCredentials();



app.MapGet("/me", (HttpContext context) =>
    Results.Ok(new
    {
        userId = context.User.FindFirst("sub")?.Value
    })).RequireAuthorization();

var peers = new ConcurrentDictionary<string, SignalingPeer>();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.UseWebSockets();




// Register each WebSocket by deviceId and forward Offer/Answer/ICE messages to the requested peer.
// deviceId and roomId currently provide no user authentication or room authorization; this signaling server is
// for development tests.
app.Map("/signal", async context =>
{
    string deviceId = context.Request.Query["deviceId"].ToString();

    if (!context.WebSockets.IsWebSocketRequest || string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 128)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var peer = new SignalingPeer(socket);


    if(!peers.TryAdd(deviceId, peer))
    {
        await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Device ID already connected",context.RequestAborted);
        return;
    }

    try
    {
        var buffer = new byte[4096];

        while (socket.State == WebSocketState.Open)
        {
            using var messageStream = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);


                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", context.RequestAborted);
                    return;

                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType, "Only text messages are supported", context.RequestAborted);
                    return;
                }

                if (messageStream.Length + result.Count > 64 * 1024)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Messages up to 64 KiB are supported", context.RequestAborted);
                    return;
                }



                messageStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);


            SignalingMessage? message;

            try
            {
                message = JsonSerializer.Deserialize<SignalingMessage>(messageStream.ToArray(), jsonOptions);
            }
            catch (JsonException)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid message format", context.RequestAborted);
                return;
            }

            if (message is null || message.Type is not ("offer" or "answer" or "ice-candidate") ||
                  string.IsNullOrWhiteSpace(message.To) ||
                  string.IsNullOrWhiteSpace(message.RoomId))
            {
                await peer.SendAsync(
                    new SignalingMessage(
                        "error", "server", deviceId, message?.RoomId,
                        JsonSerializer.SerializeToElement(
                            new { code = "invalid-message" })),
                    jsonOptions, context.RequestAborted);
                continue;
            }


            var forwarded = message with { From = deviceId };

            if (peers.TryGetValue(message.To, out var target))
            {
                try
                {
                    await target.SendAsync(forwarded, jsonOptions, context.RequestAborted);
                    continue;

                }
                catch (WebSocketException)
                {

                }
                catch (InvalidOperationException)
                {

                }

            }


            await peer.SendAsync(
                new SignalingMessage(
                    "error", "server", deviceId, message.RoomId,
                    JsonSerializer.SerializeToElement(
                        new { code = "peer-not-found", target = message.To })),
                jsonOptions, context.RequestAborted);



        }
    }
    catch (OperationCanceledException)
    {

    }
    catch (WebSocketException)
    {

    }
    finally
    {
        peers.TryRemove(deviceId, out _);
    }



});


app.Run();




sealed record SignalingMessage(
    String Type, String From, String? To, String? RoomId, JsonElement? Payload);


// Serialize sends because messages from multiple peers can target the same WebSocket concurrently.
sealed class SignalingPeer(WebSocket socket) : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly object _lifetimeLock = new();

    private int _activeSends;
    private bool _disposed;


 
    public async Task SendAsync(SignalingMessage message, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        byte [] bytes = JsonSerializer.SerializeToUtf8Bytes(message, options);


        lock(_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeSends++;
        }
        try
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if(socket.State != WebSocketState.Open)
                    throw new InvalidOperationException("Peer is disconnected");
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);


            }


            finally
            {
                _sendLock.Release();
            }


        }
        finally
        {
            lock (_lifetimeLock){
                _activeSends--;
                if(_disposed && _activeSends == 0)
                    _sendLock.Dispose();
            }


        }

    }

    public void Dispose()
    {
        lock (_lifetimeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_activeSends == 0)
                _sendLock.Dispose();
        }
     
    }


}
