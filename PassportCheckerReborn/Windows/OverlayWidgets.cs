using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.UI;
using System;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

// Pieces shared by the member info and party list overlays. Every cell lines its text up with the
// frame height, so rows read evenly next to job icons and badges.
internal static class OverlayWidgets
{
    private const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.BordersInnerH |
        ImGuiTableFlags.SizingFixedFit |
        ImGuiTableFlags.NoHostExtendX;

    public static Vector4 Muted => M3.Alpha(M3.Scheme.OnSurfaceVariant, 0.85f);

    public static void Header(FontAwesomeIcon icon, string title, string? subtitle)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var start = ImGui.GetCursorScreenPos();
        var iconBox = 28f * scale;
        var lineGap = 1f * scale;

        Vector2 titleSize;
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            titleSize = ImGui.CalcTextSize(title);
        }

        var subtitleSize = string.IsNullOrEmpty(subtitle) ? Vector2.Zero : ImGui.CalcTextSize(subtitle);
        var textHeight = titleSize.Y + (subtitleSize.Y > 0f ? lineGap + subtitleSize.Y : 0f);
        var height = MathF.Max(iconBox, textHeight);
        var textX = start.X + iconBox + M3.Space2;

        ImGui.Dummy(new Vector2(textX - start.X + MathF.Max(titleSize.X, subtitleSize.X), height));

        var drawList = ImGui.GetWindowDrawList();
        var center = new Vector2(start.X + (iconBox * 0.5f), start.Y + (height * 0.5f));
        var half = new Vector2(iconBox * 0.5f);
        drawList.AddCircleFilled(center, iconBox * 0.5f, M3.U32(s.PrimaryContainer), 24);
        M3Draw.IconCentered(drawList, icon, center - half, center + half, s.OnPrimaryContainer);

        var textY = start.Y + ((height - textHeight) * 0.5f);
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            drawList.AddText(new Vector2(textX, textY), M3.U32(s.OnSurface), title);
        }

        if (subtitleSize.Y > 0f)
        {
            drawList.AddText(new Vector2(textX, textY + titleSize.Y + lineGap), M3.U32(Muted), subtitle);
        }
    }

    public static void EmptyState(FontAwesomeIcon icon, string message, string? detail = null)
    {
        LeadingIcon(icon);
        using var group = ImRaii.Group();
        ImGui.TextUnformatted(message);

        if (!string.IsNullOrEmpty(detail))
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Muted);
            ImGui.TextUnformatted(detail);
        }
    }

    public static void StatusLine(FontAwesomeIcon icon, string message)
    {
        LeadingIcon(icon);
        ImGui.TextColored(Muted, message);
    }

    // Draws a muted icon and leaves the cursor after it, on the same line.
    private static void LeadingIcon(FontAwesomeIcon icon)
    {
        var start = ImGui.GetCursorScreenPos();
        var iconSize = M3Draw.MeasureIcon(icon);
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon,
            new Vector2(start.X, start.Y + ((ImGui.GetTextLineHeight() - iconSize.Y) * 0.5f)), Muted);

        ImGui.SetCursorScreenPos(new Vector2(start.X + iconSize.X + M3.Space2, start.Y));
    }

    // The player table both overlays show, with its header row drawn. False when the table is clipped;
    // otherwise the caller draws the rows and calls ImGui.EndTable.
    public static bool BeginMemberTable(string id, bool hasTomestone, bool hasFFLogs)
    {
        ReadOnlySpan<string?> columns = ["Player", hasTomestone ? "Tomestone" : null, hasFFLogs ? "FFLogs" : null];
        if (!ImGui.BeginTable(id, 1 + (hasTomestone ? 1 : 0) + (hasFFLogs ? 1 : 0), TableFlags))
        {
            return false;
        }

        foreach (var column in columns)
        {
            if (column != null)
            {
                ImGui.TableSetupColumn(column, ImGuiTableColumnFlags.WidthFixed);
            }
        }

        // A plain row: text in a Headers row is left out of the column's fitted width.
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.TableHeaderBg));
        using var font = ImRaii.PushFont(M3.LabelSmall);
        using var color = ImRaii.PushColor(ImGuiCol.Text, Muted);
        foreach (var column in columns)
        {
            if (column != null)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(column.ToUpperInvariant());
            }
        }

        return true;
    }

    // The job's icon at frame height, or its abbreviation when it has none.
    public static void JobIcon(string jobAbbreviation)
    {
        if (FFLogsService.GetJobIconId(jobAbbreviation) is { } iconId && GetIconTexture(iconId) is { } texture)
        {
            ImGui.Image(texture.Handle, new Vector2(ImGui.GetFrameHeight()));
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(M3.Scheme.Tertiary, string.IsNullOrWhiteSpace(jobAbbreviation) ? "?" : jobAbbreviation);
    }

    // Text that opens the player's Tomestone page when clicked, underlined on hover like a link.
    public static void PlayerLink(string text, PartyMemberInfo member, Vector4? color = null)
    {
        var tone = color ?? M3.Scheme.OnSurface;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(tone, text);

        if (ImGui.IsItemHovered())
        {
            var scale = M3.Scale;
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(min.X, max.Y - (1f * scale)),
                new Vector2(max.X, max.Y - (1f * scale)),
                M3.U32(tone, 0.8f), 1f * scale);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImguiTooltips.ShowTooltip("Open on Tomestone.gg");
        }

        if (ImGui.IsItemClicked())
        {
            Util.OpenLink($"https://tomestone.gg/character-name/{Uri.EscapeDataString(member.World)}/{Uri.EscapeDataString(member.Name)}");
        }
    }

    public static void Pending()
    {
        ImGui.AlignTextToFramePadding();
        MutedText("…");
    }

    public static void TomestoneCell(TomestoneCharacterInfo? info)
    {
        var s = M3.Scheme;
        ImGui.AlignTextToFramePadding();

        if (info == null)
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
        else
        {
            MutedText("Hidden Profile");
        }
    }

    public static void FFLogsCell(EncounterParseResult? result, PartyMemberInfo member)
    {
        var s = M3.Scheme;
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
            ImGui.TextColored(s.Warning, $"{result.LowestBossHpPct.Value:F0}%");
        }
        else
        {
            DrawNoLogsWithAverage(result.AverageParsePercent);
        }
    }

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
        else
        {
            MutedText("P1 No logs");
        }

        ImGui.SameLine();

        if (result.Phase2LowestBossHpPct.HasValue)
        {
            ImGui.TextColored(s.Warning, $"P2 {result.Phase2LowestBossHpPct.Value:F0}%");
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

    // FFLogs' own percentile colours, which stay fixed rather than following the theme.
    public static Vector4 ParseColor(double percentile) => percentile switch
    {
        >= 99 => new Vector4(0.898f, 0.800f, 0.502f, 1.0f),
        >= 95 => new Vector4(0.894f, 0.510f, 0.200f, 1.0f),
        >= 75 => new Vector4(0.635f, 0.282f, 0.808f, 1.0f),
        >= 50 => new Vector4(0.118f, 0.392f, 1.000f, 1.0f),
        >= 25 => new Vector4(0.118f, 0.784f, 0.118f, 1.0f),
        _ => new Vector4(0.600f, 0.600f, 0.600f, 1.0f),
    };

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

    // Shown only when the player's best parse is on a job other than the one they are on.
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

    // Reserves the frame height and centres the image in it: placed directly, an image would sit at
    // the top of the line, above the text.
    private static void InlineIcon(IDalamudTextureWrap texture)
    {
        var frameHeight = ImGui.GetFrameHeight();
        var lineHeight = ImGui.GetTextLineHeight();
        ImGui.Dummy(new Vector2(lineHeight, frameHeight));

        var min = ImGui.GetItemRectMin() + new Vector2(0f, (frameHeight - lineHeight) * 0.5f);
        ImGui.GetWindowDrawList().AddImage(texture.Handle, min, min + new Vector2(lineHeight));
    }

    private static void MutedText(string text)
    {
        ImGui.TextColored(Muted, text);
    }

    private static IDalamudTextureWrap? GetIconTexture(uint iconId)
    {
        return PassportCheckerReborn.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
    }
}
