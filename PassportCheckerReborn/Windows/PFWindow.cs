using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Windows;

/// <summary>
/// The member-info overlay that appears alongside the Party Finder detail pane.
/// It lists all current party-finder members (fetched via
/// <see cref="Services.PartyFinderManager"/>) and, via two shared buttons below the
/// player list, performs batch Tomestone / FFLogs lookups for every member at once.
/// </summary>
public class PFWindow(PassportCheckerReborn plugin) : Window("PF Member Info##PFCheckerOverlay",
           ImGuiWindowFlags.NoTitleBar |
               ImGuiWindowFlags.NoResize |
               ImGuiWindowFlags.NoMove |
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.AlwaysAutoResize), IDisposable
{
    private readonly PassportCheckerReborn plugin = plugin;

    // Per-member FFLogs encounter cache (index → result)
    private Dictionary<int, EncounterParseResult?> fflogsEncounterCache = [];
    private bool fflogsBatchInProgress;

    // Per-member Tomestone info cache (index → character info)
    private Dictionary<int, TomestoneCharacterInfo?> tomestoneInfoCache = [];
    private bool tomestoneBatchInProgress;

    // Tracks the PartyFinderManager generation so we can clear caches on new detail open
    private int lastDetailGeneration = -1;

    // Tracks whether FFLogs data has been fetched (user clicked button) for this generation
    private bool fflogsFetched;

    // Tracks whether Tomestone data has been fetched (user clicked button) for this generation
    private bool tomestoneFetched;

    // Cached size of this overlay window from the previous frame, used for clamping in PreDraw
    private Vector2 lastWindowSize = new(300f, 200f);

    private M3Style.Scope? theme;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    public override unsafe void PreDraw()
    {
        theme = M3Style.Push(compact: true);

        // Position this window to the left or right of the PF Details addon.
        //
        // Uses GameGui.GetAddonByName to find the LookingForGroupDetail addon,
        // reads its position and size, and sets the ImGui window position accordingly.
        try
        {
            var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroupDetail", 1);
            if (!addonPtr.IsNull)
            {
                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon->IsVisible)
                {
                    var addonX = addon->X;
                    var addonY = addon->Y;
                    var addonWidth = addon->GetScaledWidth(true);
                    var addonHeight = addon->GetScaledHeight(true);

                    // Get the ImGui viewport offset for coordinate conversion
                    var vpPos = ImGui.GetMainViewport().Pos;

                    var overlayY = vpPos.Y + addonY;

                    if (plugin.Configuration.ShowOverlayOnLeftSide)
                    {
                        // Anchor the top-right corner of the overlay to the left edge of the addon
                        // so the window grows leftward and does not cover LookingForGroupDetail.
                        var anchorX = vpPos.X + addonX - 10;

                        // Clamp so the left edge of the window (anchorX - windowWidth) stays on screen.
                        // Use the previous frame's size for estimation; falls back to a safe default on first frame.
                        var windowWidth = lastWindowSize.X;
                        var vpSize = ImGui.GetMainViewport().Size;
                        var minAnchorX = vpPos.X + windowWidth;
                        var maxAnchorX = vpPos.X + vpSize.X;
                        anchorX = Math.Clamp(anchorX, minAnchorX, maxAnchorX);

                        ImGui.SetNextWindowPos(new Vector2(anchorX, overlayY), ImGuiCond.Always, new Vector2(1f, 0f));
                        Position = null;
                    }
                    else
                    {
                        // Place overlay to the right of the addon, clamped to screen edge.
                        var vpSize = ImGui.GetMainViewport().Size;
                        var overlayX = vpPos.X + addonX + addonWidth + 10;
                        var windowWidth = lastWindowSize.X;
                        overlayX = Math.Min(overlayX, vpPos.X + vpSize.X - windowWidth);
                        overlayX = Math.Max(overlayX, vpPos.X);
                        Position = new Vector2(overlayX, overlayY);
                    }
                    return;
                }
            }
        }
        catch (Exception)
        {
        }

        // If the addon isn't found or isn't visible, don't force a position
        // (let the window float freely so it's still usable during development).
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        // Clear cached data when a new LookingForGroupDetail pane is opened
        var gen = plugin.PartyFinderManager.DetailOpenGeneration;
        if (gen != lastDetailGeneration)
        {
            lastDetailGeneration = gen;
            fflogsEncounterCache = [];
            tomestoneInfoCache = [];
            fflogsFetched = false;
            tomestoneFetched = false;
        }

        if (!cfg.ShowMemberInfoOverlay || !plugin.PartyFinderManager.IsDetailOpen)
        {
            IsOpen = false;
            return;
        }

        // If "Only Show for High-End Duties" is enabled, check the duty type
        if (cfg.OnlyShowOverlayForHighEndDuties && !plugin.PartyFinderManager.IsHighEndDuty)
        {
            // Hide if we positively know it's not high-end (either via ID or name)
            if (plugin.PartyFinderManager.IsDetailOpen &&
                (plugin.PartyFinderManager.CurrentDutyId > 0 ||
                 !string.IsNullOrEmpty(plugin.PartyFinderManager.CurrentDutyName)))
            {
                OverlayWidgets.EmptyState(FontAwesomeIcon.Filter, "Not a high-end duty.");
                return;
            }
        }

        var members = plugin.PartyFinderManager.CurrentMembers;
        var dutyName = plugin.PartyFinderManager.CurrentDutyName;

        OverlayWidgets.Header(FontAwesomeIcon.Users, "Member Info", string.IsNullOrWhiteSpace(dutyName) ? null : dutyName);
        ImGui.Dummy(new Vector2(0f, M3.Space1));

        if (members.Count == 0)
        {
            OverlayWidgets.EmptyState(FontAwesomeIcon.Search, "No party finder listing selected.",
                "Open a PF detail window to see member info.");
            return;
        }

        // ── Player rows (info + cached data, no per-row buttons) ────────────
        var hasTomestone = cfg.EnableTomestoneIntegration && !string.IsNullOrEmpty(cfg.TomestoneApiKey);
        var hasFFLogs = cfg.EnableFFLogsIntegrationOverlay && !string.IsNullOrEmpty(cfg.FFLogsClientId) && !string.IsNullOrEmpty(cfg.FFLogsClientSecret);

        var columnCount = 1 + (hasTomestone ? 1 : 0) + (hasFFLogs ? 1 : 0);

        if (ImGui.BeginTable("##members_table", columnCount, OverlayWidgets.TableFlags))
        {
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed);
            if (hasTomestone)
            {
                ImGui.TableSetupColumn("Tomestone", ImGuiTableColumnFlags.WidthFixed);
            }

            if (hasFFLogs)
            {
                ImGui.TableSetupColumn("FFLogs", ImGuiTableColumnFlags.WidthFixed);
            }

            OverlayWidgets.BeginHeaderRow();
            OverlayWidgets.HeaderCell("Player");
            if (hasTomestone)
            {
                OverlayWidgets.HeaderCell("Tomestone");
            }

            if (hasFFLogs)
            {
                OverlayWidgets.HeaderCell("FFLogs");
            }

            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                DrawMemberRow(member, i, cfg, hasTomestone, hasFFLogs);
            }

            ImGui.EndTable();
        }

        // ── Shared Tomestone / FFLogs buttons below all rows ────────────────
        if (cfg.EnableTomestoneIntegration || cfg.EnableFFLogsIntegrationOverlay)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            DrawLookupButtons(members, cfg);
        }

        // Capture this frame's window size for use in PreDraw() clamping next frame
        lastWindowSize = ImGui.GetWindowSize();
    }

    /// <summary>The batch lookup buttons, one per enabled integration.</summary>
    private void DrawLookupButtons(IReadOnlyList<PartyMemberInfo> members, Configuration cfg)
    {
        var isResolving = plugin.PartyFinderManager.HasUnresolvedMembers;

        if (cfg.EnableTomestoneIntegration)
        {
            var hasKey = !string.IsNullOrEmpty(cfg.TomestoneApiKey);
            if (LookupButton("##ts_all", "Tomestone", hasKey, "Tomestone API Key Needed", isResolving, tomestoneBatchInProgress))
            {
                tomestoneBatchInProgress = true;
                tomestoneFetched = true;
                _ = FetchAllTomestoneInfoAsync(members);
            }

            ImguiTooltips.HoveredTooltip(!hasKey
                ? "Add your Tomestone API key in Settings → Tomestone."
                : LookupTooltip("Tomestone", isResolving, tomestoneBatchInProgress));

            if (cfg.EnableFFLogsIntegrationOverlay)
            {
                ImGui.SameLine(0f, M3.Space2);
            }
        }

        if (cfg.EnableFFLogsIntegrationOverlay)
        {
            var hasCredentials = !string.IsNullOrEmpty(cfg.FFLogsClientId) && !string.IsNullOrEmpty(cfg.FFLogsClientSecret);
            if (LookupButton("##ff_all", "FFLogs", hasCredentials, "FFLogs API Key Needed", isResolving, fflogsBatchInProgress))
            {
                fflogsBatchInProgress = true;
                fflogsFetched = true;
                _ = FetchAllFFLogsDataAsync(members);
            }

            ImguiTooltips.HoveredTooltip(!hasCredentials
                ? "Add your FFLogs API client in Settings → FFLogs."
                : LookupTooltip("FFLogs", isResolving, fflogsBatchInProgress));
        }
    }

    /// <summary>
    /// One batch lookup button. It stays disabled until the integration is configured and every
    /// player's name is resolved, and while its lookup is running. Returns true when clicked.
    /// </summary>
    private static bool LookupButton(string id, string name, bool configured, string unconfiguredLabel, bool isResolving, bool inProgress)
    {
        var (label, icon) = !configured ? (unconfiguredLabel, FontAwesomeIcon.Key)
            : inProgress ? (name, FontAwesomeIcon.HourglassHalf)
            : isResolving ? ($"{name} (resolving…)", FontAwesomeIcon.HourglassHalf)
            : (name, FontAwesomeIcon.Search);

        return M3Widgets.Button(id, label, M3ButtonStyle.Tonal, icon, enabled: configured && !isResolving && !inProgress);
    }

    private static string LookupTooltip(string name, bool isResolving, bool inProgress)
    {
        return isResolving ? "Waiting for player names to be resolved…"
            : inProgress ? $"Looking up {name} data for all players…"
            : $"Look up {name} data for all players";
    }

    private void DrawMemberRow(PartyMemberInfo member, int index, Configuration cfg, bool hasTomestone, bool hasFFLogs)
    {
        ImGui.TableNextRow();
        using var id = ImRaii.PushId(index);

        // ── Known-player / blacklist checks ──────────────────────────────────
        var isKnown = cfg.SpecialBorderColorForKnownPlayers &&
                      plugin.PartyFinderManager.IsKnownPlayer(member.Name, member.World);
        var isBlacklisted = plugin.PartyFinderManager.IsBlacklisted(member.Name, member.World);

        if (isKnown)
        {
            var rowColor = ImGui.ColorConvertFloat4ToU32(cfg.KnownPlayerBorderColor with { W = 0.18f });
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, rowColor);
        }

        // ── Column 0: Job icon + player name + badges ─────────────────────
        ImGui.TableSetColumnIndex(0);

        if (cfg.ShowPartyJobIcons && !string.IsNullOrWhiteSpace(member.JobAbbreviation))
        {
            OverlayWidgets.JobIcon(member.JobAbbreviation);
            ImGui.SameLine();
        }

        // Player label
        var isUnresolved = member.Name.StartsWith(PartyFinderManager.UnresolvedNamePrefix)
            || member.Name.StartsWith(PartyFinderManager.UnresolvedPlayerPrefix);
        var isResolved = !isUnresolved;
        string displayName;
        if (member.IsPrivate)
        {
            displayName = $"Private Player {index + 1}";
        }
        else if (isUnresolved && member.Name.StartsWith(PartyFinderManager.UnresolvedPlayerPrefix))
        {
            displayName = member.Name;
        }
        else if (cfg.ShowResolvedPlayerNames && isResolved)
        {
            displayName = $"{member.Name}@{member.World}";
        }
        else
        {
            displayName = $"Player {index + 1}";
        }

        ImGui.AlignTextToFramePadding();
        if (member.IsPrivate)
        {
            ImGui.TextColored(OverlayWidgets.Muted, displayName);
        }
        else if (isResolved)
        {
            OverlayWidgets.LinkText(displayName, $"https://tomestone.gg/character-name/{member.World}/{member.Name}",
                isKnown ? cfg.KnownPlayerBorderColor : null, "Open on Tomestone.gg");
        }
        else
        {
            ImGui.TextUnformatted(displayName);
        }

        if (member.IsPrivate)
        {
            ImGui.SameLine();
            M3Badge.Draw("Private", M3.Scheme.OnSurfaceVariant, "Adventure plate is hidden or unavailable");
        }

        if (isBlacklisted)
        {
            ImGui.SameLine();
            M3Badge.Draw("BL", M3.Scheme.Error, "On your blacklist");
        }

        // ── Column 1: Tomestone data ──────────────────────────────────────
        if (hasTomestone)
        {
            ImGui.TableNextColumn();
            if (!member.IsPrivate && tomestoneFetched)
            {
                if (tomestoneInfoCache.TryGetValue(index, out var cachedTs))
                {
                    OverlayWidgets.TomestoneCell(cachedTs);
                }
                else if (tomestoneBatchInProgress)
                {
                    OverlayWidgets.Pending();
                }
            }
        }

        // ── Column 2: FFLogs data ─────────────────────────────────────────
        if (hasFFLogs)
        {
            ImGui.TableNextColumn();
            if (!member.IsPrivate && fflogsFetched)
            {
                if (fflogsEncounterCache.TryGetValue(index, out var cachedFf))
                {
                    OverlayWidgets.FFLogsCell(cachedFf, member);
                }
                else if (fflogsBatchInProgress)
                {
                    OverlayWidgets.Pending();
                }
            }
        }
    }

    /// <summary>
    /// Fetches FFLogs encounter data for all members in a batch.
    /// Uses encounter-specific queries when the duty is detected,
    /// falls back to general zone parse otherwise.
    /// Updates the cache progressively as data is fetched.
    /// </summary>
    private async Task FetchAllFFLogsDataAsync(IReadOnlyList<PartyMemberInfo> members)
    {
        try
        {
            var encounterIds = FFLogsService.GetEncounterIdsForDuty(
                plugin.PartyFinderManager.CurrentDutyId,
                plugin.PartyFinderManager.CurrentDutyName);

            if (encounterIds.HasValue)
            {
                // Encounter-specific batch query
                var memberData = new List<(string Name, string World, string JobAbbreviation)>();
                for (var i = 0; i < members.Count; i++)
                {
                    var m = members[i];
                    var isUnresolvedSlot = m.IsPrivate
                        || m.Name.StartsWith(PartyFinderManager.UnresolvedNamePrefix)
                        || m.Name.StartsWith(PartyFinderManager.UnresolvedPlayerPrefix);
                    memberData.Add(isUnresolvedSlot
                        ? (string.Empty, string.Empty, m.JobAbbreviation)
                        : (m.Name, m.World, m.JobAbbreviation));
                }

                Dictionary<int, EncounterParseResult> results;
                if (encounterIds.Value.SecondaryEncounterId.HasValue)
                {
                    results = await plugin.FFLogsService.GetMultiEncounterDataForAllAsync(
                        memberData,
                        encounterIds.Value.PrimaryEncounterId,
                        encounterIds.Value.SecondaryEncounterId.Value);
                }
                else
                {
                    results = await plugin.FFLogsService.GetEncounterDataForAllAsync(
                        memberData, encounterIds.Value.PrimaryEncounterId);
                }

                // Update cache immediately with bulk results
                foreach (var (index, result) in results)
                {
                    fflogsEncounterCache[index] = result;
                }

                // Fill in any missing indices
                for (var i = 0; i < members.Count; i++)
                {
                    if (!fflogsEncounterCache.ContainsKey(i))
                    {
                        fflogsEncounterCache[i] = new EncounterParseResult(false, true, 0, null, null, null);
                    }
                }

                // For players with no encounter-specific data, fetch their
                // general average parse so we can show "No logs - Average percentage parse X%"
                for (var i = 0; i < members.Count; i++)
                {
                    var cached = fflogsEncounterCache[i];
                    var hasEncounterData = cached is not null &&
                                           (cached.TotalKills > 0 ||
                                            cached.LowestBossHpPct.HasValue ||
                                            cached.Phase1BestParse.HasValue ||
                                            cached.Phase2BestParse.HasValue ||
                                            cached.Phase2LowestBossHpPct.HasValue);

                    var isUnresolvedMember = members[i].Name.StartsWith(PartyFinderManager.UnresolvedNamePrefix)
                        || members[i].Name.StartsWith(PartyFinderManager.UnresolvedPlayerPrefix);
                    if (cached is not null && !hasEncounterData && !members[i].IsPrivate && !isUnresolvedMember)
                    {
                        try
                        {
                            var avg = await plugin.FFLogsService.GetBestPerfAvgAsync(
                                members[i].Name, members[i].World);
                            if (avg.HasValue)
                            {
                                fflogsEncounterCache[i] = cached with { AverageParsePercent = avg.Value };
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            else
            {
                // Fallback: general zone parse for each member - update cache progressively
                for (var i = 0; i < members.Count; i++)
                {
                    var member = members[i];
                    if (member.IsPrivate
                        || member.Name.StartsWith(PartyFinderManager.UnresolvedNamePrefix)
                        || member.Name.StartsWith(PartyFinderManager.UnresolvedPlayerPrefix))
                    {
                        fflogsEncounterCache[i] = null;
                        continue;
                    }

                    try
                    {
                        var avg = await plugin.FFLogsService.GetBestPerfAvgAsync(
                            member.Name, member.World);
                        fflogsEncounterCache[i] = avg.HasValue
                            ? new EncounterParseResult(true, false, 0, avg.Value, null, null)
                            : new EncounterParseResult(false, false, 0, null, null, null);
                    }
                    catch (Exception ex)
                    {
                        PassportCheckerReborn.Log.Warning(ex,
                            $"[OverlayWindow] FFLogs lookup failed for {member.Name}@{member.World}");
                        fflogsEncounterCache[i] = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[OverlayWindow] FFLogs batch lookup failed.");
        }
        finally
        {
            fflogsBatchInProgress = false;
        }
    }

    /// <summary>
    /// Opens FFLogs character pages in the browser for all members.
    /// </summary>
    private async Task OpenAllFFLogsBrowserAsync(IReadOnlyList<PartyMemberInfo> members)
    {
        foreach (var member in members)
        {
            try
            {
                var characterId = await plugin.FFLogsService.GetCharacterIdAsync(member.Name, member.World);
                if (characterId.HasValue)
                {
                    var url = $"https://www.fflogs.com/character/id/{characterId.Value}";
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Warning(ex,
                    $"[OverlayWindow] FFLogs browser open failed for {member.Name}@{member.World}");
            }
        }
    }

    /// <summary>
    /// Fetches Tomestone character info for all members in a batch.
    /// Passes the current duty name so the API can return encounter-specific data.
    /// Updates the cache progressively as each player's data is fetched.
    /// </summary>
    private async Task FetchAllTomestoneInfoAsync(IReadOnlyList<PartyMemberInfo> members)
    {
        try
        {
            var dutyName = string.IsNullOrWhiteSpace(plugin.PartyFinderManager.CurrentDutyNameEnglish)
                ? plugin.PartyFinderManager.CurrentDutyName
                : plugin.PartyFinderManager.CurrentDutyNameEnglish;

            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member.IsPrivate
                    || member.Name.StartsWith(PartyFinderManager.UnresolvedNamePrefix)
                    || member.Name.StartsWith(PartyFinderManager.UnresolvedPlayerPrefix))
                {
                    tomestoneInfoCache[i] = null;
                    continue;
                }

                try
                {
                    var info = await plugin.TomestoneService.GetCharacterInfoAsync(
                        member.Name, member.World, dutyName);
                    tomestoneInfoCache[i] = info;
                }
                catch (Exception ex)
                {
                    PassportCheckerReborn.Log.Warning(ex,
                        $"[OverlayWindow] Tomestone lookup failed for {member.Name}@{member.World}");
                    tomestoneInfoCache[i] = null;
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[OverlayWindow] Tomestone batch lookup failed.");
        }
        finally
        {
            tomestoneBatchInProgress = false;
        }
    }

    /// <summary>
    /// Opens Tomestone.gg profile pages in the browser for all members.
    /// </summary>
    private async Task OpenAllTomestoneBrowserAsync(IReadOnlyList<PartyMemberInfo> members)
    {
        foreach (var member in members)
        {
            try
            {
                var info = await plugin.TomestoneService.GetCharacterInfoAsync(member.Name, member.World);
                var characterId = info?.CharacterId;

                if (string.IsNullOrWhiteSpace(characterId))
                {
                    characterId = await plugin.TomestoneService.ResolveLodestoneIdAsync(member.Name, member.World);
                }

                TomestoneService.OpenTomestonePage(member.Name, member.World, characterId);
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Warning(ex,
                    $"[OverlayWindow] Tomestone browser open failed for {member.Name}@{member.World}");
                TomestoneService.OpenTomestonePage(member.Name, member.World);
            }
        }
    }
}

/// <summary>Data object representing a single party member seen in a PF listing or party.</summary>
public record PartyMemberInfo(
    string Name,
    string World,
    string JobAbbreviation,
    ulong ContentId = 0,
    bool IsPrivate = false,
    ushort WorldId = 0);
