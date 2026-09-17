using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? activeGameDialog;

    private void DrawGamePageDialogs()
    {
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8f, 8f));
        using var frames = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10f, 6f))
            .Push(ImGuiStyleVar.FrameRounding, Ui(7f));
        using var colors = ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.10f, 0.09f, 0.18f, 1f))
            .Push(ImGuiCol.FrameBgHovered, new Vector4(0.20f, 0.16f, 0.32f, 1f))
            .Push(ImGuiCol.CheckMark, Accent);
        if (gameKeyboardControlsOpenRequested)
        {
            activeGameDialog = "Keyboard controls";
            gameKeyboardControlsOpenRequested = false;
        }
        if (gameControllerControlsOpenRequested)
        {
            activeGameDialog = "Controller controls";
            gameControllerControlsOpenRequested = false;
            controllerBindingCapture = null;
            controllerBindingSetter = null;
        }
        if (snesRomSourcesPopupRequested)
        {
            activeGameDialog = "Game ROM information";
            snesRomSourcesPopupRequested = false;
        }
        DrawCompactGameKeyboardPopup(selectedGameSystem == GameSystem.Snes);
        DrawCompactGameControllerPopup(selectedGameSystem == GameSystem.Snes);
        DrawGameLibraryDialog();
        snesFileDialog.Draw();
        gameBoyFileDialog.Draw();
        nesFileDialog.Draw();
        gameBoyAdvanceFileDialog.Draw();
        masterSystemFileDialog.Draw();
        gameGearFileDialog.Draw();
        DrawGameRomHelp();
    }

    // Match the Add Media overlays: intercept input and shade only Alpha Channel.
    private bool BeginGameDialog(string id, string title, FontAwesomeIcon icon, float width, float height)
    {
        if (activeGameDialog != id) return false;
        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var size = new Vector2(MathF.Min(Ui(width), MathF.Max(1f, parentSize.X - Ui(40f))),
            MathF.Min(Ui(height), MathF.Max(1f, parentSize.Y - Ui(40f))));
        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("##gameDialogOverlay", ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground))
        {
            ImGui.End();
            return false;
        }
        ImGui.GetWindowDrawList().AddRectFilled(parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)));
        ImGui.SetCursorScreenPos(parentPosition + (parentSize - size) / 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Ui(14f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, Ui(1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.03f, 0.06f, 0.995f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f));
        ImGui.BeginChild("##gameDialogCard", size, true);
        var headingStart = ImGui.GetCursorScreenPos();
        var headingWidth = ImGui.GetContentRegionAvail().X;
        GameLayoutHeading(title, icon);
        var afterHeading = ImGui.GetCursorScreenPos();
        if (id == "Game library")
        {
            ImGui.SetCursorScreenPos(headingStart + new Vector2(headingWidth - Ui(28f), -Ui(4f)));
            using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
                if (ImGui.Button("X##closeLibrary", UiVec(28f, 28f))) activeGameDialog = null;
            ImGui.SetCursorScreenPos(afterHeading);
        }
        ImGui.Separator();
        ImGui.Spacing();
        return true;
    }

    private static void EndGameDialog()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        ImGui.End();
    }

    private void DrawGameBindingCell(string label, int value, Action<int> setValue)
    {
        ImGui.TableNextColumn();
        ImGui.PushID(label);
        if (ImGui.BeginTable("##bindingPair", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch, 0.42f);
            ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthStretch, 0.58f);
            DrawSnesConfigRow(label, value, setValue);
            ImGui.EndTable();
        }
        ImGui.PopID();
    }

    private void DrawGameRomHelp()
    {
        var snes = selectedGameSystem == GameSystem.Snes;
        var nes = selectedGameSystem == GameSystem.Nes;
        var gameBoyAdvance = selectedGameSystem == GameSystem.GameBoyAdvance;
        var masterSystem = selectedGameSystem == GameSystem.MasterSystem;
        var gameGear = selectedGameSystem == GameSystem.GameGear;
        if (!BeginGameDialog("Game ROM information", "ROM Information", FontAwesomeIcon.InfoCircle, 650f, snes || nes || gameBoyAdvance || masterSystem || gameGear ? 465f : 560f)) return;
        var systemName = snes ? "SNES" : nes ? "NES" : gameBoyAdvance ? "Game Boy Advance" : masterSystem ? "Master System and SG-1000" : gameGear ? "Game Gear" : "Game Boy and Game Boy Color";
        ImGui.TextWrapped($"It is possible to obtain {systemName} ROMs from many websites, which can be found using your preferred search engine.");
        ImGui.Spacing();
        if (snes)
        {
            DrawGameRomFileGuidance("SNES", ".sfc or .smc");
        }
        else if (nes)
        {
            DrawGameRomFileGuidance("NES", ".nes");
        }
        else if (gameBoyAdvance)
        {
            DrawGameRomFileGuidance("Game Boy Advance", ".gba");
        }
        else if (masterSystem)
        {
            DrawGameRomFileGuidance("Master System", ".sms");
            DrawGameRomFileGuidance("SG-1000", ".sg");
        }
        else if (gameGear)
        {
            DrawGameRomFileGuidance("Game Gear", ".gg");
        }
        else
        {
            DrawGameRomFileGuidance("Game Boy", ".gb or .dmg");
            ImGui.Spacing();
            DrawGameRomFileGuidance("Game Boy Color", ".gbc");
        }
        ImGui.Spacing();
        ImGui.TextColored(Gold, "Third-party download notice");
        ImGui.TextWrapped("Alpha Channel does not host these files and is not responsible for the content, safety, or legality of downloads from third-party websites. Only download ROMs you are legally permitted to use and exercise normal internet safety when downloading files from unfamiliar sources.");
        ImGui.Spacing();
        if (GameLayoutButton("Close", FontAwesomeIcon.Check, ImGui.GetContentRegionAvail().X, true)) activeGameDialog = null;
        EndGameDialog();
    }

    private static void DrawGameRomFileGuidance(string console, string extensions)
    {
        ImGui.TextColored(Accent, $"{console} file type");
        ImGui.TextWrapped($"A {console} ROM should normally use the {extensions} file extension. Checking the extension can help you identify whether a downloaded file is likely to be for the correct console.");
        ImGui.Spacing();
        ImGui.TextWrapped("ROM downloads may be supplied inside a .zip file. If so, extract the ZIP file first, then import the ROM file contained inside it.");
    }

    private bool DrawGameCrtToggle(ref bool enabled)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("CRT filter");
        ImGui.SameLine(0f, Ui(14f));
        var origin = ImGui.GetCursorScreenPos();
        var size = UiVec(42f, 24f);
        var clicked = ImGui.InvisibleButton("##gameCrtToggle", size);
        if (clicked) enabled = !enabled;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(enabled ? Accent : new Vector4(0.24f, 0.26f, 0.32f, 1f)), size.Y / 2f);
        draw.AddCircleFilled(origin + new Vector2(enabled ? size.X - size.Y / 2f : size.Y / 2f, size.Y / 2f), size.Y / 2f - Ui(3f), ImGui.GetColorU32(enabled ? Vector4.One : MutedText));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Apply a CRT screen style filter to the game.");
        ImGui.SameLine(0f, Ui(10f));
        using (ImRaii.PushFont(UiBuilder.IconFont)) ImGui.TextColored(MutedText, FontAwesomeIcon.InfoCircle.ToIconString());
        return clicked;
    }
}
