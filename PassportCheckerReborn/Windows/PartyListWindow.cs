using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using PassportCheckerReborn.Services;

namespace PassportCheckerReborn.Windows;

/// <summary>
/// An overlay window that shows FFLogs parse data for current party members,
/// attached to the in-game Party Members UI element (_PartyList) or as a free-floating window.
/// </summary>
public class PartyListWindow(PassportCheckerReborn plugin) : Window("Party Member Info##PFCheckerPartyList",
           ImGuiWindowFlags.NoTitleBar |
               ImGuiWindowFlags.NoResize |
               ImGuiWindowFlags.NoMove |
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.AlwaysAutoResize), IDisposable
{
    private readonly PassportCheckerReborn plugin = plugin;

    /// <summary>Cached overlay size from the previous frame, used for Above positioning.</summary>
    private Vector2 lastFrameSize = new(310, 200);

    // Cached party member list
    private List<PartyMemberInfo> cachedPartyMembers = [];

    // Per-member FFLogs encounter cache (index → result)
    private Dictionary<int, EncounterParseResult?> fflogsCache = [];
    private bool fflogsBatchInProgress;

    // Per-member Tomestone info cache (index → character info)
    private Dictionary<int, TomestoneCharacterInfo?> tomestoneCache = [];
    private bool tomestoneBatchInProgress;

    // Duty selection for encounter-specific lookups
    private string[] dutyNames = [];
    private int selectedDutyIndex;
    private string? selectedDutyName;
    private bool dutyListInitialized;

    // Tracks when party composition changes to re-fetch data
    private string lastPartyCompositionKey = string.Empty;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public override unsafe void PreDraw()
    {
        var position = plugin.Configuration.PartyListOverlayPosition;

        // For Unbound mode, make the window freely movable.
        if (position == PartyListOverlayPosition.Unbound)
        {
            Flags = ImGuiWindowFlags.NoTitleBar |
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.AlwaysAutoResize;
            // Clear any previously set position so ImGui allows free movement.
            Position = null;
            return;
        }

        // All other modes: lock position/movement and snap to the party list addon.
        Flags = ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.AlwaysAutoResize;

        // Position this window relative to the party list addon.
        // Try _PartyList first, then fall back to _CrossWorldPartyList for crossworld parties.
        if (TryPositionRelativeToAddon("_PartyList", position))
            return;
        if (TryPositionRelativeToAddon("_CrossWorldPartyList", position))
            return;
    }

    /// <summary>
    /// Attempts to position this window relative to the named addon.
    /// Returns <c>true</c> if the addon was found, visible, and the position was set.
    /// </summary>
    private unsafe bool TryPositionRelativeToAddon(string addonName, PartyListOverlayPosition position)
    {
        try
        {
            var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName(addonName, 1);
            if (addonPtr.IsNull)
                return false;

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (!addon->IsVisible)
                return false;

            var addonX = addon->X;
            var addonY = addon->Y;
            var addonWidth = addon->GetScaledWidth(true);
            var addonHeight = addon->GetScaledHeight(true);

            var vpPos = ImGui.GetMainViewport().Pos;

            float overlayX;
            float overlayY;

            switch (position)
            {
                case PartyListOverlayPosition.Left:
                    overlayX = vpPos.X + addonX - 310;
                    if (overlayX < vpPos.X)
                        overlayX = vpPos.X; // Clamp to screen edge
                    overlayY = vpPos.Y + addonY;
                    break;

                case PartyListOverlayPosition.Right:
                    overlayX = vpPos.X + addonX + addonWidth + 5;
                    overlayY = vpPos.Y + addonY;
                    break;

                case PartyListOverlayPosition.Above:
                    overlayX = vpPos.X + addonX;
                    overlayY = vpPos.Y + addonY - lastFrameSize.Y - 5;
                    if (overlayY < vpPos.Y)
                        overlayY = vpPos.Y; // Clamp to screen edge
                    break;

                case PartyListOverlayPosition.Below:
                    overlayX = vpPos.X + addonX;
                    overlayY = vpPos.Y + addonY + addonHeight + 5;
                    break;

                default:
                    return false;
            }

            Position = new Vector2(overlayX, overlayY);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        if (!cfg.ShowPartyListOverlay ||
            (!cfg.EnableFFLogsIntegrationOverlay && !cfg.EnableTomestoneIntegration))
        {
            IsOpen = false;
            return;
        }

        // Read current party members
        var partyMembers = ReadPartyMembers();
        if (partyMembers.Count == 0)
        {
            var partyListVisible = IsPartyListVisible();
            if (!partyListVisible)
            {
                IsOpen = false;
                return;
            }
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Waiting for party data\u2026");
            return;
        }

        // Build the duty name list once
        if (!dutyListInitialized)
        {
            InitializeDutyList();
            dutyListInitialized = true;
        }

        // Check if party composition changed
        var compositionKey = BuildCompositionKey(partyMembers);
        if (compositionKey != lastPartyCompositionKey)
        {
            lastPartyCompositionKey = compositionKey;
            cachedPartyMembers = partyMembers;
            fflogsCache = [];
            tomestoneCache = [];

            // Auto-fetch data for party members
            AutoFetchData(partyMembers, cfg);
        }

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Party Member Info");
        ImGui.SameLine();
        if (ImGui.SmallButton("Hide"))
        {
            cfg.ShowPartyListOverlay = false;
            cfg.Save();
            IsOpen = false;
            return;
        }
        ImGui.Separator();
        ImGui.Spacing();

        for (var i = 0; i < cachedPartyMembers.Count; i++)
        {
            var member = cachedPartyMembers[i];
            DrawPartyMemberRow(member, i, cfg);
        }

        if (fflogsBatchInProgress || tomestoneBatchInProgress)
        {
            ImGui.Spacing();
            var loadingText = fflogsBatchInProgress && tomestoneBatchInProgress
                ? "Loading FFLogs & Tomestone data\u2026"
                : fflogsBatchInProgress
                    ? "Loading FFLogs data\u2026"
                    : "Loading Tomestone data\u2026";
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), loadingText);
        }

        // ── Duty selection dropdown ─────────────────────────────────────────
        if (dutyNames.Length > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "Duty:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(250f);
            if (ImGui.Combo("##party_duty_select", ref selectedDutyIndex, dutyNames, dutyNames.Length))
            {
                selectedDutyName = selectedDutyIndex > 0 ? dutyNames[selectedDutyIndex] : null;
                // Re-fetch data with new duty selection
                fflogsCache = [];
                tomestoneCache = [];
                AutoFetchData(cachedPartyMembers, cfg);
            }
        }

        // Cache the window size for Above positioning on the next frame.
        lastFrameSize = ImGui.GetWindowSize();
    }

    private void DrawPartyMemberRow(PartyMemberInfo member, int index, Configuration cfg)
    {
        ImGui.PushID($"party_{index}");

        var jobIconId = 0u;
        if (!string.IsNullOrWhiteSpace(member.JobAbbreviation))
        {
            var spec = FFLogsService.GetSpecForJob(member.JobAbbreviation);
            var resolvedIconId = FFLogsService.GetJobIconIdForSpec(spec);
            if (resolvedIconId.HasValue)
                jobIconId = resolvedIconId.Value;
        }

        // ── Job icon ────────────────────────────────────────────────────────
        if (cfg.ShowPartyJobIcons)
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

        // Player name + world
        var displayName = string.IsNullOrEmpty(member.World)
            ? member.Name
            : $"{member.Name}@{member.World}";
        ImGui.TextUnformatted(displayName);

        // ── Cached FFLogs data ──────────────────────────────────────────────
        if (cfg.EnableFFLogsIntegrationOverlay && !string.IsNullOrEmpty(cfg.FFLogsClientId) && !string.IsNullOrEmpty(cfg.FFLogsClientSecret) && fflogsCache.TryGetValue(index, out var cachedFf))
        {
            ImGui.SameLine();
            if (cachedFf is null || !cachedFf.HasData)
            {
                OverlayWindow.DrawNoLogsWithAverage(cachedFf?.AverageParsePercent);
            }
            else if (cachedFf.BestParse.HasValue)
            {
                // Show best parse for current job
                if (cachedFf.CurrentJobBestParse.HasValue)
                {
                    var color = OverlayWindow.GetParseColor(cachedFf.CurrentJobBestParse.Value);
                    ImGui.TextColored(color, $"{cachedFf.CurrentJobBestParse.Value:F0}%");
                }
                else
                {
                    var color = OverlayWindow.GetParseColor(cachedFf.BestParse.Value);
                    ImGui.TextColored(color, $"{cachedFf.BestParse.Value:F0}%");
                }

                // If best parse is on a different job, show it with icon
                if (cachedFf.BestParseJobAbbreviation != null &&
                    !string.Equals(cachedFf.BestParseJobAbbreviation, member.JobAbbreviation,
                        StringComparison.OrdinalIgnoreCase) &&
                    (!cachedFf.CurrentJobBestParse.HasValue ||
                     cachedFf.BestParse.Value > cachedFf.CurrentJobBestParse.Value))
                {
                    OverlayWindow.DrawJobSpecBestParse(cachedFf.BestParse.Value,
                        cachedFf.BestParseJobAbbreviation, cachedFf.BestParseJobIconId);
                }
            }
            else if (cachedFf.AverageParsePercent.HasValue)
            {
                OverlayWindow.DrawNoLogsWithAverage(cachedFf.AverageParsePercent);
            }
            else
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "No logs");
            }
        }

        // ── Cached Tomestone data ───────────────────────────────────────────
        if (cfg.EnableTomestoneIntegration && !string.IsNullOrEmpty(cfg.TomestoneApiKey) && tomestoneCache.TryGetValue(index, out var cachedTs))
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

        ImGui.PopID();
    }

    /// <summary>
    /// Reads party members from the Dalamud IPartyList service, falling back to
    /// <see cref="InfoProxyCrossRealm"/> for crossworld parties when IPartyList is empty.
    /// </summary>
    private static List<PartyMemberInfo> ReadPartyMembers()
    {
        var result = new List<PartyMemberInfo>();

        try
        {
            var partyList = PassportCheckerReborn.PartyList;
            if (partyList != null && partyList.Length > 0)
            {
                for (var i = 0; i < partyList.Length; i++)
                {
                    var member = partyList[i];
                    if (member == null)
                        continue;

                    var name = member.Name.TextValue;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var world = member.World.ValueNullable?.Name.ToString() ?? string.Empty;
                    var classJob = member.ClassJob.ValueNullable;
                    var jobAbbreviation = classJob?.Abbreviation.ToString() ?? "???";

                    result.Add(new PartyMemberInfo(name, world, jobAbbreviation));
                }

                return result;
            }

            // Fallback: read from InfoProxyCrossRealm for crossworld parties
            result = ReadCrossRealmPartyMembers();
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>
    /// Reads party members from <see cref="InfoProxyCrossRealm"/> when in a crossworld party.
    /// This provides member data (including ContentId) even when <see cref="Dalamud.Plugin.Services.IPartyList"/> hasn't populated.
    /// </summary>
    private static unsafe List<PartyMemberInfo> ReadCrossRealmPartyMembers()
    {
        var result = new List<PartyMemberInfo>();
        try
        {
            var cwProxy = InfoProxyCrossRealm.Instance();
            if (cwProxy == null || !cwProxy->IsInCrossRealmParty)
            {
                return result;
            }

            var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();
            var classJobSheet = PassportCheckerReborn.DataManager.GetExcelSheet<ClassJob>();

            var localIndex = cwProxy->LocalPlayerGroupIndex;
            var memberCount = InfoProxyCrossRealm.GetGroupMemberCount(localIndex);
            for (var i = 0; i < memberCount; i++)
            {
                var memberPtr = InfoProxyCrossRealm.GetGroupMember((uint)i, localIndex);
                if (memberPtr == null)
                    continue;

                var member = *memberPtr;
                if (member.HomeWorld == -1 || string.IsNullOrEmpty(member.NameString))
                    continue;

                var worldName = worldSheet?.GetRowOrDefault((uint)member.HomeWorld)?.Name.ToString()
                    ?? string.Empty;
                var jobAbbreviation = classJobSheet?.GetRowOrDefault(member.ClassJobId)?.Abbreviation.ToString()
                    ?? "???";
                var contentId = member.ContentId;
                result.Add(new PartyMemberInfo(member.NameString, worldName, jobAbbreviation, contentId));
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>
    /// Checks whether the party list addon (<c>_PartyList</c>) or crossworld party list
    /// addon (<c>_CrossWorldPartyList</c>) is currently visible.
    /// </summary>
    internal static unsafe bool IsPartyListVisible()
    {
        try
        {
            var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("_PartyList", 1);
            if (!addonPtr.IsNull)
            {
                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon->IsVisible)
                {
                    return true;
                }
            }

            // Fallback: check the crossworld party list addon
            var cwAddonPtr = PassportCheckerReborn.GameGui.GetAddonByName("_CrossWorldPartyList", 1);
            if (!cwAddonPtr.IsNull)
            {
                var cwAddon = (AtkUnitBase*)cwAddonPtr.Address;
                if (cwAddon->IsVisible)
                {
                    return true;
                }
            }

        }
        catch
        {
            // Ignore addon access failures
        }

        return false;
    }

    private static string BuildCompositionKey(List<PartyMemberInfo> members)
    {
        var parts = new List<string>();
        foreach (var m in members)
            parts.Add($"{m.Name}@{m.World}:{m.JobAbbreviation}");
        parts.Sort();
        return string.Join("|", parts);
    }

    /// <summary>
    /// Initializes the duty name dropdown list from both FFLogs and Tomestone duty maps.
    /// </summary>
    private void InitializeDutyList()
    {
        var allDuties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in FFLogsService.GetAllSupportedDutyNames())
            allDuties.Add(name);
        foreach (var name in TomestoneService.GetAllSupportedDutyNames())
            allDuties.Add(name);

        var sorted = new List<string>(allDuties);
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        sorted.Insert(0, "(None)");
        dutyNames = [.. sorted];
        selectedDutyIndex = 0;
        selectedDutyName = null;
    }

    /// <summary>
    /// Auto-fetches FFLogs and/or Tomestone data for the given party members.
    /// Uses the selected duty name from the dropdown, falling back to the PF
    /// current duty name.
    /// </summary>
    private void AutoFetchData(List<PartyMemberInfo> members, Configuration cfg)
    {
        if (members.Count == 0) return;

        if (cfg.EnableFFLogsIntegrationOverlay && !string.IsNullOrEmpty(cfg.FFLogsClientId) && !string.IsNullOrEmpty(cfg.FFLogsClientSecret))
        {
            fflogsBatchInProgress = true;
            _ = FetchFFLogsDataAsync(members);
        }

        if (cfg.EnableTomestoneIntegration && !string.IsNullOrEmpty(cfg.TomestoneApiKey))
        {
            tomestoneBatchInProgress = true;
            _ = FetchTomestoneDataAsync(members);
        }
    }

    /// <summary>
    /// Returns the effective duty name to use for lookups: the dropdown selection
    /// if one is chosen, otherwise the PF detail's current duty name.
    /// </summary>
    private string? GetEffectiveDutyName()
        => selectedDutyName ?? plugin.PartyFinderManager.CurrentDutyName;

    /// <summary>
    /// Fetches FFLogs data for all party members.
    /// Uses the selected duty name or the PF overlay's current duty name for
    /// encounter-specific queries, falling back to general zone parse.
    /// </summary>
    private async Task FetchFFLogsDataAsync(List<PartyMemberInfo> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        try
        {
            var tempCache = new Dictionary<int, EncounterParseResult?>();

            // Try to get encounter data if a duty is detected
            var dutyName = GetEffectiveDutyName();
            var encounterIds = FFLogsService.GetEncounterIdsForDuty(dutyName);

            if (encounterIds.HasValue)
            {
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

                for (var i = 0; i < members.Count; i++)
                {
                    if (!tempCache.ContainsKey(i))
                        tempCache[i] = new EncounterParseResult(false, true, 0, null, null, null);
                }
            }
            else
            {
                // Fallback: general zone parse
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
                            $"[PartyListWindow] FFLogs lookup failed for {member.Name}@{member.World}");
                        tempCache[i] = null;
                    }
                }
            }

            fflogsCache = tempCache;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyListWindow] FFLogs batch lookup failed.");
        }
        finally
        {
            fflogsBatchInProgress = false;
        }
    }

    /// <summary>
    /// Fetches Tomestone character info for all party members in a batch.
    /// Uses the selected duty name from the dropdown for encounter-specific data.
    /// </summary>
    private async Task FetchTomestoneDataAsync(List<PartyMemberInfo> members)
    {
        try
        {
            var tempCache = new Dictionary<int, TomestoneCharacterInfo?>();
            var dutyName = GetEffectiveDutyName();

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
                        $"[PartyListWindow] Tomestone lookup failed for {member.Name}@{member.World}");
                    tempCache[i] = null;
                }
            }

            tomestoneCache = tempCache;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyListWindow] Tomestone batch lookup failed.");
        }
        finally
        {
            tomestoneBatchInProgress = false;
        }
    }
}
