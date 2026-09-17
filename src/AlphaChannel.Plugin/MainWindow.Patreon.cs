using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool patreonPopupOpen;

    private static readonly (FontAwesomeIcon Icon, string Label)[] PatreonMedia =
    [
        (FontAwesomeIcon.Gamepad, "Retro games"),
        (FontAwesomeIcon.Film, "Local videos"),
        (FontAwesomeIcon.Desktop, "Live streaming"),
        (FontAwesomeIcon.Music, "DJ Live"),
        (FontAwesomeIcon.Globe, "Web browser"),
    ];

    private static readonly (FontAwesomeIcon Icon, string Title, string Copy, bool Orange)[] PatreonBenefits =
    [
        (FontAwesomeIcon.Users, "Larger Watch Parties", "Invite more viewers to watch together.", false),
        (FontAwesomeIcon.Star, "Higher Video Quality", "Broadcast supported content at enhanced quality.", false),
        (FontAwesomeIcon.Bullhorn, "Promote Your Parties", "Give your public Watch Parties more visibility.", false),
        (FontAwesomeIcon.Bolt, "Early Feature Access", "Try selected features before general release.", false),
        (FontAwesomeIcon.Heart, "Patreon Plugin Badge", "Show your support throughout Alpha Channel.", true),
        (FontAwesomeIcon.Comments, "Exclusive Discord Role", "Receive a supporter role in the community.", false),
    ];

    private void DrawPatreonPopup()
    {
        if (!patreonPopupOpen)
        {
            return;
        }

        var parentPos = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var cardSize = new Vector2(
            MathF.Min(Ui(760f), parentSize.X - Ui(40f)),
            MathF.Min(Ui(720f), parentSize.Y - Ui(40f)));

        ImGui.SetNextWindowPos(parentPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!ImGui.Begin("##patreonOverlay", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.GetWindowDrawList().AddRectFilled(
            parentPos, parentPos + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0.01f, 0.035f, 0.76f)));
        ImGui.SetCursorScreenPos(parentPos + (parentSize - cardSize) * 0.5f);

        using var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(22f, 18f))
            .Push(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.03f, 0.06f, 0.995f))
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.9f));
        ImGui.BeginChild("##patreonCard", cardSize, true, ImGuiWindowFlags.NoScrollbar);

        DrawPatreonHeader();
        ImGui.Dummy(UiVec(0f, 6f));
        DrawPatreonHero();
        ImGui.Dummy(UiVec(0f, 8f));
        DrawPatreonBenefits();
        ImGui.Separator();
        ImGui.Dummy(UiVec(0f, 6f));
        DrawPatreonFooter();

        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawPatreonHeader()
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        var heartCenter = new Vector2(start.X + width * 0.5f, start.Y + Ui(25f));
        draw.AddCircleFilled(heartCenter, Ui(23f), ImGui.GetColorU32(new Vector4(0.075f, 0.06f, 0.14f, 1f)), 40);
        draw.AddCircle(heartCenter, Ui(23f), ImGui.GetColorU32(Accent), 40, Ui(2.5f));
        draw.AddCircle(heartCenter, Ui(20f), ImGui.GetColorU32(new Vector4(PatreonOrange.X, PatreonOrange.Y, PatreonOrange.Z, 0.8f)), 40, Ui(1.5f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var heart = FontAwesomeIcon.Heart.ToIconString();
            var heartSize = ImGui.CalcTextSize(heart);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), 
                heartCenter - heartSize * 0.5f + UiVec(0f, 1f),
                ImGui.GetColorU32(PatreonOrange), heart);
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X + width - Ui(30f), start.Y - Ui(5f)));
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                   .Push(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f)))
        {
            if (ImGui.Button("×##closePatreon", UiVec(30f, 28f)))
            {
                patreonPopupOpen = false;
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + Ui(53f)));
        DrawPatreonCenteredText("Support Alpha Channel", new Vector4(0.96f, 0.96f, 1f, 1f), 1.38f);
        DrawPatreonCenteredText(
            "Help us keep building new ways to watch, play, and share together.", MutedText);
    }

    private void DrawPatreonHero()
    {
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.055f, 0.065f, 0.12f, 1f))
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.65f));
        using var style = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(11f));
        ImGui.BeginChild("##patreonHero", UiVec(0f, 146f), true, ImGuiWindowFlags.NoScrollbar);
        var heroStart = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var people = FontAwesomeIcon.Users.ToIconString();
            draw.AddText(UiBuilder.IconFont, ImGui.GetFontSize() * 2.25f,
                heroStart + UiVec(16f, 13f), ImGui.GetColorU32(Accent), people);
        }
        ImGui.SetCursorScreenPos(heroStart + UiVec(78f, 8f));
        SetUiFontScale(1.16f);
        ImGui.TextUnformatted("Share more with your Watch Party");
        SetUiFontScale(1f);
        ImGui.SetCursorScreenPos(heroStart + UiVec(78f, 36f));
        ImGui.TextColored(MutedText,
            "Become a Patreon member and unlock broadcasting of live content direct to your watch party! Including:");
        ImGui.SetCursorScreenPos(new Vector2(heroStart.X, heroStart.Y + Ui(82f)));

        if (ImGui.BeginTable("##patreonMedia", PatreonMedia.Length, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var item in PatreonMedia)
            {
                ImGui.TableNextColumn();
                DrawPatreonMediaChip(item.Icon, item.Label);
            }
            ImGui.EndTable();
        }
        ImGui.EndChild();
    }

    private void DrawPatreonMediaChip(FontAwesomeIcon icon, string label)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(40f));
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(0.04f, 0.05f, 0.09f, 1f)), Ui(7f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.45f)), Ui(7f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin + UiVec(8f, 12f), ImGui.GetColorU32(Accent), icon.ToIconString());
        }
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin + UiVec(29f, 12f), ImGui.GetColorU32(new Vector4(0.92f, 0.92f, 1f, 1f)), label);
    }

    private void DrawPatreonBenefits()
    {
        if (!ImGui.BeginTable("##patreonBenefits", 2, ImGuiTableFlags.SizingStretchSame))
        {
            return;
        }
        foreach (var benefit in PatreonBenefits)
        {
            ImGui.TableNextColumn();
            DrawPatreonBenefitCard(benefit.Icon, benefit.Title, benefit.Copy, benefit.Orange);
        }
        ImGui.EndTable();
    }

    private void DrawPatreonBenefitCard(FontAwesomeIcon icon, string title, string copy, bool orange)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(66f));
        ImGui.Dummy(size + UiVec(0f, 5f));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(0.045f, 0.055f, 0.095f, 1f)), Ui(9f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(new Vector4(0.24f, 0.3f, 0.46f, 0.9f)), Ui(9f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            draw.AddText(UiBuilder.IconFont, ImGui.GetFontSize() * 1.35f,
                origin + UiVec(13f, 22f), ImGui.GetColorU32(orange ? PatreonOrange : Accent), icon.ToIconString());
        }
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin + UiVec(46f, 10f), ImGui.GetColorU32(new Vector4(0.96f, 0.96f, 1f, 1f)), title);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), origin + UiVec(46f, 34f), ImGui.GetColorU32(MutedText), copy);
    }

    private void DrawPatreonFooter()
    {
        DrawPatreonCenteredText("Help shape what comes next", new Vector4(0.96f, 0.96f, 1f, 1f), 1.08f);
        DrawPatreonCenteredText(
            "Support hosting, streaming infrastructure, and continued development.", MutedText);
        ImGui.Dummy(UiVec(0f, 5f));
        var buttonOrigin = ImGui.GetCursorScreenPos();
        var buttonSize = new Vector2(ImGui.GetContentRegionAvail().X, Ui(40f));
        ImGui.InvisibleButton("##joinPatreon", buttonSize);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(buttonOrigin, buttonOrigin + buttonSize,
            ImGui.GetColorU32(hovered ? AccentHover : Accent), Ui(8f));
        const string buttonText = "Join us on Patreon";
        var textSize = ImGui.CalcTextSize(buttonText);
        var groupWidth = Ui(22f) + textSize.X;
        var groupX = buttonOrigin.X + (buttonSize.X - groupWidth) * 0.5f;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(groupX, buttonOrigin.Y + Ui(12f)),
                ImGui.GetColorU32(PatreonOrange), FontAwesomeIcon.Heart.ToIconString());
        }
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(groupX + Ui(22f), buttonOrigin.Y + (buttonSize.Y - textSize.Y) * 0.5f),
            ImGui.GetColorU32(Vector4.One), buttonText);
        DrawPatreonCenteredText("Opens in your default browser", MutedText);
    }

    private void DrawPatreonCenteredText(string text, Vector4 color, float scale = 1f)
    {
        SetUiFontScale(scale);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - ImGui.CalcTextSize(text).X) * 0.5f);
        ImGui.TextColored(color, text);
        SetUiFontScale(1f);
    }

    private void DrawPatreonFeatureTag(float gap = 12f)
    {
        ImGui.SameLine(0f, Ui(gap));
        SetUiFontScale(0.76f);
        ImGui.TextColored(PatreonOrange, "PATREON FEATURE");
        SetUiFontScale(1f);
    }
}
