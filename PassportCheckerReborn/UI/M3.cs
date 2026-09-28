using Dalamud.Interface.Utility;
using System.Numerics;

namespace PassportCheckerReborn.UI;

/// <summary>Material Design 3 stuff: the resolved scheme, shape and spacing scales, state-layer opacities and fonts.</summary>
internal static class M3
{
	public static readonly Vector4 DefaultSeed = M3ColorMath.FromRgb(0xB0201F);

	// The seed as configured, not as resolved: remembering the fallback instead would make every
	// access see a changed seed and rebuild the scheme, for as long as an unusable colour is set.
	private static Vector4 _configuredSeed = DefaultSeed;
	private static M3Scheme _scheme = M3Scheme.FromSeed(DefaultSeed);

	public static float Scale => ImGuiHelpers.GlobalScale;

	public static M3Scheme Scheme
	{
		get
		{
			var seed = PassportCheckerReborn.Config.UiAccentColor;
			if (seed.X != _configuredSeed.X || seed.Y != _configuredSeed.Y || seed.Z != _configuredSeed.Z)
			{
				_configuredSeed = seed;

				// A desaturated or pitch-black seed resolves to an unusable grey scheme, so fall back
				// to the plugin default rather than leaving the user with no accent at all.
				M3ColorMath.ToLch(seed, out var lightness, out var chroma, out _);
				_scheme = M3Scheme.FromSeed(lightness < 5f || chroma < 2f ? DefaultSeed : seed);
			}

			return _scheme;
		}
	}

	public static float ShapeExtraSmall => 4f * Scale;
	public static float ShapeSmall => 8f * Scale;
	public static float ShapeMedium => 12f * Scale;
	public static float ShapeLarge => 16f * Scale;
	public static float ShapeFull => 999f;

	public static float Space1 => 4f * Scale;
	public static float Space2 => 8f * Scale;
	public static float Space3 => 12f * Scale;

	public const float StateHover = 0.08f;
	public const float StatePressed = 0.10f;
	public const float DisabledContent = 0.38f;
	public const float DisabledContainer = 0.12f;

	public static ImFontPtr HeadlineSmall => FontManager.GetFont(22f);
	public static ImFontPtr TitleLarge => FontManager.GetFont(19f);
	public static ImFontPtr TitleMedium => FontManager.GetFont(16f);
	public static ImFontPtr LabelSmall => FontManager.GetFont(11f);

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
}

internal enum M3Severity
{
	Neutral,
	Info,
	Success,
	Warning,
	Error,
}
