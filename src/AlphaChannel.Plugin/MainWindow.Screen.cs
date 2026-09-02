using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Screen is a calibration tool — transform + clearer presets (Venues deferred from launch nav).
internal sealed partial class MainWindow
{
    private string presetNameInput = string.Empty;

    private void DrawScreenControls()
    {
        var engine = screenController.Engine;

        // ---------------------------------------------------------
        // Transform
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Transform");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        ImGui.SetWindowFontScale(0.86f);

        ImGui.TextColored(
            MutedText,
            "Drag while looking at the in-world panel, or fine-tune it below.");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 10f));

        var position = engine.ScreenPosition;
        var yaw = engine.ScreenYaw;
        var scale = engine.ScreenScale;
        var changed = false;

        // ---------------------------------------------------------
        // Position
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Position");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 2f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(12f, 8f)))
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

        ImGui.Dummy(new Vector2(0f, 7f));

        // ---------------------------------------------------------
        // Yaw
        // ---------------------------------------------------------

        const float transformLabelWidth = 58f;

        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Yaw");

        ImGui.SetWindowFontScale(1f);

        ImGui.SameLine(
            transformLabelWidth);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(12f, 8f)))
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

        ImGui.Dummy(new Vector2(0f, 7f));

        // ---------------------------------------------------------
        // Scale
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(0.82f);

        ImGui.TextColored(
            MutedText,
            "Scale");

        ImGui.SetWindowFontScale(1f);

        ImGui.SameLine(
            transformLabelWidth);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(12f, 8f)))
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

            changed |= ImGui.SliderFloat(
                "##screenScale",
                ref scale,
                VideoEngine.MinScreenScale,
                VideoEngine.MaxScreenScale);
        }

        if (changed)
        {
            engine.SetScreenTransform(
                position,
                yaw,
                scale);
        }

        ImGui.Dummy(new Vector2(0f, 9f));

        // ---------------------------------------------------------
        // Recenter
        // ---------------------------------------------------------

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
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
                new Vector2(-1f, 38f)))
            {
                engine.RecenterScreen();
            }
        }

        ImGui.Dummy(new Vector2(0f, 14f));

        // ---------------------------------------------------------
        // Divider
        // ---------------------------------------------------------

        var origin =
            ImGui.GetCursorScreenPos();

        var width =
            ImGui.GetContentRegionAvail().X;

        ImGui.GetWindowDrawList().AddRectFilled(
            origin,
            origin + new Vector2(width, 1f),
            ImGui.GetColorU32(BorderSubtle));

        ImGui.Dummy(new Vector2(width, 14f));

        // ---------------------------------------------------------
        // Presets
        // ---------------------------------------------------------

        ImGui.SetWindowFontScale(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Presets");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 4f));

        ImGui.SetWindowFontScale(0.86f);

        ImGui.TextColored(
            MutedText,
            "Save this screen position for places you return to.");

        ImGui.SetWindowFontScale(1f);

        ImGui.Dummy(new Vector2(0f, 10f));

        // ---------------------------------------------------------
        // Preset name
        // ---------------------------------------------------------

        ImGui.SetNextItemWidth(-1f);

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f)
            .Push(
                ImGuiStyleVar.FramePadding,
                new Vector2(12f, 9f)))
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

        ImGui.Dummy(new Vector2(0f, 8f));

        // ---------------------------------------------------------
        // Save preset
        // ---------------------------------------------------------

        using (ImRaii.Disabled(
            presetNameInput.Trim().Length == 0))
        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
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
                new Vector2(-1f, 38f)))
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
                        Yaw = engine.ScreenYaw,
                        Scale = engine.ScreenScale,
                    });

                Plugin.Cfg.Save();

                presetNameInput =
                    string.Empty;
            }
        }

        ImGui.Dummy(new Vector2(0f, 12f));

        // ---------------------------------------------------------
        // Empty state
        // ---------------------------------------------------------

        if (Plugin.Cfg.ScreenPresets.Count == 0)
        {
            ImGui.SetWindowFontScale(0.88f);

            ImGui.TextColored(
                MutedText,
                "No presets yet. Place the screen, then save it above.");

            ImGui.SetWindowFontScale(1f);

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
                8f))
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
                        new Vector2(14f, 12f));

                    ImGui.TextColored(
                        Vector4.One,
                        preset.Name);

                    // Metadata
                    ImGui.SetCursorScreenPos(
                        rowOrigin +
                        new Vector2(14f, 39f));

                    ImGui.SetWindowFontScale(0.82f);

                    ImGui.TextColored(
                        MutedText,
                        $"xyz {preset.X:0.0}, {preset.Y:0.0}, {preset.Z:0.0}  •  scale {preset.Scale:0.00}");

                    ImGui.SetWindowFontScale(1f);

                    // Buttons
                    ImGui.SetCursorScreenPos(
                        rowOrigin +
                        new Vector2(14f, 66f));

                    // Load
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        7f))
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
                            new Vector2(90f, 30f)))
                        {
                            engine.SetScreenTransform(
                                new Vector3(
                                    preset.X,
                                    preset.Y,
                                    preset.Z),
                                preset.Yaw,
                                preset.Scale);
                        }
                    }

                    ImGui.SameLine(0f, 8f);

                    // Overwrite
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        7f))
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
                            new Vector2(100f, 30f)))
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

                            Plugin.Cfg.Save();
                        }
                    }

                    ImGui.SameLine(0f, 8f);

                    // Delete
                    using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        7f))
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
                            new Vector2(90f, 30f)))
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
                new Vector2(0f, 8f));
        }
    }
}

