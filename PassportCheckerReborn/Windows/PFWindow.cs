using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
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
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Not a high-end duty.");
                return;
            }
        }

        var members = plugin.PartyFinderManager.CurrentMembers;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), $"PF Member Info - {plugin.PartyFinderManager.CurrentDutyName}");
        ImGui.Separator();
        ImGui.Spacing();

        if (members.Count == 0)
        {
            ImGui.TextUnformatted("No party finder listing selected.");
            ImGui.TextUnformatted("Open a PF detail window to see member info.");
            return;
        }

        // ── Player rows (info + cached data, no per-row buttons) ────────────
        var hasTomestone = cfg.EnableTomestoneIntegration && !string.IsNullOrEmpty(cfg.TomestoneApiKey);
        var hasFFLogs = cfg.EnableFFLogsIntegrationOverlay && !string.IsNullOrEmpty(cfg.FFLogsClientId) && !string.IsNullOrEmpty(cfg.FFLogsClientSecret);

        var columnCount = 1 + (hasTomestone ? 1 : 0) + (hasFFLogs ? 1 : 0);
        var tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX;

        if (ImGui.BeginTable("##members_table", columnCount, tableFlags))
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

            ImGui.TableHeadersRow();

            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                DrawMemberRow(member, i, cfg, hasTomestone, hasFFLogs);
            }

            ImGui.EndTable();
        }

        // ── Shared Tomestone / FFLogs buttons below all rows ────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var isResolving = plugin.PartyFinderManager.HasUnresolvedMembers;

        if (cfg.EnableTomestoneIntegration)
        {
            if (string.IsNullOrEmpty(cfg.TomestoneApiKey))
            {
                ImGui.BeginDisabled();
                ImGui.SmallButton("Tomestone API Key Needed##ts_all");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("Configure your Tomestone API key in Settings \u2192 Tomestone Integration.");
                }
            }
            else
            {
                var tsDisabled = isResolving || tomestoneBatchInProgress;
                var tsLabel = tomestoneBatchInProgress
                    ? "\u2026##ts_all"
                    : isResolving
                        ? "Tomestone (resolving\u2026)##ts_all"
                        : "Tomestone##ts_all";

                if (tsDisabled)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.SmallButton(tsLabel) && !tsDisabled)
                {
                    tomestoneBatchInProgress = true;
                    tomestoneFetched = true;
                    _ = FetchAllTomestoneInfoAsync(members);
                }

                if (tsDisabled)
                {
                    ImGui.EndDisabled();
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    if (isResolving)
                    {
                        ImGui.SetTooltip("Waiting for player names to be resolved\u2026");
                    }
                    else if (tomestoneBatchInProgress)
                    {
                        ImGui.SetTooltip("Looking up Tomestone data for all players\u2026");
                    }
                    else
                    {
                        ImGui.SetTooltip("Look up Tomestone data for all players");
                    }
                }
            }

            ImGui.SameLine();
        }

        if (cfg.EnableFFLogsIntegrationOverlay)
        {
            if (string.IsNullOrEmpty(cfg.FFLogsClientId) || string.IsNullOrEmpty(cfg.FFLogsClientSecret))
            {
                ImGui.BeginDisabled();
                ImGui.SmallButton("FFLogs API Key Needed##ff_all");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("Configure your FFLogs credentials in Settings \u2192 FFLogs Integration.");
                }
            }
            else
            {
                var ffDisabled = isResolving || fflogsBatchInProgress;
                var ffLabel = fflogsBatchInProgress
                    ? "\u2026##ff_all"
                    : isResolving
                        ? "FFLogs (resolving\u2026)##ff_all"
                        : "FFLogs##ff_all";

                if (ffDisabled)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.SmallButton(ffLabel) && !ffDisabled)
                {
                    fflogsBatchInProgress = true;
                    fflogsFetched = true;
                    _ = FetchAllFFLogsDataAsync(members);
                }

                if (ffDisabled)
                {
                    ImGui.EndDisabled();
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    if (isResolving)
                    {
                        ImGui.SetTooltip("Waiting for player names to be resolved\u2026");
                    }
                    else if (fflogsBatchInProgress)
                    {
                        ImGui.SetTooltip("Looking up FFLogs data for all players\u2026");
                    }
                    else
                    {
                        ImGui.SetTooltip("Look up FFLogs data for all players");
                    }
                }
            }
        }

        // Capture this frame's window size for use in PreDraw() clamping next frame
        lastWindowSize = ImGui.GetWindowSize();
    }

    private void DrawMemberRow(PartyMemberInfo member, int index, Configuration cfg, bool hasTomestone, bool hasFFLogs)
    {
        ImGui.TableNextRow();
        ImGui.PushID(index);

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

        // ── Job icon ──────────────────────────────────────────────────────
        var jobIconId = 0u;
        if (!string.IsNullOrWhiteSpace(member.JobAbbreviation))
        {
            var spec = FFLogsService.GetSpecForJob(member.JobAbbreviation);
            var resolvedIconId = FFLogsService.GetJobIconIdForSpec(spec);
            if (resolvedIconId.HasValue)
            {
                jobIconId = resolvedIconId.Value;
            }
        }

        if (cfg.ShowPartyJobIcons && jobIconId > 0)
        {
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
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{member.JobAbbreviation,-3}]");
                    ImGui.SameLine();
                }
            }
            catch
            {
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{member.JobAbbreviation,-3}]");
                ImGui.SameLine();
            }
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

        if (member.IsPrivate)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), displayName);
        }
        else if (isKnown)
        {
            ClickableText(displayName, $"https://tomestone.gg/character-name/{member.World}/{member.Name}", cfg.KnownPlayerBorderColor);
        }
        else
        {
            if (!isResolved)
                ImGui.TextUnformatted(displayName);
            else
                ClickableText(displayName, $"https://tomestone.gg/character-name/{member.World}/{member.Name}");
        }

        if (member.IsPrivate)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "[Private]");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Adventure plate is hidden or unavailable");
            }
        }

        if (isBlacklisted)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.9f, 0.2f, 0.2f, 1.0f), "[BL]");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("On your blacklist");
            }
        }

        // ── Column 1: Tomestone data ──────────────────────────────────────
        if (hasTomestone)
        {
            ImGui.TableNextColumn();
            if (!member.IsPrivate && tomestoneFetched)
            {
                if (tomestoneInfoCache.TryGetValue(index, out var cachedTs))
                {
                    if (cachedTs != null)
                    {
                        if (cachedTs.NoLogs)
                        {
                            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No Logs");
                        }
                        else
                        {
                            var hasClears = cachedTs.TotalClears.HasValue && cachedTs.TotalClears.Value > 0;
                            var hasProgPoint = !string.IsNullOrWhiteSpace(cachedTs.ProgPoint);
                            var hasBestParse = cachedTs.BestPercent.HasValue;

                            if (hasClears)
                            {
                                var clearsText = "Cleared";
                                if (!string.IsNullOrWhiteSpace(cachedTs.CompletionWeek))
                                {
                                    clearsText += $" ({cachedTs.CompletionWeek})";
                                }

                                if (hasBestParse)
                                {
                                    clearsText += $" | Best: {cachedTs.BestPercent:F0}%";
                                }

                                ImGui.TextColored(new Vector4(0.4f, 0.8f, 0.4f, 1.0f), clearsText);
                            }
                            else if (hasProgPoint)
                            {
                                var progText = cachedTs.ProgPoint!;
                                if (!string.IsNullOrWhiteSpace(cachedTs.DisplayPercent))
                                {
                                    progText += $" ({cachedTs.DisplayPercent})";
                                }

                                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), progText);
                            }
                            else if (hasBestParse)
                            {
                                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
                                    $"Best: {cachedTs.BestPercent:F0}%");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Hidden Profile");
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Hidden Profile");
                    }
                }
                else if (tomestoneBatchInProgress)
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "\u2026");
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
                                if (cachedFf.CurrentJobBestParse.HasValue)
                                {
                                    ImGui.TextColored(new Vector4(0.4f, 0.8f, 0.4f, 1.0f),
                                        $"Cleared {cachedFf.TotalKills}x");
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
                                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f),
                                        $"Cleared {cachedFf.TotalKills}x P1 {p1Parse.Value:F0}% P2 {p2Parse.Value:F0}%");
                                }

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

                                DrawBestParseOnDifferentJob(cachedFf, member);
                            }
                        }
                        else if (cachedFf.TotalKills > 0)
                        {
                            if (cachedFf.CurrentJobBestParse.HasValue)
                            {
                                ImGui.TextColored(GetParseColor(cachedFf.CurrentJobBestParse.Value),
                                    $"Cleared {cachedFf.TotalKills}x {cachedFf.CurrentJobBestParse.Value:F0}%");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f),
                                    $"Cleared {cachedFf.TotalKills}x No Current Job Logs");
                            }

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
                        if (cachedFf.BestParse.HasValue)
                        {
                            ImGui.TextColored(GetParseColor(cachedFf.BestParse.Value),
                                $"Average overall parse {cachedFf.BestParse.Value:F1}%");
                        }
                        else
                        {
                            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "N/A");
                        }
                    }
                }
                else if (fflogsBatchInProgress)
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "\u2026");
                }
            }
        }

        ImGui.PopID();
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
        _ => new Vector4(0.600f, 0.600f, 0.600f, 1.0f),  // Grey (<25)
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
    internal static void DrawBestParseOnDifferentJob(EncounterParseResult cachedFf, PartyMemberInfo member)
    {
        if (!cachedFf.BestParse.HasValue || cachedFf.BestParseJobAbbreviation == null)
        {
            return;
        }

        // If current job is the best job, only current job parse is shown – skip
        if (string.Equals(cachedFf.BestParseJobAbbreviation, member.JobAbbreviation,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // If the current job parse already matches or exceeds the best, skip
        if (cachedFf.CurrentJobBestParse.HasValue &&
            cachedFf.BestParse.Value <= cachedFf.CurrentJobBestParse.Value)
        {
            return;
        }

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

    internal static void ClickableText(string content, string url, Vector4? col = null)
    {
        using var _ = ImRaii.PushColor(ImGuiCol.Text, col);
        if (ImGui.Selectable(content)) Dalamud.Utility.Util.OpenLink(url);
        if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
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
