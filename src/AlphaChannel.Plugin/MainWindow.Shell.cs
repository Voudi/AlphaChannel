using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace AlphaChannel.Plugin;

// Mockup chrome: Home right rail + always-on bottom media bar + optional custom window background.
internal sealed partial class MainWindow
{
    private IDalamudTextureWrap? homeHero;
    private string? homeHeroLoadedPath;
    private bool homeHeroLoadStarted;
    private ISharedImmediateTexture? createRoomImage;
    private ISharedImmediateTexture? joinRoomImage;
    private ISharedImmediateTexture? leaveRoomImage;
    private ISharedImmediateTexture? viewRoomImage;
    private bool playerFocusJoin;

    private IDalamudTextureWrap? customBackground;
    private string? customBackgroundLoadedPath;
    private bool customBackgroundLoadStarted;
    private string customBackgroundPathInput = string.Empty;
    private bool customBackgroundPathSynced;
    private string? customBackgroundError;

    private string customHomeHeroPathInput = string.Empty;
    private bool customHomeHeroPathSynced;
    private string? customHomeHeroError;
    private string chatPlaceholder = "You can chat once you're in a room";

    private double playbackStartedAt;
    private bool lastPlaybackState;
    private double playbackStoppedAt;
    private bool playbackWasActive;

    private void EnsureHomeHeroLoaded()
    {
        var path = ResolveHomeHeroPath();
        if (path is null)
        {
            return;
        }

        if (homeHero is not null &&
            string.Equals(homeHeroLoadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (homeHeroLoadStarted &&
            string.Equals(homeHeroLoadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        homeHeroLoadStarted = true;
        homeHeroLoadedPath = path;
        _ = LoadHomeHeroAsync(path);
    }

    private void EnsureRoomButtonsLoaded()
    {
        var dir = Plugin.PluginInterface.AssemblyLocation.DirectoryName;

        if (dir is null)
        {
            return;
        }

        if (createRoomImage is null)
        {
            var path = Path.Combine(dir, "Assets", "create.png");

            if (File.Exists(path))
            {
                createRoomImage = Plugin.TextureProvider.GetFromFile(path);
            }
        }

        if (joinRoomImage is null)
        {
            var path = Path.Combine(dir, "Assets", "join.png");

            if (File.Exists(path))
            {
                joinRoomImage = Plugin.TextureProvider.GetFromFile(path);
            }
        }
        if (leaveRoomImage is null)
        {
            var path = Path.Combine(dir, "Assets", "leaveroom.png");

            if (File.Exists(path))
            {
                leaveRoomImage = Plugin.TextureProvider.GetFromFile(path);
            }
        }

        if (viewRoomImage is null)
        {
            var path = Path.Combine(dir, "Assets", "viewroom.png");

            if (File.Exists(path))
            {
                viewRoomImage = Plugin.TextureProvider.GetFromFile(path);
            }
        }
    }

    private static string? ResolveHomeHeroPath()
    {
        var custom = Plugin.Cfg.CustomHomeHeroPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
        {
            return custom;
        }

        var dir = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        if (dir is null)
        {
            return null;
        }

        var bundled = Path.Combine(dir, "Assets", "home-hero.png");
        return File.Exists(bundled) ? bundled : null;
    }

    private async Task LoadHomeHeroAsync(string path)
    {
        try
        {
            var sourceBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            using var image = Image.Load(sourceBytes);
            using var pngStream = new MemoryStream();
            await image.SaveAsync(pngStream, new PngEncoder()).ConfigureAwait(false);
            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(pngStream.ToArray())
                .ConfigureAwait(false);

            var old = homeHero;
            homeHero = wrap;
            homeHeroLoadedPath = path;
            old?.Dispose();
        }
        catch (Exception exception)
        {
            customHomeHeroError = "Couldn't load that Home illustration.";
            AepLog.Warning($"[Home] Failed to load hero {path}: {exception.Message}");
        }
        finally
        {
            homeHeroLoadStarted = false;
        }
    }

    private bool TryApplyCustomHomeHeroFromPath(string rawPath)
    {
        customHomeHeroError = null;
        var path = rawPath.Trim().Trim('"');
        if (path.Length == 0 || !File.Exists(path))
        {
            customHomeHeroError = "Pick an existing png, jpg, or webp file.";
            return false;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
        {
            customHomeHeroError = "Use a png, jpg, webp, or bmp image.";
            return false;
        }

        try
        {
            var destDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Backgrounds");
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "home-hero" + ext);
            File.Copy(path, dest, overwrite: true);

            Plugin.Cfg.CustomHomeHeroPath = dest;
            Plugin.Cfg.ShowHomeHeroImage = true;
            Plugin.Cfg.Save();

            customHomeHeroPathInput = dest;
            homeHero?.Dispose();
            homeHero = null;
            homeHeroLoadedPath = null;
            homeHeroLoadStarted = false;
            EnsureHomeHeroLoaded();
            return true;
        }
        catch (Exception exception)
        {
            customHomeHeroError = "Couldn't copy that file into the plugin folder.";
            AepLog.Warning($"[Home] Hero copy failed: {exception.Message}");
            return false;
        }
    }

    private void ClearCustomHomeHero()
    {
        Plugin.Cfg.CustomHomeHeroPath = null;
        Plugin.Cfg.Save();
        homeHero?.Dispose();
        homeHero = null;
        homeHeroLoadedPath = null;
        homeHeroLoadStarted = false;
        customHomeHeroPathInput = string.Empty;
        customHomeHeroPathSynced = false;
        customHomeHeroError = null;
        if (Plugin.Cfg.ShowHomeHeroImage)
        {
            EnsureHomeHeroLoaded();
        }
    }

    private void EnsureCustomBackgroundLoaded()
    {
        var path = Plugin.Cfg.CustomBackgroundPath;
        if (Plugin.Cfg.UiBackground != UiBackground.Custom || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (customBackground is not null &&
            string.Equals(customBackgroundLoadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (customBackgroundLoadStarted &&
            string.Equals(customBackgroundLoadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        customBackgroundLoadStarted = true;
        customBackgroundLoadedPath = path;
        _ = LoadCustomBackgroundAsync(path);
    }

    private async Task LoadCustomBackgroundAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                customBackgroundError = "Background file not found.";
                return;
            }

            var sourceBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            using var image = Image.Load(sourceBytes);
            using var pngStream = new MemoryStream();
            await image.SaveAsync(pngStream, new PngEncoder()).ConfigureAwait(false);
            var wrap = await Plugin.TextureProvider.CreateFromImageAsync(pngStream.ToArray())
                .ConfigureAwait(false);

            var old = customBackground;
            customBackground = wrap;
            customBackgroundLoadedPath = path;
            customBackgroundError = null;
            old?.Dispose();
        }
        catch (Exception exception)
        {
            customBackgroundError = "Couldn't load that image.";
            AepLog.Warning($"[Background] Failed to load {path}: {exception.Message}");
        }
        finally
        {
            customBackgroundLoadStarted = false;
        }
    }

    private void DrawCustomBackgroundLayer()
    {
        if (Plugin.Cfg.UiBackground != UiBackground.Custom || customBackground is null)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var boxW = max.X - min.X;
        var boxH = max.Y - min.Y;

        // Fit the whole image inside the window (contain) and center it — no stretch, no crop.
        var (imgMin, imgMax) = ContainRect(
            customBackground.Width, customBackground.Height, min, boxW, boxH);
        drawList.AddImage(customBackground.Handle, imgMin, imgMax);

        var dim = Math.Clamp(Plugin.Cfg.CustomBackgroundDim, 0f, 0.9f);
        if (dim > 0.01f)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, dim)));
        }
    }

    // Scale the image to fit inside the box, then center the resulting rect.
    private static (Vector2 Min, Vector2 Max) ContainRect(
        float texW, float texH, Vector2 boxOrigin, float boxW, float boxH)
    {
        if (texW <= 0 || texH <= 0 || boxW <= 0 || boxH <= 0)
        {
            return (boxOrigin, boxOrigin + new Vector2(boxW, boxH));
        }

        var scale = MathF.Min(boxW / texW, boxH / texH);
        var drawW = texW * scale;
        var drawH = texH * scale;
        var origin = boxOrigin + new Vector2((boxW - drawW) * 0.5f, (boxH - drawH) * 0.5f);
        return (origin, origin + new Vector2(drawW, drawH));
    }

    private bool TryApplyCustomBackgroundFromPath(string rawPath)
    {
        customBackgroundError = null;
        var path = rawPath.Trim().Trim('"');
        if (path.Length == 0 || !File.Exists(path))
        {
            customBackgroundError = "Pick an existing png, jpg, or webp file.";
            return false;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
        {
            customBackgroundError = "Use a png, jpg, webp, or bmp image.";
            return false;
        }

        try
        {
            var destDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Backgrounds");
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, "custom" + ext);
            File.Copy(path, dest, overwrite: true);

            Plugin.Cfg.CustomBackgroundPath = dest;
            Plugin.Cfg.UiBackground = UiBackground.Custom;
            Plugin.Cfg.Save();

            customBackgroundPathInput = dest;
            customBackground?.Dispose();
            customBackground = null;
            customBackgroundLoadedPath = null;
            customBackgroundLoadStarted = false;
            EnsureCustomBackgroundLoaded();
            Colors = ThemeCatalog.Get(Plugin.Cfg.UiTheme, UiBackground.Custom);
            return true;
        }
        catch (Exception exception)
        {
            customBackgroundError = "Couldn't copy that file into the plugin folder.";
            AepLog.Warning($"[Background] Copy failed: {exception.Message}");
            return false;
        }
    }

    private void ClearCustomBackground()
    {
        Plugin.Cfg.CustomBackgroundPath = null;
        if (Plugin.Cfg.UiBackground == UiBackground.Custom)
        {
            Plugin.Cfg.UiBackground = UiBackground.Theme;
        }

        Plugin.Cfg.Save();
        customBackground?.Dispose();
        customBackground = null;
        customBackgroundLoadedPath = null;
        customBackgroundLoadStarted = false;
        customBackgroundPathInput = string.Empty;
        customBackgroundPathSynced = false;
        customBackgroundError = null;
        Colors = ThemeCatalog.Get(Plugin.Cfg.UiTheme, Plugin.Cfg.UiBackground);
    }

    private static string? FindImageInDownloads()
    {
        try
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (!Directory.Exists(downloads))
            {
                return null;
            }

            string[] patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"];
            return patterns
                .SelectMany(pattern => Directory.GetFiles(downloads, pattern))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private void DrawHomeRightRail()
    {
        EnsureRoomButtonsLoaded();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(Accent, FontAwesomeIcon.Users.ToIconString());
        }

        ImGui.SameLine(0, 10);

        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted("Room Management");
        ImGui.SetWindowFontScale(1f);

        

        ImGui.TextColored(MutedText, "Join or create a room to get started.");

        ImGui.Dummy(new Vector2(0, 10));

        var roomButtonWidth = ImGui.GetContentRegionAvail().X;
        var roomButtonHeight = roomButtonWidth * (86f / 320f);
        var roomButtonSize = new Vector2(roomButtonWidth, roomButtonHeight);

        var createWrap = createRoomImage?.GetWrapOrDefault();

        if (createWrap is not null)
        {
            ImGui.Image(
                createWrap.Handle,
                roomButtonSize);
        }



        var joinWrap = joinRoomImage?.GetWrapOrDefault();

        if (joinWrap is not null)
        {
            ImGui.Image(
                joinWrap.Handle,
                roomButtonSize);
        }

        ImGui.Dummy(new Vector2(0, 10));

        ImGui.Separator();






        var roomBoxSize = new Vector2(
            ImGui.GetContentRegionAvail().X,
            MathF.Min(450f, ImGui.GetContentRegionAvail().Y));

        var roomBoxPos = ImGui.GetCursorScreenPos();

        var drawList = ImGui.GetWindowDrawList();

        DrawDashedRect(
            drawList,
            roomBoxPos,
            roomBoxPos + roomBoxSize,
            ImGui.GetColorU32(new Vector4(
                MutedText.X,
                MutedText.Y,
                MutedText.Z,
                0.45f)),
            10f);

        ImGui.Dummy(roomBoxSize);

        ImGui.SetCursorScreenPos(
    roomBoxPos + new Vector2(14, 14));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.Users.ToIconString());
        }

        ImGui.SameLine(0, 8);

        ImGui.TextUnformatted("Active Room");

        var centerX = roomBoxPos.X + (roomBoxSize.X * 0.5f);

        ImGui.SetCursorScreenPos(
            new Vector2(centerX - 8, roomBoxPos.Y + 48));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var icon = FontAwesomeIcon.Desktop.ToIconString();
            var iconSize = ImGui.CalcTextSize(icon);

            ImGui.SetCursorScreenPos(
                new Vector2(centerX - iconSize.X / 2, roomBoxPos.Y + 70));

            ImGui.TextColored(
                MutedText,
                icon);
        }

        var title = "No room joined";
        var titleSize = ImGui.CalcTextSize(title);

        ImGui.SetCursorScreenPos(
            new Vector2(centerX - titleSize.X / 2, roomBoxPos.Y + 108));

        ImGui.TextUnformatted(title);

        var sub = "Join or create a room\nto see details here.";

        var subSize = ImGui.CalcTextSize("Join or create a room");

        ImGui.SetCursorScreenPos(
            new Vector2(centerX - subSize.X / 2, roomBoxPos.Y + 138));

        ImGui.TextColored(
            MutedText,
            sub);


        const float browsePadding = 18f;
        const float browseShiftLeft = 9f;

        var browseWidth = roomBoxSize.X - (browsePadding * 2) + 18f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                roomBoxPos.X + browsePadding - browseShiftLeft,
                roomBoxPos.Y + roomBoxSize.Y - 62));

        DrawRailAction(
            FontAwesomeIcon.Search,
            Accent,
            "Browse Public Rooms",
            "Explore rooms & venues",
            browseWidth);


    }

    private void DrawPlaybackRightRail()
    {
        EnsureRoomButtonsLoaded();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(Accent, FontAwesomeIcon.Users.ToIconString());
        }

        ImGui.SameLine(0, 10);

        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted("Room Management");
        ImGui.SetWindowFontScale(1f);



        ImGui.TextColored(Good, "●");

        ImGui.SameLine(0, 6);

        ImGui.TextColored(MutedText, "Connected");

        ImGui.Dummy(new Vector2(0, 10));

        var roomButtonWidth = ImGui.GetContentRegionAvail().X;
        var roomButtonHeight = roomButtonWidth * (86f / 320f);
        var roomButtonSize = new Vector2(roomButtonWidth, roomButtonHeight);

        var leaveWrap = leaveRoomImage?.GetWrapOrDefault();

        if (leaveWrap is not null)
        {
            ImGui.Image(
                leaveWrap.Handle,
                roomButtonSize);
        }



        var viewWrap = viewRoomImage?.GetWrapOrDefault();

        if (viewWrap is not null)
        {
            ImGui.Image(
                viewWrap.Handle,
                roomButtonSize);
        }

        ImGui.Dummy(new Vector2(0, 10));

        ImGui.Separator();







        var roomBoxSize = new Vector2(
           ImGui.GetContentRegionAvail().X,
           MathF.Min(450f, ImGui.GetContentRegionAvail().Y));

        var roomBoxPos = ImGui.GetCursorScreenPos();

        var drawList = ImGui.GetWindowDrawList();

        DrawDashedRect(
            drawList,
            roomBoxPos,
            roomBoxPos + roomBoxSize,
            ImGui.GetColorU32(new Vector4(
                MutedText.X,
                MutedText.Y,
                MutedText.Z,
                0.45f)),
            10f);

        ImGui.Dummy(roomBoxSize);

        ImGui.SetCursorScreenPos(
    roomBoxPos + new Vector2(14, 14));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.Users.ToIconString());
        }

        ImGui.SameLine(0, 8);

        ImGui.TextUnformatted("Active Room");

        var centerX = roomBoxPos.X + (roomBoxSize.X * 0.5f);



        var avatarIcon = CurrentSession?.AvatarIcon;
        var avatarColor = CurrentSession?.AvatarColorHex ?? "#9966FA";
        var avatarImage = CurrentSession?.AvatarImageUrl;

        ImGui.SetCursorScreenPos(
            roomBoxPos + new Vector2(18, 52));

        DrawAvatarChip(
            avatarIcon,
            avatarColor,
            64,
            avatarImage);

        var avatarCenter = roomBoxPos + new Vector2(18 + 32, 52 + 32);

        ImGui.GetWindowDrawList().AddCircle(
            avatarCenter,
            33,
            ImGui.GetColorU32(Accent),
            64,
            2f);


        var hostName = CurrentDisplayName is { Length: > 0 }
            ? CurrentDisplayName
            : "Kodie";


        ImGui.SetCursorScreenPos(
     roomBoxPos + new Vector2(90, 56));

        ImGui.TextUnformatted(hostName);


        // Host badge - transparent with purple outline
        var tagPos = roomBoxPos + new Vector2(90, 82);

        ImGui.SetCursorScreenPos(tagPos);
        var tagSize = new Vector2(48, 22);

        ImGui.InvisibleButton(
            "##hostTag",
            tagSize);

        ImGui.GetWindowDrawList().AddRect(
            tagPos,
            tagPos + tagSize,
            ImGui.GetColorU32(Accent),
            8f,
            ImDrawFlags.None,
            1f);

        ImGui.SetCursorScreenPos(
            tagPos + new Vector2(8, 3));

        ImGui.TextColored(
            Accent,
            "Host");

        // Room name placeholder with edit icon
        ImGui.SetCursorScreenPos(
            roomBoxPos + new Vector2(18, 135));

        ImGui.TextUnformatted("Pixar Stream Night");

        ImGui.SameLine(0, 8);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                MutedText,
                FontAwesomeIcon.Edit.ToIconString());
        }


        // Divider above location
        ImGui.GetWindowDrawList().AddLine(
            roomBoxPos + new Vector2(18, 160),
            roomBoxPos + new Vector2(roomBoxSize.X - 18, 160),
            ImGui.GetColorU32(new Vector4(
                MutedText.X,
                MutedText.Y,
                MutedText.Z,
                0.25f)),
            1f);

        // Location on its own full-width line
        ImGui.SetCursorScreenPos(
            roomBoxPos + new Vector2(18, 175));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.MapMarker.ToIconString());
        }

        ImGui.SameLine(0, 8);

        ImGui.TextColored(
            MutedText,
            "Ward 3, Plot 12, Goblet");

        // Member count placeholder
        ImGui.SetCursorScreenPos(
            roomBoxPos + new Vector2(18, 215));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.Users.ToIconString());
        }

        ImGui.SameLine(0, 8);

        ImGui.TextColored(
            MutedText,
            "5 Members");

        // Room visibility
        ImGui.SetCursorScreenPos(
            roomBoxPos + new Vector2(18, 250));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.Globe.ToIconString());
        }

        ImGui.SameLine(0, 8);

        ImGui.TextColored(
            MutedText,
            "Public Room");

        ImGui.GetWindowDrawList().AddLine(
    roomBoxPos + new Vector2(18, 290),
    roomBoxPos + new Vector2(roomBoxSize.X - 18, 290),
    ImGui.GetColorU32(new Vector4(
        MutedText.X,
        MutedText.Y,
        MutedText.Z,
        0.25f)),
    1f);

        // Now Playing section

        ImGui.SetCursorScreenPos(
            roomBoxPos + new Vector2(18, 305));

        ImGui.TextColored(
            Accent,
            "NOW PLAYING");

        if (queue.Current is { } current)
        {
            ImGui.SetCursorScreenPos(
                roomBoxPos + new Vector2(18, 335));

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(
                    Accent,
                    FontAwesomeIcon.Video.ToIconString());
            }

            ImGui.SameLine(0, 8);

            var title = current.Title;

            var maxTitleWidth = roomBoxSize.X - 80f;

            if (ImGui.CalcTextSize(title).X > maxTitleWidth)
            {
                while (title.Length > 3 &&
                       ImGui.CalcTextSize(title + "...").X > maxTitleWidth)
                {
                    title = title[..^1];
                }

                title += "...";
            }

            ImGui.TextUnformatted(title);
        }

        var (position, duration, _) = video.GetProgress();

        var progress = duration > 0
            ? Math.Clamp(position / duration, 0f, 1f)
            : 0f;

        var barLeft = roomBoxPos.X + 18;
        var barRight = roomBoxPos.X + roomBoxSize.X - 18;
        var barY = roomBoxPos.Y + 360;
        var barHeight = 6f;


        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(barLeft, barY),
            new Vector2(barRight, barY + barHeight),
            ImGui.GetColorU32(new Vector4(
                MutedText.X,
                MutedText.Y,
                MutedText.Z,
                0.15f)),
            3f);


        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(barLeft, barY),
            new Vector2(
                barLeft + ((barRight - barLeft) * progress),
                barY + barHeight),
            ImGui.GetColorU32(Accent),
            3f);

        ImGui.SetCursorScreenPos(
    new Vector2(
        barLeft,
        barY + 14));

        ImGui.TextColored(
            MutedText,
            FormatTime(position));


        var durationText = FormatTime(duration);

        ImGui.SetCursorScreenPos(
            new Vector2(
                barRight - ImGui.CalcTextSize(durationText).X,
                barY + 18));

        ImGui.TextColored(
            MutedText,
            durationText);

        // Chat divider
        var chatDividerY = roomBoxPos.Y + 405;

        ImGui.GetWindowDrawList().AddLine(
            roomBoxPos + new Vector2(18, 405),
            roomBoxPos + new Vector2(roomBoxSize.X - 18, 405),
            ImGui.GetColorU32(new Vector4(
                MutedText.X,
                MutedText.Y,
                MutedText.Z,
                0.25f)),
            1f);


        // Room chat enabled (left)
        ImGui.SetCursorScreenPos(
            roomBoxPos + new Vector2(14, 420));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.Comment.ToIconString());
        }

        ImGui.SameLine(0, 8);

        ImGui.TextColored(
            MutedText,
            "Chat Enabled");


        // View chat (right)
        var viewChatText = "View Chat";

        var viewChatTextSize = ImGui.CalcTextSize(viewChatText);
        var arrowSize = ImGui.CalcTextSize(FontAwesomeIcon.ChevronRight.ToIconString());

        var viewChatX = roomBoxPos.X
    + roomBoxSize.X
    - 18
    - arrowSize.X
    - viewChatTextSize.X;

        ImGui.SetCursorScreenPos(
            new Vector2(
                viewChatX,
                roomBoxPos.Y + 420));

        ImGui.TextColored(
            Accent,
            viewChatText);

        ImGui.SameLine(0, 6);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.ChevronRight.ToIconString());
        }
    }

    private static void DrawRailCard(string id, Action draw)
    {
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14, 14)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 14f))
        using (var card = ImRaii.Child(id, new Vector2(-1, 0), false,
                   PaddedChild | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar))
        {
            if (card)
            {
                draw();
            }
        }
    }

    private static void DrawDashedRect(
    ImDrawListPtr drawList,
    Vector2 min,
    Vector2 max,
    uint color,
    float rounding)
    {
        const float dash = 6f;
        const float gap = 5f;

        // Top + bottom edges
        for (float x = min.X + rounding; x < max.X - rounding; x += dash + gap)
        {
            drawList.AddLine(
                new Vector2(x, min.Y),
                new Vector2(MathF.Min(x + dash, max.X - rounding), min.Y),
                color,
                1f);

            drawList.AddLine(
                new Vector2(x, max.Y),
                new Vector2(MathF.Min(x + dash, max.X - rounding), max.Y),
                color,
                1f);
        }

        // Left + right edges
        for (float y = min.Y + rounding; y < max.Y - rounding; y += dash + gap)
        {
            drawList.AddLine(
                new Vector2(min.X, y),
                new Vector2(min.X, MathF.Min(y + dash, max.Y - rounding)),
                color,
                1f);

            drawList.AddLine(
                new Vector2(max.X, y),
                new Vector2(max.X, MathF.Min(y + dash, max.Y - rounding)),
                color,
                1f);
        }

        const int segments = 8;

        // Top-left corner
        drawList.PathArcTo(
            new Vector2(min.X + rounding, min.Y + rounding),
            rounding,
            MathF.PI,
            MathF.PI * 1.5f,
            segments);

        drawList.PathStroke(color, ImDrawFlags.None, 1f);

        // Top-right corner
        drawList.PathArcTo(
            new Vector2(max.X - rounding, min.Y + rounding),
            rounding,
            MathF.PI * 1.5f,
            MathF.PI * 2f,
            segments);

        drawList.PathStroke(color, ImDrawFlags.None, 1f);

        // Bottom-right corner
        drawList.PathArcTo(
            new Vector2(max.X - rounding, max.Y - rounding),
            rounding,
            0f,
            MathF.PI * 0.5f,
            segments);

        drawList.PathStroke(color, ImDrawFlags.None, 1f);

        // Bottom-left corner
        drawList.PathArcTo(
            new Vector2(min.X + rounding, max.Y - rounding),
            rounding,
            MathF.PI * 0.5f,
            MathF.PI,
            segments);

        drawList.PathStroke(color, ImDrawFlags.None, 1f);
    }

    private static bool DrawRailAction(FontAwesomeIcon icon, Vector4 color, string title, string subtitle, float? customWidth = null)
    {
        var width = customWidth ?? ImGui.GetContentRegionAvail().X;
        const float height = 52f;

        var origin = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton(
            $"##rail{title}",
            new Vector2(width, height));

        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();

        var min = origin;
        var max = origin + new Vector2(width, height);


        // Purple outlined border
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.8f)),
            10f,
            ImDrawFlags.None,
            1.5f);


        if (hovered)
        {
            drawList.AddRectFilled(
                origin,
                origin + new Vector2(width, height),
                ImGui.GetColorU32(CardBgHover),
                10f);
        }


        const float disc = 32f;

        var discOrigin =
            origin + new Vector2(6, (height - disc) / 2);


        drawList.AddRectFilled(
            discOrigin,
            discOrigin + new Vector2(disc, disc),
            ImGui.GetColorU32(
                new Vector4(
                    color.X,
                    color.Y,
                    color.Z,
                    0.22f)),
            10f);


        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var size = ImGui.CalcTextSize(glyph);

            drawList.AddText(
                discOrigin + new Vector2(disc, disc) / 2 - size / 2,
                ImGui.GetColorU32(color),
                glyph);
        }


        drawList.AddText(
            origin + new Vector2(48, 10),
            ImGui.GetColorU32(Vector4.One),
            title);


        drawList.AddText(
            origin + new Vector2(48, 28),
            ImGui.GetColorU32(MutedText),
            subtitle);


        var chevron = FontAwesomeIcon.ChevronRight.ToIconString();

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var cSize = ImGui.CalcTextSize(chevron);

            drawList.AddText(
                origin + new Vector2(width - cSize.X - 6, (height - cSize.Y) / 2),
                ImGui.GetColorU32(MutedText),
                chevron);
        }


        return clicked;
    }

    void DrawBottomBar(bool playbackActive)
    {
        var windowPos = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();

        const float height = BottomBarHeight;

        const float sidebarWidth = 195f;

        var targetY = windowPos.Y + windowSize.Y - height;

        var hiddenY = windowPos.Y + windowSize.Y;

        var slidingIn = playbackActive;

        var elapsed = (float)(
            ImGui.GetTime() -
            (slidingIn ? playbackStartedAt : playbackStoppedAt));

        var slide = Math.Clamp(
            elapsed / 0.35f,
            0f,
            1f);

        if (!slidingIn)
        {
            slide = 1f - slide;
        }

        slide = slide * slide * (3f - 2f * slide);

        var y = hiddenY + (targetY - hiddenY) * slide;

        var pos = new Vector2(
            windowPos.X + sidebarWidth,
            y);

        var width = windowSize.X - sidebarWidth;

        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(new Vector2(width, height));

        using var window = ImRaii.Child(
            "##bottomTransportOverlay",
            new Vector2(width, height),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings);

        if (window)
        {
            DrawBottomTransport(width, height);
        }

    }

    private void DrawBottomProfile(float width, float height)
    {
        // No nested Child — a fixed-height Child was clipping the Online line under ItemSpacing.
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8, 2)))
        {
            var name = CurrentDisplayName is { Length: > 0 } n ? n : "Guest";
            var icon = CurrentSession?.AvatarIcon;
            var color = CurrentSession?.AvatarColorHex ?? "#9966FA";
            var imageUrl = CurrentSession?.AvatarImageUrl;
            var start = ImGui.GetCursorScreenPos();

            DrawAvatarChip(icon, color, 56, imageUrl);

            var avatarCenter = start + new Vector2(28, 28);

            ImGui.GetWindowDrawList().AddCircle(
                avatarCenter,
                29,
                ImGui.GetColorU32(Accent),
                64,
                2f);

            ImGui.SameLine(0, 12);

            ImGui.BeginGroup();
            ImGui.TextUnformatted(name);
            ImGui.TextColored(CurrentSession is null ? MutedText : Good,
                CurrentSession is null ? "Not signed in" : "Online");
            ImGui.EndGroup();

            // Reserve the column so later absolute draws don't collide in layout terms.
            ImGui.SetCursorScreenPos(start);
            ImGui.Dummy(new Vector2(width, height));
        }
    }

   

    private void DrawBottomTransport(float width, float height)
    {
        using var _ = ImRaii.Child("##bottomTransport", new Vector2(width, height), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var current = queue.Current;
        if (current is null)
        {
            // Center the idle block inside the transport column (mockup media island).
            const float idleBlock = 210f;
            var pad = MathF.Max(0f, (width - idleBlock) * 0.5f);
            var top = MathF.Max(0f, (height - ImGui.GetTextLineHeight() * 2f - 4f) * 0.5f);
            ImGui.SetCursorPos(new Vector2(pad, top));
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                ImGui.TextColored(MutedText, FontAwesomeIcon.Tv.ToIconString());
            }

            ImGui.SameLine(0, 12);
            ImGui.BeginGroup();
            ImGui.SameLine(0, 12);

            ImGui.BeginGroup();

            ImGui.TextUnformatted("Nothing Playing");

            var subtitle = "Pick something to watch.";

            var titleWidth = ImGui.CalcTextSize("Nothing Playing").X;
            var subtitleWidth = ImGui.CalcTextSize(subtitle).X;

            ImGui.SetCursorPosX(
                ImGui.GetCursorPosX() + ((titleWidth - subtitleWidth) / 2f));

            ImGui.TextColored(
                MutedText,
                subtitle);

            ImGui.EndGroup();

            return;
        }

        DrawBottomTransportPlaying(current, width, height);
    }

    // Spotify-style island: centered prev/play/next, seek with times underneath, volume on the right.
    private void DrawBottomTransportPlaying(VideoQueueEntry current, float width, float height)
    {
        var barDrawList = ImGui.GetWindowDrawList();
        var childPos = ImGui.GetWindowPos();

        barDrawList.AddLine(
childPos + new Vector2(0, 2),
childPos + new Vector2(width, 2),
            ImGui.GetColorU32(new Vector4(
        Accent.X,
        Accent.Y,
        Accent.Z,
        0.14f)),
           2f);

        var (position, duration, isPaused) = video.GetProgress();
        if (!seekDragging)
        {
            seekPreview = position;
        }
            const float playSize = 32f;
            const float skipSize = 20f;
            const float gap = 14f;
            const float volSliderW = 90f;
            const float volIconW = 26f;

            var volClusterW = volIconW + 6f + volSliderW;

            var controlsW =
                skipSize + gap +
                playSize + gap +
                skipSize;

            var lineH = ImGui.GetTextLineHeight();

            // Title sits left; controls + volume share the rest.
            var title = Truncate(current.Title, 80);



            const float thumbnailWidth = 64f;



            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap, 4f)))
            {
                // --- Top row: title | centered transport | volume ---
                var topY = 14f;

                // --- Left title area ---
                ImGui.SetCursorPos(new Vector2(
                    thumbnailWidth + 24f,
                    topY + 8f));

                ImGui.TextUnformatted(
                    Truncate(title, 45));

                var thumbX = 8f;
                var thumbY = topY;

                var drawList = ImGui.GetWindowDrawList();

                var windowPos = ImGui.GetWindowPos();

                var thumbMin = windowPos + new Vector2(thumbX, thumbY);
                var thumbMax = windowPos + new Vector2(
                    thumbX + thumbnailWidth,
                    thumbY + 56f);

                drawList.AddRectFilled(
    thumbMin,
    thumbMax,
    ImGui.GetColorU32(CardBgHover),
    6f);

                drawList.AddRect(
                    thumbMin,
                    thumbMax,
                    ImGui.GetColorU32(Accent),
                    6f,
                    ImDrawFlags.None,
                    1f);

            var thumbnail = thumbnails.Get(current.ThumbnailUrl);

            if (thumbnail is not null)
            {
                drawList.AddImageRounded(
                    thumbnail.Handle,
                    thumbMin,
                    thumbMax,
                    Vector2.Zero,
                    Vector2.One,
                    uint.MaxValue,
                    6f);
            }
            else
            {
                // Keep current placeholder when no thumbnail exists
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    var icon = FontAwesomeIcon.Play.ToIconString();
                    var size = ImGui.CalcTextSize(icon);

                    drawList.AddText(
                        thumbMin +
                        (thumbMax - thumbMin) / 2 -
                        size / 2,
                        ImGui.GetColorU32(Accent),
                        icon);
                }
            }

            // Reset cursor after thumbnail drawing
            ImGui.SetCursorPos(new Vector2(0f, topY));



                var volumeGap = 36f;

                var fullClusterW = controlsW + volumeGap + volClusterW;

                var clusterX = (width - fullClusterW) * 0.5f;
                var controlsX =
     (width - controlsW) * 0.5f;

                var volX = width - volClusterW - 70f;

                // Stop button - far right
                ImGui.SetCursorPos(new Vector2(
                    width - skipSize - 42f,
                    topY + (playSize - skipSize) * 0.5f));

                if (DrawTransportStopButton(skipSize))
                {
                    StopPlayback();
                }


                // Center transport
                ImGui.SetCursorPos(new Vector2(
                    controlsX,
                    topY + (playSize - skipSize) * 0.5f));

                if (DrawTransportGhostButton(FontAwesomeIcon.StepBackward, skipSize))
                {
                    video.Seek(0);
                }

                ImGui.SameLine(0, gap);

                ImGui.SetCursorPosY(topY);

                if (DrawTransportPlayButton(isPaused, playSize))
                {
                    video.Pause(!isPaused);
                }

                ImGui.SameLine(0, gap);
                ImGui.SetCursorPosY(topY + (playSize - skipSize) * 0.5f);
                if (DrawTransportGhostButton(FontAwesomeIcon.StepForward, skipSize))
                {
                    queue.Advance();
                }


                ImGui.SameLine(0, gap);



                ImGui.SetCursorPos(new Vector2(volX, topY + (playSize - skipSize) * 0.5f));
                DrawBottomVolume(volIconW, volSliderW, 18f);

                // --- Timestamp under title ---
                var timeLeft = FormatTime(position);
                var timeRight = FormatTime(duration);



                // --- Seek row ---
                var seekY = topY + playSize + 18f;

                var timeLeftWidth = ImGui.CalcTextSize(timeLeft).X;

                ImGui.SetCursorPos(new Vector2(84f, seekY + 2f));

                ImGui.TextColored(MutedText, timeLeft);

                ImGui.SameLine(0, 8);

                ImGui.SetNextItemWidth(
                   width - timeLeftWidth - ImGui.CalcTextSize(timeRight).X - 120f);

                using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f)
                           .Push(ImGuiStyleVar.GrabRounding, 6f)
                           .Push(ImGuiStyleVar.FramePadding, new Vector2(0, 0)))
                {
                    ImGui.SliderFloat("##bottomSeek", ref seekPreview, 0f, MathF.Max(duration, 0.01f), "");
                }

                seekDragging = ImGui.IsItemActive();

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    video.Seek(seekPreview);
                }

                ImGui.SameLine(0, 8);
                ImGui.TextColored(MutedText, timeRight);
             
            }
        }
    

    private void DrawBottomVolume(float iconSize, float sliderW, float rowH)
    {
        var muted = Plugin.Cfg.Muted;
        if (DrawTransportGhostButton(
                muted || Plugin.Cfg.Volume == 0 ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp,
                iconSize))
        {
            Plugin.Cfg.Muted = !Plugin.Cfg.Muted;
            video.SetVolume(Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume);
            Plugin.Cfg.Save();
        }

        ImGui.SameLine(0, 6);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 5f);
        ImGui.SetNextItemWidth(sliderW);

        var volume = Plugin.Cfg.Volume;

        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f)
                   .Push(ImGuiStyleVar.GrabRounding, 4f)
                   .Push(ImGuiStyleVar.FramePadding, new Vector2(0, -2f)))
        {
            if (ImGui.SliderInt("##bottomVol", ref volume, 0, 100, ""))
            {
                Plugin.Cfg.Volume = volume;
                if (volume > 0 && Plugin.Cfg.Muted)
                {
                    Plugin.Cfg.Muted = false;
                }

                video.SetVolume(Plugin.Cfg.Muted ? 0 : volume);
            }
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Plugin.Cfg.Save();
        }
    }

    private static bool DrawTransportGhostButton(FontAwesomeIcon icon, float size)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID((int)icon + (int)(size * 10));
        var clicked = ImGui.InvisibleButton("##transportGhost", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = icon.ToIconString();
            var textSize = ImGui.CalcTextSize(glyph);
            ImGui.GetWindowDrawList().AddText(
                origin + new Vector2(size, size) / 2f - textSize / 2f,
                ImGui.GetColorU32(hovered ? Vector4.One : MutedText),
                glyph);
        }

        return clicked;
    }

    private static bool DrawTransportStopButton(float size)
    {
        var origin = ImGui.GetCursorScreenPos();

        ImGui.PushID("##transportStop");
        var clicked = ImGui.InvisibleButton(
            "##hit",
            new Vector2(size, size));

        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = FontAwesomeIcon.Stop.ToIconString();
            var textSize = ImGui.CalcTextSize(glyph);

            ImGui.GetWindowDrawList().AddText(
                origin + new Vector2(size, size) / 2f - textSize / 2f,
                ImGui.GetColorU32(
                    hovered
                        ? new Vector4(1f, 0.4f, 0.4f, 1f)
                        : new Vector4(1f, 0.2f, 0.2f, 1f)),
                glyph);
        }

        return clicked;
    }

    private static bool DrawTransportPlayButton(bool isPaused, float size)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.PushID("##transportPlay");
        var clicked = ImGui.InvisibleButton("##hit", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        var center = origin + new Vector2(size, size) / 2f;
        var fill = hovered ? AccentHover : Accent;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddCircleFilled(center, size * 0.5f, ImGui.GetColorU32(fill));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = (isPaused ? FontAwesomeIcon.Play : FontAwesomeIcon.Pause).ToIconString();
            var textSize = ImGui.CalcTextSize(glyph);
            // Play triangle sits optically left in FA — nudge so it reads centered.
            var nudge = isPaused ? new Vector2(1.2f, 0f) : Vector2.Zero;
            drawList.AddText(center - textSize / 2f + nudge, ImGui.GetColorU32(Vector4.One), glyph);
        }

        return clicked;
    }

    private void DrawBottomActions(float width, float height)
    {
        using var _ = ImRaii.Child(
            "##bottomActions",
            new Vector2(width, height),
            false,
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse);

        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);

        var clicked = ImGui.InvisibleButton(
            "##miniModeToggle",
            size);

        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : CardBg),
            12f);

        var icon = miniMode
            ? FontAwesomeIcon.Expand
            : FontAwesomeIcon.Compress;

        var label = miniMode
            ? "Full Size Mode"
            : "Mini Mode";

        var iconText = icon.ToIconString();

        Vector2 iconSize;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var labelSize = ImGui.CalcTextSize(label);

        const float gap = 8f;

        var totalWidth =
            iconSize.X +
            gap +
            labelSize.X;

        var start = new Vector2(
            origin.X + (width - totalWidth) * 0.5f,
            origin.Y + (height - labelSize.Y) * 0.5f);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                start,
                ImGui.GetColorU32(
                    hovered
                        ? Vector4.One
                        : MutedText),
                iconText);
        }

        drawList.AddText(
            start + new Vector2(iconSize.X + gap, 0f),
            ImGui.GetColorU32(
                hovered
                    ? Vector4.One
                    : MutedText),
            label);

        if (clicked)
        {
            miniMode = !miniMode;
        }
    }

    private static bool DrawBottomAction(FontAwesomeIcon icon, string label, float iconH)
    {
        const float colW = 40f;
        ImGui.BeginGroup();

        var clicked = false;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                   .Push(ImGuiCol.ButtonHovered, CardBgHover)
                   .Push(ImGuiCol.ButtonActive, CardBg)
                   .Push(ImGuiCol.Text, MutedText))
        {
            clicked = ImGui.Button($"{icon.ToIconString()}##bottom{label}", new Vector2(colW, iconH));
        }

        var labelSize = ImGui.CalcTextSize(label);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (colW - labelSize.X) * 0.5f));
        ImGui.TextColored(MutedText, label);
        ImGui.EndGroup();

        // Whole column is clickable, not just the icon button.
        if (!clicked && ImGui.IsItemClicked())
        {
            clicked = true;
        }

        return clicked;
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}
