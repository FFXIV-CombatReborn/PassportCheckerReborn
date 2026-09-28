using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace PassportCheckerReborn.UI;

/// <summary>
/// A small tonal tag that sits on a line of text, such as a status marker after a player's name.
/// It reserves the frame height and centres itself in it, so it lines up with text placed by
/// <c>ImGui.AlignTextToFramePadding</c> and with framed widgets on the same line.
/// </summary>
internal static class M3Badge
{
    public static void Draw(string label, Vector4 accent, string? tooltip = null)
    {
        var scale = M3.Scale;
        var frameHeight = ImGui.GetFrameHeight();
        var lineHeight = ImGui.GetTextLineHeight();
        var paddingX = 6f * scale;

        using (ImRaii.PushFont(M3.LabelSmall))
        {
            var textSize = ImGui.CalcTextSize(label);
            var pillSize = new Vector2(textSize.X + (paddingX * 2f), lineHeight);

            ImGui.Dummy(new Vector2(pillSize.X, frameHeight));
            var pillMin = ImGui.GetItemRectMin() + new Vector2(0f, (frameHeight - lineHeight) * 0.5f);
            var pillMax = pillMin + pillSize;

            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRectFilled(pillMin, pillMax, M3.U32(accent, 0.18f), M3.ShapeFull);
            drawList.AddRect(pillMin, pillMax, M3.U32(accent, 0.45f), M3.ShapeFull, ImDrawFlags.None, 1f * scale);
            drawList.AddText(new Vector2(pillMin.X + paddingX, pillMin.Y + ((lineHeight - textSize.Y) * 0.5f)), M3.U32(accent, 0.95f), label);
        }

        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
        {
            ImguiTooltips.ShowTooltip(tooltip);
        }
    }
}
