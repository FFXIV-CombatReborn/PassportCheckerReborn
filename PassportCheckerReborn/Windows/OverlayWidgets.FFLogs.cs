using PassportCheckerReborn.Integrations.FFLogs;
using PassportCheckerReborn.Players;
using System;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

internal static partial class OverlayWidgets
{
    public static void FFLogsCell(EncounterParseResult? result, PartyMemberInfo member)
    {
        ImGui.AlignTextToFramePadding();

        if (result is null || !result.HasData)
        {
            DrawNoLogsWithAverage(result?.AverageParsePercent);
            return;
        }

        if (!result.IsEncounterSpecific)
        {
            if (result.BestParse.HasValue)
            {
                ImGui.TextColored(ParseColor(result.BestParse.Value), $"Average overall parse {result.BestParse.Value:F1}%");
            }
            else
            {
                MutedText("N/A");
            }

            return;
        }

        var isMultiPhase = result.Phase1TotalKills.HasValue
            || result.Phase2TotalKills.HasValue
            || result.Phase1BestParse.HasValue
            || result.Phase2BestParse.HasValue
            || result.Phase2LowestBossHpPct.HasValue;

        if (isMultiPhase)
        {
            DrawMultiPhase(result);
            DrawBestParseOnDifferentJob(result, member);
        }
        else if (result.TotalKills > 0)
        {
            if (result.CurrentJobBestParse.HasValue)
            {
                ImGui.TextColored(ParseColor(result.CurrentJobBestParse.Value),
                    $"Cleared {result.TotalKills}x {result.CurrentJobBestParse.Value:F0}%");
            }
            else
            {
                MutedText($"Cleared {result.TotalKills}x No Current Job Logs");
            }

            DrawBestParseOnDifferentJob(result, member);
        }
        else if (result.LowestBossHpPct.HasValue)
        {
            DrawBestPull(string.Empty, result.LowestBossHpPct.Value);
        }
        else
        {
            DrawNoLogsWithAverage(result.AverageParsePercent);
        }
    }

    public static Vector4 ParseColor(double percentile) => percentile switch
    {
        >= 99 => new Vector4(0.898f, 0.800f, 0.502f, 1.0f),
        >= 95 => new Vector4(0.894f, 0.510f, 0.200f, 1.0f),
        >= 75 => new Vector4(0.635f, 0.282f, 0.808f, 1.0f),
        >= 50 => new Vector4(0.118f, 0.392f, 1.000f, 1.0f),
        >= 25 => new Vector4(0.118f, 0.784f, 0.118f, 1.0f),
        _ => new Vector4(0.600f, 0.600f, 0.600f, 1.0f),
    };

    private static void DrawMultiPhase(EncounterParseResult result)
    {
        var s = M3.Scheme;
        var p1 = result.Phase1BestParse;
        var p2 = result.Phase2BestParse;

        if (result.TotalKills > 0 && p1.HasValue && p2.HasValue)
        {
            if (!result.CurrentJobBestParse.HasValue)
            {
                MutedText($"Cleared {result.TotalKills}x P1 {p1.Value:F0}% P2 {p2.Value:F0}%");
                return;
            }

            ImGui.TextColored(s.Success, $"Cleared {result.TotalKills}x");
            ImGui.SameLine();
            MutedText("P1");
            ImGui.SameLine();
            ImGui.TextColored(ParseColor(p1.Value), $"{p1.Value:F0}%");
            ImGui.SameLine();
            MutedText("P2");
            ImGui.SameLine();
            ImGui.TextColored(ParseColor(p2.Value), $"{p2.Value:F0}%");
            return;
        }

        if (p1.HasValue)
        {
            ImGui.TextColored(ParseColor(p1.Value), $"P1 {p1.Value:F0}%");
        }
        else if (result.Phase1LowestBossHpPct.HasValue)
        {
            DrawBestPull("P1 ", result.Phase1LowestBossHpPct.Value);
        }
        else
        {
            MutedText("P1 No logs");
        }

        ImGui.SameLine();

        if (result.Phase2LowestBossHpPct.HasValue)
        {
            DrawBestPull("P2 ", result.Phase2LowestBossHpPct.Value);
        }
        else if (p2.HasValue)
        {
            ImGui.TextColored(ParseColor(p2.Value), $"P2 {p2.Value:F0}%");
        }
        else
        {
            MutedText("P2 No logs");
        }
    }

    private static void DrawBestPull(string prefix, double fightPercentLeft)
    {
        ImGui.TextColored(M3.Scheme.Warning, $"{prefix}Best pull {fightPercentLeft:F1}% left");
        if (ImGui.IsItemHovered())
        {
            M3Tooltip.Show("No kill yet. Their furthest wipe in their 10 most recent FFLogs reports, by how much of the fight was left.");
        }
    }

    private static void DrawNoLogsWithAverage(double? averageParsePercent)
    {
        if (averageParsePercent.HasValue)
        {
            ImGui.TextColored(ParseColor(averageParsePercent.Value),
                $"No logs - Average percentage parse {averageParsePercent.Value:F0}%");
        }
        else
        {
            MutedText("No logs");
        }
    }

    private static void DrawBestParseOnDifferentJob(EncounterParseResult result, PartyMemberInfo member)
    {
        if (!result.BestParse.HasValue
            || result.BestParseJobAbbreviation == null
            || string.Equals(result.BestParseJobAbbreviation, member.JobAbbreviation, StringComparison.OrdinalIgnoreCase)
            || result.BestParse <= result.CurrentJobBestParse)
        {
            return;
        }

        ImGui.SameLine();
        MutedText("Best:");
        ImGui.SameLine();

        if (result.BestParseJobIconId is { } iconId && GetIconTexture(iconId) is { } texture)
        {
            InlineIcon(texture);
        }
        else
        {
            ImGui.TextColored(M3.Scheme.Tertiary, $"[{result.BestParseJobAbbreviation}]");
        }

        ImGui.SameLine();
        ImGui.TextColored(ParseColor(result.BestParse.Value), $"{result.BestParse.Value:F0}%");
    }
}
