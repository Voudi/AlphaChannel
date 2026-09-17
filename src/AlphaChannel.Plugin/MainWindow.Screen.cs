using System.Text.Json;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Screen is a calibration tool — transform + clearer presets (Venues deferred from launch nav).
internal sealed partial class MainWindow
{
    private string presetNameInput =
    string.Empty;

    private string screenClipboardStatus =
        string.Empty;

    private bool screenClipboardStatusIsError;

    private sealed class ScreenClipboardPayload
    {
        public string? Type { get; set; }

        public int Version { get; set; }

        public float? X { get; set; }

        public float? Y { get; set; }

        public float? Z { get; set; }

        public float? Yaw { get; set; }

        public bool DisableFixedScaleRatio { get; set; }

        public float? WidthScale { get; set; }

        public float? HeightScale { get; set; }
    }

    private void DrawScreenControls()
    {
        var engine = screenController.Engine;

        // ---------------------------------------------------------
        // Transform
        // ---------------------------------------------------------

        SetUiFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Transform");

        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0f, 4f));

        SetUiFontScale(0.86f);

        ImGui.TextColored(
            MutedText,
            "Drag while looking at the in-world panel, or fine-tune it below.");

        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0f, 10f));

        var position =
            engine.ScreenPosition;

        var yaw =
            engine.ScreenYaw;

        var disableFixedScaleRatio =
            VideoEngine.IndependentScreenScalingEnabled &&
            engine.DisableFixedScreenScaleRatio;

        var widthScale =
            engine.ScreenWidthScale;

        var heightScale =
            engine.ScreenHeightScale;

        var changed =
            false;

        // ---------------------------------------------------------
        // Position
        // ---------------------------------------------------------

        SetUiFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Position");

        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0f, 2f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            Ui(8f))
            .Push(
                ImGuiStyleVar.FramePadding,
                UiVec(12f, 8f)))
        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(
                ImGuiCol.FrameBgHovered,
                new Vector4(0.07f, 0.09f, 0.145f, 1f))
            .Push(
                ImGuiCol.FrameBgActive,
                new Vector4(0.07f, 0.09f, 0.145f, 1f)))
        {
            ImGui.SetNextItemWidth(-1f);

            changed |= ImGui.DragFloat3(
                "##screenPosition",
                ref position,
                0.05f);
        }

        ImGui.Dummy(UiVec(0f, 7f));

        // ---------------------------------------------------------
        // Yaw
        // ---------------------------------------------------------

        var transformLabelWidth = Ui(58f);

        SetUiFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Yaw");

        SetUiFontScale(1f);

        ImGui.SameLine(
            transformLabelWidth);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            Ui(8f))
            .Push(
                ImGuiStyleVar.FramePadding,
                UiVec(12f, 8f)))
        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(
                ImGuiCol.FrameBgHovered,
                new Vector4(0.07f, 0.09f, 0.145f, 1f))
            .Push(
                ImGuiCol.FrameBgActive,
                new Vector4(0.07f, 0.09f, 0.145f, 1f)))
        {
            ImGui.SetNextItemWidth(-1f);

            changed |= ImGui.SliderAngle(
                "##screenYaw",
                ref yaw);
        }

        ImGui.Dummy(UiVec(0f, 7f));

        // ---------------------------------------------------------
        // Scale
        // ---------------------------------------------------------

        void DrawScaleSlider(
            string label,
            string id,
            ref float value)
        {
            SetUiFontScale(
                0.82f);

            ImGui.TextColored(
                MutedText,
                label);

            SetUiFontScale(
                1f);

            ImGui.SameLine(
                transformLabelWidth);

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(8f))
                   .Push(
                       ImGuiStyleVar.FramePadding,
                       UiVec(12f, 8f)))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBg,
                       new Vector4(
                           0.055f,
                           0.07f,
                           0.115f,
                           1f))
                   .Push(
                       ImGuiCol.FrameBgHovered,
                       new Vector4(
                           0.07f,
                           0.09f,
                           0.145f,
                           1f))
                   .Push(
                       ImGuiCol.FrameBgActive,
                       new Vector4(
                           0.07f,
                           0.09f,
                           0.145f,
                           1f)))
            {
                ImGui.SetNextItemWidth(
                    -1f);

                changed |=
                    ImGui.SliderFloat(
                        id,
                        ref value,
                        VideoEngine.MinScreenScale,
                        VideoEngine.MaxScreenScale,
                        "%.3f");
            }
        }

        if (disableFixedScaleRatio)
        {
            DrawScaleSlider(
                "Width",
                "##screenWidthScale",
                ref widthScale);

            ImGui.Dummy(
                UiVec(0f, 7f));

            DrawScaleSlider(
                "Height",
                "##screenHeightScale",
                ref heightScale);
        }
        else
        {
            DrawScaleSlider(
                "Scale",
                "##screenScale",
                ref widthScale);

            heightScale =
                widthScale;
        }

        if (changed)
        {
            engine.SetScreenTransform(
                position,
                yaw,
                disableFixedScaleRatio,
                widthScale,
                heightScale);

            Plugin.Cfg.ScreenPosition =
                position;

            Plugin.Cfg.ScreenYaw =
                yaw;

            Plugin.Cfg.ScreenScale =
                widthScale;

            Plugin.Cfg.DisableFixedScreenScaleRatio =
                disableFixedScaleRatio;

            Plugin.Cfg.ScreenWidthScale =
                widthScale;

            Plugin.Cfg.ScreenHeightScale =
                heightScale;

            Plugin.Cfg.Save();
        }

        ImGui.Dummy(UiVec(0f, 9f));

        // ---------------------------------------------------------
        // Recenter
        // ---------------------------------------------------------

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            Ui(8f)))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            Accent)
            .Push(
                ImGuiCol.ButtonHovered,
                AccentHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button(
                "Recenter in front of me",
                UiVec(-1f, 38f)))
            {
                engine.RecenterScreen();
            }
        }

        ImGui.Dummy(
            UiVec(0f, 10f));

        // ---------------------------------------------------------
        // Import / Export
        // ---------------------------------------------------------

        var clipboardButtonGap =
            Ui(10f);

        var clipboardButtonWidth =
            (
                ImGui.GetContentRegionAvail().X -
                clipboardButtonGap
            ) /
            2f;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(8f)))
        {
            if (ImGui.Button(
         "Export settings",
         new Vector2(
             clipboardButtonWidth,
             Ui(38f))))
            {
                ExportScreenSettingsToClipboard(
                    engine);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Export current screen settings to clipboard");
            }

            ImGui.SameLine(
                            0f,
                clipboardButtonGap);

            if (ImGui.Button(
             "Import settings",
             new Vector2(
                 clipboardButtonWidth,
                 Ui(38f))))
            {
                ImportScreenSettingsFromClipboard(
                    engine);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Import screen settings from clipboard");
            }
        }

        if (!string.IsNullOrWhiteSpace(
                screenClipboardStatus))
        {
            ImGui.Dummy(
                UiVec(0f, 7f));

            ImGui.TextColored(
                screenClipboardStatusIsError
                    ? Danger
                    : Good,
                screenClipboardStatus);
        }

        ImGui.Dummy(UiVec(0f, 14f));

        // ---------------------------------------------------------
        // Divider
        // ---------------------------------------------------------

        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X;

        ImGui.GetWindowDrawList().AddRectFilled(
            origin,
            origin + new Vector2(width, Ui(1f)),
            ImGui.GetColorU32(BorderSubtle));

        ImGui.Dummy(new Vector2(width, Ui(14f)));

        // ---------------------------------------------------------
        // Presets
        // ---------------------------------------------------------

        SetUiFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Presets");

        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0f, 4f));

        SetUiFontScale(0.86f);

        ImGui.TextColored(
            MutedText,
            "Save this screen position for places you return to.");

        SetUiFontScale(1f);

        ImGui.Dummy(UiVec(0f, 10f));

        // ---------------------------------------------------------
        // Preset name
        // ---------------------------------------------------------

        ImGui.SetNextItemWidth(-1f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            Ui(8f))
            .Push(
                ImGuiStyleVar.FramePadding,
                UiVec(12f, 9f)))
        using (ImRaii.PushColor(
            ImGuiCol.FrameBg,
            new Vector4(0.055f, 0.07f, 0.115f, 1f))
            .Push(
                ImGuiCol.FrameBgHovered,
                new Vector4(0.07f, 0.09f, 0.145f, 1f))
            .Push(
                ImGuiCol.FrameBgActive,
                new Vector4(0.07f, 0.09f, 0.145f, 1f)))
        {
            ImGui.InputTextWithHint(
                "##presetName",
                "Name this spot...",
                ref presetNameInput,
                48);
        }

        ImGui.Dummy(UiVec(0f, 8f));

        // ---------------------------------------------------------
        // Save preset
        // ---------------------------------------------------------

        using (ImRaii.Disabled(
            presetNameInput.Trim().Length == 0))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            Ui(8f)))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            Accent)
            .Push(
                ImGuiCol.ButtonHovered,
                AccentHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button(
                "Save current position",
                UiVec(-1f, 38f)))
            {
                var savePos =
                    engine.ScreenPosition;

                Plugin.Cfg.ScreenPresets.Add(
                    new ScreenPositionPreset
                    {
                        Name = presetNameInput.Trim(),
                        X = savePos.X,
                        Y = savePos.Y,
                        Z = savePos.Z,
                        Yaw =
    engine.ScreenYaw,

                        Scale =
    engine.ScreenScale,

                        DisableFixedScaleRatio =
    engine.DisableFixedScreenScaleRatio,

                        WidthScale =
    engine.ScreenWidthScale,

                        HeightScale =
    engine.ScreenHeightScale,
                    });

                Plugin.Cfg.Save();

                presetNameInput =
                    string.Empty;
            }
        }

        ImGui.Dummy(UiVec(0f, 12f));

        // ---------------------------------------------------------
        // Empty state
        // ---------------------------------------------------------

        if (Plugin.Cfg.ScreenPresets.Count == 0)
        {
            SetUiFontScale(0.88f);

            ImGui.TextColored(
                MutedText,
                "No presets yet. Place the screen, then save it above.");

            SetUiFontScale(1f);

            return;
        }

        // ---------------------------------------------------------
        // Preset cards
        // ---------------------------------------------------------

        for (var index = 0;
             index < Plugin.Cfg.ScreenPresets.Count;
             index++)
        {
            var preset =
                Plugin.Cfg.ScreenPresets[index];

            ImGui.PushID(index);

            var cardHeight = Ui(106f);

            using (ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(8f)))
            using (ImRaii.PushColor(
                ImGuiCol.ChildBg,
                new Vector4(0.045f, 0.06f, 0.10f, 1f)))
            using (var row = ImRaii.Child(
                "##presetRow",
                new Vector2(-1f, cardHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (row)
                {
                    var rowOrigin =
                        ImGui.GetCursorScreenPos();

                    // Preset name
                    ImGui.SetCursorScreenPos(
                        rowOrigin +
                        UiVec(14f, 12f));

                    ImGui.TextColored(
                        Vector4.One,
                        preset.Name);

                    // Metadata
                    ImGui.SetCursorScreenPos(
                        rowOrigin +
                        UiVec(14f, 39f));

                    SetUiFontScale(0.82f);

                    var presetWidthScale =
                        preset.WidthScale ??
                        preset.Scale;

                    var presetHeightScale =
                        preset.HeightScale ??
                        preset.Scale;

                    var presetScaleText =
                        preset.DisableFixedScaleRatio
                            ? $"width {presetWidthScale:0.00}  •  height {presetHeightScale:0.00}"
                            : $"scale {presetWidthScale:0.00}";

                    ImGui.TextColored(
                        MutedText,
                        $"xyz {preset.X:0.0}, {preset.Y:0.0}, {preset.Z:0.0}  •  {presetScaleText}");

                    SetUiFontScale(1f);

                    // Buttons
                    ImGui.SetCursorScreenPos(
                        rowOrigin +
                        UiVec(14f, 66f));

                    // Load
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        Ui(7f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.Button,
                        Accent)
                        .Push(
                            ImGuiCol.ButtonHovered,
                            AccentHover)
                        .Push(
                            ImGuiCol.ButtonActive,
                            AccentActive))
                    {
                        if (ImGui.Button(
                            "Load",
                            UiVec(90f, 30f)))
                        {
                            engine.ApplyScreenPreset(
                                preset);
                        }
                    }

                    ImGui.SameLine(0f, Ui(8f));

                    // Overwrite
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        Ui(7f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.Button,
                        new Vector4(0.055f, 0.07f, 0.115f, 1f))
                        .Push(
                            ImGuiCol.ButtonHovered,
                            new Vector4(0.075f, 0.095f, 0.15f, 1f))
                        .Push(
                            ImGuiCol.ButtonActive,
                            new Vector4(0.075f, 0.095f, 0.15f, 1f)))
                    {
                        if (ImGui.Button(
                            "Overwrite",
                            UiVec(100f, 30f)))
                        {
                            var pos =
                                engine.ScreenPosition;

                            preset.X = pos.X;
                            preset.Y = pos.Y;
                            preset.Z = pos.Z;
                            preset.Yaw =
                                engine.ScreenYaw;

                            preset.Scale =
                                engine.ScreenScale;

                            preset.DisableFixedScaleRatio =
                                engine.DisableFixedScreenScaleRatio;

                            preset.WidthScale =
                                engine.ScreenWidthScale;

                            preset.HeightScale =
                                engine.ScreenHeightScale;

                            Plugin.Cfg.Save();
                        }
                    }

                    ImGui.SameLine(0f, Ui(8f));

                    // Delete
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        Ui(7f)))
                    using (ImRaii.PushColor(
                        ImGuiCol.Button,
                        new Vector4(0.16f, 0.055f, 0.07f, 1f))
                        .Push(
                            ImGuiCol.ButtonHovered,
                            new Vector4(0.22f, 0.07f, 0.09f, 1f))
                        .Push(
                            ImGuiCol.ButtonActive,
                            new Vector4(0.25f, 0.08f, 0.10f, 1f)))
                    {
                        if (ImGui.Button(
                            "Delete",
                            UiVec(90f, 30f)))
                        {
                            Plugin.Cfg.ScreenPresets.RemoveAt(
                                index);

                            Plugin.Cfg.Save();

                            ImGui.PopID();

                            break;
                        }
                    }
                }
            }

            ImGui.PopID();

            ImGui.Dummy(
                UiVec(0f, 8f));
        }
    }

    private void ExportScreenSettingsToClipboard(
    VideoEngine engine)
    {
        var position =
            engine.ScreenPosition;

        var payload =
            new ScreenClipboardPayload
            {
                Type =
                    "AlphaChannelScreen",

                Version =
                    1,

                X =
                    position.X,

                Y =
                    position.Y,

                Z =
                    position.Z,

                Yaw =
                    engine.ScreenYaw,

                DisableFixedScaleRatio =
                    engine.DisableFixedScreenScaleRatio,

                WidthScale =
                    engine.ScreenWidthScale,

                HeightScale =
                    engine.ScreenHeightScale,
            };

        var json =
            JsonSerializer.Serialize(
                payload);

        ImGui.SetClipboardText(
            json);

        screenClipboardStatus =
            "Screen settings copied to clipboard.";

        screenClipboardStatusIsError =
            false;
    }

    private void ImportScreenSettingsFromClipboard(
        VideoEngine engine)
    {
        try
        {
            var clipboardText =
                ImGui.GetClipboardText();

            if (string.IsNullOrWhiteSpace(
                    clipboardText))
            {
                SetScreenImportError(
                    "The clipboard is empty.");

                return;
            }

            var payload =
                JsonSerializer.Deserialize<ScreenClipboardPayload>(
                    clipboardText);

            if (payload is null ||
                !string.Equals(
                    payload.Type,
                    "AlphaChannelScreen",
                    StringComparison.Ordinal) ||
                payload.Version != 1 ||
                payload.X is not { } x ||
                payload.Y is not { } y ||
                payload.Z is not { } z ||
                payload.Yaw is not { } yaw ||
                payload.WidthScale is not { } widthScale ||
                payload.HeightScale is not { } heightScale ||
                !float.IsFinite(x) ||
                !float.IsFinite(y) ||
                !float.IsFinite(z) ||
                !float.IsFinite(yaw) ||
                !float.IsFinite(widthScale) ||
                !float.IsFinite(heightScale) ||
                widthScale <
                VideoEngine.MinScreenScale ||
                widthScale >
                VideoEngine.MaxScreenScale ||
                heightScale <
                VideoEngine.MinScreenScale ||
                heightScale >
                VideoEngine.MaxScreenScale)
            {
                SetScreenImportError(
                    "The clipboard does not contain valid AlphaChannel screen settings.");

                return;
            }

            if (!payload.DisableFixedScaleRatio)
            {
                heightScale =
                    widthScale;
            }

            var position =
                new Vector3(
                    x,
                    y,
                    z);

            engine.SetScreenTransform(
                position,
                yaw,
                payload.DisableFixedScaleRatio,
                widthScale,
                heightScale);

            Plugin.Cfg.ScreenPosition =
                position;

            Plugin.Cfg.ScreenYaw =
                yaw;

            Plugin.Cfg.ScreenScale =
                widthScale;

            Plugin.Cfg.DisableFixedScreenScaleRatio =
                payload.DisableFixedScaleRatio;

            Plugin.Cfg.ScreenWidthScale =
                widthScale;

            Plugin.Cfg.ScreenHeightScale =
                heightScale;

            Plugin.Cfg.Save();

            screenClipboardStatus =
                "Screen settings imported successfully.";

            screenClipboardStatusIsError =
                false;
        }
        catch (JsonException)
        {
            SetScreenImportError(
                "The clipboard does not contain valid AlphaChannel screen settings.");
        }
        catch (Exception exception)
        {
            AepLog.Warning(
                $"[Screen] Import failed: {exception.Message}");

            SetScreenImportError(
                "Screen settings could not be imported.");
        }
    }

    private void SetScreenImportError(
        string message)
    {
        screenClipboardStatus =
            message;

        screenClipboardStatusIsError =
            true;
    }

}

