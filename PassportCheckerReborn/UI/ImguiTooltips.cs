using Dalamud.Interface.Utility.Raii;

namespace PassportCheckerReborn.UI;

internal static class ImguiTooltips
{
    // In multiples of the font size.
    private const float WrapEms = 28f;

    public static void HoveredTooltip(string? text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ShowTooltip(text);
        }
    }

    public static void ShowTooltip(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        using var tooltip = ImRaii.Tooltip();
        using var wrap = ImRaii.TextWrapPos(ImGui.GetFontSize() * WrapEms);
        ImGui.TextUnformatted(text);
    }
}
