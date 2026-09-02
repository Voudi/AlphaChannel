using System.Net.WebSockets;
using System.Text.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Server;
using AlphaChannel.Server.Admin;
using AlphaChannel.Server.Auth;
using AlphaChannel.Server.Data;
using AlphaChannel.Server.Live;
using AlphaChannel.Server.Moderation;
using AlphaChannel.Server.Social;
using AlphaChannel.Server.Twitch;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<RoomDirectoryService>();
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddSingleton<ConnectionHandler>();

// Factory rather than a plain scoped DbContext: singletons (ConnectionHandler today, presence
// tomorrow) can't safely hold a scoped DbContext, so every consumer opens its own short-lived
// context per call regardless of whether it's a request handler or a long-lived service.
builder.Services.AddDbContextFactory<AlphaChannelDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("AlphaChannel")));

// Flat env vars (XIVAUTH_CLIENT_ID etc, see .env.example), matching the existing ADMIN_TOKEN
// convention rather than a nested config section. Endpoint paths confirmed against XIVAuth's
// "Authenticating to the API" docs - see XivAuthOptions.cs and XivAuthClient.cs.
builder.Services.AddSingleton(new XivAuthOptions
{
    ClientId = builder.Configuration["XIVAUTH_CLIENT_ID"] ?? string.Empty,
    ClientSecret = builder.Configuration["XIVAUTH_CLIENT_SECRET"] ?? string.Empty,
    DeviceAuthorizationEndpoint = builder.Configuration["XIVAUTH_DEVICE_ENDPOINT"] ?? string.Empty,
    TokenEndpoint = builder.Configuration["XIVAUTH_TOKEN_ENDPOINT"] ?? string.Empty,
    CharactersEndpoint = builder.Configuration["XIVAUTH_CHARACTERS_ENDPOINT"] ?? string.Empty,
});
builder.Services.AddHttpClient<IXivAuthClient, XivAuthClient>();
builder.Services.AddSingleton<XivAuthFlowStore>();
builder.Services.AddSingleton(new DiscordNotifier(new HttpClient(), builder.Configuration["DISCORD_LALAFELL_WEBHOOK_URL"]));
builder.Services.AddHttpClient<LodestoneRaceChecker>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddSingleton<AvatarStorage>();
builder.Services.AddScoped<AccountAuthFilter>();
builder.Services.AddScoped<FriendService>();
builder.Services.AddSingleton<ActivityService>();
builder.Services.AddScoped<DmService>();
builder.Services.AddScoped<TweeterService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ModerationAdminService>();
builder.Services.AddScoped<LalafellGateFilter>();
builder.Services.AddScoped<LalafellReviewService>();
builder.Services.AddScoped<AdminTokenFilter>();
builder.Services.AddScoped<PluginHubService>();
builder.Services.AddScoped<VenueService>();
builder.Services.AddSingleton<LiveDirectory>();
builder.Services.AddScoped<LiveService>();
builder.Services.AddSingleton<RadioService>();
builder.Services.AddScoped<MediaWebhookFilter>();

builder.Services.AddSingleton(new TwitchOptions
{
    ClientId = builder.Configuration["TWITCH_CLIENT_ID"] ?? string.Empty,
    ClientSecret = builder.Configuration["TWITCH_CLIENT_SECRET"] ?? string.Empty,
});
builder.Services.AddHttpClient<TwitchHelixClient>();
builder.Services.AddSingleton<TwitchTrendingService>();
builder.Services.AddHostedService<TwitchTrendingRefreshService>();

var app = builder.Build();
app.UseWebSockets();

// Applied on every startup - fine at this scale (single instance, no concurrent-migration race to
// worry about) and means `docker compose up` alone is enough to get a fresh DB to the current schema.
using (var migrationDb = app.Services.GetRequiredService<IDbContextFactory<AlphaChannelDbContext>>().CreateDbContext())
{
    await migrationDb.Database.MigrateAsync();

    // LiveDirectory is in-memory only — rebuild from open sessions so presence labels stay correct
    // across server restarts while MediaMTX publishers are still connected.
    var openLiveIds = await migrationDb.LiveSessions
        .AsNoTracking()
        .Where(s => s.EndedAtUtc == null)
        .Select(s => s.AccountId.ToString())
        .ToListAsync();
    app.Services.GetRequiredService<LiveDirectory>().Load(openLiveIds);
}

app.MapGet("/", () => "AlphaChannel relay is running.");
app.MapAuthEndpoints();
app.MapFriendEndpoints();
app.MapActivityEndpoints();
app.MapKeyEndpoints();
app.MapDmEndpoints();
app.MapTweeterEndpoints();
app.MapReportEndpoints();
app.MapModerationAdminEndpoints();
app.MapLalafellAdminEndpoints();
app.MapAdminUiEndpoint();
app.MapPluginHubEndpoints();
app.MapVenueEndpoints();
app.MapLiveEndpoints();
app.MapRadioEndpoints();
app.MapRoomEndpoints();
app.MapTwitchEndpoints();

app.Map("/rt", async (HttpContext context, ConnectionHandler handler, AccountService accounts) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var auth = context.Request.Headers.Authorization.ToString();
    var rawToken = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth["Bearer ".Length..] : null;
    if (string.IsNullOrWhiteSpace(rawToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var account = await accounts.ValidateTokenAsync(rawToken, context.RequestAborted);
    if (account is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await handler.RunAsync(socket, account.Id.ToString(), context.RequestAborted);
});

// Operator-only: clears a player's display name and, if they're currently connected, tells their
// client to prompt them for a new one immediately. Not exposed in the plugin UI - this is meant to
// be called by whoever runs the relay (e.g. via curl) if someone picks an abusive name.
app.MapPost("/admin/reset-username/{userId}", async (string userId, HttpContext context, UserDirectory directory) =>
{
    if (directory.TryResetDisplayName(userId, out var socket) && socket is { State: WebSocketState.Open })
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new StreamControl { Type = SignalType.StreamRenameRequired });
        await socket.SendAsync(json, WebSocketMessageType.Text, true, context.RequestAborted);
    }

    return Results.Ok();
}).AddEndpointFilter<AdminTokenFilter>();

app.Run();
