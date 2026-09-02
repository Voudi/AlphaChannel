using System.Security.Cryptography;
using System.Text;
using AlphaChannel.Contracts;
using AlphaChannel.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AlphaChannel.Server.Auth;

internal enum UpdateProfileResult
{
    Updated,
    NameTaken,
    InvalidFormat,
    NotFound,
}

internal sealed record UpdateProfileOutcome(UpdateProfileResult Result, AccountSummary? Account);

// All account creation, character linking, and bearer-token issuance/validation lives here so
// there is exactly one place that touches AccountCharacter (the real-identity table) and exactly
// one place that ever compares a raw token against the hashed values in AuthTokens.
//
// Takes IDbContextFactory rather than AlphaChannelDbContext directly: this service is called both
// from short-lived REST endpoint handlers (where a plain injected DbContext would be fine) and,
// once presence/friend-push land, from singleton services like ConnectionHandler that outlive any
// single request - a scoped DbContext can't be safely held by a singleton, so every method here
// opens its own short-lived context instead.
internal sealed class AccountService(
    IDbContextFactory<AlphaChannelDbContext> dbFactory, DiscordNotifier discord, LodestoneRaceChecker lodestone)
{
    public async Task<(Account Account, bool IsNew)> FindOrCreateAccountForCharacterAsync(
        string characterName, string world, bool isLalafell, Guid? linkToAccountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var existingCharacter = await db.AccountCharacters
            .FirstOrDefaultAsync(c => c.CharacterName == characterName && c.World == world, cancellationToken);

        if (existingCharacter is not null)
        {
            if (linkToAccountId is { } wantedAccountId && existingCharacter.AccountId != wantedAccountId)
            {
                throw new InvalidOperationException("This character is already linked to a different AlphaChannel account.");
            }

            return (await db.Accounts.FirstAsync(a => a.Id == existingCharacter.AccountId, cancellationToken), false);
        }

        if (linkToAccountId is { } accountId)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
                ?? throw new InvalidOperationException("The account to link this character to no longer exists.");

            db.AccountCharacters.Add(new AccountCharacter
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                CharacterName = characterName,
                World = world,
                IsPrimary = false,
                LinkedAtUtc = DateTime.UtcNow,
            });

            // Linking a Lalafell character onto a previously-unflagged account puts it under
            // review too - same policy as if it had been Lalafell from account creation.
            if (isLalafell && !account.IsLalafell)
            {
                account.IsLalafell = true;
                account.LalafellSocialStatus = LalafellSocialStatus.Pending;
                await db.SaveChangesAsync(cancellationToken);
                await NotifyPendingReviewAsync(account, characterName, world);
                FireAndForgetLodestoneCheck(account.Id, characterName, world, isLalafell);
                return (account, false);
            }

            await db.SaveChangesAsync(cancellationToken);
            return (account, false);
        }

        var handle = await GenerateUniqueHandleAsync(db, cancellationToken);
        var newAccount = new Account
        {
            Id = Guid.NewGuid(),
            Handle = handle,
            // Deliberately NOT the real character name - defaulting the display name to the
            // anonymous handle too means nothing about a fresh account hints at who's behind it
            // until the player chooses to change it themselves (a future PATCH /me).
            DisplayName = handle,
            InviteCode = GenerateInviteCode(),
            CreatedAtUtc = DateTime.UtcNow,
            IsLalafell = isLalafell,
            LalafellSocialStatus = isLalafell ? LalafellSocialStatus.Pending : LalafellSocialStatus.NotApplicable,
        };
        db.Accounts.Add(newAccount);
        db.AccountCharacters.Add(new AccountCharacter
        {
            Id = Guid.NewGuid(),
            AccountId = newAccount.Id,
            CharacterName = characterName,
            World = world,
            IsPrimary = true,
            LinkedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);

        if (isLalafell)
        {
            await NotifyPendingReviewAsync(newAccount, characterName, world);
        }

        FireAndForgetLodestoneCheck(newAccount.Id, characterName, world, isLalafell);

        return (newAccount, true);
    }

    public async Task<List<LinkedCharacterDto>> GetLinkedCharactersAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AccountCharacters
            .Where(c => c.AccountId == accountId)
            .OrderByDescending(c => c.IsPrimary)
            .Select(c => new LinkedCharacterDto(c.CharacterName, c.World, c.IsPrimary))
            .ToListAsync(cancellationToken);
    }

    // Onboarding, asked once at account creation but editable later (see AccountEndpoints'
    // PATCH /me). Also nudges another Lodestone check when the self-reported races change, since
    // that's a second independent signal worth reconciling against the automated lookup.
    public async Task UpdateOnboardingAsync(Guid accountId, string[] races, bool wantsToSeeLalafellContent, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return;
        }

        account.SelfReportedRaces = string.Join(",", races);
        account.WantsToSeeLalafellContent = wantsToSeeLalafellContent;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<UpdateProfileOutcome> UpdateProfileAsync(Guid accountId, UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return new UpdateProfileOutcome(UpdateProfileResult.NotFound, null);
        }

        if (request.DisplayName is { Length: > 0 } displayName)
        {
            var trimmed = displayName.Trim();

            // DisplayName is now the searchable/add-a-friend identifier (see FriendService), so its
            // format has to stay narrow enough to be an unambiguous search key - see
            // DisplayNameRules's own header comment for why.
            if (!DisplayNameRules.IsValid(trimmed))
            {
                return new UpdateProfileOutcome(UpdateProfileResult.InvalidFormat, null);
            }

            // Unique, case-insensitive, since two players picking "Ysera"/"ysera" would otherwise be
            // indistinguishable at lookup time.
            var taken = await db.Accounts.AnyAsync(
                a => a.Id != accountId && a.DisplayName.ToLower() == trimmed.ToLower(), cancellationToken);
            if (taken)
            {
                return new UpdateProfileOutcome(UpdateProfileResult.NameTaken, null);
            }

            account.DisplayName = trimmed;
        }

        // AvatarIcon is a key into a client-curated icon list, not free text a user types - still
        // length-capped defensively rather than validated against that list, since the server has
        // no reason to know the client's icon set.
        if (request.AvatarIcon is { Length: > 0 } avatarIcon)
        {
            account.AvatarIcon = avatarIcon.Trim()[..Math.Min(avatarIcon.Trim().Length, 32)];
        }

        if (request.AvatarColorHex is { Length: > 0 } avatarColor)
        {
            account.AvatarColorHex = avatarColor.Trim()[..Math.Min(avatarColor.Trim().Length, 16)];
        }

        if (request.Bio is { } bio)
        {
            account.Bio = bio.Trim() is { Length: > 0 } trimmedBio ? trimmedBio[..Math.Min(trimmedBio.Length, 160)] : null;
        }

        if (request.StatusMessage is { } status)
        {
            account.StatusMessage = status.Trim() is { Length: > 0 } trimmedStatus ? trimmedStatus[..Math.Min(trimmedStatus.Length, 64)] : null;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new UpdateProfileOutcome(UpdateProfileResult.Updated, ToSummary(account));
    }

    internal static AccountSummary ToSummary(Account account) => new(
        account.Id.ToString(), account.Handle, account.DisplayName, account.InviteCode,
        account.AvatarIcon, account.AvatarColorHex, account.Bio, account.StatusMessage,
        AvatarStorage.ToPublicUrl(account.AvatarImagePath),
        account.PatreonTier,
        account.IsDeveloper);

    public async Task<UpdateProfileOutcome> SetAvatarImageAsync(
        Guid accountId, string fileName, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return new UpdateProfileOutcome(UpdateProfileResult.NotFound, null);
        }

        account.AvatarImagePath = fileName;
        await db.SaveChangesAsync(cancellationToken);
        return new UpdateProfileOutcome(UpdateProfileResult.Updated, ToSummary(account));
    }

    public async Task<UpdateProfileOutcome> ClearAvatarImageAsync(
        Guid accountId, AvatarStorage storage, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return new UpdateProfileOutcome(UpdateProfileResult.NotFound, null);
        }

        storage.DeleteIfExists(account.AvatarImagePath);
        account.AvatarImagePath = null;
        await db.SaveChangesAsync(cancellationToken);
        return new UpdateProfileOutcome(UpdateProfileResult.Updated, ToSummary(account));
    }

    private Task NotifyPendingReviewAsync(Account account, string characterName, string world) =>
        discord.NotifyAsync(
            $"New Lalafell account pending social review: @{account.Handle} ({characterName} @ {world}). " +
            "Review and approve/deny in the admin panel (/admin/ui).");

    // Fire-and-forget: never awaited by a caller, never allowed to affect sign-in latency or
    // success/failure - see LodestoneRaceChecker's own header comment on why this is advisory-only.
    private void FireAndForgetLodestoneCheck(Guid accountId, string characterName, string world, bool clientReportedLalafell)
    {
        _ = Task.Run(async () =>
        {
            var lodestoneRace = await lodestone.TryGetRaceAsync(characterName, world, CancellationToken.None);
            if (lodestoneRace is null)
            {
                return;
            }

            var mismatch = clientReportedLalafell != string.Equals(lodestoneRace, "Lalafell", StringComparison.Ordinal);

            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, CancellationToken.None);
            if (account is null)
            {
                return;
            }

            account.LodestoneRaceMismatch = mismatch;
            account.LodestoneCheckedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            if (mismatch)
            {
                await discord.NotifyAsync(
                    $"Lodestone race mismatch for @{account.Handle}: client reported Lalafell={clientReportedLalafell}, " +
                    $"Lodestone shows {lodestoneRace}. Worth a look in the admin panel.");
            }
        });
    }

    public async Task<string> IssueTokenAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var raw = GenerateToken();
        db.AuthTokens.Add(new AuthToken
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TokenHash = Hash(raw),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return raw;
    }

    // Used when an account is suspended/banned - every active session dies immediately rather than
    // staying valid until it naturally expires. See Moderation/ModerationAdminService.
    public async Task RevokeAllTokensAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tokens = await db.AuthTokens.Where(t => t.AccountId == accountId && t.RevokedAtUtc == null).ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var token in tokens)
        {
            token.RevokedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RevokeTokenAsync(string rawToken, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hash = Hash(rawToken);
        var token = await db.AuthTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is not null)
        {
            token.RevokedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // Returns null for anything that shouldn't be treated as authenticated: unknown token, revoked
    // token, or an account that's currently banned - callers (the /rt handler, REST endpoints) all
    // just see "not authenticated" and don't need to special-case bans themselves.
    public async Task<Account?> ValidateTokenAsync(string rawToken, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hash = Hash(rawToken);

        var accountId = await db.AuthTokens
            .Where(t => t.TokenHash == hash && t.RevokedAtUtc == null)
            .Select(t => (Guid?)t.AccountId)
            .FirstOrDefaultAsync(cancellationToken);
        if (accountId is null)
        {
            return null;
        }

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var currentlyBanned = account.IsBanned && (account.BannedUntilUtc is null || account.BannedUntilUtc > DateTime.UtcNow);
        return currentlyBanned ? null : account;
    }

    private static async Task<string> GenerateUniqueHandleAsync(AlphaChannelDbContext db, CancellationToken cancellationToken)
    {
        // Random, not derived from the character name - see the DisplayName comment above. A
        // handle that hints at the real character name defeats the point of it being the only
        // thing other players can look you up by.
        string candidate;
        do
        {
            candidate = $"player{RandomNumberGenerator.GetString("23456789abcdefghjkmnpqrstuvwxyz", 6)}";
        }
        while (await db.Accounts.AnyAsync(a => a.Handle == candidate, cancellationToken));

        return candidate;
    }

    private static string GenerateInviteCode() => RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 8);

    private static string GenerateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string value) => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(value)));
}
