using AlphaChannel.Contracts;
using AlphaChannel.Server.Auth;

namespace AlphaChannel.Server.Social;

internal static class FriendEndpoints
{
    public static void MapFriendEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/").AddEndpointFilter<AccountAuthFilter>().AddEndpointFilter<LalafellGateFilter>();

        // Exact-match lookup on the chosen DisplayName (case-insensitive) - used by the request-
        // sending path itself (SendRequestAsync does its own separate lookup, this endpoint is for
        // callers that just want to resolve a name). See AccountAuthFilter's AddEndpointFilter
        // above: this still requires being signed in yourself, it's just not restricted to
        // already-friends the way /friends is. Route name kept as "by-handle" for now even though
        // the lookup key is DisplayName, to avoid also having to bump every client.
        group.MapGet("/accounts/by-handle/{handle}", async (string handle, HttpContext context, FriendService friends, CancellationToken ct) =>
        {
            var account = await friends.FindAccountByDisplayNameAsync(handle, context.GetAccount().Id, ct);
            return account is null
                ? Results.NotFound()
                : Results.Ok(new AccountSummaryDto(account.Id.ToString(), account.Handle, account.DisplayName));
        });

        // Live search-as-you-type for the Friends page - prefix match, small result cap, see
        // FriendService.SearchByDisplayNamePrefixAsync. "q" query param stays short since this fires
        // on every keystroke.
        group.MapGet("/friends/search", async (string? q, HttpContext context, FriendService friends, CancellationToken ct) =>
            Results.Ok(await friends.SearchByDisplayNamePrefixAsync(context.GetAccount().Id, q ?? string.Empty, ct)));

        group.MapGet("/accounts/{id:guid}/profile", async (Guid id, HttpContext context, FriendService friends, CancellationToken ct) =>
        {
            var profile = await friends.GetProfileAsync(context.GetAccount().Id, id, ct);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapGet("/friends", async (HttpContext context, FriendService friends, CancellationToken ct) =>
            Results.Ok(await friends.GetFriendsAsync(context.GetAccount().Id, ct)));

        group.MapGet("/friends/requests", async (HttpContext context, FriendService friends, CancellationToken ct) =>
            Results.Ok(await friends.GetRequestsAsync(context.GetAccount().Id, ct)));

        group.MapPost("/friends/requests", async (SendFriendRequestRequest request, HttpContext context, FriendService friends, CancellationToken ct) =>
        {
            var result = await friends.SendRequestAsync(context.GetAccount().Id, request.DisplayName, ct);
            return result switch
            {
                SendFriendRequestResult.Sent => Results.Created(),
                SendFriendRequestResult.NotFound => Results.NotFound(),
                _ => Results.Conflict(),
            };
        });

        // Right-click "Add Friend" in-game (Plugin.cs's OnMenuOpened) - see FriendService.
        // SendRequestByCharacterAsync for why this resolves by character identity instead of name.
        group.MapPost("/friends/requests/by-character", async (SendFriendRequestByCharacterRequest request, HttpContext context, FriendService friends, CancellationToken ct) =>
        {
            var result = await friends.SendRequestByCharacterAsync(context.GetAccount().Id, request.CharacterName, request.World, ct);
            return result switch
            {
                SendFriendRequestResult.Sent => Results.Created(string.Empty, new FriendRequestOutcomeDto("sent")),
                SendFriendRequestResult.NotFound => Results.NotFound(new FriendRequestOutcomeDto("not_found")),
                SendFriendRequestResult.AlreadyFriends => Results.Conflict(new FriendRequestOutcomeDto("already_friends")),
                _ => Results.Conflict(new FriendRequestOutcomeDto("already_pending")),
            };
        });

        group.MapGet("/streams/by-character", async (string characterName, string world, HttpContext context, FriendService friends, CancellationToken ct) =>
        {
            var stream = await friends.FindJoinableStreamByCharacterAsync(
                context.GetAccount().Id, characterName, world, ct);
            return stream is null ? Results.NotFound() : Results.Ok(stream);
        });

        group.MapPost("/friends/invite/redeem", async (RedeemInviteCodeRequest request, HttpContext context, FriendService friends, CancellationToken ct) =>
        {
            var result = await friends.RedeemInviteCodeAsync(context.GetAccount().Id, request.InviteCode, ct);
            return result switch
            {
                RedeemInviteCodeResult.Friended => Results.Ok(),
                RedeemInviteCodeResult.NotFound => Results.NotFound(),
                RedeemInviteCodeResult.Self => Results.BadRequest(),
                _ => Results.Conflict(),
            };
        });

        group.MapPost("/friends/requests/{id:guid}/accept", async (Guid id, HttpContext context, FriendService friends, CancellationToken ct) =>
            await friends.AcceptRequestAsync(id, context.GetAccount().Id, ct) ? Results.Ok() : Results.NotFound());

        group.MapPost("/friends/requests/{id:guid}/decline", async (Guid id, HttpContext context, FriendService friends, CancellationToken ct) =>
            await friends.DeclineRequestAsync(id, context.GetAccount().Id, ct) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/friends/{accountId:guid}", async (Guid accountId, HttpContext context, FriendService friends, CancellationToken ct) =>
        {
            await friends.RemoveFriendAsync(context.GetAccount().Id, accountId, ct);
            return Results.NoContent();
        });

        group.MapGet("/blocks", async (HttpContext context, FriendService friends, CancellationToken ct) =>
            Results.Ok(await friends.GetBlocksAsync(context.GetAccount().Id, ct)));

        group.MapPost("/blocks/{accountId:guid}", async (Guid accountId, HttpContext context, FriendService friends, CancellationToken ct) =>
        {
            await friends.BlockAsync(context.GetAccount().Id, accountId, ct);
            return Results.NoContent();
        });

        group.MapDelete("/blocks/{accountId:guid}", async (Guid accountId, HttpContext context, FriendService friends, CancellationToken ct) =>
        {
            await friends.UnblockAsync(context.GetAccount().Id, accountId, ct);
            return Results.NoContent();
        });
    }
}
