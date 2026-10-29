using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PassportCheckerReborn.Integrations.FFLogs;
using PassportCheckerReborn.Integrations.Tomestone;
using PassportCheckerReborn.Players;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class MemberInfoWindow(Plugin plugin) : Window("PF Member Info##PFCheckerOverlay",
           ImGuiWindowFlags.NoTitleBar |
               ImGuiWindowFlags.NoResize |
               ImGuiWindowFlags.NoMove |
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.AlwaysAutoResize)
{
    private const float AddonGap = 10f;

    private readonly Plugin plugin = plugin;

    private ConcurrentDictionary<ulong, EncounterParseResult?>? fflogsResults;
    private ConcurrentDictionary<ulong, TomestoneCharacterInfo?>? tomestoneResults;
    private bool fflogsLoading;
    private bool tomestoneLoading;

    private int lastListingGeneration = -1;

    private Vector2 lastWindowSize = new(300f, 200f);

    private M3Style.Scope? theme;

    public override unsafe void PreDraw()
    {
        theme = M3Style.Push(M3Density.Compact);

        var addonPtr = Plugin.GameGui.GetAddonByName("LookingForGroupDetail", 1);
        if (addonPtr.IsNull)
        {
            return;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (!addon->IsVisible)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        var screenLeft = viewport.Pos.X;
        var screenRight = viewport.Pos.X + viewport.Size.X;
        var top = viewport.Pos.Y + addon->Y;

        if (Plugin.Configuration.ShowOverlayOnLeftSide)
        {
            var right = MathF.Min(MathF.Max(screenLeft + addon->X - AddonGap, screenLeft + lastWindowSize.X), screenRight);
            ImGui.SetNextWindowPos(new Vector2(right, top), ImGuiCond.Always, new Vector2(1f, 0f));
            Position = null;
        }
        else
        {
            var left = screenLeft + addon->X + addon->GetScaledWidth(true) + AddonGap;
            Position = new Vector2(MathF.Max(MathF.Min(left, screenRight - lastWindowSize.X), screenLeft), top);
        }
    }

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    public override void Draw()
    {
        var cfg = Plugin.Configuration;
        var partyFinder = plugin.PartyFinderManager;

        if (partyFinder.ListingGeneration != lastListingGeneration)
        {
            lastListingGeneration = partyFinder.ListingGeneration;
            fflogsResults = null;
            tomestoneResults = null;
            fflogsLoading = false;
            tomestoneLoading = false;
        }

        if (cfg.OnlyShowOverlayForHighEndDuties
            && !partyFinder.IsHighEndDuty
            && (partyFinder.CurrentDutyId > 0 || !string.IsNullOrEmpty(partyFinder.CurrentDutyName)))
        {
            OverlayWidgets.EmptyState(FontAwesomeIcon.Filter, "Not a high-end duty.");
            return;
        }

        var members = partyFinder.CurrentMembers;
        var dutyName = partyFinder.CurrentDutyName;

        OverlayWidgets.Header(FontAwesomeIcon.Users, "Member Info", string.IsNullOrWhiteSpace(dutyName) ? null : dutyName);
        ImGui.Dummy(new Vector2(0f, M3.Space1));

        if (members.Count == 0)
        {
            OverlayWidgets.EmptyState(FontAwesomeIcon.Search, "No party finder listing selected.",
                "Open a PF detail window to see member info.");
            return;
        }

        var hasTomestone = cfg.EnableTomestoneIntegration && cfg.HasTomestoneKey();
        var hasFFLogs = cfg.EnableFFLogsIntegrationOverlay && cfg.HasFFLogsCredentials();

        if (OverlayWidgets.BeginMemberTable("##members_table", hasTomestone, hasFFLogs))
        {
            for (var i = 0; i < members.Count; i++)
            {
                DrawMemberRow(members[i], i, cfg, hasTomestone, hasFFLogs);
            }

            ImGui.EndTable();
        }

        if (cfg.EnableTomestoneIntegration || cfg.EnableFFLogsIntegrationOverlay)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            DrawLookupButtons(members, cfg);
        }

        lastWindowSize = ImGui.GetWindowSize();
    }

    private void DrawLookupButtons(IReadOnlyList<PartyMemberInfo> members, Configuration cfg)
    {
        var resolving = plugin.PartyFinderManager.HasPendingMembers;

        if (cfg.EnableTomestoneIntegration)
        {
            var hasKey = cfg.HasTomestoneKey();
            var tooltip = !hasKey
                ? "Add your Tomestone API key in Settings → Tomestone."
                : LookupTooltip("Tomestone", resolving, tomestoneLoading);
            if (LookupButton("##ts_all", "Tomestone", hasKey, "Tomestone API Key Needed", resolving, tomestoneLoading, tooltip))
            {
                tomestoneLoading = true;
                tomestoneResults = new ConcurrentDictionary<ulong, TomestoneCharacterInfo?>();
                _ = FetchTomestoneAsync([.. members], tomestoneResults);
            }

            if (cfg.EnableFFLogsIntegrationOverlay)
            {
                ImGui.SameLine(0f, M3.Space2);
            }
        }

        if (cfg.EnableFFLogsIntegrationOverlay)
        {
            var hasCredentials = cfg.HasFFLogsCredentials();
            var tooltip = !hasCredentials
                ? "Add your FFLogs API client in Settings → FFLogs."
                : LookupTooltip("FFLogs", resolving, fflogsLoading);
            if (LookupButton("##ff_all", "FFLogs", hasCredentials, "FFLogs API Key Needed", resolving, fflogsLoading, tooltip))
            {
                fflogsLoading = true;
                fflogsResults = new ConcurrentDictionary<ulong, EncounterParseResult?>();
                _ = FetchFFLogsAsync([.. members], fflogsResults);
            }
        }
    }

    private static bool LookupButton(string id, string name, bool configured, string unconfiguredLabel, bool resolving, bool loading, string tooltip)
    {
        var (label, icon) = !configured ? (unconfiguredLabel, FontAwesomeIcon.Key)
            : loading ? (name, FontAwesomeIcon.HourglassHalf)
            : resolving ? ($"{name} (resolving…)", FontAwesomeIcon.HourglassHalf)
            : (name, FontAwesomeIcon.Search);

        return M3Widgets.Button(id, label, M3ButtonStyle.Tonal, icon, enabled: configured && !resolving && !loading, tooltip: tooltip);
    }

    private static string LookupTooltip(string name, bool resolving, bool loading)
    {
        return resolving ? "Waiting for player names to be resolved…"
            : loading ? $"Looking up {name} data for all players…"
            : $"Look up {name} data for all players";
    }

    private void DrawMemberRow(PartyMemberInfo member, int index, Configuration cfg, bool hasTomestone, bool hasFFLogs)
    {
        ImGui.TableNextRow();
        using var id = ImRaii.PushId(index);

        var isKnown = cfg.SpecialBorderColorForKnownPlayers
            && plugin.PartyFinderManager.IsKnownPlayer(member.Name, member.World);
        if (isKnown)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.ColorConvertFloat4ToU32(cfg.KnownPlayerBorderColor with { W = 0.18f }));
        }

        ImGui.TableSetColumnIndex(0);

        if (cfg.ShowPartyJobIcons && !string.IsNullOrWhiteSpace(member.JobAbbreviation))
        {
            OverlayWidgets.JobIcon(member.JobAbbreviation);
            ImGui.SameLine();
        }

        ImGui.AlignTextToFramePadding();
        switch (member.NameState)
        {
            case MemberNameState.Private:
                ImGui.TextColored(OverlayWidgets.Muted, $"Private Player {index + 1}");
                ImGui.SameLine();
                M3Badge.Draw("Private", M3.Scheme.OnSurfaceVariant, "Adventure plate is hidden or unavailable");
                break;

            case MemberNameState.Unresolved:
                ImGui.TextUnformatted($"Unresolved Player {index + 1}");
                break;

            case MemberNameState.Pending:
                ImGui.TextUnformatted($"Player {index + 1}");
                break;

            default:
                OverlayWidgets.PlayerLink(
                    cfg.ShowResolvedPlayerNames ? $"{member.Name}@{member.World}" : $"Player {index + 1}",
                    member, isKnown ? cfg.KnownPlayerBorderColor : null);
                break;
        }

        if (plugin.BlacklistMonitor.IsBlacklisted(member.Name, member.World))
        {
            ImGui.SameLine();
            M3Badge.Draw("BL", M3.Scheme.Error, "On your blacklist");
        }

        if (hasTomestone)
        {
            ImGui.TableNextColumn();
            if (!member.IsPrivate && tomestoneResults != null)
            {
                if (tomestoneResults.TryGetValue(member.ContentId, out var tomestone))
                {
                    OverlayWidgets.TomestoneCell(tomestone);
                }
                else if (tomestoneLoading)
                {
                    OverlayWidgets.Pending();
                }
            }
        }

        if (hasFFLogs)
        {
            ImGui.TableNextColumn();
            if (!member.IsPrivate && fflogsResults != null)
            {
                if (fflogsResults.TryGetValue(member.ContentId, out var fflogs))
                {
                    OverlayWidgets.FFLogsCell(fflogs, member);
                }
                else if (fflogsLoading)
                {
                    OverlayWidgets.Pending();
                }
            }
        }
    }
}
