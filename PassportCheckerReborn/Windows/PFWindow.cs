using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.UI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Windows;

// The member info overlay beside the Party Finder detail pane: the listing's members, and buttons
// that look all of them up on Tomestone and FFLogs.
public class PFWindow(PassportCheckerReborn plugin) : Window("PF Member Info##PFCheckerOverlay",
           ImGuiWindowFlags.NoTitleBar |
               ImGuiWindowFlags.NoResize |
               ImGuiWindowFlags.NoMove |
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.AlwaysAutoResize)
{
    // Unscaled gap between the overlay and the detail pane.
    private const float AddonGap = 10f;

    private readonly PassportCheckerReborn plugin = plugin;

    // Results by member index, filled in by a lookup running off the main thread. Null until the
    // lookup has been asked for.
    private ConcurrentDictionary<int, EncounterParseResult?>? fflogsResults;
    private ConcurrentDictionary<int, TomestoneCharacterInfo?>? tomestoneResults;
    private bool fflogsLoading;
    private bool tomestoneLoading;

    private int lastDetailGeneration = -1;

    // From the previous frame, to keep the window on screen.
    private Vector2 lastWindowSize = new(300f, 200f);

    private M3Style.Scope? theme;

    public override unsafe void PreDraw()
    {
        theme = M3Style.Push(compact: true);

        var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroupDetail", 1);
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

        if (plugin.Configuration.ShowOverlayOnLeftSide)
        {
            // Anchored by its top-right corner, so it grows leftwards and never covers the detail pane.
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
        var cfg = plugin.Configuration;
        var partyFinder = plugin.PartyFinderManager;

        if (partyFinder.DetailOpenGeneration != lastDetailGeneration)
        {
            lastDetailGeneration = partyFinder.DetailOpenGeneration;
            fflogsResults = null;
            tomestoneResults = null;
            fflogsLoading = false;
            tomestoneLoading = false;
        }

        if (!cfg.ShowMemberInfoOverlay || !partyFinder.IsDetailOpen)
        {
            IsOpen = false;
            return;
        }

        // Only once the duty is known not to be high-end.
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
            if (LookupButton("##ts_all", "Tomestone", hasKey, "Tomestone API Key Needed", resolving, tomestoneLoading))
            {
                tomestoneLoading = true;
                tomestoneResults = new ConcurrentDictionary<int, TomestoneCharacterInfo?>();
                _ = FetchTomestoneAsync([.. members], tomestoneResults);
            }

            ImguiTooltips.HoveredTooltip(!hasKey
                ? "Add your Tomestone API key in Settings → Tomestone."
                : LookupTooltip("Tomestone", resolving, tomestoneLoading));

            if (cfg.EnableFFLogsIntegrationOverlay)
            {
                ImGui.SameLine(0f, M3.Space2);
            }
        }

        if (cfg.EnableFFLogsIntegrationOverlay)
        {
            var hasCredentials = cfg.HasFFLogsCredentials();
            if (LookupButton("##ff_all", "FFLogs", hasCredentials, "FFLogs API Key Needed", resolving, fflogsLoading))
            {
                fflogsLoading = true;
                fflogsResults = new ConcurrentDictionary<int, EncounterParseResult?>();
                _ = FetchFFLogsAsync([.. members], fflogsResults);
            }

            ImguiTooltips.HoveredTooltip(!hasCredentials
                ? "Add your FFLogs API client in Settings → FFLogs."
                : LookupTooltip("FFLogs", resolving, fflogsLoading));
        }
    }

    // Disabled until the integration is configured and every name is resolved, and while it is running.
    private static bool LookupButton(string id, string name, bool configured, string unconfiguredLabel, bool resolving, bool loading)
    {
        var (label, icon) = !configured ? (unconfiguredLabel, FontAwesomeIcon.Key)
            : loading ? (name, FontAwesomeIcon.HourglassHalf)
            : resolving ? ($"{name} (resolving…)", FontAwesomeIcon.HourglassHalf)
            : (name, FontAwesomeIcon.Search);

        return M3Widgets.Button(id, label, M3ButtonStyle.Tonal, icon, enabled: configured && !resolving && !loading);
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

        if (plugin.PartyFinderManager.IsBlacklisted(member.Name, member.World))
        {
            ImGui.SameLine();
            M3Badge.Draw("BL", M3.Scheme.Error, "On your blacklist");
        }

        if (hasTomestone)
        {
            ImGui.TableNextColumn();
            if (!member.IsPrivate && tomestoneResults != null)
            {
                if (tomestoneResults.TryGetValue(index, out var tomestone))
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
                if (fflogsResults.TryGetValue(index, out var fflogs))
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

    // Both lookups work on a copy of the members and write into the dictionary they were started
    // with, so results for a listing that has since been closed never show up under the next one.
    private async Task FetchFFLogsAsync(PartyMemberInfo[] members, ConcurrentDictionary<int, EncounterParseResult?> results)
    {
        try
        {
            var service = plugin.FFLogsService;
            if (FFLogsService.GetEncounterIdsForDuty(plugin.PartyFinderManager.LookupDutyName) is not { } encounterIds)
            {
                // The duty has no FFLogs encounter, so each player's overall standing is shown instead.
                for (var i = 0; i < members.Length; i++)
                {
                    results[i] = members[i].IsResolved
                        ? await service.GetOverallParseAsync(members[i].Name, members[i].World)
                        : null;
                }

                return;
            }

            var encounterResults = await service.GetEncounterDataAsync(members, encounterIds);
            foreach (var (index, result) in encounterResults)
            {
                results[index] = result;
            }

            // Players with nothing logged for the encounter get their overall average beside "No logs".
            foreach (var (index, result) in encounterResults)
            {
                var hasEncounterData = result.TotalKills > 0
                    || result.LowestBossHpPct.HasValue
                    || result.Phase1BestParse.HasValue
                    || result.Phase2BestParse.HasValue
                    || result.Phase2LowestBossHpPct.HasValue;

                if (!hasEncounterData
                    && members[index].IsResolved
                    && await service.GetBestPerfAvgAsync(members[index].Name, members[index].World) is { } average)
                {
                    results[index] = result with { AverageParsePercent = average };
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PFWindow] FFLogs lookup failed.");
        }
        finally
        {
            if (ReferenceEquals(results, fflogsResults))
            {
                fflogsLoading = false;
            }
        }
    }

    private async Task FetchTomestoneAsync(PartyMemberInfo[] members, ConcurrentDictionary<int, TomestoneCharacterInfo?> results)
    {
        try
        {
            var dutyName = plugin.PartyFinderManager.LookupDutyName;
            for (var i = 0; i < members.Length; i++)
            {
                results[i] = members[i].IsResolved
                    ? await plugin.TomestoneService.GetCharacterInfoAsync(members[i].Name, members[i].World, dutyName)
                    : null;
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PFWindow] Tomestone lookup failed.");
        }
        finally
        {
            if (ReferenceEquals(results, tomestoneResults))
            {
                tomestoneLoading = false;
            }
        }
    }
}
