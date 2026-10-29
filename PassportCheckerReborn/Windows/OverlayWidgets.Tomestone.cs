using PassportCheckerReborn.Integrations.Tomestone;

namespace PassportCheckerReborn.Windows;

internal static partial class OverlayWidgets
{
    public static void TomestoneCell(TomestoneCharacterInfo? info)
    {
        var s = M3.Scheme;
        ImGui.AlignTextToFramePadding();

        if (info == null)
        {
            MutedText("Hidden Profile");
        }
        else if (info.NotFound)
        {
            MutedText("Hidden Profile");
        }
        else if (info.NoLogs)
        {
            MutedText("No Logs");
        }
        else if (info.TotalClears > 0)
        {
            ImGui.TextColored(s.Success, string.IsNullOrWhiteSpace(info.CompletionWeek) ? "Cleared" : $"Cleared ({info.CompletionWeek})");

            if (info.BestPercent.HasValue)
            {
                ImGui.SameLine();
                ImGui.TextColored(s.Success, $"Best: {info.BestPercent:F0}%");
            }
        }
        else if (!string.IsNullOrWhiteSpace(info.ProgPoint))
        {
            ImGui.TextColored(s.Warning, string.IsNullOrWhiteSpace(info.DisplayPercent) ? info.ProgPoint : $"{info.ProgPoint} ({info.DisplayPercent})");
        }
        else if (info.BestPercent.HasValue)
        {
            ImGui.TextColored(s.Warning, $"Best: {info.BestPercent:F0}%");
        }
        else if (info.ActivityHidden)
        {
            MutedText("Activity Hidden");
        }
        else if (info.LookupFailed)
        {
            MutedText("Lookup Failed");
        }
        else
        {
            MutedText("No Logs");
        }
    }
}
