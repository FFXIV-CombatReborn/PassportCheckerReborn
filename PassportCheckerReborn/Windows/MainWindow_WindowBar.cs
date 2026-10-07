using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

// The window's own controls, in place of a title bar. Minimizing folds the window into that bar,
// leaving a pill that says PCR.
public partial class MainWindow
{
    private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoScrollbar
        // The page runs past the window's edges while it folds, and must not scroll the window.
        | ImGuiWindowFlags.NoScrollWithMouse;

    private static readonly Vector2 DefaultSize = new(820f, 600f);

    private static readonly WindowSizeConstraints DefaultSizeConstraints = new()
    {
        MinimumSize = new Vector2(440, 360),
        MaximumSize = new Vector2(1600, 1400),
    };

    // Left to right ahead of minimize and close. They give way when the window is too narrow for them.
    private static readonly M3WindowAction[] WindowActions =
    [
        new("##window_kofi", FontAwesomeIcon.MugHot, "Support the developer on Ko-fi"),
    ];

    private const string LogoResource = "PassportCheckerReborn.Resources.PCR_Icon.png";

    // Null until the texture has loaded.
    private static IDalamudTextureWrap? Logo => PassportCheckerReborn.TextureProvider
        .GetFromManifestResource(typeof(MainWindow).Assembly, LogoResource).GetWrapOrDefault();

    private static M3WindowBrand Brand => new(Logo, "PCR", FontAwesomeIcon.Passport);

    private readonly M3WindowFold fold = new();

    // How many actions the top app bar found room for this frame.
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
            // Unfolded again, so the window is the user's to move and resize.
            Position = null;
            Size = DefaultSize;
            SizeCondition = ImGuiCond.FirstUseEver;
            SizeConstraints = DefaultSizeConstraints;
        }
    }

    // Begun after the page, so it draws over the page and takes the mouse first while the two overlap mid-fold.
    private void DrawWindowBar()
    {
        var first = WindowActions.Length - shownActions;
        var pressed = fold.DrawBar("##pcr_window_actions", WindowActions.AsSpan(first), Brand, out var closed,
            M3.Scheme.SurfaceContainerHigh);

        // Indices follow WindowActions.
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
