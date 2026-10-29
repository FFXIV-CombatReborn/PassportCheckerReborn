using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PassportCheckerReborn.Integrations.FFLogs;
using PassportCheckerReborn.Integrations.Tomestone;
using PassportCheckerReborn.Players;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class PartyListWindow(Plugin plugin) : Window("Party Member Info##PFCheckerPartyList", LockedFlags)
{
    private const ImGuiWindowFlags FreeFlags = ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.AlwaysAutoResize;

    private const ImGuiWindowFlags LockedFlags = FreeFlags | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;

    private static readonly string[] PartyListAddons = ["_PartyList", "_CrossWorldPartyList"];

    private readonly Plugin plugin = plugin;

    private IReadOnlyList<PartyMemberInfo> members = [];
    private int membersVersion = -1;

    private Dictionary<int, EncounterParseResult?> fflogsResults = [];
    private Dictionary<int, TomestoneCharacterInfo?> tomestoneResults = [];
    private bool fflogsLoading;
    private bool tomestoneLoading;

    private int lookupSerial;

    private int selectedDutyIndex;

    private Vector2 lastWindowSize = new(310, 200);
    private float lastContentWidth;

    private M3Style.Scope? theme;

    public override unsafe bool DrawConditions()
    {
        return plugin.PartyMonitor.Members.Count > 0 || FindPartyListAddon() != null;
    }

    public override void PreDraw()
    {
        theme = M3Style.Push(M3Density.Compact);

        var position = Plugin.Configuration.PartyListOverlayPosition;
        if (position == PartyListOverlayPosition.Unbound)
        {
            Flags = FreeFlags;
            Position = null;
            return;
        }

        Flags = LockedFlags;
        PositionBeside(position);
    }

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    private static unsafe AtkUnitBase* FindPartyListAddon()
    {
        foreach (var name in PartyListAddons)
        {
            var addonPtr = Plugin.GameGui.GetAddonByName(name, 1);
            if (!addonPtr.IsNull && ((AtkUnitBase*)addonPtr.Address)->IsVisible)
            {
                return (AtkUnitBase*)addonPtr.Address;
            }
        }

        return null;
    }

    private unsafe void PositionBeside(PartyListOverlayPosition position)
    {
        var addon = FindPartyListAddon();
        if (addon == null)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        var left = viewport.Pos.X + addon->X;
        var top = viewport.Pos.Y + addon->Y;

        switch (position)
        {
            case PartyListOverlayPosition.Left:
                var right = MathF.Min(MathF.Max(left - 10f, viewport.Pos.X + lastWindowSize.X), viewport.Pos.X + viewport.Size.X);
                ImGui.SetNextWindowPos(new Vector2(right, top), ImGuiCond.Always, new Vector2(1f, 0f));
                Position = null;
                break;

            case PartyListOverlayPosition.Right:
                Position = new Vector2(left + addon->GetScaledWidth(true) + 5f, top);
                break;

            case PartyListOverlayPosition.Above:
                Position = new Vector2(left, MathF.Max(top - lastWindowSize.Y - 5f, viewport.Pos.Y));
                break;

            case PartyListOverlayPosition.Below:
                Position = new Vector2(left, top + addon->GetScaledHeight(true) + 5f);
                break;
        }
    }

    public override void Draw()
    {
        var cfg = Plugin.Configuration;
        var monitor = plugin.PartyMonitor;

        if (monitor.Members.Count == 0)
        {
            OverlayWidgets.EmptyState(FontAwesomeIcon.HourglassHalf, "Waiting for party data…");
            return;
        }

        if (monitor.Version != membersVersion)
        {
            membersVersion = monitor.Version;
            members = monitor.Members;
            StartLookups(cfg);
        }

        var contentStartX = ImGui.GetCursorScreenPos().X;
        if (!DrawHeader(cfg, contentStartX, out var contentRight))
        {
            return;
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        contentRight = MathF.Max(contentRight, DrawMemberTable(cfg));

        if (fflogsLoading || tomestoneLoading)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            OverlayWidgets.StatusLine(FontAwesomeIcon.HourglassHalf,
                fflogsLoading && tomestoneLoading ? "Loading FFLogs & Tomestone data…"
                : fflogsLoading ? "Loading FFLogs data…"
                : "Loading Tomestone data…");
            contentRight = MathF.Max(contentRight, ImGui.GetItemRectMax().X);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        if (DrawDutySelector(out var selectorRight))
        {
            StartLookups(cfg);
        }

        contentRight = MathF.Max(contentRight, selectorRight);

        lastContentWidth = MathF.Floor(contentRight - contentStartX);
        lastWindowSize = ImGui.GetWindowSize();
    }

    private bool DrawHeader(Configuration cfg, float contentStartX, out float contentRight)
    {
        var dutyName = TomestoneDutyName;
        OverlayWidgets.Header(FontAwesomeIcon.UserFriends, "Party Members", string.IsNullOrWhiteSpace(dutyName) ? null : dutyName);
        var headerMin = ImGui.GetItemRectMin();
        var headerMax = ImGui.GetItemRectMax();
        contentRight = headerMax.X;

        var buttonSize = 26f * M3.Scale;
        ImGui.SameLine();
        var buttonX = MathF.Max(ImGui.GetCursorScreenPos().X, contentStartX + lastContentWidth - buttonSize);
        ImGui.SetCursorScreenPos(new Vector2(buttonX, headerMin.Y + ((headerMax.Y - headerMin.Y - buttonSize) * 0.5f)));

        if (M3Widgets.IconButton("##hide_party_overlay", FontAwesomeIcon.EyeSlash,
                "Hide the party list overlay. Turn it back on in settings or with /pcrparty.", diameter: buttonSize))
        {
            cfg.ShowPartyListOverlay = false;
            cfg.Save();
            return false;
        }

        return true;
    }

    private bool DrawDutySelector(out float right)
    {
        const string label = "Duty";
        var width = 240f * M3.Scale;
        var start = ImGui.GetCursorScreenPos();
        var labelSize = ImGui.CalcTextSize(label);

        ImGui.GetWindowDrawList().AddText(
            new Vector2(start.X, start.Y + ((M3Widgets.ComboHeight - labelSize.Y) * 0.5f)),
            M3.U32(OverlayWidgets.Muted), label);

        var comboX = start.X + labelSize.X + M3.Space2;
        ImGui.SetCursorScreenPos(new Vector2(comboX, start.Y));
        right = comboX + width;
        return M3Widgets.Combo("##party_duty_select", ref selectedDutyIndex, DutyNames, width);
    }

    private float DrawMemberTable(Configuration cfg)
    {
        var hasTomestone = cfg.EnableTomestoneIntegration && cfg.HasTomestoneKey();
        var hasFFLogs = cfg.EnableFFLogsIntegrationOverlay && cfg.HasFFLogsCredentials();
        if (!OverlayWidgets.BeginMemberTable("##PartyMemberTable", hasTomestone, hasFFLogs))
        {
            return 0f;
        }

        var right = ImGui.GetItemRectMax().X;
        for (var i = 0; i < members.Count; i++)
        {
            DrawMemberRow(members[i], i, cfg, hasTomestone, hasFFLogs);
            right = MathF.Max(right, ImGui.GetItemRectMax().X);
        }

        ImGui.EndTable();
        return right;
    }

    private void DrawMemberRow(PartyMemberInfo member, int index, Configuration cfg, bool hasTomestone, bool hasFFLogs)
    {
        using var id = ImRaii.PushId(index);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        if (cfg.ShowPartyJobIcons)
        {
            OverlayWidgets.JobIcon(member.JobAbbreviation);
            ImGui.SameLine();
        }

        if (string.IsNullOrEmpty(member.World))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(member.Name);
        }
        else
        {
            OverlayWidgets.PlayerLink($"{member.Name}@{member.World}", member);
        }

        if (hasTomestone)
        {
            ImGui.TableNextColumn();
            if (tomestoneResults.TryGetValue(index, out var tomestone))
            {
                OverlayWidgets.TomestoneCell(tomestone);
            }
            else
            {
                OverlayWidgets.Pending();
            }
        }

        if (hasFFLogs)
        {
            ImGui.TableNextColumn();
            if (fflogsResults.TryGetValue(index, out var fflogs))
            {
                OverlayWidgets.FFLogsCell(fflogs, member);
            }
            else
            {
                OverlayWidgets.Pending();
            }
        }
    }
}
