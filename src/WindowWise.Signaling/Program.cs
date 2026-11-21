using WindowWise.Signaling;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;


var builder = WebApplication.CreateBuilder(args);


// Trust HTTPS information only from the configured tunnel connector
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    string? address = builder.Configuration["ReverseProxy:Address"];
    if (!string.IsNullOrWhiteSpace(address))
    {
        var proxy = IPAddress.Parse(address);
        options.KnownProxies.Add(proxy);
        options.KnownProxies.Add(proxy.MapToIPv6());

    }

});


// /signal forwards connection negotiation; /turn-credentials issues temporary Cloudflare TURN credentials.
// File contents travel over the WebRTC channel, either directly or through TURN, rather than through this
// server.


builder.Services.AddTurnCredentials();
builder.Services.AddRoomSignaling();


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

app.UseForwardedHeaders();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapTurnCredentials();



app.MapGet("/me", (HttpContext context) =>
    Results.Ok(new
    {
        userId = context.User.FindFirst("sub")?.Value
    })).RequireAuthorization();

app.UseWebSockets();
app.MapRoomSignaling();
app.Run();
