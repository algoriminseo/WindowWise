using WindowWise.Signaling;
using Microsoft.AspNetCore.Authentication.JwtBearer;



var builder = WebApplication.CreateBuilder(args);

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
