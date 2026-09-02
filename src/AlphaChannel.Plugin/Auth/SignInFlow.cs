using System.Diagnostics;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin;

namespace AlphaChannel.Plugin.Auth;

internal enum SignInOutcome
{
    Success,
    Denied,
    Expired,
    Banned,
    Error,
    Cancelled,
}

internal sealed record SignInResult(SignInOutcome Outcome, CharacterSession? Session, string? Message, bool IsNewAccount = false);

// Orchestrates one XIVAuth device-flow sign-in (or character-link) end to end: start the flow,
// open the verification URL in the system browser (same Process.Start pattern the Ko-fi button
// already uses), poll on the server-provided interval, and resolve to a CharacterSession or a
// reason it didn't work. Mirrors Aetherphone's SignInFlow.cs at a much smaller scale - AlphaChannel
// only has the one XIVAuth-backed flow, not Aetherphone's Lodestone-code alternative.
internal sealed class SignInFlow(AuthClient authClient)
{
    internal async Task<SignInResult> RunAsync(
        string characterName,
        string world,
        bool isLalafell,
        string? linkBearerToken,
        Action<AuthStartResponse> onFlowStarted,
        CancellationToken cancellationToken)
    {
        var start = linkBearerToken is null
            ? await authClient.StartAsync(characterName, world, isLalafell).ConfigureAwait(false)
            : await authClient.StartLinkAsync(linkBearerToken, characterName, world, isLalafell).ConfigureAwait(false);

        if (start is null)
        {
            return new SignInResult(SignInOutcome.Error, null, "Could not reach the AlphaChannel relay.");
        }

        onFlowStarted(start);
        TryOpenBrowser(start.VerificationUriComplete ?? start.VerificationUri);

        var interval = TimeSpan.FromSeconds(Math.Max(1, start.IntervalSeconds));
        var deadline = DateTime.UtcNow.AddSeconds(start.ExpiresInSeconds);

        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SignInResult(SignInOutcome.Cancelled, null, null);
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new SignInResult(SignInOutcome.Cancelled, null, null);
            }

            var poll = linkBearerToken is null
                ? await authClient.PollAsync(start.FlowId).ConfigureAwait(false)
                : await authClient.PollLinkAsync(linkBearerToken, start.FlowId).ConfigureAwait(false);

            if (poll is null)
            {
                // Transient network hiccup talking to our own relay - back off a little and keep
                // trying rather than aborting the whole sign-in over one dropped request.
                interval += TimeSpan.FromSeconds(1);
                continue;
            }

            switch (poll.Status)
            {
                case AuthPollStatus.Pending:
                    continue;

                case AuthPollStatus.Success when poll.Account is not null && poll.Token is not null:
                    return new SignInResult(SignInOutcome.Success, new CharacterSession
                    {
                        AccountId = poll.Account.AccountId,
                        Token = poll.Token,
                        Handle = poll.Account.Handle,
                        DisplayName = poll.Account.DisplayName,
                        AvatarIcon = poll.Account.AvatarIcon,
                        AvatarColorHex = poll.Account.AvatarColorHex,
                        AvatarImageUrl = poll.Account.AvatarImageUrl,
                        Bio = poll.Account.Bio,
                        StatusMessage = poll.Account.StatusMessage,
                        InviteCode = poll.Account.InviteCode,
                        PatreonTier = poll.Account.PatreonTier,
                        IsDeveloper = poll.Account.IsDeveloper,
                    }, null, poll.IsNewAccount);

                case AuthPollStatus.Denied:
                    return new SignInResult(SignInOutcome.Denied, null, poll.ErrorMessage);

                case AuthPollStatus.Expired:
                    return new SignInResult(SignInOutcome.Expired, null, poll.ErrorMessage);

                case AuthPollStatus.Banned:
                    return new SignInResult(SignInOutcome.Banned, null, poll.ErrorMessage);

                default:
                    return new SignInResult(SignInOutcome.Error, null, poll.ErrorMessage);
            }
        }

        return new SignInResult(SignInOutcome.Expired, null, "Sign-in code expired.");
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[Auth] couldn't open browser automatically: {exception.Message}");
        }
    }
}
