using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace PassportCheckerReborn.UI;

/// <summary>One option in a <see cref="M3Widgets.SegmentedButtons"/> group.</summary>
internal readonly record struct M3Segment(
	string Label,
	FontAwesomeIcon Icon = FontAwesomeIcon.None,
	string? Tooltip = null,
	Vector4? Accent = null);

internal enum M3ButtonStyle
{
	/// <summary>High emphasis: solid primary fill.</summary>
	Filled,

	/// <summary>Medium emphasis: secondary container fill.</summary>
	Tonal,

	/// <summary>Medium emphasis: outlined, transparent fill.</summary>
	Outlined,

	/// <summary>Low emphasis: no container at all.</summary>
	Text,

	/// <summary>Destructive action: error container fill.</summary>
	Danger,
}

/// <summary>
/// The Material 3 control set. Drawn by hand wherever the shape language matters; where ImGui.s own
/// behaviour is worth keeping — text editing, ctrl-click on sliders, keyboard navigation — the native
/// widget is kept and only its painting is replaced.
/// </summary>
internal static class M3Widgets
{
	#region Switch

	public static Vector2 SwitchSize()
	{
		return new Vector2(52f, 32f) * M3.Scale;
	}

	public static bool Switch(string id, ref bool value, bool enabled = true)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var size = SwitchSize();

		var changed = false;
		if (ImGui.InvisibleButton(id, size) && enabled)
		{
			value = !value;
			changed = true;
		}

		var hovered = enabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var held = enabled && ImGui.IsItemActive();
		var min = ImGui.GetItemRectMin();
		var drawList = ImGui.GetWindowDrawList();
		var dim = enabled ? 1f : M3.DisabledContent;

		var trackSize = new Vector2(48f, 28f) * scale;
		var trackMin = min + ((size - trackSize) * 0.5f);
		var trackMax = trackMin + trackSize;
		var trackRadius = trackSize.Y * 0.5f;

		var progress = M3Motion.Approach(ImGui.GetID(id), value ? 1f : 0f, M3Motion.FastDuration);

		var trackFill = M3ColorMath.Mix(s.SurfaceContainerHighest, s.Primary, progress);
		var trackOutline = M3ColorMath.Mix(s.Outline, s.Primary, progress);
		if (hovered || held)
		{
			trackFill = M3.StateLayer(trackFill, value ? s.OnPrimary : s.OnSurface, hovered, held);
		}

		drawList.AddRectFilled(trackMin, trackMax, M3.U32(trackFill, dim), trackRadius);
		drawList.AddRect(trackMin, trackMax, M3.U32(trackOutline, 0.9f * dim), trackRadius, ImDrawFlags.None, 2f * scale);

		// The thumb is 16dp unselected, 24dp selected, and swells to 28dp while pressed.
		var thumbDiameter = float.Lerp(16f, 24f, progress) * scale;
		if (held)
		{
			thumbDiameter = 28f * scale;
		}

		var thumbRadius = thumbDiameter * 0.5f;
		var travelStart = trackMin.X + (4f * scale) + (8f * scale);
		var travelEnd = trackMax.X - (4f * scale) - (12f * scale);
		var thumbCenter = new Vector2(float.Lerp(travelStart, travelEnd, progress), trackMin.Y + trackRadius);
		var thumbColor = M3ColorMath.Mix(s.Outline, s.OnPrimary, progress);

		if (hovered || held)
		{
			var halo = value ? s.Primary : s.OnSurface;
			drawList.AddCircleFilled(thumbCenter, 20f * scale, M3.U32(halo, held ? M3.StatePressed : M3.StateHover));
		}

		drawList.AddCircleFilled(thumbCenter, thumbRadius, M3.U32(thumbColor, dim), 24);

		// Selected switches carry a check glyph inside the thumb, per the M3 spec.
		if (progress > 0.55f)
		{
			var tick = thumbRadius * 0.45f;
			var checkColor = M3.U32(s.OnPrimaryContainer, (progress - 0.55f) / 0.45f * dim);
			drawList.AddLine(
				thumbCenter + new Vector2(-tick, 0f),
				thumbCenter + new Vector2(-tick * 0.2f, tick * 0.7f),
				checkColor, 2f * scale);
			drawList.AddLine(
				thumbCenter + new Vector2(-tick * 0.2f, tick * 0.7f),
				thumbCenter + new Vector2(tick, -tick * 0.6f),
				checkColor, 2f * scale);
		}

		return changed;
	}

	#endregion

	#region Checkbox

	public static Vector2 CheckboxSize()
	{
		return new Vector2(20f, 20f) * M3.Scale;
	}

	public static bool Checkbox(string id, ref bool value)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var box = CheckboxSize();
		var hit = box + (new Vector2(8f, 8f) * scale);

		var changed = false;
		if (ImGui.InvisibleButton(id, hit))
		{
			value = !value;
			changed = true;
		}

		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var held = ImGui.IsItemActive();
		var min = ImGui.GetItemRectMin();
		var center = min + (hit * 0.5f);
		var boxMin = center - (box * 0.5f);
		var boxMax = center + (box * 0.5f);
		var drawList = ImGui.GetWindowDrawList();
		var progress = M3Motion.Approach(ImGui.GetID(id), value ? 1f : 0f, M3Motion.FastDuration);

		if (hovered || held)
		{
			drawList.AddCircleFilled(center, hit.X * 0.55f, M3.U32(value ? s.Primary : s.OnSurface, held ? M3.StatePressed : M3.StateHover));
		}

		if (progress > 0.01f)
		{
			drawList.AddRectFilled(boxMin, boxMax, M3.U32(s.Primary, progress), M3.ShapeExtraSmall * 0.5f);
		}

		if (progress < 0.99f)
		{
			drawList.AddRect(boxMin, boxMax, M3.U32(s.OnSurfaceVariant, 1f - progress), M3.ShapeExtraSmall * 0.5f, ImDrawFlags.None, 2f * scale);
		}

		if (progress > 0.2f)
		{
			var tick = box.X * 0.26f;
			var color = M3.U32(s.OnPrimary, progress);
			drawList.AddLine(center + new Vector2(-tick, 0f), center + new Vector2(-tick * 0.25f, tick * 0.8f), color, 2f * scale);
			drawList.AddLine(center + new Vector2(-tick * 0.25f, tick * 0.8f), center + new Vector2(tick, -tick * 0.7f), color, 2f * scale);
		}

		return changed;
	}

	#endregion

	#region Buttons

	public static float ButtonHeight => 40f * M3.Scale;

	public static float ButtonWidth(FontAwesomeIcon icon, string label)
	{
		var scale = M3.Scale;
		var width = 24f * scale * 2f;
		if (icon != FontAwesomeIcon.None)
		{
			width += M3Draw.MeasureIcon(icon).X + (8f * scale);
		}

		if (!string.IsNullOrEmpty(label))
		{
			width += ImGui.CalcTextSize(label).X;
		}

		return MathF.Max(width, 64f * scale);
	}

	public static bool Button(string id, string label, M3ButtonStyle style = M3ButtonStyle.Tonal, FontAwesomeIcon icon = FontAwesomeIcon.None, float? width = null, bool enabled = true, string? tooltip = null)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var height = ButtonHeight;
		var size = new Vector2(width ?? ButtonWidth(icon, label), height);

		var pressed = ImGui.InvisibleButton(id, size) && enabled;
		var hovered = enabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var held = enabled && ImGui.IsItemActive();
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var drawList = ImGui.GetWindowDrawList();
		var rounding = height * 0.5f;

		var (container, content, outline) = style switch
		{
			M3ButtonStyle.Filled => (s.Primary, s.OnPrimary, (Vector4?)null),
			M3ButtonStyle.Tonal => (s.SecondaryContainer, s.OnSecondaryContainer, (Vector4?)null),
			M3ButtonStyle.Outlined => (M3.Alpha(s.Surface, 0f), s.Primary, s.Outline),
			M3ButtonStyle.Danger => (s.ErrorContainer, s.OnErrorContainer, (Vector4?)null),
			_ => (M3.Alpha(s.Surface, 0f), s.Primary, (Vector4?)null),
		};

		if (!enabled)
		{
			container = container.W > 0f ? M3.Alpha(s.OnSurface, M3.DisabledContainer) : container;
			content = M3.Alpha(s.OnSurface, M3.DisabledContent);
			outline = outline is null ? null : M3.Alpha(s.OnSurface, M3.DisabledContainer);
		}
		else if (hovered || held)
		{
			container = container.W > 0f
				? M3.StateLayer(container, content, hovered, held)
				: M3.Alpha(content, held ? M3.StatePressed : M3.StateHover);
		}

		M3Draw.Container(drawList, min, max, container, rounding, outline, 1f);

		var iconWidth = icon == FontAwesomeIcon.None ? 0f : M3Draw.MeasureIcon(icon).X;
		var gap = icon == FontAwesomeIcon.None || string.IsNullOrEmpty(label) ? 0f : 8f * scale;
		var textSize = string.IsNullOrEmpty(label) ? Vector2.Zero : ImGui.CalcTextSize(label);
		var contentWidth = iconWidth + gap + textSize.X;
		var cursorX = min.X + ((size.X - contentWidth) * 0.5f);

		if (icon != FontAwesomeIcon.None)
		{
			using (ImRaii.PushFont(UiBuilder.IconFont))
			{
				var glyph = icon.ToIconString();
				var glyphSize = ImGui.CalcTextSize(glyph);
				drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(),
					new Vector2(cursorX, min.Y + ((height - glyphSize.Y) * 0.5f)), M3.U32(content), glyph);
			}

			cursorX += iconWidth + gap;
		}

		if (!string.IsNullOrEmpty(label))
		{
			drawList.AddText(new Vector2(cursorX, min.Y + ((height - textSize.Y) * 0.5f)), M3.U32(content), label);
		}

		if (hovered && !string.IsNullOrEmpty(tooltip))
		{
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
			ImGui.SetTooltip(tooltip);
		}
		else if (hovered)
		{
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
		}

		return pressed;
	}

	public static float IconButtonSize => 36f * M3.Scale;

	public static bool IconButton(string id, FontAwesomeIcon icon, string? tooltip = null, M3ButtonStyle style = M3ButtonStyle.Text, Vector4? tint = null, float? diameter = null)
	{
		var s = M3.Scheme;
		var size = Vector2.One * (diameter ?? IconButtonSize);

		var pressed = ImGui.InvisibleButton(id, size);
		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var held = ImGui.IsItemActive();
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var center = (min + max) * 0.5f;
		var drawList = ImGui.GetWindowDrawList();

		var (container, content) = style switch
		{
			M3ButtonStyle.Filled => (s.Primary, s.OnPrimary),
			M3ButtonStyle.Tonal => (s.SecondaryContainer, s.OnSecondaryContainer),
			M3ButtonStyle.Danger => (s.ErrorContainer, s.OnErrorContainer),
			M3ButtonStyle.Outlined => (M3.Alpha(s.Surface, 0f), s.OnSurfaceVariant),
			_ => (M3.Alpha(s.Surface, 0f), tint ?? s.OnSurfaceVariant),
		};

		if (tint is { } explicitTint)
		{
			content = explicitTint;
		}

		if (container.W > 0f)
		{
			var fill = hovered || held ? M3.StateLayer(container, content, hovered, held) : container;
			drawList.AddCircleFilled(center, size.X * 0.5f, M3.U32(fill), 32);
		}
		else if (hovered || held)
		{
			drawList.AddCircleFilled(center, size.X * 0.5f, M3.U32(content, held ? M3.StatePressed : M3.StateHover), 32);
		}

		if (style == M3ButtonStyle.Outlined)
		{
			drawList.AddCircle(center, size.X * 0.5f, M3.U32(s.Outline, 0.8f), 32, 1f * M3.Scale);
		}

		M3Draw.IconCentered(drawList, icon, min, max, content);

		if (hovered)
		{
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
			if (!string.IsNullOrEmpty(tooltip))
			{
				ImGui.SetTooltip(tooltip);
			}
		}

		return pressed;
	}

	#endregion

	#region Segmented buttons

	public static float SegmentedHeight => 32f * M3.Scale;

	/// <summary>Minimum width a segmented group needs to show every label in full.</summary>
	public static float SegmentedWidth(ReadOnlySpan<M3Segment> segments)
	{
		var scale = M3.Scale;
		var widest = 0f;

		foreach (var segment in segments)
		{
			var width = ImGui.CalcTextSize(segment.Label).X + (14f * scale * 2f);
			if (segment.Icon != FontAwesomeIcon.None)
			{
				width += M3Draw.MeasureIcon(segment.Icon).X + (6f * scale);
			}

			widest = MathF.Max(widest, width);
		}

		return widest * segments.Length;
	}

	/// <summary>
	/// A Material segmented button: one pill split into mutually exclusive options, the active one
	/// filled. Returns the newly selected index, or -1 when the selection did not change.
	/// </summary>
	public static int SegmentedButtons(string id, ReadOnlySpan<M3Segment> segments, int selectedIndex, float? width = null)
	{
		if (segments.Length == 0)
		{
			return -1;
		}

		var s = M3.Scheme;
		var scale = M3.Scale;
		var height = SegmentedHeight;
		var total = width ?? SegmentedWidth(segments);
		var segmentWidth = total / segments.Length;
		var rounding = height * 0.5f;
		var origin = ImGui.GetCursorScreenPos();
		var drawList = ImGui.GetWindowDrawList();
		var result = -1;

		using var idScope = ImRaii.PushId(id);

		for (var i = 0; i < segments.Length; i++)
		{
			var segment = segments[i];
			var selected = i == selectedIndex;
			var segmentMin = new Vector2(origin.X + (segmentWidth * i), origin.Y);

			ImGui.SetCursorScreenPos(segmentMin);
			if (ImGui.InvisibleButton($"##segment{i}", new Vector2(segmentWidth, height)) && !selected)
			{
				result = i;
			}

			var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
			var held = ImGui.IsItemActive();
			var segmentMax = segmentMin + new Vector2(segmentWidth, height);
			var tone = segment.Accent ?? s.Primary;

			// Only the outer edges of the group are rounded, so the segments read as one control.
			var corners = segments.Length == 1 ? ImDrawFlags.RoundCornersAll
				: i == 0 ? ImDrawFlags.RoundCornersLeft
				: i == segments.Length - 1 ? ImDrawFlags.RoundCornersRight
				: ImDrawFlags.RoundCornersNone;

			if (selected)
			{
				drawList.AddRectFilled(segmentMin, segmentMax, M3.U32(s.SecondaryContainer, 0.95f), rounding, corners);
			}

			if (hovered || held)
			{
				drawList.AddRectFilled(segmentMin, segmentMax,
					M3.U32(selected ? tone : s.OnSurface, held ? M3.StatePressed : M3.StateHover), rounding, corners);
				ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
			}

			var content = selected ? tone : M3.Alpha(s.OnSurfaceVariant, hovered ? 1f : 0.85f);
			var iconWidth = segment.Icon == FontAwesomeIcon.None ? 0f : M3Draw.MeasureIcon(segment.Icon).X;
			var gap = segment.Icon == FontAwesomeIcon.None ? 0f : 6f * scale;
			var textSize = ImGui.CalcTextSize(segment.Label);
			var cursorX = segmentMin.X + ((segmentWidth - iconWidth - gap - textSize.X) * 0.5f);

			if (segment.Icon != FontAwesomeIcon.None)
			{
				M3Draw.Icon(drawList, segment.Icon,
					new Vector2(cursorX, segmentMin.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f)), content);
				cursorX += iconWidth + gap;
			}

			drawList.AddText(new Vector2(cursorX, segmentMin.Y + ((height - textSize.Y) * 0.5f)), M3.U32(content), segment.Label);

			// Dividers sit between segments only.
			if (i > 0)
			{
				drawList.AddLine(segmentMin + new Vector2(0f, 1f * scale), new Vector2(segmentMin.X, segmentMax.Y - (1f * scale)),
					M3.U32(s.Outline, 0.55f), 1f * scale);
			}

			if (hovered && !string.IsNullOrEmpty(segment.Tooltip))
			{
				ImguiTooltips.ShowTooltip(segment.Tooltip);
			}
		}

		drawList.AddRect(origin, origin + new Vector2(total, height), M3.U32(s.Outline, 0.65f),
			rounding, ImDrawFlags.RoundCornersAll, 1f * scale);

		ImGui.SetCursorScreenPos(origin);
		ImGui.Dummy(new Vector2(total, height));
		return result;
	}

	#endregion

	#region Chips and pills

	public static float ChipHeight => 32f * M3.Scale;

	public static float ChipWidth(string label, FontAwesomeIcon icon = FontAwesomeIcon.None)
	{
		var scale = M3.Scale;
		var width = ImGui.CalcTextSize(label).X + (16f * scale * 2f);
		if (icon != FontAwesomeIcon.None)
		{
			width += M3Draw.MeasureIcon(icon).X + (8f * scale);
		}

		return width;
	}

	public static bool Chip(string id, string label, bool selected, FontAwesomeIcon icon = FontAwesomeIcon.None, string? tooltip = null, Vector4? accent = null)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var height = ChipHeight;
		var size = new Vector2(ChipWidth(label, icon), height);
		var tone = accent ?? s.Primary;

		var pressed = ImGui.InvisibleButton(id, size);
		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var held = ImGui.IsItemActive();
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var drawList = ImGui.GetWindowDrawList();
		var rounding = M3.ShapeSmall;

		var container = selected ? s.SecondaryContainer : M3.Alpha(s.Surface, 0f);
		var content = selected ? s.OnSecondaryContainer : s.OnSurfaceVariant;
		if (hovered || held)
		{
			container = container.W > 0f
				? M3.StateLayer(container, content, hovered, held)
				: M3.Alpha(s.OnSurface, held ? M3.StatePressed : M3.StateHover);
		}

		M3Draw.Container(drawList, min, max, container, rounding, selected ? null : M3.Alpha(s.Outline, 0.8f), 1f);

		var iconWidth = icon == FontAwesomeIcon.None ? 0f : M3Draw.MeasureIcon(icon).X;
		var gap = icon == FontAwesomeIcon.None ? 0f : 8f * scale;
		var textSize = ImGui.CalcTextSize(label);
		var cursorX = min.X + ((size.X - (iconWidth + gap + textSize.X)) * 0.5f);

		if (icon != FontAwesomeIcon.None)
		{
			using (ImRaii.PushFont(UiBuilder.IconFont))
			{
				var glyph = icon.ToIconString();
				drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(),
					new Vector2(cursorX, min.Y + ((height - ImGui.CalcTextSize(glyph).Y) * 0.5f)),
					M3.U32(selected ? tone : content), glyph);
			}

			cursorX += iconWidth + gap;
		}

		drawList.AddText(new Vector2(cursorX, min.Y + ((height - textSize.Y) * 0.5f)), M3.U32(content), label);

		if (hovered)
		{
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
			if (!string.IsNullOrEmpty(tooltip))
			{
				ImGui.SetTooltip(tooltip);
			}
		}

		return pressed;
	}

	public static Vector2 PillSize(string label, FontAwesomeIcon icon = FontAwesomeIcon.None)
	{
		var scale = M3.Scale;
		var width = ImGui.CalcTextSize(label).X + (12f * scale * 2f);
		width += icon == FontAwesomeIcon.None
			? (6f * scale) + (6f * scale)
			: M3Draw.MeasureIcon(icon).X + (6f * scale);
		return new Vector2(width, 24f * M3.Scale);
	}

	public static bool Pill(string id, string label, Vector4 accent, FontAwesomeIcon icon = FontAwesomeIcon.None, string? tooltip = null, bool interactive = false)
	{
		var scale = M3.Scale;
		var size = PillSize(label, icon);

		var clicked = false;
		if (interactive)
		{
			clicked = ImGui.InvisibleButton(id, size);
		}
		else
		{
			ImGui.Dummy(size);
		}

		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var drawList = ImGui.GetWindowDrawList();
		var rounding = size.Y * 0.5f;

		drawList.AddRectFilled(min, max, M3.U32(accent, hovered ? 0.20f : 0.13f), rounding);
		drawList.AddRect(min, max, M3.U32(accent, hovered ? 0.62f : 0.40f), rounding, ImDrawFlags.None, 1f * scale);

		var cursorX = min.X + (12f * scale);
		if (icon == FontAwesomeIcon.None)
		{
			var radius = 3f * scale;
			drawList.AddCircleFilled(new Vector2(cursorX + radius, min.Y + (size.Y * 0.5f)), radius, M3.U32(accent), 12);
			cursorX += (radius * 2f) + (6f * scale);
		}
		else
		{
			using (ImRaii.PushFont(UiBuilder.IconFont))
			{
				var glyph = icon.ToIconString();
				var glyphSize = ImGui.CalcTextSize(glyph);
				drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(),
					new Vector2(cursorX, min.Y + ((size.Y - glyphSize.Y) * 0.5f)), M3.U32(accent), glyph);
				cursorX += glyphSize.X + (6f * scale);
			}
		}

		var textSize = ImGui.CalcTextSize(label);
		drawList.AddText(new Vector2(cursorX, min.Y + ((size.Y - textSize.Y) * 0.5f)), M3.U32(M3.Scheme.OnSurface, 0.95f), label);

		if (hovered && !string.IsNullOrEmpty(tooltip))
		{
			ImGui.SetTooltip(tooltip);
		}

		return clicked;
	}

	#endregion

	#region Sliders

	/// <summary>
	/// Pushes ImGui's slider chrome fully transparent, so the native widget still supplies
	/// ctrl-click editing, keyboard input and drag capture while the track is painted by hand.
	/// </summary>
	private static ImRaii.ColorDisposable PushInvisibleSliderChrome()
	{
		return ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0f))
			.Push(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f))
			.Push(ImGuiCol.FrameBgActive, new Vector4(0f, 0f, 0f, 0f))
			.Push(ImGuiCol.SliderGrab, new Vector4(0f, 0f, 0f, 0f))
			.Push(ImGuiCol.SliderGrabActive, new Vector4(0f, 0f, 0f, 0f))
			.Push(ImGuiCol.Text, new Vector4(0f, 0f, 0f, 0f));
	}

	public static bool Slider(string id, ref float value, float min, float max, string displayValue, float width)
	{
		var scale = M3.Scale;
		bool changed;

		using (PushInvisibleSliderChrome())
		using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(0f, 10f) * scale))
		{
			ImGui.SetNextItemWidth(width);
			changed = ImGui.SliderFloat(id, ref value, min, max, string.Empty, ImGuiSliderFlags.NoRoundToFormat);
		}

		DrawSliderVisual(value, min, max, displayValue, M3.Scheme, scale);
		return changed;
	}

	public static bool SliderInt(string id, ref int value, int min, int max, string displayValue, float width)
	{
		var scale = M3.Scale;
		bool changed;

		using (PushInvisibleSliderChrome())
		using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(0f, 10f) * scale))
		{
			ImGui.SetNextItemWidth(width);
			changed = ImGui.SliderInt(id, ref value, min, max, string.Empty);
		}

		DrawSliderVisual(value, min, max, displayValue, M3.Scheme, scale);
		return changed;
	}

	private static void DrawSliderVisual(float value, float min, float max, string displayValue, M3Scheme s, float scale)
	{
		var itemMin = ImGui.GetItemRectMin();
		var itemMax = ImGui.GetItemRectMax();
		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var active = ImGui.IsItemActive();
		var drawList = ImGui.GetWindowDrawList();

		var centerY = (itemMin.Y + itemMax.Y) * 0.5f;
		var trackHeight = 4f * scale;
		var handleRadius = 10f * scale;
		var trackLeft = itemMin.X + handleRadius;
		var trackRight = itemMax.X - handleRadius;
		var fraction = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
		var handleX = float.Lerp(trackLeft, trackRight, fraction);

		drawList.AddRectFilled(
			new Vector2(trackLeft, centerY - (trackHeight * 0.5f)),
			new Vector2(trackRight, centerY + (trackHeight * 0.5f)),
			M3.U32(s.SurfaceContainerHighest), trackHeight);

		if (handleX > trackLeft)
		{
			drawList.AddRectFilled(
				new Vector2(trackLeft, centerY - (trackHeight * 0.5f)),
				new Vector2(handleX, centerY + (trackHeight * 0.5f)),
				M3.U32(s.Primary), trackHeight);
		}

		if (hovered || active)
		{
			drawList.AddCircleFilled(new Vector2(handleX, centerY), handleRadius + (8f * scale),
				M3.U32(s.Primary, active ? M3.StatePressed : M3.StateHover), 24);
		}

		drawList.AddCircleFilled(new Vector2(handleX, centerY), handleRadius, M3.U32(s.Primary), 24);
		drawList.AddCircleFilled(new Vector2(handleX, centerY), handleRadius * 0.45f, M3.U32(s.OnPrimary, 0.9f), 16);

		if (string.IsNullOrEmpty(displayValue))
		{
			return;
		}

		// The readout rides just past the end of the track, plus a bubble above the handle while dragging.
		var textSize = ImGui.CalcTextSize(displayValue);
		if (active)
		{
			var padding = new Vector2(8f, 4f) * scale;
			var bubbleMin = new Vector2(handleX - (textSize.X * 0.5f) - padding.X, itemMin.Y - textSize.Y - (padding.Y * 2f) - (6f * scale));
			var bubbleMax = bubbleMin + textSize + (padding * 2f);
			drawList.AddRectFilled(bubbleMin, bubbleMax, M3.U32(s.InverseSurface), M3.ShapeSmall);
			drawList.AddText(bubbleMin + padding, M3.U32(s.InverseOnSurface), displayValue);
		}

		drawList.AddText(new Vector2(itemMax.X + (10f * scale), centerY - (textSize.Y * 0.5f)),
			M3.U32(s.OnSurfaceVariant), displayValue);
	}

	/// <summary>Width taken by the value readout drawn to the right of a slider.</summary>
	public static float SliderValueGutter(string longestValue)
	{
		return ImGui.CalcTextSize(longestValue).X + (14f * M3.Scale);
	}

	#endregion

	#region Full-width setting rows

	private static (string Label, string Id) SplitLabel(string label)
	{
		// ImGui's own rules: "###" replaces the identity with whatever follows it, while "##" only
		// hides the display text and keeps the *whole* string as the identity. Keeping the visible
		// half out of the id would collapse every control that shares a suffix — several settings
		// on the same action, say — into one id, so they would fight over ImGui's interaction and
		// animation state.
		var explicitId = label.IndexOf("###", StringComparison.Ordinal);
		if (explicitId >= 0)
		{
			var trailing = label[(explicitId + 3)..];
			return (label[..explicitId], string.IsNullOrEmpty(trailing) ? label : trailing);
		}

		var hidden = label.IndexOf("##", StringComparison.Ordinal);
		return hidden < 0 ? (label, label) : (label[..hidden], label);
	}

	/// <summary>
	/// Renders a value for a slider readout. Accepts both printf specifiers, as ImGui's own drag
	/// and slider widgets take, and strings the caller already formatted.
	/// </summary>
	private static string FormatValue(string format, float value)
	{
		if (string.IsNullOrEmpty(format))
		{
			return value.ToString("0.##");
		}

		// Find the conversion specifier, stepping over "%%" escapes.
		var percent = -1;
		for (var i = 0; i < format.Length - 1; i++)
		{
			if (format[i] != '%')
			{
				continue;
			}

			if (format[i + 1] == '%')
			{
				i++;
				continue;
			}

			percent = i;
			break;
		}

		if (percent < 0)
		{
			return Unescape(format);
		}

		var cursor = percent + 1;
		var digits = 0;
		var hasDigits = false;

		if (cursor < format.Length && format[cursor] == '.')
		{
			cursor++;
			while (cursor < format.Length && char.IsAsciiDigit(format[cursor]))
			{
				digits = (digits * 10) + (format[cursor] - '0');
				cursor++;
				hasDigits = true;
			}
		}

		if (cursor >= format.Length)
		{
			return Unescape(format);
		}

		var rendered = format[cursor] switch
		{
			'f' => value.ToString($"F{(hasDigits ? digits : 3)}"),
			'd' or 'i' => ((int)value).ToString(),
			_ => null,
		};

		return rendered == null
			? Unescape(format)
			: Unescape(string.Concat(format.AsSpan(0, percent), rendered, format.AsSpan(cursor + 1)));
	}

	/// <summary>
	/// Collapses printf's "%%" escape to a single percent sign. ImGui would do this while
	/// formatting; these readouts are drawn directly, so it has to happen here.
	/// </summary>
	private static string Unescape(string text)
	{
		return text.Contains("%%", StringComparison.Ordinal)
			? text.Replace("%%", "%", StringComparison.Ordinal)
			: text;
	}

	public static bool RowSwitch(string label, ref bool value, string? supporting = null)
	{
		var (display, id) = SplitLabel(label);
		var row = M3SettingRow.Begin(display, supporting, SwitchSize());
		ImGui.SetCursorScreenPos(row.ControlPosition);
		var changed = Switch($"##{id}_switch", ref value);
		M3SettingRow.End(row);
		return changed;
	}

	public static bool RowDragFloat(string label, ref float value, float min, float max, string format)
	{
		var (display, id) = SplitLabel(label);
		var trackWidth = 150f * M3.Scale;
		var readout = FormatValue(format, value);
		var controlSize = new Vector2(trackWidth + SliderValueGutter(FormatValue(format, max)), ButtonHeight);

		var row = M3SettingRow.Begin(display, null, controlSize);
		ImGui.SetCursorScreenPos(row.ControlPosition);
		var changed = Slider($"##{id}_slider", ref value, min, max, readout, trackWidth);
		M3SettingRow.End(row);
		return changed;
	}

	public static bool RowDragInt(string label, ref int value, int min, int max)
	{
		var (display, id) = SplitLabel(label);
		var trackWidth = 150f * M3.Scale;
		var controlSize = new Vector2(trackWidth + SliderValueGutter(max.ToString()), ButtonHeight);

		var row = M3SettingRow.Begin(display, null, controlSize);
		ImGui.SetCursorScreenPos(row.ControlPosition);
		var changed = SliderInt($"##{id}_slider", ref value, min, max, value.ToString(), trackWidth);
		M3SettingRow.End(row);
		return changed;
	}

	#endregion

	#region Text fields

	public static bool SearchField(string id, string hint, ref string text, float width, int maxLength = 128)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var height = 40f * scale;
		var origin = ImGui.GetCursorScreenPos();
		var drawList = ImGui.GetWindowDrawList();
		var min = origin;
		var max = origin + new Vector2(width, height);
		var hoveringField = ImGui.IsMouseHoveringRect(min, max);

		drawList.AddRectFilled(min, max, M3.U32(s.SurfaceContainerHigh, hoveringField ? 1f : 0.92f), height * 0.5f);

		var iconPadding = 14f * scale;
		M3Draw.Icon(drawList, FontAwesomeIcon.Search,
			new Vector2(min.X + iconPadding, min.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f)),
			M3.Alpha(s.OnSurfaceVariant, 0.9f));

		var iconWidth = M3Draw.MeasureIcon(FontAwesomeIcon.Search).X;
		var fieldStart = min.X + iconPadding + iconWidth + (10f * scale);
		var hasText = !string.IsNullOrEmpty(text);
		var clearWidth = hasText ? 32f * scale : 0f;
		var fieldWidth = MathF.Max(24f * scale, max.X - fieldStart - (14f * scale) - clearWidth);

		ImGui.SetCursorScreenPos(new Vector2(fieldStart, min.Y + ((height - ImGui.GetFrameHeight()) * 0.5f)));
		ImGui.SetNextItemWidth(fieldWidth);

		bool changed;
		using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0f))
			.Push(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f))
			.Push(ImGuiCol.FrameBgActive, new Vector4(0f, 0f, 0f, 0f)))
		{
			changed = ImGui.InputTextWithHint(id, hint, ref text, maxLength, ImGuiInputTextFlags.AutoSelectAll);
		}

		if (hasText)
		{
			ImGui.SetCursorScreenPos(new Vector2(max.X - clearWidth - (6f * scale), min.Y + ((height - (26f * scale)) * 0.5f)));
			if (IconButton($"{id}_clear", FontAwesomeIcon.Times, "Clear search", diameter: 26f * scale))
			{
				text = string.Empty;
				changed = true;
			}
		}

		ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
		ImGui.Dummy(new Vector2(width, 0f));
		return changed;
	}

	#endregion

	#region Structure

	public static void Divider(float verticalPadding = 8f)
	{
		var scale = M3.Scale;
		var pad = verticalPadding * scale;
		ImGui.Dummy(new Vector2(0f, pad));
		var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
		var origin = ImGui.GetCursorScreenPos();
		ImGui.GetWindowDrawList().AddLine(origin, origin + new Vector2(width, 0f), M3.U32(M3.Scheme.OutlineVariant, 0.7f), 1f * scale);
		ImGui.Dummy(new Vector2(width, pad));
	}

	public static void SectionLabel(string text, Vector4? accent = null)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var tone = accent ?? s.Primary;
		var label = text.ToUpperInvariant();

		using var font = ImRaii.PushFont(M3.LabelSmall);
		var textSize = ImGui.CalcTextSize(label);
		var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
		var height = textSize.Y + (14f * scale);

		ImGui.Dummy(new Vector2(width, height));
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var drawList = ImGui.GetWindowDrawList();
		var textY = min.Y + (10f * scale);
		var lineY = textY + (textSize.Y * 0.5f);

		drawList.AddText(new Vector2(min.X, textY), M3.U32(tone, 0.95f), label);

		var lineStart = min.X + textSize.X + (10f * scale);
		if (max.X > lineStart)
		{
			drawList.AddLine(new Vector2(lineStart, lineY), new Vector2(max.X, lineY), M3.U32(s.OutlineVariant, 0.55f), 1f * scale);
		}
	}

	public static bool Banner(string id, string message, M3Severity severity, FontAwesomeIcon icon, string? actionLabel = null, string? tooltip = null)
	{
		return Banner(id, message, severity, icon, out _, actionLabel, tooltip);
	}

	/// <summary>
	/// As <see cref="Banner(string, string, M3Severity, FontAwesomeIcon, string, string)"/>, but also
	/// reports whether the pointer is over the banner — the trailing item is the action button, so
	/// callers cannot test this themselves.
	/// </summary>
	public static bool Banner(string id, string message, M3Severity severity, FontAwesomeIcon icon, out bool hovered, string? actionLabel = null, string? tooltip = null)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var accent = M3.Severity(severity);
		var container = M3.SeverityContainer(severity);
		var padding = new Vector2(16f, 12f) * scale;
		var width = MathF.Max(64f * scale, ImGui.GetContentRegionAvail().X);

		var iconWidth = M3Draw.MeasureIcon(icon).X + (12f * scale);
		var actionWidth = string.IsNullOrEmpty(actionLabel) ? 0f : ButtonWidth(FontAwesomeIcon.None, actionLabel) + (12f * scale);
		var textWidth = MathF.Max(32f * scale, width - (padding.X * 2f) - iconWidth - actionWidth);
		var textSize = ImGui.CalcTextSize(message, false, textWidth);
		var height = MathF.Max(textSize.Y, string.IsNullOrEmpty(actionLabel) ? 0f : ButtonHeight) + (padding.Y * 2f);

		var origin = ImGui.GetCursorScreenPos();
		var min = origin;
		var max = origin + new Vector2(width, height);
		var drawList = ImGui.GetWindowDrawList();
		hovered = ImGui.IsMouseHoveringRect(min, max, false) && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);

		drawList.AddRectFilled(min, max, M3.U32(container, 0.55f), M3.ShapeMedium);
		M3Draw.AccentRail(drawList, min.X + (2f * scale), 3f * scale, min.Y + (6f * scale), max.Y - (6f * scale), M3.Alpha(accent, 0.9f),
			M3Draw.ResolveFade(40f, 0.35f, height));

		M3Draw.Icon(drawList, icon, new Vector2(min.X + padding.X, min.Y + padding.Y + (ImGui.GetTextLineHeight() * 0.1f)), accent);
		_ = M3Draw.WrappedText(message, new Vector2(min.X + padding.X + iconWidth, min.Y + padding.Y), textWidth, M3.Alpha(s.OnSurface, 0.94f));

		var clicked = false;
		if (!string.IsNullOrEmpty(actionLabel))
		{
			ImGui.SetCursorScreenPos(new Vector2(max.X - padding.X - actionWidth + (12f * scale), min.Y + ((height - ButtonHeight) * 0.5f)));
			clicked = Button($"{id}_action", actionLabel, M3ButtonStyle.Text);
		}

		ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
		ImGui.Dummy(new Vector2(width, 0f));

		if (hovered && !string.IsNullOrEmpty(tooltip))
		{
			ImGui.SetTooltip(tooltip);
		}

		return clicked;
	}

	#endregion

	#region Select

	public static float ComboHeight => 38f * M3.Scale;

	public static bool Combo(string id, ref int index, IReadOnlyList<string> items, float width, string? emptyText = null)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var height = ComboHeight;
		var popupId = $"{id}_menu";

		var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var held = ImGui.IsItemActive();
		var open = ImGui.IsPopupOpen(popupId);
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var drawList = ImGui.GetWindowDrawList();
		var rounding = M3.ShapeSmall;

		var fill = M3.Alpha(s.SurfaceContainerHighest, 0.55f);
		if (hovered || held)
		{
			fill = M3.StateLayer(fill, s.OnSurface, hovered, held);
		}

		drawList.AddRectFilled(min, max, M3.U32(fill), rounding);
		drawList.AddRect(min, max, M3.U32(open ? s.Primary : s.Outline, open ? 1f : 0.75f), rounding, ImDrawFlags.None, (open ? 2f : 1f) * scale);

		var chevronWidth = 24f * scale;
		var label = index >= 0 && index < items.Count ? items[index] : emptyText ?? string.Empty;
		var textWidth = MathF.Max(8f * scale, width - (12f * scale * 2f) - chevronWidth);
		var display = M3Navigation.Truncate(label, textWidth);
		var textSize = ImGui.CalcTextSize(display);
		drawList.AddText(new Vector2(min.X + (12f * scale), min.Y + ((height - textSize.Y) * 0.5f)), M3.U32(s.OnSurface, 0.95f), display);

		var chevronCenter = new Vector2(max.X - (16f * scale), min.Y + (height * 0.5f));
		var arm = 4.5f * scale;
		var chevronColor = M3.U32(s.OnSurfaceVariant, hovered || open ? 1f : 0.8f);
		drawList.AddLine(chevronCenter + new Vector2(-arm, -arm * 0.5f), chevronCenter + new Vector2(0f, arm * 0.6f), chevronColor, 2f * scale);
		drawList.AddLine(chevronCenter + new Vector2(0f, arm * 0.6f), chevronCenter + new Vector2(arm, -arm * 0.5f), chevronColor, 2f * scale);

		if (hovered)
		{
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
		}

		if (clicked)
		{
			ImGui.OpenPopup(popupId);
		}

		var changed = false;
		ImGui.SetNextWindowSizeConstraints(new Vector2(MathF.Max(width, 160f * scale), 0f), new Vector2(560f * scale, 420f * scale));
		using var popup = ImRaii.Popup(popupId);
		if (popup)
		{
			for (var i = 0; i < items.Count; i++)
			{
				if (MenuItem($"{popupId}_{i}", items[i], i == index))
				{
					index = i;
					changed = true;
					ImGui.CloseCurrentPopup();
				}
			}
		}

		return changed;
	}

	public static bool MenuItem(string id, string label, bool selected, FontAwesomeIcon icon = FontAwesomeIcon.None)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var height = 34f * scale;
		var checkWidth = 24f * scale;
		var textSize = ImGui.CalcTextSize(label);
		var width = MathF.Max(ImGui.GetContentRegionAvail().X, checkWidth + textSize.X + (24f * scale));

		var pressed = ImGui.InvisibleButton(id, new Vector2(width, height));
		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var held = ImGui.IsItemActive();
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var drawList = ImGui.GetWindowDrawList();

		if (selected)
		{
			drawList.AddRectFilled(min, max, M3.U32(s.SecondaryContainer, 0.8f), M3.ShapeSmall);
		}

		if (hovered || held)
		{
			drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, held ? M3.StatePressed : M3.StateHover), M3.ShapeSmall);
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
		}

		if (selected)
		{
			M3Draw.Icon(drawList, FontAwesomeIcon.Check,
				new Vector2(min.X + (8f * scale), min.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f)), s.Primary);
		}
		else if (icon != FontAwesomeIcon.None)
		{
			M3Draw.Icon(drawList, icon,
				new Vector2(min.X + (8f * scale), min.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f)), s.OnSurfaceVariant);
		}

		drawList.AddText(
			new Vector2(min.X + checkWidth + (8f * scale), min.Y + ((height - textSize.Y) * 0.5f)),
			M3.U32(selected ? s.OnSecondaryContainer : s.OnSurface, 0.95f), label);

		return pressed;
	}

	#endregion

	#region Misc

	public static bool ColorSwatch(string id, ref Vector4 color)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var diameter = 28f * scale;

		var clicked = ImGui.InvisibleButton(id, Vector2.One * diameter);
		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var min = ImGui.GetItemRectMin();
		var center = min + (Vector2.One * diameter * 0.5f);
		var drawList = ImGui.GetWindowDrawList();

		drawList.AddCircleFilled(center, diameter * 0.5f, M3.U32(color with { W = 1f }), 32);
		drawList.AddCircle(center, diameter * 0.5f, M3.U32(s.Outline, hovered ? 1f : 0.6f), 32, 1.5f * scale);

		if (hovered)
		{
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
		}

		var popupId = $"{id}_picker";
		if (clicked)
		{
			ImGui.OpenPopup(popupId);
		}

		var changed = false;
		using var popup = ImRaii.Popup(popupId);
		if (popup)
		{
			changed = ImGui.ColorPicker4($"##{popupId}_picker", ref color, ImGuiColorEditFlags.AlphaBar);
		}

		return changed;
	}

	#endregion
}
