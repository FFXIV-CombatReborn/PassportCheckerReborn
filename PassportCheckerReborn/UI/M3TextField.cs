using Dalamud.Interface.Utility.Raii;
using System;
using System.Numerics;

namespace PassportCheckerReborn.UI;

/// <summary>
/// A Material filled text field. ImGui's own input widget still does the editing (selection,
/// clipboard, IME) and covers the whole container, so a click anywhere inside focuses it; only the
/// painting is replaced, the same split <see cref="M3Widgets.SearchField"/> uses.
/// </summary>
internal static class M3TextField
{
    public static float Height => 40f * M3.Scale;

    public static bool Draw(string id, string hint, ref string text, float width, int maxLength = 256, bool password = false)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var height = Height;
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(min, max) && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);

        // The container goes down first so the input's text lands on top of it.
        var fill = hovered ? M3.StateLayer(s.SurfaceContainerHighest, s.OnSurface, true, false) : s.SurfaceContainerHighest;
        drawList.AddRectFilled(min, max, M3.U32(fill), M3.ShapeExtraSmall, ImDrawFlags.RoundCornersTop);

        // Vertical frame padding that makes the input exactly as tall as the container.
        var padding = new Vector2(12f * scale, MathF.Max(0f, (height - ImGui.GetTextLineHeight()) * 0.5f));
        var transparent = new Vector4(0f, 0f, 0f, 0f);
        var flags = password ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None;

        bool changed;
        using (ImRaii.PushId(id))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, transparent)
            .Push(ImGuiCol.FrameBgHovered, transparent)
            .Push(ImGuiCol.FrameBgActive, transparent))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, padding))
        {
            ImGui.SetNextItemWidth(width);
            changed = ImGui.InputTextWithHint("##field", hint, ref text, maxLength, flags);
        }

        // The active indicator along the bottom edge thickens and takes the primary colour on focus.
        var focused = ImGui.IsItemActive();
        var thickness = (focused ? 2f : 1f) * scale;
        var indicator = focused ? s.Primary : M3.Alpha(s.OnSurfaceVariant, hovered ? 1f : 0.7f);
        drawList.AddRectFilled(new Vector2(min.X, max.Y - thickness), max, M3.U32(indicator));

        return changed;
    }
}
