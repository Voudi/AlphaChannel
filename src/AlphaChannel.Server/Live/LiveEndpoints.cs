using AlphaChannel.Contracts;
using AlphaChannel.Server.Auth;
using AlphaChannel.Server.Social;

namespace AlphaChannel.Server.Live;

// MediaMTX's authHTTPAddress webhook payload shape - server-internal, not shared with the plugin.
// Field names match MediaMTX's own documented payload (it also sends user/password/token/ip/
// protocol/id/userAgent, all unused here - publish auth only ever needs action/path/query).
internal sealed record MediaAuthRequest(string? Action, string? Path, string? Query);

internal static class LiveEndpoints
{
    public static void MapLiveEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/live").AddEndpointFilter<AccountAuthFilter>().AddEndpointFilter<LalafellGateFilter>();

        group.MapPost("/key/rotate", async (HttpContext context, LiveService live, CancellationToken ct) =>
            Results.Ok(new RotateStreamKeyResponse(await live.RotateKeyAsync(context.GetAccount().Id, ct))));

        group.MapGet("/mine", async (HttpContext context, LiveService live, CancellationToken ct) =>
            Results.Ok(await live.GetMyStatusAsync(context.GetAccount().Id, ct)));

        group.MapGet("/friends", async (HttpContext context, LiveService live, CancellationToken ct) =>
            Results.Ok(await live.GetFriendsLiveAsync(context.GetAccount().Id, ct)));

        // Called directly by MediaMTX's own built-in authHTTPAddress mechanism - MediaMTX controls
        // that request itself, so there's no way for us to attach a custom shared-secret header to
        // it (unlike the two hooks below, which are curl commands we write ourselves). That's fine:
        // this endpoint's real security boundary is AuthenticatePublishAsync's per-account secret
        // hash check, not caller identity - knowing this URL exists gets you nothing without also
        // knowing (or successfully guessing) a real account's stream secret, which is exactly as
        // hard here as anywhere else it's checked.
        app.MapPost("/live/media/auth", async (
     MediaAuthRequest request,
     LiveService live,
     ILogger<LiveService> logger,
     CancellationToken ct) =>
        {
            logger.LogWarning(
                "[LIVE DEBUG] Media auth received: Action={Action}, Path={Path}, HasQuery={HasQuery}",
                request.Action,
                request.Path,
                !string.IsNullOrWhiteSpace(request.Query));

            if (!string.Equals(
                    request.Action,
                    "publish",
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "[LIVE DEBUG] Non-publish request allowed.");

                return Results.Ok();
            }

            var allowed =
                request.Path is { Length: > 0 } path &&
                await live.AuthenticatePublishAsync(
                    path,
                    request.Query,
                    ct);

            logger.LogWarning(
                "[LIVE DEBUG] Publish authentication result: Allowed={Allowed}, Path={Path}",
                allowed,
                request.Path);

            return allowed
                ? Results.Ok()
                : Results.Unauthorized();
        });

        // Unlike /auth above, these two don't verify any per-account secret - they just flip live
        // status for whatever account id is in `path`. Without a real gate here, anyone could POST
        // straight to this URL and falsely mark another account "live" (spamming their friends with
        // a fake presence push + activity event). mediamtx.yml's runOnOnline/runOnOffline commands
        // are curl calls we write ourselves, so - unlike /auth - they CAN carry the shared secret.
        var mediaHooks = app.MapGroup("/live/media").AddEndpointFilter<MediaWebhookFilter>();

        mediaHooks.MapPost("/ready", async (
            string path,
            LiveService live,
            ILogger<LiveService> logger,
            CancellationToken ct) =>
        {
            logger.LogWarning(
                "[LIVE DEBUG] READY webhook received. Path={Path}",
                path);

            await live.MarkLiveAsync(
                path,
                ct);

            logger.LogWarning(
                "[LIVE DEBUG] MarkLiveAsync completed. Path={Path}",
                path);

            return Results.Ok();
        });

        mediaHooks.MapPost("/notready", async (
            string path,
            LiveService live,
            ILogger<LiveService> logger,
            CancellationToken ct) =>
        {
            mediaHooks.MapPost("/ready", async (
                string path,
                LiveService live,
                ILogger<LiveService> logger,
                CancellationToken ct) =>
            {
                logger.LogWarning(
                    "[LIVE DEBUG] READY webhook received. Path={Path}",
                    path);

                await live.MarkLiveAsync(
                    path,
                    ct);

                logger.LogWarning(
                    "[LIVE DEBUG] MarkLiveAsync completed. Path={Path}",
                    path);

                return Results.Ok();
            });

            mediaHooks.MapPost("/notready", async (
                string path,
                LiveService live,
                ILogger<LiveService> logger,
                CancellationToken ct) =>
            {
                logger.LogWarning(
                    "[LIVE DEBUG] NOTREADY webhook received. Path={Path}",
                    path);

                await live.MarkOfflineAsync(
                    path,
                    ct);

                logger.LogWarning(
                    "[LIVE DEBUG] MarkOfflineAsync completed. Path={Path}",
                    path);

                return Results.Ok();
            });

            return Results.Ok();
        });
    }
}
