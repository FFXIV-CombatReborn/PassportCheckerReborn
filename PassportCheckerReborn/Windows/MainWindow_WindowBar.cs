using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PassportCheckerReborn.UI;
using System;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

// The window's own controls, in place of a title bar, and minimizing: the window folds into that bar,
// leaving a pill that says PCR. The bar's top right corner is the anchor, which holds still while the
// window folds around it; the window unfolds from wherever the pill has been dragged since.
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

    // Measured by the top app bar each frame: how many actions fit, and how far below the top of the
    // page the bar sits.
    private int shownActions = WindowActions.Length;
    private float barTop;

    // Where the fold is headed, and how far it has come: 0 open, 1 folded, linear in time.
    private bool minimized;
    private float minimizeTime;

    // While foldLayout is set, the fold places and sizes the window, from its first frame until the
    // frame after it has unfolded again. foldSettled marks that last frame.
    private bool foldLayout;
    private bool foldSettled;

    // Screen pixels: the window's size when it began to fold, and the anchor open and folded.
    private Vector2 restoreSize;
    private Vector2 anchorOpen;
    private Vector2 anchorFolded;

    // The window as Begin laid it out this frame, and the theme's padding and rounding it began from.
    private Vector2 windowPos;
    private Vector2 windowSize;
    private Vector2 openPadding;
    private float windowRounding;
    private bool foldStylePushed;

    internal bool IsMinimized => minimized;

    // minimizeTime, eased.
    private float Folded
    {
        get
        {
            var t = minimizeTime;
            return t < 0.5f ? 4f * t * t * t : 1f - (MathF.Pow((-2f * t) + 2f, 3f) * 0.5f);
        }
    }

    // How far in from the window's top right corner the bar sits while open: x from the right, y down.
    private Vector2 AnchorInset => new(openPadding.X, openPadding.Y + barTop);

    internal void Restore()
    {
        if (!minimized)
        {
            return;
        }

        // From the pill, open around wherever it has been dragged. Mid-fold, it goes back where it was.
        if (minimizeTime >= 1f)
        {
            anchorOpen = OpenAnchorNear(anchorFolded);
        }

        minimized = false;
    }

    private void ToggleMinimized(Vector2 anchor)
    {
        if (minimized)
        {
            Restore();
            return;
        }

        if (!foldLayout)
        {
            restoreSize = windowSize;
            anchorOpen = anchor;
            anchorFolded = anchor;
            foldLayout = true;
        }

        minimized = true;
    }

    // Closed while folded, the window next opens unfolded, where it would have unfolded to.
    private void RestoreOnClose()
    {
        if (!foldLayout)
        {
            return;
        }

        Restore();
        minimizeTime = 0f;
    }

    // Advances the fold and, while it runs, sets this frame's window rect through Dalamud, which applies
    // it after PreDraw. Also pushes the rounding and padding Begin reads; PopFoldStyle takes them off.
    private void PrepareFold()
    {
        var style = ImGui.GetStyle();
        openPadding = style.WindowPadding;
        windowRounding = style.WindowRounding;

        // Folded as of last frame, which placed it exactly; only from then on is the pill left to drag.
        var resting = minimized && minimizeTime >= 1f;

        var target = minimized ? 1f : 0f;
        if (minimizeTime != target)
        {
            var step = ImGui.GetIO().DeltaTime / M3Motion.EmphasisedDuration;
            minimizeTime = minimized ? MathF.Min(1f, minimizeTime + step) : MathF.Max(0f, minimizeTime - step);
        }

        if (!foldLayout)
        {
            return;
        }

        if (foldSettled && !minimized && minimizeTime <= 0f)
        {
            // Last frame put the window back at its open rect; hand it back to the user from here.
            foldLayout = false;
            foldSettled = false;
            Position = null;
            Size = DefaultSize;
            SizeCondition = ImGuiCond.FirstUseEver;
            SizeConstraints = DefaultSizeConstraints;
            Flags = BaseFlags;
            return;
        }

        var folded = Folded;
        var barSize = M3Widgets.WindowActionsSize(shownActions, Brand, folded);
        var anchor = Vector2.Lerp(anchorOpen, anchorFolded, folded);
        var inset = AnchorInset * (1f - folded);
        var size = Vector2.Lerp(restoreSize, barSize, folded);

        // At rest, the pill is the user's to drag; the rest of the time the fold places it.
        Position = resting ? null : new Vector2(anchor.X + inset.X - size.X, anchor.Y - inset.Y);
        PositionCondition = ImGuiCond.Always;
        Size = size / ImGuiHelpers.GlobalScale;
        SizeCondition = ImGuiCond.Always;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = Vector2.One,
            MaximumSize = DefaultSizeConstraints.MaximumSize,
        };

        // Saved settings are left alone, so a game closed on the pill reopens the window at its real size.
        Flags = BaseFlags | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings
            | (resting ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoMove);
        foldSettled = !minimized && minimizeTime <= 0f;

        // The corners round out into the pill, and the padding goes: the window clips half its padding
        // in from each side, which would cut into a pill that fills it.
        windowRounding = float.Lerp(windowRounding, barSize.Y * 0.5f, folded);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, windowRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Lerp(openPadding, Vector2.Zero, folded));
        foldStylePushed = true;
    }

    private void PopFoldStyle()
    {
        if (foldStylePushed)
        {
            ImGui.PopStyleVar(2);
            foldStylePushed = false;
        }
    }

    // The window's rect when open: its own, or, while folding, the one it folded from.
    private (Vector2 Pos, Vector2 Size) OpenRect()
    {
        if (!foldLayout)
        {
            return (windowPos, windowSize);
        }

        var inset = AnchorInset;
        return (new Vector2(anchorOpen.X + inset.X - restoreSize.X, anchorOpen.Y - inset.Y), restoreSize);
    }

    // Where the bar goes when the window opens around a pill at `folded`, nudged so the open window
    // stays on the game's screen. A pill dragged onto another monitor opens there.
    private Vector2 OpenAnchorNear(Vector2 folded)
    {
        var inset = AnchorInset;
        var pos = new Vector2(folded.X + inset.X - restoreSize.X, folded.Y - inset.Y);

        var viewport = ImGui.GetMainViewport();
        var min = viewport.WorkPos;
        var max = viewport.WorkPos + viewport.WorkSize;
        if (folded.X >= min.X && folded.X <= max.X && folded.Y >= min.Y && folded.Y <= max.Y)
        {
            pos.X = Math.Clamp(pos.X, min.X, MathF.Max(min.X, max.X - restoreSize.X));
            pos.Y = Math.Clamp(pos.Y, min.Y, MathF.Max(min.Y, max.Y - restoreSize.Y));
        }

        return new Vector2(pos.X + restoreSize.X - inset.X, pos.Y + inset.Y);
    }

    // A child window begun after the page, so it draws over the page and takes the mouse first while
    // the two overlap mid-fold.
    private void DrawWindowBar()
    {
        var folded = Folded;

        Vector2 anchor;
        if (!foldLayout)
        {
            var inset = AnchorInset;
            anchor = new Vector2(windowPos.X + windowSize.X - inset.X, windowPos.Y + inset.Y);
        }
        else
        {
            if (minimized && minimizeTime >= 1f)
            {
                // At rest the pill is the whole window, and follows it wherever it is dragged.
                anchorFolded = new Vector2(windowPos.X + windowSize.X, windowPos.Y);
            }

            anchor = Vector2.Lerp(anchorOpen, anchorFolded, folded);
        }

        var brand = Brand;
        var barSize = M3Widgets.WindowActionsSize(shownActions, brand, folded);
        ImGui.SetCursorScreenPos(new Vector2(anchor.X - barSize.X, anchor.Y));
        using var bar = ImRaii.Child("##pcr_window_bar", barSize, false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground);
        if (!bar)
        {
            return;
        }

        var first = WindowActions.Length - shownActions;
        var pressed = M3Widgets.WindowActions("##pcr_window_actions", anchor, WindowActions.AsSpan(first), brand, folded,
            out var toggled, out var closed, M3.Scheme.SurfaceContainerHigh);

        // Indices follow WindowActions.
        if (pressed >= 0 && first + pressed == 0)
        {
            OpenUrl(KofiUrl);
        }

        if (toggled)
        {
            ToggleMinimized(anchor);
        }

        if (closed)
        {
            IsOpen = false;
        }
    }
}
