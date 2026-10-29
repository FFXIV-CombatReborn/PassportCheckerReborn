using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoScrollWithMouse;

    private static readonly Vector2 DefaultSize = new(820f, 600f);

    private static readonly WindowSizeConstraints DefaultSizeConstraints = new()
    {
        MinimumSize = new Vector2(440, 360),
        MaximumSize = new Vector2(1600, 1400),
    };

    private static readonly M3WindowAction[] WindowActions =
    [
        new("##window_kofi", FontAwesomeIcon.MugHot, "Support the developer on Ko-fi"),
    ];

    private const string LogoResource = "PassportCheckerReborn.Resources.PCR_Icon.png";

    private static IDalamudTextureWrap? Logo => Plugin.TextureProvider
        .GetFromManifestResource(typeof(SettingsWindow).Assembly, LogoResource).GetWrapOrDefault();

    private static M3WindowBrand Brand => new(Logo, "PCR", FontAwesomeIcon.Passport);

    private readonly M3WindowFold fold = new();

    private int shownActions = WindowActions.Length;

    internal bool IsMinimized => fold.IsMinimized;

    internal void Restore()
    {
        fold.Restore();
    }

    private void PrepareFold()
    {
        Flags = BaseFlags;
        if (fold.Prepare(this, shownActions, Brand))
        {
            Position = null;
            Size = DefaultSize;
            SizeCondition = ImGuiCond.FirstUseEver;
            SizeConstraints = DefaultSizeConstraints;
        }
    }

    private void DrawWindowBar()
    {
        var first = WindowActions.Length - shownActions;
        var pressed = fold.DrawBar("##pcr_window_actions", WindowActions.AsSpan(first), Brand, out var closed,
            M3.Scheme.SurfaceContainerHigh);

        if (pressed >= 0 && first + pressed == 0)
        {
            OpenUrl(KofiUrl);
        }

        if (closed)
        {
            IsOpen = false;
        }
    }
}
