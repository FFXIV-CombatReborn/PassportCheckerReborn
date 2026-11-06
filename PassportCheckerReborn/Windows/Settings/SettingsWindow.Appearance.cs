using System;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private static readonly M3ColorChoice[] AccentPresets =
    [
        new("Crimson (default)", M3.DefaultSeed),
        new("Sapphire", M3ColorMath.FromRgb(0x2F6DB5)),
        new("Teal", M3ColorMath.FromRgb(0x00897B)),
        new("Forest", M3ColorMath.FromRgb(0x3E8E41)),
        new("Amber", M3ColorMath.FromRgb(0xC98A00)),
        new("Violet", M3ColorMath.FromRgb(0x7E57C2)),
        new("Rose", M3ColorMath.FromRgb(0xC2185B)),
    ];

    private void DrawAppearancePage()
    {
        var cfg = Configuration;

        using (M3Card.Begin("appearance_theme", "Theme", FontAwesomeIcon.Palette,
                   subtitle: "Every color in the plugin's windows is generated from one accent color."))
        {
            var accent = cfg.UiAccentColor;
            if (M3Widgets.RowColorChoices("Accent color", ref accent, AccentPresets,
                    "Pick a preset, or the last swatch for your own color. Very dark or grey colors fall back to the default."))
            {
                cfg.UiAccentColor = accent with { W = 1f };
                savePending = true;
            }
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

        using (M3Card.Begin("appearance_motion", "Motion", FontAwesomeIcon.Film))
        {
            var motionPercent = cfg.UiMotionScale * 100f;
            if (M3Widgets.RowDragFloat("Animation length", ref motionPercent, 0f, 200f, "%.0f%%",
                    "Scales how long animations take in the plugin's windows, such as folding the window and opening cards. Set it to 0% to turn them off.",
                    step: 1f))
            {
                cfg.UiMotionScale = MathF.Round(motionPercent) / 100f;
                savePending = true;
            }
        }
    }
}
