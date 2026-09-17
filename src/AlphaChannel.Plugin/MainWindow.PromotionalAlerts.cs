namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly TimeSpan PromotionalAlertLocalPartySuppression = TimeSpan.FromMinutes(60);

    private static readonly string[] PromotionalAlertMessages =
    {
        "Need some chill time? Start a stream on Alpha Channel.",
        "Your next watch party is only a few clicks away.",
        "Your friends are online. Give them something to watch!",
        "Create a Watch Party and turn background noise into a social event.",
        "Movie night doesn’t need a calendar. Start one now!",
        "Found a great video? Share it with your Watch Party.",
        "Your queue looks lonely. Add something entertaining!",
        "Queue responsibly. Your friends are judging your choices.",
        "One YouTube video never means one YouTube video. Start a Watch Party!",
        "Turn your YouTube rabbit hole into a group expedition.",
        "Found a two-hour video essay? Subject your friends to it too.",
        "Dust off a classic and broadcast some retro gaming!",
        "Load a ROM and show everyone how games were played before quest markers.",
        "Blowing into the cartridge is no longer required.",
        "Sixteen-bit graphics, full-sized entertainment. Play some SNES!",
        "Open the Alpha Channel browser and see where the internet takes you.",
        "Turn casual browsing into a Watch Party adventure.",
        "Browse together. Someone has to close all those tabs eventually.",
        "Take over the airwaves and DJ for your friends.",
        "Your playlist deserves an audience.",
        "Turn Eorzea into your own late-night radio station.",
        "No dance floor required. Start DJing live!",
        "Silence is overrated. Put something on.",
        "Some things are easier to share than explain. Stream your screen.",
        "A quiet party is just a stream waiting for viewers.",
        "The best seat in the house is wherever your Alpha Channel TV is.",
        "Somebody has to host tonight. It might as well be you.",
        "Start a party now and spend the next twenty minutes choosing what to watch.",
        "Watching together makes even bad movies entertaining.",
    };

    // Promotional timing is deliberately session-only. Constructing a new plugin instance
    // starts a fresh activation delay instead of restoring the previous session's cooldown.
    private DateTime nextPromotionalAlertUtc =
        DateTime.UtcNow + RandomDelay(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(90));

    internal void UpdatePromotionalAlerts()
    {
        var now = DateTime.UtcNow;
        if (now < nextPromotionalAlertUtc)
            return;

        // A due attempt always starts a new cooldown. This includes attempts suppressed
        // because the user is busy or recently received the more useful local-party alert.
        nextPromotionalAlertUtc = now + RandomDelay(TimeSpan.FromHours(2), TimeSpan.FromHours(6));

        if (!CanShowLocalPartyAlert() || WasLocalPartyAlertShownWithin(PromotionalAlertLocalPartySuppression))
            return;

        Plugin.ChatGui.Print($"[Alpha Channel] {PromotionalAlertMessages[Random.Shared.Next(PromotionalAlertMessages.Length)]}");
    }

    private static TimeSpan RandomDelay(TimeSpan minimum, TimeSpan maximum) =>
        TimeSpan.FromTicks(Random.Shared.NextInt64(minimum.Ticks, maximum.Ticks + 1));
}
