using System.Security.Cryptography;
using AlphaChannel.Contracts;
using AlphaChannel.Server.Auth;
using AlphaChannel.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AlphaChannel.Server.Social;

internal enum SendFriendRequestResult
{
    Sent,
    NotFound,
    AlreadyFriends,
    AlreadyPending,
}

internal enum RedeemInviteCodeResult
{
    Friended,
    NotFound,
    AlreadyFriends,
    Self,
}

// IDbContextFactory rather than a plain DbContext, matching AccountService's reasoning - this
// service pushes over sockets via UserDirectory too, and staying consistent means it's safe to
// call from a future singleton (e.g. PresenceService) without a rewrite.
internal sealed class FriendService(
    IDbContextFactory<AlphaChannelDbContext> dbFactory, UserDirectory directory, RoomManager rooms,
    ActivityService activity, Live.LiveDirectory liveDirectory)
{
    // Deliberately returns the same "not found" for a genuinely-missing name, a blocked account,
    // and (per the Lalafell visibility preference) a Lalafell account this caller has opted not to
    // see - none of those should be distinguishable from probing names.
    //
    // Looks up by DisplayName (the chosen "gamer tag" from onboarding), not Handle - Handle is
    // still the immutable random internal id, but nobody can remember or share it, which was making
    // add-a-friend unusable in practice. DisplayName is now unique (see AccountService.
    // UpdateProfileAsync) specifically so this lookup stays unambiguous.
    public async Task<Account?> FindAccountByDisplayNameAsync(string displayName, Guid callerAccountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalizedName = displayName.Trim().ToLowerInvariant();
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.DisplayName.ToLower() == normalizedName, cancellationToken);
        if (account is null)
        {
            return null;
        }

        if (await IsBlockedEitherWayAsync(db, callerAccountId, account.Id, cancellationToken))
        {
            return null;
        }

        var caller = await db.Accounts.FirstAsync(a => a.Id == callerAccountId, cancellationToken);
        var settings = await GetSettingsAsync(db, cancellationToken);
        return LalafellVisibility.IsHiddenFrom(caller, account, settings) ? null : account;
    }

    // Self always visible; anyone else only if there's an accepted friendship (same bar as DMs -
    // see DmService.StartConversationAsync). Null (not a 404-shaped "empty profile") lets the
    // endpoint return 404, indistinguishable from "no such account" - same anti-probing posture as
    // FindAccountByDisplayNameAsync above.
    public async Task<AccountProfileDto?> GetProfileAsync(Guid viewerId, Guid targetId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var target = await db.Accounts.FirstOrDefaultAsync(a => a.Id == targetId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        if (viewerId == targetId)
        {
            return ToProfileDto(target, null);
        }

        var friendship = await db.Friendships.FirstOrDefaultAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterAccountId == viewerId && f.AddresseeAccountId == targetId) ||
             (f.RequesterAccountId == targetId && f.AddresseeAccountId == viewerId)), cancellationToken);
        if (friendship is null)
        {
            return null;
        }

        var caller = await db.Accounts.FirstAsync(a => a.Id == viewerId, cancellationToken);
        var settings = await GetSettingsAsync(db, cancellationToken);
        if (LalafellVisibility.IsHiddenFrom(caller, target, settings))
        {
            return null;
        }

        return ToProfileDto(target, ToUnixSeconds(friendship.RespondedAtUtc ?? friendship.CreatedAtUtc));
    }

    private static AccountProfileDto ToProfileDto(Account a, long? friendsSinceUnix) => new(
        a.Id.ToString(), a.Handle, a.DisplayName, a.AvatarIcon, a.AvatarColorHex, a.Bio, a.StatusMessage,
        friendsSinceUnix, AvatarStorage.ToPublicUrl(a.AvatarImagePath),
        a.IsDeveloper, a.PatreonTier);

    public async Task<List<FriendDto>> GetFriendsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var friendships = await db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.RequesterAccountId == accountId || f.AddresseeAccountId == accountId))
            .ToListAsync(cancellationToken);

        var friendIds = friendships
            .Select(f => f.RequesterAccountId == accountId ? f.AddresseeAccountId : f.RequesterAccountId)
            .ToHashSet();

        var caller = await db.Accounts.FirstAsync(a => a.Id == accountId, cancellationToken);
        var settings = await GetSettingsAsync(db, cancellationToken);
        var accounts = await db.Accounts.Where(a => friendIds.Contains(a.Id)).ToListAsync(cancellationToken);

        return accounts
            .Where(a => !LalafellVisibility.IsHiddenFrom(caller, a, settings))
            .Select(a => new FriendDto(a.Id.ToString(), a.Handle, a.DisplayName,
                directory.TryGetSocket(a.Id.ToString(), out _),
                PresenceLabels.WatchingLabel(a.Id.ToString(), rooms, directory, liveDirectory),
                a.AvatarIcon, a.AvatarColorHex, a.StatusMessage, AvatarStorage.ToPublicUrl(a.AvatarImagePath)))
            .ToList();
    }

    public async Task<FriendRequestsPage> GetRequestsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var incoming = await db.Friendships
            .Where(f => f.Status == FriendshipStatus.Pending && f.AddresseeAccountId == accountId)
            .ToListAsync(cancellationToken);
        var outgoing = await db.Friendships
            .Where(f => f.Status == FriendshipStatus.Pending && f.RequesterAccountId == accountId)
            .ToListAsync(cancellationToken);

        var otherIds = incoming.Select(f => f.RequesterAccountId)
            .Concat(outgoing.Select(f => f.AddresseeAccountId))
            .Distinct()
            .ToList();
        var accounts = (await db.Accounts.Where(a => otherIds.Contains(a.Id)).ToListAsync(cancellationToken))
            .ToDictionary(a => a.Id);

        FriendRequestDto? MapIncoming(Friendship f) => accounts.TryGetValue(f.RequesterAccountId, out var other)
            ? new FriendRequestDto(f.Id.ToString(), other.Id.ToString(), other.Handle, other.DisplayName, ToUnixSeconds(f.CreatedAtUtc))
            : null;

        FriendRequestDto? MapOutgoing(Friendship f) => accounts.TryGetValue(f.AddresseeAccountId, out var other)
            ? new FriendRequestDto(f.Id.ToString(), other.Id.ToString(), other.Handle, other.DisplayName, ToUnixSeconds(f.CreatedAtUtc))
            : null;

        return new FriendRequestsPage(
            incoming.Select(MapIncoming).OfType<FriendRequestDto>().ToArray(),
            outgoing.Select(MapOutgoing).OfType<FriendRequestDto>().ToArray());
    }

    private const int SearchResultLimit = 8;

    // Backs live search-as-you-type on the Friends page (as opposed to FindAccountByDisplayNameAsync,
    // an exact-match lookup used by the request-sending path itself) - a prefix match against
    // DisplayName only, same visibility/blocking rules as everywhere else, capped small since this
    // fires on every keystroke.
    public async Task<List<FriendSearchResultDto>> SearchByDisplayNamePrefixAsync(
        Guid callerId, string query, CancellationToken cancellationToken)
    {
        var normalizedQuery = query.Trim().ToLowerInvariant();
        if (normalizedQuery.Length < DisplayNameRules.MinLength)
        {
            return [];
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var matches = await db.Accounts
            .Where(a => a.Id != callerId && a.DisplayName.ToLower().StartsWith(normalizedQuery))
            .OrderBy(a => a.DisplayName)
            .Take(SearchResultLimit * 2) // headroom for post-filtering below before capping to SearchResultLimit
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
        {
            return [];
        }

        var caller = await db.Accounts.FirstAsync(a => a.Id == callerId, cancellationToken);
        var settings = await GetSettingsAsync(db, cancellationToken);
        var matchIds = matches.Select(a => a.Id).ToList();
        var blockedIds = (await db.Blocks
                .Where(b => (b.BlockerAccountId == callerId && matchIds.Contains(b.BlockedAccountId)) ||
                            (b.BlockedAccountId == callerId && matchIds.Contains(b.BlockerAccountId)))
                .ToListAsync(cancellationToken))
            .Select(b => b.BlockerAccountId == callerId ? b.BlockedAccountId : b.BlockerAccountId)
            .ToHashSet();

        var relationships = await db.Friendships
            .Where(f => (f.RequesterAccountId == callerId && matchIds.Contains(f.AddresseeAccountId)) ||
                        (f.AddresseeAccountId == callerId && matchIds.Contains(f.RequesterAccountId)))
            .ToListAsync(cancellationToken);
        var relationByOtherId = relationships.ToDictionary(
            f => f.RequesterAccountId == callerId ? f.AddresseeAccountId : f.RequesterAccountId,
            f => f.Status == FriendshipStatus.Accepted ? FriendSearchRelation.Friends : FriendSearchRelation.Pending);

        return matches
            .Where(a => !blockedIds.Contains(a.Id) && !LalafellVisibility.IsHiddenFrom(caller, a, settings))
            .Take(SearchResultLimit)
            .Select(a => new FriendSearchResultDto(a.Id.ToString(), a.DisplayName, a.AvatarIcon, a.AvatarColorHex,
                relationByOtherId.GetValueOrDefault(a.Id, FriendSearchRelation.None),
                AvatarStorage.ToPublicUrl(a.AvatarImagePath)))
            .ToList();
    }

    public async Task<SendFriendRequestResult> SendRequestAsync(Guid requesterId, string recipientDisplayName, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalizedName = recipientDisplayName.Trim().ToLowerInvariant();
        var recipient = await db.Accounts.FirstOrDefaultAsync(a => a.DisplayName.ToLower() == normalizedName, cancellationToken);
        Console.Error.WriteLine($"[FriendDiag] requester={requesterId} rawName='{recipientDisplayName}' normalized='{normalizedName}' recipientFound={recipient is not null}");
        return await SendRequestToAccountAsync(db, requesterId, recipient, cancellationToken);
    }

    // Right-click "Add Friend" in-game (see Plugin.cs's OnMenuOpened) - resolves by the target's
    // real FFXIV character identity instead of needing anyone to know/type a chosen name at all.
    // Only works if the target has ever linked that exact character to an AlphaChannel account
    // (AccountCharacter's CharacterName+World, same lookup FindOrCreateAccountForCharacterAsync
    // itself uses) - if they don't have AlphaChannel, this is indistinguishable from any other
    // "not found" case, same anti-probing posture as the by-name lookup.
    public async Task<SendFriendRequestResult> SendRequestByCharacterAsync(
        Guid requesterId, string characterName, string world, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var character = await db.AccountCharacters
            .FirstOrDefaultAsync(c => c.CharacterName == characterName && c.World == world, cancellationToken);
        var recipient = character is null
            ? null
            : await db.Accounts.FirstOrDefaultAsync(a => a.Id == character.AccountId, cancellationToken);
        return await SendRequestToAccountAsync(db, requesterId, recipient, cancellationToken);
    }

    private async Task<SendFriendRequestResult> SendRequestToAccountAsync(
        AlphaChannelDbContext db, Guid requesterId, Account? recipient, CancellationToken cancellationToken)
    {
        if (recipient is null || recipient.Id == requesterId)
        {
            return SendFriendRequestResult.NotFound;
        }

        if (await IsBlockedEitherWayAsync(db, requesterId, recipient.Id, cancellationToken))
        {
            return SendFriendRequestResult.NotFound;
        }

        var requesterAccount = await db.Accounts.FirstAsync(a => a.Id == requesterId, cancellationToken);
        var visibilitySettings = await GetSettingsAsync(db, cancellationToken);
        if (LalafellVisibility.IsHiddenFrom(requesterAccount, recipient, visibilitySettings))
        {
            return SendFriendRequestResult.NotFound;
        }

        var existing = await db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterAccountId == requesterId && f.AddresseeAccountId == recipient.Id) ||
            (f.RequesterAccountId == recipient.Id && f.AddresseeAccountId == requesterId), cancellationToken);

        if (existing is not null)
        {
            return existing.Status == FriendshipStatus.Accepted ? SendFriendRequestResult.AlreadyFriends : SendFriendRequestResult.AlreadyPending;
        }

        var friendship = new Friendship
        {
            Id = Guid.NewGuid(),
            RequesterAccountId = requesterId,
            AddresseeAccountId = recipient.Id,
            Status = FriendshipStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Friendships.Add(friendship);
        await db.SaveChangesAsync(cancellationToken);

        await PushAsync(recipient.Id, new SocialControl
        {
            Type = SocialSignalType.FriendRequestReceived,
            AccountId = requesterId.ToString(),
            DisplayName = requesterAccount.DisplayName,
            RequestId = friendship.Id.ToString(),
        }, cancellationToken);

        return SendFriendRequestResult.Sent;
    }

    public async Task<bool> AcceptRequestAsync(Guid requestId, Guid callerId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.Friendships.FirstOrDefaultAsync(
            f => f.Id == requestId && f.AddresseeAccountId == callerId && f.Status == FriendshipStatus.Pending, cancellationToken);
        if (request is null)
        {
            return false;
        }

        request.Status = FriendshipStatus.Accepted;
        request.RespondedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var accepter = await db.Accounts.FirstAsync(a => a.Id == callerId, cancellationToken);
        await PushAsync(request.RequesterAccountId, new SocialControl
        {
            Type = SocialSignalType.FriendAccepted,
            AccountId = callerId.ToString(),
            DisplayName = accepter.DisplayName,
            RequestId = request.Id.ToString(),
        }, cancellationToken);

        await activity.RecordAsync(callerId, ActivityEventType.FriendAccepted, null, cancellationToken);
        await activity.RecordAsync(request.RequesterAccountId, ActivityEventType.FriendAccepted, null, cancellationToken);

        return true;
    }

    public async Task<bool> DeclineRequestAsync(Guid requestId, Guid callerId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await db.Friendships.FirstOrDefaultAsync(
            f => f.Id == requestId && f.AddresseeAccountId == callerId && f.Status == FriendshipStatus.Pending, cancellationToken);
        if (request is null)
        {
            return false;
        }

        db.Friendships.Remove(request);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The "share out of band" path (Discord, party chat, voice) - skips the request/accept dance
    // entirely and goes straight to Accepted, since redeeming a code someone privately shared with
    // you is already a stronger mutual-consent signal than a searchable name ever was. Rotates the
    // owner's code afterward so the one they shared can't be reused by anyone else who saw it - same
    // "spent after use" intent Account.InviteCode was originally built with.
    public async Task<RedeemInviteCodeResult> RedeemInviteCodeAsync(Guid callerId, string inviteCode, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalized = inviteCode.Trim().ToUpperInvariant();
        var owner = await db.Accounts.FirstOrDefaultAsync(a => a.InviteCode == normalized, cancellationToken);
        if (owner is null)
        {
            return RedeemInviteCodeResult.NotFound;
        }

        if (owner.Id == callerId)
        {
            return RedeemInviteCodeResult.Self;
        }

        if (await IsBlockedEitherWayAsync(db, callerId, owner.Id, cancellationToken))
        {
            return RedeemInviteCodeResult.NotFound;
        }

        var existing = await db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterAccountId == callerId && f.AddresseeAccountId == owner.Id) ||
            (f.RequesterAccountId == owner.Id && f.AddresseeAccountId == callerId), cancellationToken);

        if (existing is { Status: FriendshipStatus.Accepted })
        {
            return RedeemInviteCodeResult.AlreadyFriends;
        }

        if (existing is not null)
        {
            existing.Status = FriendshipStatus.Accepted;
            existing.RespondedAtUtc = DateTime.UtcNow;
        }
        else
        {
            db.Friendships.Add(new Friendship
            {
                Id = Guid.NewGuid(),
                RequesterAccountId = callerId,
                AddresseeAccountId = owner.Id,
                Status = FriendshipStatus.Accepted,
                CreatedAtUtc = DateTime.UtcNow,
                RespondedAtUtc = DateTime.UtcNow,
            });
        }

        string newCode;
        do
        {
            newCode = GenerateInviteCode();
        }
        while (await db.Accounts.AnyAsync(a => a.InviteCode == newCode, cancellationToken));

        owner.InviteCode = newCode;
        await db.SaveChangesAsync(cancellationToken);

        var caller = await db.Accounts.FirstAsync(a => a.Id == callerId, cancellationToken);
        await PushAsync(owner.Id, new SocialControl
        {
            Type = SocialSignalType.FriendAccepted,
            AccountId = callerId.ToString(),
            DisplayName = caller.DisplayName,
        }, cancellationToken);

        await activity.RecordAsync(callerId, ActivityEventType.FriendAccepted, null, cancellationToken);
        await activity.RecordAsync(owner.Id, ActivityEventType.FriendAccepted, null, cancellationToken);

        return RedeemInviteCodeResult.Friended;
    }

    private static string GenerateInviteCode() => RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 8);

    public async Task RemoveFriendAsync(Guid callerId, Guid otherId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var friendship = await db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterAccountId == callerId && f.AddresseeAccountId == otherId) ||
            (f.RequesterAccountId == otherId && f.AddresseeAccountId == callerId), cancellationToken);
        if (friendship is null)
        {
            return;
        }

        db.Friendships.Remove(friendship);
        await db.SaveChangesAsync(cancellationToken);

        await PushAsync(otherId, new SocialControl { Type = SocialSignalType.FriendRemoved, AccountId = callerId.ToString() }, cancellationToken);
    }

    public async Task<List<AccountSummaryDto>> GetBlocksAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var blockedIds = await db.Blocks.Where(b => b.BlockerAccountId == accountId).Select(b => b.BlockedAccountId).ToListAsync(cancellationToken);
        var blocked = await db.Accounts.Where(a => blockedIds.Contains(a.Id)).ToListAsync(cancellationToken);
        return blocked.Select(a => new AccountSummaryDto(a.Id.ToString(), a.Handle, a.DisplayName)).ToList();
    }

    // Hides bidirectionally and completely: removes any existing friendship/pending request in
    // both directions (so FindAccountByHandleAsync/SendRequestAsync's IsBlockedEitherWayAsync
    // checks take effect immediately, not just for brand-new interactions), and DM history is
    // preserved rather than deleted - only new sends are rejected (see DmService.SendMessageAsync).
    public async Task BlockAsync(Guid callerId, Guid targetId, CancellationToken cancellationToken)
    {
        if (callerId == targetId)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var alreadyBlocked = await db.Blocks.AnyAsync(b => b.BlockerAccountId == callerId && b.BlockedAccountId == targetId, cancellationToken);
        if (!alreadyBlocked)
        {
            db.Blocks.Add(new Block { Id = Guid.NewGuid(), BlockerAccountId = callerId, BlockedAccountId = targetId, CreatedAtUtc = DateTime.UtcNow });
        }

        var friendship = await db.Friendships.FirstOrDefaultAsync(f =>
            (f.RequesterAccountId == callerId && f.AddresseeAccountId == targetId) ||
            (f.RequesterAccountId == targetId && f.AddresseeAccountId == callerId), cancellationToken);
        if (friendship is not null)
        {
            db.Friendships.Remove(friendship);
        }

        // Also severs Tweeter follows both ways - blocking should mean "no contact at all", not
        // just "no friendship."
        var follows = await db.Follows.Where(f =>
            (f.FollowerAccountId == callerId && f.FolloweeAccountId == targetId) ||
            (f.FollowerAccountId == targetId && f.FolloweeAccountId == callerId)).ToListAsync(cancellationToken);
        db.Follows.RemoveRange(follows);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UnblockAsync(Guid callerId, Guid targetId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var block = await db.Blocks.FirstOrDefaultAsync(b => b.BlockerAccountId == callerId && b.BlockedAccountId == targetId, cancellationToken);
        if (block is null)
        {
            return;
        }

        db.Blocks.Remove(block);
        await db.SaveChangesAsync(cancellationToken);
    }

    // The singleton settings row is seeded via migration (see AlphaChannelDbContext.OnModelCreating)
    // so this should always find it, but falls back to defaults rather than throwing if it's ever
    // missing - a missing settings row shouldn't be able to break every friend-related endpoint.
    private static async Task<ServerSettings> GetSettingsAsync(AlphaChannelDbContext db, CancellationToken cancellationToken) =>
        await db.Settings.FirstOrDefaultAsync(s => s.Id == ServerSettings.SingletonId, cancellationToken) ?? new ServerSettings();

    private static Task<bool> IsBlockedEitherWayAsync(AlphaChannelDbContext db, Guid a, Guid b, CancellationToken cancellationToken) =>
        db.Blocks.AnyAsync(x =>
            (x.BlockerAccountId == a && x.BlockedAccountId == b) ||
            (x.BlockerAccountId == b && x.BlockedAccountId == a), cancellationToken);

    private async Task PushAsync(Guid toAccountId, SocialControl message, CancellationToken cancellationToken)
    {
        if (directory.TryGetSocket(toAccountId.ToString(), out var socket) && socket is not null)
        {
            await SocketSend.SendAsync(socket, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long ToUnixSeconds(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
