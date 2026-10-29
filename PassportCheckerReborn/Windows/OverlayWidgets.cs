using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using PassportCheckerReborn.Players;
using System;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

internal static partial class OverlayWidgets
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

    private static void LeadingIcon(FontAwesomeIcon icon)
    {
        var start = ImGui.GetCursorScreenPos();
        var iconSize = M3Draw.MeasureIcon(icon);
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon,
            new Vector2(start.X, start.Y + ((ImGui.GetTextLineHeight() - iconSize.Y) * 0.5f)), Muted);

        ImGui.SetCursorScreenPos(new Vector2(start.X + iconSize.X + M3.Space2, start.Y));
    }

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

    public static void JobIcon(string jobAbbreviation)
    {
        if (Jobs.GetIconId(jobAbbreviation) is { } iconId && GetIconTexture(iconId) is { } texture)
        {
            ImGui.Image(texture.Handle, new Vector2(ImGui.GetFrameHeight()));
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(M3.Scheme.Tertiary, string.IsNullOrWhiteSpace(jobAbbreviation) ? "?" : jobAbbreviation);
    }

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
            M3Tooltip.Show("Open on Tomestone.gg");
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
        return Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
    }
}
