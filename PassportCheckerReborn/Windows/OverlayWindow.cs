using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PassportCheckerReborn.Services;

namespace PassportCheckerReborn.Windows;

/// <summary>
/// The member-info overlay that appears alongside the Party Finder detail pane.
/// It lists all current party-finder members (fetched via
/// <see cref="Services.PartyFinderManager"/>) and, via two shared buttons below the
/// player list, performs batch Tomestone / FFLogs lookups for every member at once.
/// </summary>
public class OverlayWindow(PassportCheckerReborn plugin) : Window("PF Member Info##PFCheckerOverlay",
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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public override unsafe void PreDraw()
    {
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

                    float overlayX;
                    if (plugin.Configuration.ShowOverlayOnLeftSide)
                    {
                        // Place overlay to the left of the addon
                        // Estimate overlay width at ~300px (auto-resize will adjust)
                        overlayX = vpPos.X + addonX - 310;
                        if (overlayX < vpPos.X)
                            overlayX = vpPos.X; // Clamp to screen edge
                    }
                    else
                    {
                        // Place overlay to the right of the addon
                        overlayX = vpPos.X + addonX + addonWidth + 10;
                    }

                    var overlayY = vpPos.Y + addonY;

                    Position = new Vector2(overlayX, overlayY);
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
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Not a high-end duty.");
                return;
            }
        }

        var members = plugin.PartyFinderManager.CurrentMembers;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "PF Member Info");
        ImGui.Separator();
        ImGui.Spacing();

        if (members.Count == 0)
        {
            ImGui.TextUnformatted("No party finder listing selected.");
            ImGui.TextUnformatted("Open a PF detail window to see member info.");
            return;
        }

        // ── Player rows (info + cached data, no per-row buttons) ────────────
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            DrawMemberRow(member, i, cfg);
        }

        // ── Shared Tomestone / FFLogs buttons below all rows ────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var isResolving = plugin.PartyFinderManager.HasUnresolvedMembers;

        if (cfg.EnableTomestoneIntegration)
        {
            var tsDisabled = isResolving || tomestoneBatchInProgress;
            var tsLabel = tomestoneBatchInProgress
                ? "\u2026##ts_all"
                : isResolving
                    ? "Tomestone (resolving\u2026)##ts_all"
                    : "Tomestone##ts_all";

            if (tsDisabled)
                ImGui.BeginDisabled();

            if (ImGui.SmallButton(tsLabel) && !tsDisabled)
            {
                tomestoneBatchInProgress = true;
                tomestoneFetched = true;
                _ = FetchAllTomestoneInfoAsync(members);
            }

            if (tsDisabled)
                ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                if (isResolving)
                    ImGui.SetTooltip("Waiting for player names to be resolved\u2026");
                else if (tomestoneBatchInProgress)
                    ImGui.SetTooltip("Looking up Tomestone data for all players\u2026");
                else
                    ImGui.SetTooltip("Look up Tomestone data for all players");
            }

            ImGui.SameLine();
        }

        if (cfg.EnableFFLogsIntegrationOverlay)
        {
            var ffDisabled = isResolving || fflogsBatchInProgress;
            var ffLabel = fflogsBatchInProgress
                ? "\u2026##ff_all"
                : isResolving
                    ? "FFLogs (resolving\u2026)##ff_all"
                    : "FFLogs##ff_all";

            if (ffDisabled)
                ImGui.BeginDisabled();

            if (ImGui.SmallButton(ffLabel) && !ffDisabled)
            {
                fflogsBatchInProgress = true;
                fflogsFetched = true;
                _ = FetchAllFFLogsDataAsync(members);
            }

            if (ffDisabled)
                ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                if (isResolving)
                    ImGui.SetTooltip("Waiting for player names to be resolved\u2026");
                else if (fflogsBatchInProgress)
                    ImGui.SetTooltip("Looking up FFLogs data for all players\u2026");
                else
                    ImGui.SetTooltip("Look up FFLogs data for all players");
            }
        }
    }

    private void DrawMemberRow(PartyMemberInfo member, int index, Configuration cfg)
    {
        ImGui.PushID(index);

        // ── Known-player border (TODO item 7) ────────────────────────────────
        var isKnown = cfg.SpecialBorderColorForKnownPlayers &&
                      plugin.PartyFinderManager.IsKnownPlayer(member.Name, member.World);
        var isBlacklisted = plugin.PartyFinderManager.IsBlacklisted(member.Name, member.World);

        if (isKnown)
        {
            // Draw a coloured border around this member's row using ImGui draw list
            var cursorPos = ImGui.GetCursorScreenPos();
            var borderColor = ImGui.ColorConvertFloat4ToU32(cfg.KnownPlayerBorderColor);
            var drawList = ImGui.GetWindowDrawList();

            // Estimate row height (~20px) and width (~400px) – auto-adjusted after content
            const float estimatedRowWidth = 400f;
            const float estimatedRowHeight = 22f;
            var rowMin = cursorPos - new Vector2(2, 2);
            var rowMax = cursorPos + new Vector2(estimatedRowWidth, estimatedRowHeight);
            drawList.AddRect(rowMin, rowMax, borderColor, 3.0f, ImDrawFlags.RoundCornersAll, 2.0f);
        }

        // ── Job icon (TODO item 8) ──────────────────────────────────────────
        var jobIconId = 0u;
        if (!string.IsNullOrWhiteSpace(member.JobAbbreviation))
        {
            var spec = FFLogsService.GetSpecForJob(member.JobAbbreviation);
            var resolvedIconId = FFLogsService.GetJobIconIdForSpec(spec);
            if (resolvedIconId.HasValue)
                jobIconId = resolvedIconId.Value;
        }

        if (cfg.ShowPartyJobIcons && jobIconId > 0)
        {
            // Try to load the actual job icon texture from the game data
            try
            {
                var iconLookup = new GameIconLookup(jobIconId);
                var iconHandle = PassportCheckerReborn.TextureProvider.GetFromGameIcon(iconLookup);
                var texture = iconHandle.GetWrapOrDefault();

                if (texture is not null)
                {
                    ImGui.Image(texture.Handle, new Vector2(20, 20));
                    ImGui.SameLine();
                }
                else
                {
                    // Texture not yet loaded – show abbreviation as fallback
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{member.JobAbbreviation,-3}]");
                    ImGui.SameLine();
                }
            }
            catch
            {
                // Fallback to text abbreviation if texture loading fails
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{member.JobAbbreviation,-3}]");
                ImGui.SameLine();
            }
        }

        // Player label
        var isResolved = !member.Name.StartsWith(Services.PartyFinderManager.UnresolvedNamePrefix);
        var displayName = cfg.ShowResolvedPlayerNames && isResolved
            ? $"{member.Name}@{member.World}"
            : $"Player {index + 1}";

        if (isKnown)
            ImGui.TextColored(cfg.KnownPlayerBorderColor, displayName);
        else
            ImGui.TextUnformatted(displayName);

        if (isBlacklisted)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.9f, 0.2f, 0.2f, 1.0f), "[BL]");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("On your blacklist");
        }

        // ── Cached Tomestone data
        if (cfg.EnableTomestoneIntegration && tomestoneFetched)
        {
            if (tomestoneBatchInProgress)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "\u2026");
            }
            else if (tomestoneInfoCache.TryGetValue(index, out var cachedTs))
            {
                ImGui.SameLine();
                if (cachedTs != null)
                {
                    var hasClears = cachedTs.TotalClears.HasValue && cachedTs.TotalClears.Value > 0;
                    var hasProgPoint = !string.IsNullOrWhiteSpace(cachedTs.ProgPoint);
                    var hasBestParse = cachedTs.BestPercent.HasValue;

                    if (hasClears)
                    {
                        var clearsColor = new Vector4(0.4f, 0.8f, 0.4f, 1.0f);
                        var clearsText = "Cleared";
                        if (!string.IsNullOrWhiteSpace(cachedTs.CompletionWeek))
                            clearsText += $" ({cachedTs.CompletionWeek})";
                        if (hasBestParse)
                            clearsText += $" | Best: {cachedTs.BestPercent:F0}%";
                        ImGui.TextColored(clearsColor, clearsText);
                    }
                    else if (hasProgPoint)
                    {
                        var progText = cachedTs.ProgPoint!;
                        if (!string.IsNullOrWhiteSpace(cachedTs.DisplayPercent))
                            progText += $" ({cachedTs.DisplayPercent})";
                        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), progText);
                    }
                    else if (hasBestParse)
                    {
                        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
                            $"Best: {cachedTs.BestPercent:F0}%");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No data");
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No data");
                }
            }
        }

        // ── Cached FFLogs encounter data (only shown after user clicks FFLogs button) ──
        if (cfg.EnableFFLogsIntegrationOverlay && fflogsFetched)
        {
            if (fflogsBatchInProgress)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "\u2026");
            }
            else if (fflogsEncounterCache.TryGetValue(index, out var cachedFf))
            {
                ImGui.SameLine();
                if (cachedFf is null || !cachedFf.HasData)
                {
                    DrawNoLogsWithAverage(cachedFf?.AverageParsePercent);
                }
                else if (cachedFf.IsEncounterSpecific)
                {
                    var hasMultiPhaseData = cachedFf.Phase1TotalKills.HasValue ||
                                            cachedFf.Phase2TotalKills.HasValue ||
                                            cachedFf.Phase1BestParse.HasValue ||
                                            cachedFf.Phase2BestParse.HasValue ||
                                            cachedFf.Phase2LowestBossHpPct.HasValue;

                    if (hasMultiPhaseData)
                    {
                        var p1Parse = cachedFf.Phase1BestParse;
                        var p2Parse = cachedFf.Phase2BestParse;

                        if (cachedFf.TotalKills > 0 && p1Parse.HasValue && p2Parse.HasValue)
                        {
                            // Show best parse for current job if available
                            if (cachedFf.CurrentJobBestParse.HasValue)
                            {
                                ImGui.TextColored(new Vector4(0.4f, 0.8f, 0.4f, 1.0f),
                                    $"Cleared {cachedFf.TotalKills}X");
                                ImGui.SameLine();
                                ImGui.TextUnformatted("P1");
                                ImGui.SameLine();
                                ImGui.TextColored(GetParseColor(p1Parse.Value), $"{p1Parse.Value:F0}%");
                                ImGui.SameLine();
                                ImGui.TextUnformatted("P2");
                                ImGui.SameLine();
                                ImGui.TextColored(GetParseColor(p2Parse.Value), $"{p2Parse.Value:F0}%");
                            }
                            else
                            {
                                // Has clears but no current job logs
                                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f),
                                    $"Cleared {cachedFf.TotalKills}X P1 {p1Parse.Value:F0}% P2 {p2Parse.Value:F0}%");
                            }

                            // Show best parse on a different job if applicable
                            DrawBestParseOnDifferentJob(cachedFf, member);
                        }
                        else
                        {
                            if (p1Parse.HasValue)
                            {
                                ImGui.TextColored(GetParseColor(p1Parse.Value), $"P1 {p1Parse.Value:F0}%");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "P1 No logs");
                            }

                            ImGui.SameLine();

                            if (cachedFf.Phase2LowestBossHpPct.HasValue)
                            {
                                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
                                    $"P2 {cachedFf.Phase2LowestBossHpPct.Value:F0}%");
                            }
                            else if (p2Parse.HasValue)
                            {
                                ImGui.TextColored(GetParseColor(p2Parse.Value), $"P2 {p2Parse.Value:F0}%");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "P2 No logs");
                            }

                            // Show best parse on a different job if applicable
                            DrawBestParseOnDifferentJob(cachedFf, member);
                        }
                    }
                    else if (cachedFf.TotalKills > 0)
                    {
                        // Show best parse for current job
                        if (cachedFf.CurrentJobBestParse.HasValue)
                        {
                            var parseStr = $"{cachedFf.CurrentJobBestParse.Value:F0}%";
                            var displayStr = $"Cleared {cachedFf.TotalKills}X {parseStr}";
                            ImGui.TextColored(GetParseColor(cachedFf.CurrentJobBestParse.Value), displayStr);
                        }
                        else
                        {
                            // Player has clears but no logs on their current job
                            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f),
                                $"Cleared {cachedFf.TotalKills}X No Current Job Logs");
                        }

                        // Show best parse on a different job only if it differs from current job
                        DrawBestParseOnDifferentJob(cachedFf, member);
                    }
                    else if (cachedFf.LowestBossHpPct.HasValue)
                    {
                        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
                            $"{cachedFf.LowestBossHpPct.Value:F0}%");
                    }
                    else if (cachedFf.AverageParsePercent.HasValue)
                    {
                        DrawNoLogsWithAverage(cachedFf.AverageParsePercent);
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No logs");
                    }
                }
                else
                {
                    // General parse fallback (encounter not detected)
                    if (cachedFf.BestParse.HasValue)
                    {
                        var color = GetParseColor(cachedFf.BestParse.Value);
                        ImGui.TextColored(color, $" Best parse {cachedFf.BestParse.Value:F1}%");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "N/A");
                    }
                }
            }
        }

        ImGui.PopID();
    }

    /// <summary>
    /// Fetches FFLogs encounter data for all members in a batch.
    /// Uses encounter-specific queries when the duty is detected,
    /// falls back to general zone parse otherwise.
    /// </summary>
    private async Task FetchAllFFLogsDataAsync(IReadOnlyList<PartyMemberInfo> members)
    {
        try
        {
            var tempCache = new Dictionary<int, EncounterParseResult?>();

            var encounterIds = FFLogsService.GetEncounterIdsForDuty(
                plugin.PartyFinderManager.CurrentDutyName);

            if (encounterIds.HasValue)
            {
                // Encounter-specific batch query
                var memberData = new List<(string Name, string World, string JobAbbreviation)>();
                for (var i = 0; i < members.Count; i++)
                    memberData.Add((members[i].Name, members[i].World, members[i].JobAbbreviation));

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

                foreach (var (index, result) in results)
                    tempCache[index] = result;

                // Fill in any missing indices
                for (var i = 0; i < members.Count; i++)
                {
                    if (!tempCache.ContainsKey(i))
                        tempCache[i] = new EncounterParseResult(false, true, 0, null, null, null);
                }

                // For players with no encounter-specific data, fetch their
                // general average parse so we can show "No logs - Average percentage parse X%"
                for (var i = 0; i < members.Count; i++)
                {
                    var cached = tempCache[i];
                    var hasEncounterData = cached is not null &&
                                           (cached.TotalKills > 0 ||
                                            cached.LowestBossHpPct.HasValue ||
                                            cached.Phase1BestParse.HasValue ||
                                            cached.Phase2BestParse.HasValue ||
                                            cached.Phase2LowestBossHpPct.HasValue);

                    if (cached is not null && !hasEncounterData)
                    {
                        try
                        {
                            var avg = await plugin.FFLogsService.GetBestPerfAvgAsync(
                                members[i].Name, members[i].World);
                            if (avg.HasValue)
                                tempCache[i] = cached with { AverageParsePercent = avg.Value };
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            else
            {
                // Fallback: general zone parse for each member (sequential to avoid dictionary races)
                for (var i = 0; i < members.Count; i++)
                {
                    var member = members[i];
                    try
                    {
                        var avg = await plugin.FFLogsService.GetBestPerfAvgAsync(
                            member.Name, member.World);
                        tempCache[i] = avg.HasValue
                            ? new EncounterParseResult(true, false, 0, avg.Value, null, null)
                            : new EncounterParseResult(false, false, 0, null, null, null);
                    }
                    catch (Exception ex)
                    {
                        PassportCheckerReborn.Log.Warning(ex,
                            $"[OverlayWindow] FFLogs lookup failed for {member.Name}@{member.World}");
                        tempCache[i] = null;
                    }
                }
            }

            // Swap atomically so the UI never sees a partially populated cache
            fflogsEncounterCache = tempCache;
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
    /// </summary>
    private async Task FetchAllTomestoneInfoAsync(IReadOnlyList<PartyMemberInfo> members)
    {
        try
        {
            var tempCache = new Dictionary<int, TomestoneCharacterInfo?>();
            var dutyName = plugin.PartyFinderManager.CurrentDutyName;

            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                try
                {
                    var info = await plugin.TomestoneService.GetCharacterInfoAsync(
                        member.Name, member.World, dutyName);
                    tempCache[i] = info;
                }
                catch (Exception ex)
                {
                    PassportCheckerReborn.Log.Warning(ex,
                        $"[OverlayWindow] Tomestone lookup failed for {member.Name}@{member.World}");
                    tempCache[i] = null;
                }
            }

            // Swap atomically so the UI never sees a partially populated cache
            tomestoneInfoCache = tempCache;
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
                    characterId = await plugin.TomestoneService.ResolveLodestoneIdAsync(member.Name, member.World);

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

    /// <summary>
    /// Returns an FFLogs-style color for the given parse percentile.
    /// </summary>
    internal static Vector4 GetParseColor(double percentile) => percentile switch
    {
        >= 99 => new Vector4(0.898f, 0.800f, 0.502f, 1.0f),  // Gold (99+)
        >= 95 => new Vector4(0.894f, 0.510f, 0.200f, 1.0f),  // Orange (95-98)
        >= 75 => new Vector4(0.635f, 0.282f, 0.808f, 1.0f),  // Purple (75-94)
        >= 50 => new Vector4(0.118f, 0.392f, 1.000f, 1.0f),  // Blue (50-74)
        >= 25 => new Vector4(0.118f, 0.784f, 0.118f, 1.0f),  // Green (25-49)
        _     => new Vector4(0.600f, 0.600f, 0.600f, 1.0f),  // Grey (<25)
    };

    /// <summary>
    /// Draws "No logs - Average percentage parse X%" with color, or plain "No logs" if no average.
    /// </summary>
    internal static void DrawNoLogsWithAverage(double? averageParsePercent)
    {
        if (averageParsePercent.HasValue)
        {
            var avgColor = GetParseColor(averageParsePercent.Value);
            ImGui.TextColored(avgColor,
                $"No logs - Average percentage parse {averageParsePercent.Value:F0}%");
        }
        else
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No logs");
        }
    }

    /// <summary>
    /// If the overall best parse is on a different job from the member's current job,
    /// draws it on a new line with the job icon. Does nothing when the current job
    /// IS the best job (no redundant display).
    /// </summary>
    private static void DrawBestParseOnDifferentJob(EncounterParseResult cachedFf, PartyMemberInfo member)
    {
        if (!cachedFf.BestParse.HasValue || cachedFf.BestParseJobAbbreviation == null)
            return;

        // If current job is the best job, only current job parse is shown – skip
        if (string.Equals(cachedFf.BestParseJobAbbreviation, member.JobAbbreviation,
                StringComparison.OrdinalIgnoreCase))
            return;

        // If the current job parse already matches or exceeds the best, skip
        if (cachedFf.CurrentJobBestParse.HasValue &&
            cachedFf.BestParse.Value <= cachedFf.CurrentJobBestParse.Value)
            return;

        DrawJobSpecBestParse(cachedFf.BestParse.Value, cachedFf.BestParseJobAbbreviation,
            cachedFf.BestParseJobIconId);
    }

    /// <summary>
    /// Draws a "Best on [icon] JOB: X%" line showing a parse for a specific job.
    /// </summary>
    internal static void DrawJobSpecBestParse(double parse, string jobAbbreviation, uint? jobIconId)
    {
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Best:");

        // Draw job icon
        ImGui.SameLine();
        if (jobIconId.HasValue)
        {
            try
            {
                var iconLookup = new GameIconLookup(jobIconId.Value);
                var iconHandle = PassportCheckerReborn.TextureProvider.GetFromGameIcon(iconLookup);
                var texture = iconHandle.GetWrapOrDefault();
                if (texture is not null)
                {
                    ImGui.Image(texture.Handle, new Vector2(16, 16));
                    ImGui.SameLine();
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{jobAbbreviation}]");
                    ImGui.SameLine();
                }
            }
            catch
            {
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{jobAbbreviation}]");
                ImGui.SameLine();
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{jobAbbreviation}]");
            ImGui.SameLine();
        }

        ImGui.TextColored(GetParseColor(parse), $"{parse:F0}%");
    }
}

/// <summary>Data object representing a single party member seen in a PF listing or party.</summary>
public record PartyMemberInfo(
    string Name,
    string World,
    string JobAbbreviation,
    ulong ContentId = 0);
