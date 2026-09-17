using System.Diagnostics;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private GameLibraryStorage LibraryStorage => new(Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "GameLibrary"));
    private string librarySearch = "";
    private string? libraryError;
    private GameLibraryEntry? libraryActionEntry;
    private string? libraryAction;
    private string libraryRename = "";
    private bool libraryRestored;
    // Null represents the shared "All systems" library view. Console metadata
    // remains derived from each ROM extension rather than stored in configuration.
    private GameSystem? libraryFilter;
    // A library-dialog choice is only committed to an emulator when Play Game
    // is pressed. This keeps browsing and closing the library side-effect free.
    private GameLibraryEntry? libraryPendingSelection;

    private void InitializeLocalLibraryRecovery()
    {
        EnsureBrowserLibraryLoaded();
        try
        {
            Plugin.Cfg.GameLibrary ??= new();
            var repaired = LibraryStorage.Repair(Plugin.Cfg.GameLibrary);
            var changed = repaired.Entries.Count != Plugin.Cfg.GameLibrary.Count ||
                repaired.Entries.Where((entry, index) => index >= Plugin.Cfg.GameLibrary.Count ||
                    !LibraryEntriesEqual(entry, Plugin.Cfg.GameLibrary[index])).Any();
            Plugin.Cfg.GameLibrary = repaired.Entries;
            if (changed) Plugin.Cfg.Save();
            if (!string.IsNullOrWhiteSpace(repaired.RecoveryMessage))
                Plugin.ChatGui.Print("[AlphaChannel] " + repaired.RecoveryMessage);
        }
        catch (Exception exception)
        {
            AepLog.Warning($"[GAME LIBRARY] Startup repair failed: {exception.Message}");
            Plugin.ChatGui.Print("[AlphaChannel] The game library could not be checked. Existing files were left untouched.");
        }
    }

    private static bool LibraryEntriesEqual(GameLibraryEntry left, GameLibraryEntry right) =>
        left.Hash == right.Hash && left.Extension == right.Extension && left.Name == right.Name && left.Icon == right.Icon;

    private void SaveGameLibraryMetadata()
    {
        Plugin.Cfg.Save();
        LibraryStorage.SaveIndex(Plugin.Cfg.GameLibrary);
    }

    private string LibraryGameName(string path)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        return Plugin.Cfg.GameLibrary.FirstOrDefault(e => e.Hash == folder)?.Name ?? Path.GetFileNameWithoutExtension(path);
    }

    private void SelectLibraryGame(GameLibraryEntry entry, bool switchSystem = true)
    {
        var path = LibraryStorage.RomPath(entry);
        var system = LibraryEntrySystem(entry);

        // Selecting from the shared All view also takes the main games page to
        // the emulator that can run this ROM.
        if (switchSystem)
        {
            selectedGameSystem = system;
        }

        if (system == GameSystem.Snes)
        {
            snesSelectedRomPath = path;
            Plugin.Cfg.SelectedSnesLibraryHash = entry.Hash;
        }
        else if (system == GameSystem.Nes)
        {
            nesSelectedRomPath = path;
            Plugin.Cfg.SelectedNesLibraryHash = entry.Hash;
        }
        else if (system == GameSystem.GameBoyAdvance)
        {
            gameBoyAdvanceSelectedRomPath = path;
            Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash = entry.Hash;
        }
        else if (system == GameSystem.MasterSystem)
        {
            masterSystemSelectedRomPath = path;
            Plugin.Cfg.SelectedMasterSystemLibraryHash = entry.Hash;
        }
        else if (system == GameSystem.GameGear)
        {
            gameGearSelectedRomPath = path;
            Plugin.Cfg.SelectedGameGearLibraryHash = entry.Hash;
        }
        else
        {
            gameBoySelectedRomPath = path;
            Plugin.Cfg.SelectedGameBoyLibraryHash = entry.Hash;
        }
    }

    private void RestoreLibrarySelection()
    {
        if (libraryRestored) return;
        libraryRestored = true;
        Plugin.Cfg.GameLibrary ??= new();
        foreach (var entry in Plugin.Cfg.GameLibrary)
        {
            if (entry.Hash != Plugin.Cfg.SelectedSnesLibraryHash && entry.Hash != Plugin.Cfg.SelectedGameBoyLibraryHash && entry.Hash != Plugin.Cfg.SelectedNesLibraryHash && entry.Hash != Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash && entry.Hash != Plugin.Cfg.SelectedMasterSystemLibraryHash && entry.Hash != Plugin.Cfg.SelectedGameGearLibraryHash) continue;
            try { SelectLibraryGame(entry, switchSystem: false); }
            catch (Exception e) { libraryError = e.Message; }
        }
    }

    private static bool LibraryEntryMatchesSystem(GameLibraryEntry entry, GameSystem system) => system switch
    {
        GameSystem.Snes => GameLibraryStorage.IsSnes(entry),
        GameSystem.Nes => GameLibraryStorage.IsNes(entry),
        GameSystem.GameBoyAdvance => GameLibraryStorage.IsGameBoyAdvance(entry),
        GameSystem.MasterSystem => GameLibraryStorage.IsMasterSystem(entry),
        GameSystem.GameGear => GameLibraryStorage.IsGameGear(entry),
        GameSystem.GameBoy => !GameLibraryStorage.IsSnes(entry) && !GameLibraryStorage.IsNes(entry) && !GameLibraryStorage.IsGameBoyAdvance(entry) && !GameLibraryStorage.IsMasterSystem(entry) && !GameLibraryStorage.IsGameGear(entry),
        _ => false
    };

    private static GameSystem LibraryEntrySystem(GameLibraryEntry entry) =>
        GameLibraryStorage.IsSnes(entry)
            ? GameSystem.Snes
            : GameLibraryStorage.IsNes(entry)
                ? GameSystem.Nes
                : GameLibraryStorage.IsGameBoyAdvance(entry)
                    ? GameSystem.GameBoyAdvance
                    : GameLibraryStorage.IsMasterSystem(entry)
                        ? GameSystem.MasterSystem
                        : GameLibraryStorage.IsGameGear(entry)
                            ? GameSystem.GameGear
                        : GameSystem.GameBoy;

    private static string LibrarySystemName(GameLibraryEntry entry) =>
        GameLibraryStorage.IsSnes(entry)
            ? "Super Nintendo"
            : GameLibraryStorage.IsNes(entry)
                ? "Nintendo Entertainment System"
                : GameLibraryStorage.IsGameBoyAdvance(entry)
                    ? "Game Boy Advance"
                    : GameLibraryStorage.IsMasterSystem(entry)
                        ? entry.Extension == ".sg" ? "SG-1000" : "Sega Master System"
                    : GameLibraryStorage.IsGameGear(entry)
                        ? "Game Gear"
                    : entry.Extension == ".gbc"
                        ? "Game Boy Color"
                        : "Game Boy";

    private static string LibraryFilterName(GameSystem? system) => system switch
    {
        GameSystem.Snes => "Super Nintendo",
        GameSystem.Nes => "Nintendo Entertainment System",
        GameSystem.GameBoyAdvance => "Game Boy Advance",
        GameSystem.MasterSystem => "Master System / SG-1000",
        GameSystem.GameGear => "Game Gear",
        GameSystem.GameBoy => "Game Boy / Color",
        _ => "All systems"
    };

    private static bool LibraryEntryMatchesFilter(GameLibraryEntry entry, GameSystem? system) =>
        system is null || LibraryEntryMatchesSystem(entry, system.Value);

    private string? SelectedLibraryHash(GameSystem system) => system switch
    {
        GameSystem.Snes => Plugin.Cfg.SelectedSnesLibraryHash,
        GameSystem.Nes => Plugin.Cfg.SelectedNesLibraryHash,
        GameSystem.GameBoyAdvance => Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash,
        GameSystem.MasterSystem => Plugin.Cfg.SelectedMasterSystemLibraryHash,
        GameSystem.GameGear => Plugin.Cfg.SelectedGameGearLibraryHash,
        _ => Plugin.Cfg.SelectedGameBoyLibraryHash
    };

    private void AddLibraryGame(GameSystem? requestedSystem)
    {
        var dialog = requestedSystem == GameSystem.Snes ? snesFileDialog : requestedSystem == GameSystem.Nes ? nesFileDialog : requestedSystem == GameSystem.GameBoyAdvance ? gameBoyAdvanceFileDialog : requestedSystem == GameSystem.MasterSystem ? masterSystemFileDialog : requestedSystem == GameSystem.GameGear ? gameGearFileDialog : gameBoyFileDialog;
        var filter = requestedSystem == GameSystem.Snes ? ".sfc,.smc" : requestedSystem == GameSystem.Nes ? ".nes" : requestedSystem == GameSystem.GameBoyAdvance ? ".gba" : requestedSystem == GameSystem.MasterSystem ? ".sms,.sg" : requestedSystem == GameSystem.GameGear ? ".gg" : requestedSystem == GameSystem.GameBoy ? ".gb,.gbc,.dmg" : ".sfc,.smc,.nes,.gb,.gbc,.dmg,.gba,.sms,.sg,.gg";
        dialog.OpenFileDialog("Add game to library", filter, (success, path) =>
        {
            if (!success || string.IsNullOrWhiteSpace(path)) return;
            try
            {
                // Do not replace selection while an emulator owns the screen.
                if (screenController.Engine.IsPlayingGame) return;
                var extension = Path.GetExtension(path).ToLowerInvariant();
                var detectedSystem = extension switch
                {
                    ".sfc" or ".smc" => GameSystem.Snes,
                    ".nes" => GameSystem.Nes,
                    ".gba" => GameSystem.GameBoyAdvance,
                    ".sms" or ".sg" => GameSystem.MasterSystem,
                    ".gg" => GameSystem.GameGear,
                    ".gb" or ".gbc" or ".dmg" => GameSystem.GameBoy,
                    _ => throw new InvalidOperationException("That file is not a supported game ROM.")
                };
                if (requestedSystem is { } expectedSystem && detectedSystem != expectedSystem)
                    throw new InvalidOperationException($"Choose a {LibraryFilterName(expectedSystem)} ROM.");

                var entry = detectedSystem == GameSystem.Nes
                    ? LibraryStorage.ImportNes(path, Plugin.Cfg.GameLibrary)
                    : detectedSystem == GameSystem.GameBoyAdvance
                        ? LibraryStorage.ImportGameBoyAdvance(path, Plugin.Cfg.GameLibrary)
                        : detectedSystem == GameSystem.MasterSystem
                            ? LibraryStorage.ImportMasterSystem(path, Plugin.Cfg.GameLibrary)
                        : detectedSystem == GameSystem.GameGear
                            ? LibraryStorage.ImportGameGear(path, Plugin.Cfg.GameLibrary)
                        : LibraryStorage.Import(path, detectedSystem == GameSystem.Snes, Plugin.Cfg.GameLibrary);
                if (!Plugin.Cfg.GameLibrary.Contains(entry)) Plugin.Cfg.GameLibrary.Add(entry);
                SelectLibraryGame(entry);
                SaveGameLibraryMetadata();
                libraryError = null;
            }
            catch (Exception e) { libraryError = "Could not import game: " + e.Message; }
        });
    }

    private void DrawLibrarySelector(GameSystem system, bool playing)
    {
        using var popupRounding = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(10f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(8f, 8f));
        using var popupColors = ImRaii.PushColor(ImGuiCol.PopupBg, new Vector4(0.035f, 0.045f, 0.075f, 1f))
            .Push(ImGuiCol.Border, Accent).Push(ImGuiCol.Header, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.22f))
            .Push(ImGuiCol.HeaderHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.35f));
        var games = Plugin.Cfg.GameLibrary.Where(e => LibraryEntryMatchesSystem(e, system)).ToList();
        var selectedHash = SelectedLibraryHash(system);
        var selected = games.FirstOrDefault(e => e.Hash == selectedHash);
        ImGui.TextColored(MutedText, "Your games");
        using (ImRaii.Disabled(playing))
        {
            ImGui.SetNextItemWidth(MathF.Max(Ui(60f), ImGui.GetContentRegionAvail().X - Ui(130f)));
            if (ImGui.BeginCombo("##libraryGame", selected?.Name ?? "Add a game to get started"))
            {
                foreach (var entry in games)
                    if (DrawLibraryGameRow(entry, entry == selected, false))
                    {
                        SelectLibraryGame(entry);
                        Plugin.Cfg.Save();
                        ImGui.CloseCurrentPopup();
                    }
                ImGui.EndCombo();
            }
            ImGui.SameLine(0f, Ui(8f));
            if (GameLayoutButton("Add Games...", FontAwesomeIcon.FolderOpen, Ui(122f))) AddLibraryGame(system);
        }
        var shortName = system == GameSystem.Snes ? "SNES" : system == GameSystem.Nes ? "NES" : system == GameSystem.GameBoyAdvance ? "Game Boy Advance" : system == GameSystem.MasterSystem ? "Master System / SG-1000" : system == GameSystem.GameGear ? "Game Gear" : "Game Boy / Color";
        ImGui.TextColored(MutedText, $"{games.Count} {shortName} games in your library");
        ImGui.Spacing();
        var manageWidth = (ImGui.GetContentRegionAvail().X - Ui(8f)) / 2f;
        if (GameLayoutButton("Manage Library", FontAwesomeIcon.List, manageWidth))
        {
            libraryAction = null;
            libraryActionEntry = null;
            libraryFilter = system;
            librarySearch = string.Empty;
            libraryPendingSelection = null;
            activeGameDialog = "Game library";
        }
        ImGui.SameLine(0f, Ui(8f));
        if (GameLayoutButton("ROM information", FontAwesomeIcon.InfoCircle, manageWidth)) snesRomSourcesPopupRequested = true;
        if (!string.IsNullOrWhiteSpace(libraryError)) ImGui.TextWrapped(libraryError);
        ImGui.Dummy(UiVec(0f, 8f));
    }

    private void DrawGameLibraryDialog()
    {
        if (!BeginGameDialog("Game library", "Manage Library", FontAwesomeIcon.Gamepad, 660f, 520f)) return;
        using var popupStyle = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(9f));
        using var popupColors = ImRaii.PushColor(ImGuiCol.PopupBg, new Vector4(0.035f, 0.045f, 0.075f, 1f))
            .Push(ImGuiCol.Border, BorderSubtle).Push(ImGuiCol.HeaderHovered, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.25f));
        var system = libraryFilter;
        var playing = screenController.Engine.IsPlayingGame;
        if (libraryActionEntry is { } iconTarget && libraryAction == "Change Icon")
        {
            DrawLibraryIconGallery(iconTarget);
        }
        else if (libraryActionEntry is { } target && libraryAction is { } action)
        {
            ImGui.TextUnformatted(target.Name);
            if (action == "Rename") ImGui.InputText("Name", ref libraryRename, 120);
            else ImGui.TextWrapped(action == "Remove from Library"
                ? "Remove this game's imported ROM and save data from the library? Your original files will not be changed. This cannot be undone."
                : "Permanently clear this game's library save data? Your progress will be lost. The ROM and original files will not be changed.");
            if (playing) ImGui.TextWrapped("Exit the running game before changing library data.");
            using (ImRaii.Disabled(playing || (action == "Rename" && string.IsNullOrWhiteSpace(libraryRename))))
            {
                if (GameLayoutButton(action == "Rename" ? "Save name" : "Confirm " + action, FontAwesomeIcon.Check,
                    ImGui.GetContentRegionAvail().X, action == "Rename", action != "Rename"))
                {
                    try
                    {
                        if (action == "Rename") target.Name = libraryRename.Trim();
                        else if (action == "Clear save data") LibraryStorage.ClearSave(target);
                        else
                        {
                            LibraryStorage.Remove(target);
                            Plugin.Cfg.GameLibrary.Remove(target);
                            if (libraryPendingSelection?.Hash == target.Hash) libraryPendingSelection = null;
                            if (Plugin.Cfg.SelectedSnesLibraryHash == target.Hash) { Plugin.Cfg.SelectedSnesLibraryHash = null; snesSelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedGameBoyLibraryHash == target.Hash) { Plugin.Cfg.SelectedGameBoyLibraryHash = null; gameBoySelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedNesLibraryHash == target.Hash) { Plugin.Cfg.SelectedNesLibraryHash = null; nesSelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash == target.Hash) { Plugin.Cfg.SelectedGameBoyAdvanceLibraryHash = null; gameBoyAdvanceSelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedMasterSystemLibraryHash == target.Hash) { Plugin.Cfg.SelectedMasterSystemLibraryHash = null; masterSystemSelectedRomPath = ""; }
                            if (Plugin.Cfg.SelectedGameGearLibraryHash == target.Hash) { Plugin.Cfg.SelectedGameGearLibraryHash = null; gameGearSelectedRomPath = ""; }
                        }
                        SaveGameLibraryMetadata();
                        libraryAction = null;
                        libraryError = null;
                    }
                    catch (Exception e) { libraryError = e.Message; }
                }
            }
            if (GameLayoutButton("Cancel", FontAwesomeIcon.Times, ImGui.GetContentRegionAvail().X)) libraryAction = null;
        }
        else
        {
            ImGui.TextColored(MutedText, "Browse every imported game or filter by console.");
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.BeginCombo("##librarySystemFilter", LibraryFilterName(system)))
            {
                GameSystem?[] filters = [null, GameSystem.Snes, GameSystem.Nes, GameSystem.GameBoy, GameSystem.GameBoyAdvance, GameSystem.MasterSystem, GameSystem.GameGear];
                foreach (var filter in filters)
                {
                    var selectedFilter = filter == system;
                    if (ImGui.Selectable(LibraryFilterName(filter), selectedFilter))
                    {
                        system = filter;
                        libraryFilter = filter;
                    }
                    if (selectedFilter) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            ImGui.Spacing();
            ImGui.SetNextItemWidth(MathF.Max(Ui(60f), ImGui.GetContentRegionAvail().X - Ui(140f)));
            ImGui.InputTextWithHint("##librarySearch", "Search games...", ref librarySearch, 120);
            ImGui.SameLine(0f, Ui(8f));
            using (ImRaii.Disabled(playing))
                if (GameLayoutButton("Add Games...", FontAwesomeIcon.FolderOpen, Ui(132f), true)) AddLibraryGame(system);
            using (var list = ImRaii.Child("##libraryList", new Vector2(0f, MathF.Max(Ui(80f), ImGui.GetContentRegionAvail().Y - Ui(105f))), false))
            {
                if (list)
                {
                    var entries = Plugin.Cfg.GameLibrary.Where(e => LibraryEntryMatchesFilter(e, system) && e.Name.Contains(librarySearch, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (entries.Count == 0) ImGui.TextWrapped("No games found. Add a ROM to build your library.");
                    foreach (var entry in entries)
                    {
                        ImGui.PushID(entry.Hash);
                        using (ImRaii.Disabled(playing))
                            if (DrawLibraryGameRow(entry, entry.Hash == libraryPendingSelection?.Hash, true))
                            {
                                libraryPendingSelection = entry;
                            }
                        ImGui.SameLine(0f, Ui(8f));
                        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.055f, 0.07f, 0.115f, 1f))
                            .Push(ImGuiCol.ButtonHovered, CardBgHover))
                        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
                            if (ImGui.Button("...", UiVec(36f, 56f))) ImGui.OpenPopup("Actions");
                        if (ImGui.BeginPopup("Actions"))
                        {
                            if (LibraryMenuItem("Open Folder", FontAwesomeIcon.FolderOpen))
                            {
                                try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(LibraryStorage.RomPath(entry))!) { UseShellExecute = true }); }
                                catch (Exception e) { libraryError = e.Message; }
                            }
                            if (LibraryMenuItem("Change Icon", FontAwesomeIcon.ThLarge))
                            {
                                libraryActionEntry = entry;
                                libraryAction = "Change Icon";
                            }
                            using (ImRaii.Disabled(playing))
                                foreach (var option in new[] { "Rename", "Clear save data", "Remove from Library" })
                                {
                                    using var actionColor = ImRaii.PushColor(ImGuiCol.Text, option == "Rename" ? Vector4.One : Danger);
                                    var optionIcon = option == "Rename" ? FontAwesomeIcon.Pen : option == "Clear save data" ? FontAwesomeIcon.Eraser : FontAwesomeIcon.Trash;
                                    if (LibraryMenuItem(option, optionIcon)) { libraryActionEntry = entry; libraryAction = option; libraryRename = entry.Name; }
                                }
                            ImGui.EndPopup();
                        }
                        ImGui.PopID();
                    }
                }
            }
            using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                ImGui.TextWrapped("Imported games are copied into your library. Originals stay untouched.\nROMs and save data are kept together.");
            ImGui.Separator();
            var footerWidth = ImGui.GetContentRegionAvail().X;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MutedText, $"{Plugin.Cfg.GameLibrary.Count(e => LibraryEntryMatchesFilter(e, system))} games");
            var footerGap = Ui(8f);
            var closeWidth = Ui(132f);
            var playWidth = Ui(120f);
            ImGui.SameLine(footerWidth - closeWidth - playWidth - footerGap + ImGui.GetStyle().WindowPadding.X);
            if (GameLayoutButton("Close Library", FontAwesomeIcon.Times, closeWidth))
            {
                libraryPendingSelection = null;
                activeGameDialog = null;
            }
            ImGui.SameLine(0f, footerGap);
            using (ImRaii.Disabled(playing || libraryPendingSelection is null))
            {
                if (GameLayoutButton("Play Game", FontAwesomeIcon.Play, playWidth, true) &&
                    libraryPendingSelection is { } selectedGame)
                {
                    SelectLibraryGame(selectedGame);
                    Plugin.Cfg.Save();
                    libraryPendingSelection = null;
                    activeGameDialog = null;
                }
            }
        }
        if (libraryError is not null) ImGui.TextWrapped(libraryError);
        EndGameDialog();
    }

    private bool DrawLibraryGameRow(GameLibraryEntry entry, bool selected, bool showDetails)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (showDetails ? Ui(44f) : 0f));
        var size = new Vector2(width, Ui(showDetails ? 56f : 38f));
        var clicked = ImGui.InvisibleButton("##libraryRow_" + entry.Hash, size);
        var hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetTooltip(entry.Name);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(hovered ? CardBgHover : CardBg), Ui(8f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(selected ? Accent : BorderSubtle), Ui(8f));
        var tile = Ui(showDetails ? 40f : 26f);
        var tileOrigin = origin + UiVec(7f, 7f);
        draw.AddRectFilled(tileOrigin, tileOrigin + new Vector2(tile), ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.30f)), Ui(7f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = GetLibraryIcon(entry).ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), tileOrigin + (new Vector2(tile) - glyphSize) / 2f, ImGui.GetColorU32(Accent), glyph);
        }
        var textX = tileOrigin.X + tile + Ui(10f);
        var badgeWidth = selected && width > Ui(270f) ? Ui(70f) : 0f;
        draw.PushClipRect(new Vector2(textX, origin.Y), new Vector2(origin.X + width - badgeWidth - Ui(10f), origin.Y + size.Y), true);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(textX, origin.Y + Ui(showDetails ? 9f : 10f)), ImGui.GetColorU32(ImGuiCol.Text), entry.Name);
        if (showDetails)
        {
            var systemName = LibrarySystemName(entry);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(textX, origin.Y + Ui(31f)), ImGui.GetColorU32(MutedText), systemName + "  •  " + entry.Extension);
        }
        draw.PopClipRect();
        if (badgeWidth > 0f)
        {
            var badgeOrigin = origin + new Vector2(width - badgeWidth - Ui(7f), (size.Y - Ui(22f)) / 2f);
            draw.AddRectFilled(badgeOrigin, badgeOrigin + new Vector2(badgeWidth, Ui(22f)), ImGui.GetColorU32(Accent), Ui(11f));
            var labelSize = ImGui.CalcTextSize("Selected");
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), badgeOrigin + (new Vector2(badgeWidth, Ui(22f)) - labelSize) / 2f, ImGui.GetColorU32(ImGuiCol.Text), "Selected");
        }
        return clicked;
    }

    private static readonly FontAwesomeIcon[] LibraryIconChoices =
    [
        FontAwesomeIcon.Gamepad, FontAwesomeIcon.Star, FontAwesomeIcon.Heart,
        FontAwesomeIcon.Bolt, FontAwesomeIcon.Fire, FontAwesomeIcon.Moon,
        FontAwesomeIcon.Sun, FontAwesomeIcon.Ghost, FontAwesomeIcon.Dragon,
        FontAwesomeIcon.HatWizard, FontAwesomeIcon.ShieldAlt, FontAwesomeIcon.Crown,
        FontAwesomeIcon.Gem, FontAwesomeIcon.Rocket, FontAwesomeIcon.Car,
        FontAwesomeIcon.Futbol, FontAwesomeIcon.ChessKnight, FontAwesomeIcon.Dice,
        FontAwesomeIcon.PuzzlePiece, FontAwesomeIcon.Music, FontAwesomeIcon.Leaf,
        FontAwesomeIcon.Paw, FontAwesomeIcon.Fish, FontAwesomeIcon.Skull
    ];

    private static FontAwesomeIcon GetLibraryIcon(GameLibraryEntry entry) =>
        Enum.TryParse<FontAwesomeIcon>(entry.Icon, out var icon) && LibraryIconChoices.Contains(icon)
            ? icon : FontAwesomeIcon.Gamepad;

    private bool LibraryMenuItem(string label, FontAwesomeIcon icon)
    {
        var clicked = ImGui.MenuItem("      " + label);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var size = ImGui.CalcTextSize(glyph);
            ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(min.X + Ui(5f), min.Y + (max.Y - min.Y - size.Y) / 2f),
                ImGui.GetColorU32(ImGuiCol.Text), glyph);
        }
        return clicked;
    }

    private void DrawLibraryIconGallery(GameLibraryEntry entry)
    {
        ImGui.TextUnformatted(entry.Name);
        ImGui.TextColored(MutedText, "Choose an icon for this game.");
        ImGui.Spacing();
        var gap = Ui(10f);
        var columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / Ui(66f)), 1, 8);
        var tile = MathF.Min(Ui(56f), (ImGui.GetContentRegionAvail().X - gap * (columns - 1)) / columns);
        for (var index = 0; index < LibraryIconChoices.Length; index++)
        {
            if (index % columns != 0) ImGui.SameLine(0f, gap);
            var icon = LibraryIconChoices[index];
            using var color = ImRaii.PushColor(ImGuiCol.Button, GetLibraryIcon(entry) == icon ? Accent : CardBg)
                .Push(ImGuiCol.ButtonHovered, AccentHover);
            bool clicked;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                clicked = ImGui.Button(icon.ToIconString() + "##libraryIcon" + index, new Vector2(tile));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(icon.ToString());
            if (clicked)
            {
                entry.Icon = icon.ToString();
                SaveGameLibraryMetadata();
                libraryAction = null;
            }
        }
        ImGui.Spacing();
        if (GameLayoutButton("Back to Library", FontAwesomeIcon.ArrowLeft, ImGui.GetContentRegionAvail().X)) libraryAction = null;
    }
}
