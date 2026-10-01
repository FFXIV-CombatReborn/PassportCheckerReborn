using Dalamud.Interface.Utility;
using System;
using System.Numerics;

namespace PassportCheckerReborn.UI;

/// <summary>Material Design 3 stuff: the resolved scheme, shape and spacing scales, state-layer opacities and fonts.</summary>
internal static class M3
{
    public static readonly Vector4 DefaultSeed = M3ColorMath.FromRgb(0xB0201F);

    // The seed as configured, not as resolved: remembering the fallback instead would make every
    // access see a changed seed and rebuild the scheme, for as long as an unusable colour is set.
    private static Vector4 ConfiguredSeed = DefaultSeed;
    private static M3Scheme Schemeseed = M3Scheme.FromSeed(DefaultSeed);

    private static float WindowScale = 1f;

    private static float ElementScale = 1f;

    public static float Scale => ImGuiHelpers.GlobalScale * ElementScale * WindowScale;

    public static void BeginFrame()
    {
        if (!ImGui.IsAnyItemActive())
        {
            ElementScale = Math.Clamp(PassportCheckerReborn.Config.UiElementScale, 0.5f, 2.5f);
        }
    }

    public static float TextScale => Math.Clamp(PassportCheckerReborn.Config.UiTextScale, 0.5f, 3f) * WindowScale;

    public static WindowScaleScope PushWindowScale(float scale)
    {
        var previous = WindowScale;
        WindowScale = previous * Math.Clamp(scale, 0.1f, 10f);
        return new WindowScaleScope(previous);
    }

    public static M3Scheme Scheme
    {
        get
        {
            var seed = PassportCheckerReborn.Config.UiAccentColor;
            if (seed.X != ConfiguredSeed.X || seed.Y != ConfiguredSeed.Y || seed.Z != ConfiguredSeed.Z)
            {
                ConfiguredSeed = seed;

                // A desaturated or pitch-black seed resolves to an unusable grey scheme, so fall back
                // to the plugin default rather than leaving the user with no accent at all.
                M3ColorMath.ToLch(seed, out var lightness, out var chroma, out _);
                Schemeseed = M3Scheme.FromSeed(lightness < 5f || chroma < 2f ? DefaultSeed : seed);
            }

            return Schemeseed;
        }
    }

    public static float ShapeExtraSmall => 4f * Scale;
    public static float ShapeSmall => 8f * Scale;
    public static float ShapeMedium => 12f * Scale;
    public static float ShapeLarge => 16f * Scale;
    public static float ShapeExtraLarge => 28f * Scale;
    public static float ShapeFull => 999f;

    public static float Space1 => 4f * Scale;
    public static float Space2 => 8f * Scale;
    public static float Space3 => 12f * Scale;

    public const float StateHover = 0.08f;
    public const float StatePressed = 0.10f;
    public const float DisabledContent = 0.38f;
    public const float DisabledContainer = 0.12f;

    /// <summary>Body text: Dalamud's default font at <see cref="TextScale"/>.</summary>
    public static ImFontPtr Body => FontManager.GetDefaultFont(TextScale);
    public static ImFontPtr HeadlineSmall => FontManager.GetFont(22f * TextScale);
    public static ImFontPtr TitleLarge => FontManager.GetFont(19f * TextScale);
    public static ImFontPtr TitleMedium => FontManager.GetFont(16f * TextScale);
    public static ImFontPtr LabelSmall => FontManager.GetFont(11f * TextScale);
    public static FontScope PushBody()
    {
        if (MathF.Abs(TextScale - 1f) < 0.005f)
        {
            return default;
        }

        ImGui.PushFont(Body);
        return new FontScope(true);
    }

    public static float FitText(float height, float padding)
    {
        return MathF.Max(height * Scale, ImGui.GetTextLineHeight() + (padding * 2f * Scale));
    }

    public static Vector4 Alpha(Vector4 color, float alpha)
    {
        return color with { W = alpha };
    }

    /// <summary>Composites a state layer: the content colour over the container at the interaction's opacity.</summary>
    public static Vector4 StateLayer(Vector4 container, Vector4 content, bool hovered, bool active)
    {
        if (!hovered && !active)
        {
            return container;
        }

        var opacity = active ? StatePressed + StateHover : StateHover;
        var mixed = M3ColorMath.Mix(container, content, opacity);
        return mixed with { W = container.W };
    }

    public static Vector4 ContentOn(Vector4 fill)
    {
        M3ColorMath.ToLch(fill, out var lightness, out _, out _);
        return lightness > 60f ? Scheme.Surface : Scheme.OnSurface;
    }

    public static uint U32(Vector4 color)
    {
        return ImGui.GetColorU32(color);
    }

    public static uint U32(Vector4 color, float alpha)
    {
        return ImGui.GetColorU32(color with { W = alpha });
    }

    public static Vector4 Severity(M3Severity severity)
    {
        var scheme = Scheme;
        return severity switch
        {
            M3Severity.Error => scheme.Error,
            M3Severity.Warning => scheme.Warning,
            M3Severity.Success => scheme.Success,
            M3Severity.Info => scheme.Info,
            _ => scheme.Primary,
        };
    }

    public static Vector4 SeverityContainer(M3Severity severity)
    {
        var scheme = Scheme;
        return severity switch
        {
            M3Severity.Error => scheme.ErrorContainer,
            M3Severity.Warning => scheme.WarningContainer,
            M3Severity.Success => scheme.SuccessContainer,
            M3Severity.Info => scheme.SecondaryContainer,
            _ => scheme.PrimaryContainer,
        };
    }

    internal readonly struct WindowScaleScope(float previous) : IDisposable
    {
        public void Dispose()
        {
            // A default scope pushed nothing, and must not zero the scale on its way out.
            if (previous > 0f)
            {
                WindowScale = previous;
            }
        }
    }

    internal readonly struct FontScope(bool pushed) : IDisposable
    {
        public void Dispose()
        {
            if (pushed)
            {
                ImGui.PopFont();
            }
        }
    }
}

internal enum M3Severity
{
    Neutral,
    Info,
    Success,
    Warning,
    Error,
}
