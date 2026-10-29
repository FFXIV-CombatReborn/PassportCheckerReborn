using System;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private static readonly (string Name, Vector4 Color)[] AccentPresets =
    [
        ("Crimson (default)", M3.DefaultSeed),
        ("Sapphire", M3ColorMath.FromRgb(0x2F6DB5)),
        ("Teal", M3ColorMath.FromRgb(0x00897B)),
        ("Forest", M3ColorMath.FromRgb(0x3E8E41)),
        ("Amber", M3ColorMath.FromRgb(0xC98A00)),
        ("Violet", M3ColorMath.FromRgb(0x7E57C2)),
        ("Rose", M3ColorMath.FromRgb(0xC2185B)),
    ];

    private void DrawAppearancePage()
    {
        var cfg = Configuration;

        using (M3Card.Begin("appearance_theme", "Theme", FontAwesomeIcon.Palette,
                   subtitle: "Every color in the plugin's windows is generated from one accent color."))
        {
            var accent = cfg.UiAccentColor;
            if (M3Widgets.RowColor("Accent color", ref accent, M3.DefaultSeed,
                    "Very dark or grey colors fall back to the default.", alpha: false))
            {
                cfg.UiAccentColor = accent with { W = 1f };
                savePending = true;
            }

            DrawAccentPresets();
        }

        using (M3Card.Begin("appearance_size", "Size", FontAwesomeIcon.TextHeight,
                   subtitle: "Applies to every window of the plugin, on top of Dalamud's global scale."))
        {
            var textPercent = cfg.UiTextScale * 100f;
            if (M3Widgets.RowDragFloat("Text size", ref textPercent, 75f, 175f, "%.0f%%",
                    "Scales the text, on top of Dalamud's own font settings.", step: 1f))
            {
                cfg.UiTextScale = MathF.Round(textPercent) / 100f;
                savePending = true;
            }

            var elementPercent = cfg.UiElementScale * 100f;
            if (M3Widgets.RowDragFloat("Element size", ref elementPercent, 75f, 175f, "%.0f%%",
                    "Scales the padding, spacing and controls. Turn it down for a more compact layout.", step: 1f))
            {
                cfg.UiElementScale = MathF.Round(elementPercent) / 100f;
                savePending = true;
            }

            var paddingPercent = cfg.UiPaddingScale * 100f;
            if (M3Widgets.RowDragFloat("Spacing", ref paddingPercent, 50f, 200f, "%.0f%%",
                    "Scales only the space around and between things, leaving the controls their size.", step: 1f))
            {
                cfg.UiPaddingScale = MathF.Round(paddingPercent) / 100f;
                savePending = true;
            }
        }
    }

    private void DrawAccentPresets()
    {
        var cfg = Configuration;
        var s = M3.Scheme;
        var scale = M3.Scale;
        var diameter = 28f * scale;
        var gap = M3.Space2;
        var controlWidth = (AccentPresets.Length * diameter) + ((AccentPresets.Length - 1) * gap);

        var row = M3SettingRow.Begin("Presets", null, new Vector2(controlWidth, diameter));
        var origin = row.ControlPosition;
        var drawList = ImGui.GetWindowDrawList();

        for (var i = 0; i < AccentPresets.Length; i++)
        {
            var (name, color) = AccentPresets[i];
            ImGui.SetCursorScreenPos(origin + new Vector2(i * (diameter + gap), 0f));
            var pressed = ImGui.InvisibleButton($"##accent_preset_{i}", new Vector2(diameter));
            var hovered = ImGui.IsItemHovered();
            var center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;

            drawList.AddCircleFilled(center, diameter * 0.5f, M3.U32(color), 32);
            if (IsSameColor(cfg.UiAccentColor, color))
            {
                drawList.AddCircle(center, (diameter * 0.5f) + (3f * scale), M3.U32(s.OnSurface), 32, 2f * scale);
            }
            else if (hovered)
            {
                drawList.AddCircle(center, (diameter * 0.5f) + (2f * scale), M3.U32(s.Outline), 32, 1.5f * scale);
            }

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                M3Tooltip.Show(name);
            }

            if (pressed)
            {
                cfg.UiAccentColor = color;
                cfg.Save();
            }
        }

        M3SettingRow.End(row);
    }

    private static bool IsSameColor(Vector4 a, Vector4 b)
    {
        const float tolerance = 0.002f;
        return MathF.Abs(a.X - b.X) < tolerance
            && MathF.Abs(a.Y - b.Y) < tolerance
            && MathF.Abs(a.Z - b.Z) < tolerance;
    }
}
