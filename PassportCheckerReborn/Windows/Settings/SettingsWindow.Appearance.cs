using System;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private const float ScaleStep = 0.05f;
    private const float MotionStepPercent = 5f;

    private static readonly M3ColorChoice[] AccentPresets =
    [
        new("Blue (default)", Configuration.DefaultAccentColor),
        new("Ocean", M3ColorMath.FromRgb(0x2F6DB5)),
        new("Teal", M3ColorMath.FromRgb(0x00897B)),
        new("Forest", M3ColorMath.FromRgb(0x3E8E41)),
        new("Amber", M3ColorMath.FromRgb(0xC98A00)),
        new("Violet", M3ColorMath.FromRgb(0x7E57C2)),
        new("Crimson", M3.DefaultSeed),
    ];

    private void DrawAppearancePage()
    {
        using var card = M3Card.Begin("appearance_theme", "Theme", FontAwesomeIcon.Palette,
            subtitle: "Applies to every Passport Checker Reborn window.");

        var cfg = Configuration;

        var accent = cfg.UiAccentColor;
        if (M3Widgets.RowColorChoices("Accent color", ref accent, AccentPresets,
                "The whole color scheme is built from this one color. Pick a preset, or the last swatch for your own color."))
        {
            cfg.UiAccentColor = accent with { W = 1f };
            savePending = true;
        }

        var textScale = cfg.UiTextScale;
        if (M3Widgets.RowDragFloat("Text size", ref textScale, 0.75f, 2f, "%.2fx",
                "Scales the text, on top of Dalamud's own font size.", step: ScaleStep))
        {
            cfg.UiTextScale = MathF.Round(textScale, 2);
            savePending = true;
        }

        var elementScale = cfg.UiElementScale;
        if (M3Widgets.RowDragFloat("Control size", ref elementScale, 0.75f, 2f, "%.2fx",
                "Scales buttons, switches, fields and the other controls.", step: ScaleStep))
        {
            cfg.UiElementScale = MathF.Round(elementScale, 2);
            savePending = true;
        }

        var paddingScale = cfg.UiPaddingScale;
        if (M3Widgets.RowDragFloat("Spacing", ref paddingScale, 0.5f, 2f, "%.2fx",
                "Scales the space around and between things, without resizing them.", step: ScaleStep))
        {
            cfg.UiPaddingScale = MathF.Round(paddingScale, 2);
            savePending = true;
        }

        var motionPercent = cfg.UiMotionScale * 100f;
        if (M3Widgets.RowDragFloat("Animation length", ref motionPercent, 0f, 200f, "%.0f%%",
                "How long animations take, such as folding a window or opening a card. 0% turns them off.", step: MotionStepPercent))
        {
            cfg.UiMotionScale = MathF.Round(motionPercent) / 100f;
            savePending = true;
        }

        var compactLayout = cfg.UiCompactLayout;
        if (M3Widgets.RowSwitch("Compact layout", ref compactLayout,
                "Tighter padding and shorter rows, so more fits on screen."))
        {
            cfg.UiCompactLayout = compactLayout;
            cfg.Save();
        }

        var inlineDescriptions = cfg.UiInlineDescriptions;
        if (M3Widgets.RowSwitch("Inline descriptions", ref inlineDescriptions,
                "Show each setting's description under its name. Turn it off to show descriptions on hover instead."))
        {
            cfg.UiInlineDescriptions = inlineDescriptions;
            cfg.Save();
        }

        var iconSidebar = cfg.UiIconSidebar;
        if (M3Widgets.RowSwitch("Icons-only sidebar", ref iconSidebar,
                "Turn the sidebar into a narrow strip of icons. Hover an icon to see its name."))
        {
            cfg.UiIconSidebar = iconSidebar;
            cfg.Save();
        }

        var isDefault = cfg is { UiTextScale: 1f, UiElementScale: 1f, UiPaddingScale: 1f, UiMotionScale: 1f };
        if (M3Widgets.Button("##appearance_reset_sizes", "Reset sizes", M3ButtonStyle.Text, FontAwesomeIcon.Undo, enabled: !isDefault))
        {
            cfg.UiTextScale = 1f;
            cfg.UiElementScale = 1f;
            cfg.UiPaddingScale = 1f;
            cfg.UiMotionScale = 1f;
            cfg.Save();
        }
    }
}
