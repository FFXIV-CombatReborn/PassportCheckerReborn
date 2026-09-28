using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.UI;
using System;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

/// <summary>
/// Material-styled pieces shared by the member info and party list overlays. Every cell lines its
/// text up with the frame height, so rows read evenly next to job icons and badges.
/// </summary>
internal static class OverlayWidgets
{
    public const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.BordersInnerH |
        ImGuiTableFlags.SizingFixedFit |
        ImGuiTableFlags.NoHostExtendX;

    public static Vector4 Muted => M3.Alpha(M3.Scheme.OnSurfaceVariant, 0.85f);

    /// <summary>The overlay title: an icon in a tonal circle, the title, and an optional muted second line.</summary>
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

    /// <summary>A muted icon and message, for when there is nothing to list.</summary>
    public static void EmptyState(FontAwesomeIcon icon, string message, string? detail = null)
    {
        var start = ImGui.GetCursorScreenPos();
        var iconSize = M3Draw.MeasureIcon(icon);
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon,
            new Vector2(start.X, start.Y + ((ImGui.GetTextLineHeight() - iconSize.Y) * 0.5f)), Muted);

        ImGui.SetCursorScreenPos(new Vector2(start.X + iconSize.X + M3.Space2, start.Y));
        using var group = ImRaii.Group();
        ImGui.TextUnformatted(message);

        if (!string.IsNullOrEmpty(detail))
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Muted);
            ImGui.TextUnformatted(detail);
        }
    }

    /// <summary>A muted status line with a leading icon, such as a loading notice under the table.</summary>
    public static void StatusLine(FontAwesomeIcon icon, string message)
    {
        var start = ImGui.GetCursorScreenPos();
        var iconSize = M3Draw.MeasureIcon(icon);
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon,
            new Vector2(start.X, start.Y + ((ImGui.GetTextLineHeight() - iconSize.Y) * 0.5f)), Muted);

        ImGui.SetCursorScreenPos(new Vector2(start.X + iconSize.X + M3.Space2, start.Y));
        ImGui.TextColored(Muted, message);
    }

    // ── Table ────────────────────────────────────────────────────────────────

    public static void BeginHeaderRow()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
    }

    /// <summary>A column label in the Material data-table style: small, uppercase, muted.</summary>
    public static void HeaderCell(string label)
    {
        ImGui.TableNextColumn();
        using var font = ImRaii.PushFont(M3.LabelSmall);
        using var color = ImRaii.PushColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(label.ToUpperInvariant());
    }

    /// <summary>Draws the job's icon at frame height, falling back to its abbreviation.</summary>
    public static void JobIcon(string jobAbbreviation)
    {
        var texture = GetJobIconTexture(jobAbbreviation);
        if (texture is not null)
        {
            ImGui.Image(texture.Handle, new Vector2(ImGui.GetFrameHeight()));
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(M3.Scheme.Tertiary, string.IsNullOrWhiteSpace(jobAbbreviation) ? "?" : jobAbbreviation);
    }

    /// <summary>Text that opens <paramref name="url"/> when clicked, underlined on hover like a link.</summary>
    public static void LinkText(string text, string url, Vector4? color = null, string? tooltip = null)
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
            ImguiTooltips.ShowTooltip(tooltip);
        }

        if (ImGui.IsItemClicked())
        {
            Util.OpenLink(url);
        }
    }

    /// <summary>Placeholder for a cell whose lookup is still running.</summary>
    public static void Pending()
    {
        ImGui.AlignTextToFramePadding();
        MutedText("…");
    }

    // ── Tomestone ────────────────────────────────────────────────────────────

    public static void TomestoneCell(TomestoneCharacterInfo? info)
    {
        var s = M3.Scheme;
        ImGui.AlignTextToFramePadding();

        if (info == null)
        {
            MutedText("Hidden Profile");
            return;
        }

        if (info.NoLogs)
        {
            MutedText("No Logs");
            return;
        }

        var hasClears = info.TotalClears.HasValue && info.TotalClears.Value > 0;
        var hasProgPoint = !string.IsNullOrWhiteSpace(info.ProgPoint);
        var hasBestPercent = info.BestPercent.HasValue;

        if (hasClears)
        {
            var clearsText = "Cleared";
            if (!string.IsNullOrWhiteSpace(info.CompletionWeek))
            {
                clearsText += $" ({info.CompletionWeek})";
            }

            ImGui.TextColored(s.Success, clearsText);

            if (hasBestPercent)
            {
                ImGui.SameLine();
                ImGui.TextColored(s.Success, $"Best: {info.BestPercent:F0}%");
            }
        }
        else if (hasProgPoint)
        {
            var progText = info.ProgPoint!;
            if (!string.IsNullOrWhiteSpace(info.DisplayPercent))
            {
                progText += $" ({info.DisplayPercent})";
            }

            ImGui.TextColored(s.Warning, progText);
        }
        else if (hasBestPercent)
        {
            ImGui.TextColored(s.Warning, $"Best: {info.BestPercent:F0}%");
        }
        else
        {
            MutedText("Hidden Profile");
        }
    }

    // ── FFLogs ───────────────────────────────────────────────────────────────

    public static void FFLogsCell(EncounterParseResult? result, PartyMemberInfo member)
    {
        var s = M3.Scheme;
        ImGui.AlignTextToFramePadding();

        if (result is null || !result.HasData)
        {
            DrawNoLogsWithAverage(result?.AverageParsePercent);
        }
        else if (result.IsEncounterSpecific)
        {
            var hasMultiPhaseData = result.Phase1TotalKills.HasValue ||
                                    result.Phase2TotalKills.HasValue ||
                                    result.Phase1BestParse.HasValue ||
                                    result.Phase2BestParse.HasValue ||
                                    result.Phase2LowestBossHpPct.HasValue;

            if (hasMultiPhaseData)
            {
                var p1Parse = result.Phase1BestParse;
                var p2Parse = result.Phase2BestParse;

                if (result.TotalKills > 0 && p1Parse.HasValue && p2Parse.HasValue)
                {
                    if (result.CurrentJobBestParse.HasValue)
                    {
                        ImGui.TextColored(s.Success, $"Cleared {result.TotalKills}x");
                        ImGui.SameLine();
                        MutedText("P1");
                        ImGui.SameLine();
                        ImGui.TextColored(ParseColor(p1Parse.Value), $"{p1Parse.Value:F0}%");
                        ImGui.SameLine();
                        MutedText("P2");
                        ImGui.SameLine();
                        ImGui.TextColored(ParseColor(p2Parse.Value), $"{p2Parse.Value:F0}%");
                    }
                    else
                    {
                        MutedText($"Cleared {result.TotalKills}x P1 {p1Parse.Value:F0}% P2 {p2Parse.Value:F0}%");
                    }

                    DrawBestParseOnDifferentJob(result, member);
                }
                else
                {
                    if (p1Parse.HasValue)
                    {
                        ImGui.TextColored(ParseColor(p1Parse.Value), $"P1 {p1Parse.Value:F0}%");
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
                    else if (p2Parse.HasValue)
                    {
                        ImGui.TextColored(ParseColor(p2Parse.Value), $"P2 {p2Parse.Value:F0}%");
                    }
                    else
                    {
                        MutedText("P2 No logs");
                    }

                    DrawBestParseOnDifferentJob(result, member);
                }
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
            else if (result.AverageParsePercent.HasValue)
            {
                DrawNoLogsWithAverage(result.AverageParsePercent);
            }
            else
            {
                MutedText("No logs");
            }
        }
        else
        {
            if (result.BestParse.HasValue)
            {
                ImGui.TextColored(ParseColor(result.BestParse.Value),
                    $"Average overall parse {result.BestParse.Value:F1}%");
            }
            else
            {
                MutedText("N/A");
            }
        }
    }

    /// <summary>
    /// FFLogs' own percentile colours. These stay fixed rather than following the theme, so parses
    /// read the way players already know them.
    /// </summary>
    public static Vector4 ParseColor(double percentile) => percentile switch
    {
        >= 99 => new Vector4(0.898f, 0.800f, 0.502f, 1.0f),  // Gold (99+)
        >= 95 => new Vector4(0.894f, 0.510f, 0.200f, 1.0f),  // Orange (95-98)
        >= 75 => new Vector4(0.635f, 0.282f, 0.808f, 1.0f),  // Purple (75-94)
        >= 50 => new Vector4(0.118f, 0.392f, 1.000f, 1.0f),  // Blue (50-74)
        >= 25 => new Vector4(0.118f, 0.784f, 0.118f, 1.0f),  // Green (25-49)
        _ => new Vector4(0.600f, 0.600f, 0.600f, 1.0f),  // Grey (<25)
    };

    /// <summary>Draws "No logs - Average percentage parse X%" with colour, or plain "No logs" if no average.</summary>
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

    /// <summary>
    /// If the overall best parse is on a different job from the member's current job, draws it after
    /// the current result with that job's icon. Draws nothing when the current job is the best job.
    /// </summary>
    private static void DrawBestParseOnDifferentJob(EncounterParseResult result, PartyMemberInfo member)
    {
        if (!result.BestParse.HasValue || result.BestParseJobAbbreviation == null)
        {
            return;
        }

        if (string.Equals(result.BestParseJobAbbreviation, member.JobAbbreviation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (result.CurrentJobBestParse.HasValue && result.BestParse.Value <= result.CurrentJobBestParse.Value)
        {
            return;
        }

        ImGui.SameLine();
        MutedText("Best:");
        ImGui.SameLine();

        var texture = result.BestParseJobIconId.HasValue ? GetIconTexture(result.BestParseJobIconId.Value) : null;
        if (texture is not null)
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

    /// <summary>
    /// A text-sized icon on a frame-aligned line. It reserves the frame height and centres the image,
    /// because an image placed directly would sit at the top of the line, above the text.
    /// </summary>
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

    private static IDalamudTextureWrap? GetJobIconTexture(string jobAbbreviation)
    {
        if (string.IsNullOrWhiteSpace(jobAbbreviation))
        {
            return null;
        }

        var iconId = FFLogsService.GetJobIconIdForSpec(FFLogsService.GetSpecForJob(jobAbbreviation));
        return iconId.HasValue ? GetIconTexture(iconId.Value) : null;
    }

    private static IDalamudTextureWrap? GetIconTexture(uint iconId)
    {
        try
        {
            return PassportCheckerReborn.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
