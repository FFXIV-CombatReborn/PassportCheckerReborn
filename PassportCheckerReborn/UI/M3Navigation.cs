using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace PassportCheckerReborn.UI;

/// <summary>One entry in the navigation rail or drawer.</summary>
internal readonly record struct M3NavItem(
	string Id,
	string Label,
	FontAwesomeIcon Icon,
	bool Selected,
	string? Tooltip = null,
	Vector4? Accent = null,
	string? Badge = null,
	bool SeparatorAfter = false);

/// <summary>
/// Material navigation: a full-width drawer when there is room for labels, collapsing to an icon
/// rail when the side column is narrower than <see cref="DrawerBreakpoint"/>.
/// </summary>
internal static class M3Navigation
{
	/// <summary>Minimum column width at which labels are shown.</summary>
	public const float DrawerBreakpoint = 128f;

	private static float DrawerRowHeight => 48f * M3.Scale;
	private static float RailItemHeight => 58f * M3.Scale;

	/// <summary>Draws the navigation list. Returns the id of the item clicked this frame, if any.</summary>
	public static string? Draw(string id, IReadOnlyList<M3NavItem> items, bool expanded)
	{
		string? clicked = null;

		using var idScope = ImRaii.PushId(id);
		for (var i = 0; i < items.Count; i++)
		{
			var item = items[i];
			if (expanded ? DrawerItem(item) : RailItem(item))
			{
				clicked = item.Id;
			}

			if (item.SeparatorAfter)
			{
				M3Widgets.Divider(6f);
			}
		}

		return clicked;
	}

	private static bool DrawerItem(in M3NavItem item)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var tone = item.Accent ?? s.Primary;
		var width = MathF.Max(32f * scale, ImGui.GetContentRegionAvail().X);
		var height = DrawerRowHeight;

		var pressed = ImGui.InvisibleButton($"nav_{item.Id}", new Vector2(width, height));
		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var held = ImGui.IsItemActive();
		var min = ImGui.GetItemRectMin();
		var max = ImGui.GetItemRectMax();
		var drawList = ImGui.GetWindowDrawList();
		var rounding = height * 0.5f;

		var selection = M3Motion.Approach($"nav_sel_{item.Id}", item.Selected ? 1f : 0f, M3Motion.EmphasisedDuration);

		if (selection > 0.01f)
		{
			drawList.AddRectFilled(min, max, M3.U32(s.SecondaryContainer, 0.95f * selection), rounding);
		}

		if (hovered || held)
		{
			drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, held ? M3.StatePressed : M3.StateHover), rounding);
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
		}

		var iconColor = M3ColorMath.Mix(s.OnSurfaceVariant, tone, selection);
		var textColor = M3ColorMath.Mix(M3.Alpha(s.OnSurfaceVariant, 0.95f), s.OnSecondaryContainer, selection);

		var iconSize = M3Draw.MeasureIcon(item.Icon);
		var iconX = min.X + (16f * scale);
		M3Draw.Icon(drawList, item.Icon, new Vector2(iconX, min.Y + ((height - iconSize.Y) * 0.5f)), iconColor);

		var textX = iconX + MathF.Max(iconSize.X, 18f * scale) + (14f * scale);
		var available = MathF.Max(16f * scale, max.X - textX - (12f * scale));
		var label = Truncate(item.Label, available);
		var labelSize = ImGui.CalcTextSize(label);
		drawList.AddText(new Vector2(textX, min.Y + ((height - labelSize.Y) * 0.5f)), M3.U32(textColor), label);

		if (!string.IsNullOrEmpty(item.Badge))
		{
			using var badgeFont = ImRaii.PushFont(M3.LabelSmall);
			var badgeSize = ImGui.CalcTextSize(item.Badge);
			var padding = new Vector2(6f, 2f) * scale;
			var badgeMax = new Vector2(max.X - (12f * scale), min.Y + ((height + badgeSize.Y + (padding.Y * 2f)) * 0.5f));
			var badgeMin = badgeMax - badgeSize - (padding * 2f);
			drawList.AddRectFilled(badgeMin, badgeMax, M3.U32(s.Error, 0.9f), M3.ShapeFull);
			drawList.AddText(badgeMin + padding, M3.U32(s.OnError), item.Badge);
		}

		if (hovered && !string.IsNullOrEmpty(item.Tooltip))
		{
			ImguiTooltips.ShowTooltip(item.Tooltip);
		}

		return pressed;
	}

	private static bool RailItem(in M3NavItem item)
	{
		var s = M3.Scheme;
		var scale = M3.Scale;
		var tone = item.Accent ?? s.Primary;
		var width = MathF.Max(32f * scale, ImGui.GetContentRegionAvail().X);
		var height = RailItemHeight;

		var pressed = ImGui.InvisibleButton($"nav_{item.Id}", new Vector2(width, height));
		var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
		var held = ImGui.IsItemActive();
		var min = ImGui.GetItemRectMin();
		var drawList = ImGui.GetWindowDrawList();
		var selection = M3Motion.Approach($"nav_sel_{item.Id}", item.Selected ? 1f : 0f, M3Motion.EmphasisedDuration);

		// The rail indicator is a 56x32 pill sitting behind the icon.
		var indicatorSize = new Vector2(MathF.Min(56f * scale, width - (4f * scale)), 32f * scale);
		var indicatorMin = new Vector2(min.X + ((width - indicatorSize.X) * 0.5f), min.Y + (4f * scale));
		var indicatorMax = indicatorMin + indicatorSize;

		if (selection > 0.01f)
		{
			var grow = (1f - selection) * 6f * scale;
			drawList.AddRectFilled(
				indicatorMin + new Vector2(grow, 0f),
				indicatorMax - new Vector2(grow, 0f),
				M3.U32(s.SecondaryContainer, 0.95f * selection), indicatorSize.Y * 0.5f);
		}

		if (hovered || held)
		{
			drawList.AddRectFilled(indicatorMin, indicatorMax,
				M3.U32(s.OnSurface, held ? M3.StatePressed : M3.StateHover), indicatorSize.Y * 0.5f);
			ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
		}

		var iconColor = M3ColorMath.Mix(s.OnSurfaceVariant, tone, selection);
		M3Draw.IconCentered(drawList, item.Icon, indicatorMin, indicatorMax, iconColor);

		using (ImRaii.PushFont(M3.LabelSmall))
		{
			var label = Truncate(item.Label, width - (4f * scale));
			var labelSize = ImGui.CalcTextSize(label);
			var textColor = M3ColorMath.Mix(M3.Alpha(s.OnSurfaceVariant, 0.9f), s.OnSurface, selection);
			drawList.AddText(
				new Vector2(min.X + ((width - labelSize.X) * 0.5f), indicatorMax.Y + (4f * scale)),
				M3.U32(textColor), label);
		}

		if (hovered)
		{
			var tip = string.IsNullOrEmpty(item.Tooltip) ? item.Label : $"{item.Label}\n \n{item.Tooltip}";
			ImguiTooltips.ShowTooltip(tip);
		}

		return pressed;
	}

	/// <summary>Clips a label to the available width, appending an ellipsis when it does not fit.</summary>
	public static string Truncate(string text, float maxWidth)
	{
		if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth)
		{
			return text;
		}

		var ellipsisWidth = ImGui.CalcTextSize("…").X;
		for (var length = text.Length - 1; length > 0; length--)
		{
			var candidate = text[..length];
			if (ImGui.CalcTextSize(candidate).X + ellipsisWidth <= maxWidth)
			{
				return candidate + "…";
			}
		}

		return "…";
	}
}
