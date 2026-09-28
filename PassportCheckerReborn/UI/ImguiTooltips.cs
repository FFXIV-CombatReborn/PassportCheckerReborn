using Dalamud.Interface.Utility.Raii;

namespace PassportCheckerReborn.UI;

/// <summary>Tooltips that wrap long text instead of stretching across the screen.</summary>
internal static class ImguiTooltips
{
    /// <summary>Wrap width, in multiples of the current font size.</summary>
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
