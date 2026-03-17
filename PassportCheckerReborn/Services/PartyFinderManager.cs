using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace PassportCheckerReborn.Services;

/// <summary>
/// Manages hooks into the game's Party Finder UI and network packets
/// to discover the names of players in any selected PF listing.
///
/// <para>
/// Features implemented:
/// <list type="bullet">
///   <item>IAddonLifecycle hooks for LookingForGroupDetail open/refresh/close.</item>
///   <item>IAddonLifecycle hooks for LookingForGroup (list addon) setup/finalize.</item>
///   <item>IPartyFinderGui.ReceiveListing subscription to cache PF listing host data.</item>
///   <item>AgentLookingForGroup.PopulateListingData hook to extract member content IDs.</item>
///   <item>Auto-refresh timer (configurable interval, pauses when detail pane is open).</item>
///   <item>Context-menu "View Recruitment" injection.</item>
///   <item>High-end duty detection via ContentFinderCondition sheet.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PartyFinderManager : IDisposable
{
    private readonly PassportCheckerReborn plugin;

    /// <summary>Players visible in the currently selected PF listing.</summary>
    public IReadOnlyList<Windows.PartyMemberInfo> CurrentMembers => currentMembers;
    private readonly List<Windows.PartyMemberInfo> currentMembers = [];

    /// <summary>Whether the PF detail addon is currently open.</summary>
    public bool IsDetailOpen { get; private set; }

    /// <summary>Whether the PF list addon is currently open.</summary>
    public bool IsListOpen { get; private set; }

    /// <summary>The detected duty ID from the current PF listing (0 if unknown).</summary>
    public uint CurrentDutyId { get; private set; }

    /// <summary>Whether the current listing is for a high-end duty.</summary>
    public bool IsHighEndDuty { get; private set; }

    /// <summary>The detected duty name from the current PF listing (empty if unknown).</summary>
    public string CurrentDutyName { get; private set; } = string.Empty;

    /// <summary>
    /// Monotonically increasing counter that increments each time a new
    /// LookingForGroupDetail pane is opened. Used by the overlay to detect
    /// when cached data should be cleared.
    /// </summary>
    public int DetailOpenGeneration { get; private set; }

    /// <summary>
    /// Whether any current members have unresolved names (still being looked up
    /// via adventure plate / CharaCard). The overlay uses this to disable buttons
    /// that require resolved names.
    /// </summary>
    public bool HasUnresolvedMembers
    {
        get
        {
            for (var i = 0; i < currentMembers.Count; i++)
            {
                if (currentMembers[i].Name.StartsWith(UnresolvedNamePrefix))
                    return true;
            }
            return false;
        }
    }

    // ── Auto-refresh ─────────────────────────────────────────────────────────
    private System.Timers.Timer? autoRefreshTimer;
    private int autoRefreshCountdown;

    // ── Known-player cache ───────────────────────────────────────────────────
    /// <summary>
    /// Set of "Name@World" strings for players the local user has previously
    /// grouped with. Persisted in memory only (resets on plugin reload).
    /// Populated from friends list + encounter history once member discovery
    /// is implemented.
    /// </summary>
    public ConcurrentDictionary<string, bool> KnownPlayers { get; } = new();

    // ── PF Listing cache (from IPartyFinderGui.ReceiveListing) ──────────────
    /// <summary>
    /// Cache of player info collected from PF listing packets.
    /// Maps ContentId → (Name, HomeWorldId) for PF listing hosts.
    /// Populated via <see cref="IPartyFinderGui.ReceiveListing"/> events,
    /// following the same pattern as OpenRadar's Network.ListingHostExtract.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, (string Name, ushort WorldId)> pfListingPlayerCache = new();

    // ── PopulateListingData hook (extracts member content IDs from PF detail) ─
    /// <summary>
    /// Hook on <see cref="AgentLookingForGroup.PopulateListingData"/> to intercept
    /// the detailed listing data when a user clicks on a PF listing.
    /// This provides <c>MemberContentIds</c> and <c>Jobs</c> arrays for all party
    /// slots, following the same pattern as OpenRadar's PopulateListingDataDetour.
    /// </summary>
    private Hook<AgentLookingForGroup.Delegates.PopulateListingData>? populateListingHook;

    /// <summary>
    /// The most recently intercepted detailed listing data from PopulateListingData.
    /// Contains MemberContentIds and Jobs for all party members in the current listing.
    /// </summary>
    private AgentLookingForGroup.Detailed? currentDetailedPost;

    // ── CharaCard (adventure plate) name resolution ─────────────────────────
    /// <summary>
    /// Hook on <see cref="CharaCard.HandleCurrentCharaCardDataPacket"/> to intercept
    /// adventure plate responses that contain a player's name and world.
    /// Follows the same pattern as OpenRadar's CharaCardPacketHandlerDetour.
    /// </summary>
    private Hook<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>? charaCardPacketHandlerHook;

    /// <summary>
    /// Hook on <see cref="RaptureLogModule.ShowLogMessage"/> to suppress error messages
    /// (logMessageIds 5854-5861) generated when a CharaCard request fails (e.g. player
    /// has adventure plate disabled). Also signals a failed lookup so the awaiter can
    /// move on.
    /// </summary>
    private Hook<RaptureLogModule.Delegates.ShowLogMessage>? showLogMessageHook;

    /// <summary>Fired when an adventure plate response is received (name resolved or null on failure).</summary>
    private event Action<(ulong ContentId, string Name, ushort WorldId)?>? OnCharaCardReceived;

    /// <summary>Serialises CharaCard requests so only one is in-flight at a time.</summary>
    private readonly SemaphoreSlim charaCardRequestGate = new(1, 1);

    /// <summary>Cancellation source for the ongoing async name resolution batch.</summary>
    private CancellationTokenSource? resolveCts;

    /// <summary>Throttle interval between CharaCard requests (milliseconds).</summary>
    private const int CharaCardThrottleMs = 900;

    /// <summary>Prefix used for members whose names could not be resolved from cache.</summary>
    private const string UnresolvedNamePrefix = "Player ";

    // ── Context-menu entry ───────────────────────────────────────────────────
    public PartyFinderManager(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        RegisterHooks();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Hook Registration
    // ═════════════════════════════════════════════════════════════════════════

    private unsafe void RegisterHooks()
    {
        // ── LookingForGroupDetail (PF Detail pane) ──────────────────────────
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup, "LookingForGroupDetail", OnPFDetailSetup);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostRefresh, "LookingForGroupDetail", OnPFDetailRefresh);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize, "LookingForGroupDetail", OnPFDetailFinalize);

        // ── LookingForGroup (PF List) ───────────────────────────────────────
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup, "LookingForGroup", OnPFListSetup);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostRefresh, "LookingForGroup", OnPFListRefresh);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize, "LookingForGroup", OnPFListFinalize);

        // ── IPartyFinderGui listing subscription ────────────────────────────
        // Captures host ContentId/Name/World from every PF listing packet.
        PassportCheckerReborn.PartyFinderGui.ReceiveListing += OnReceiveListing;

        // ── AgentLookingForGroup.PopulateListingData hook ───────────────────
        // Intercepts the detailed listing data to extract MemberContentIds + Jobs.
        try
        {
            populateListingHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<AgentLookingForGroup.Delegates.PopulateListingData>(
                AgentLookingForGroup.Addresses.PopulateListingData.Value,
                PopulateListingDataDetour);
            populateListingHook.Enable();
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to hook PopulateListingData.");
        }

        // ── CharaCard (adventure plate) hooks ───────────────────────────────
        // Intercepts adventure plate responses to resolve player names from CIDs.
        // Follows the same pattern as OpenRadar's CharaCard hooks.
        try
        {
            charaCardPacketHandlerHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>(
                CharaCard.Addresses.HandleCurrentCharaCardDataPacket.Value,
                CharaCardPacketHandlerDetour);
            charaCardPacketHandlerHook.Enable();

            showLogMessageHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<RaptureLogModule.Delegates.ShowLogMessage>(
                RaptureLogModule.Addresses.ShowLogMessage.Value,
                ShowLogMessageDetour);
            showLogMessageHook.Enable();
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to hook CharaCard/ShowLogMessage.");
        }

        // ── Context menu (right-click → "View Recruitment") ─────────────────
        if (plugin.Configuration.RightClickPlayerNameForRecruitment2)
            RegisterContextMenu();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // IPartyFinderGui Listing Handler
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called for each PF listing received from the game server.
    /// Caches the listing host's ContentId, Name, and World so they can be
    /// used when the user opens a PF detail pane.
    /// </summary>
    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (listing.ContentId == 0)
            return;

        var name = listing.Name.TextValue;
        var worldId = (ushort)listing.HomeWorld.RowId;
        pfListingPlayerCache[listing.ContentId] = (name, worldId);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // PopulateListingData Hook (extracts member content IDs)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Detour for <see cref="AgentLookingForGroup.PopulateListingData"/>.
    /// Intercepts the <see cref="AgentLookingForGroup.Detailed"/> struct that the game
    /// populates when the user clicks on a PF listing. This struct contains
    /// <c>MemberContentIds</c> and <c>Jobs</c> arrays for all 8 party slots.
    /// </summary>
    private unsafe void PopulateListingDataDetour(AgentLookingForGroup* thisPtr, AgentLookingForGroup.Detailed* listingData)
    {
        try
        {
            currentDetailedPost = *listingData;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] PopulateListingData detour error.");
        }

        populateListingHook!.Original(thisPtr, listingData);
    }

    /// <summary>
    /// Attempts to resolve a content ID to a player name and world using the PF listing cache.
    /// </summary>
    public (string Name, string World)? ResolvePlayerFromCache(ulong contentId)
    {
        if (contentId == 0)
            return null;

        if (pfListingPlayerCache.TryGetValue(contentId, out var cached))
        {
            var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();
            var worldName = worldSheet?.GetRowOrDefault(cached.WorldId)?.Name.ToString() ?? string.Empty;
            return (cached.Name, worldName);
        }

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CharaCard (Adventure Plate) Hooks & Resolution
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Detour for <see cref="CharaCard.HandleCurrentCharaCardDataPacket"/>.
    /// Fires when the game receives an adventure plate response for a requested CID.
    /// Extracts ContentId, Name, and WorldId from the packet.
    /// </summary>
    private unsafe void CharaCardPacketHandlerDetour(CharaCard* thisPtr, AgentCharaCard.CharaCardPacket* packet)
    {
        try
        {
            OnCharaCardReceived?.Invoke((packet->ContentId, packet->NameString, packet->WorldId));
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] CharaCard packet detour error.");
        }

        charaCardPacketHandlerHook!.Original(thisPtr, packet);
    }

    /// <summary>
    /// Detour for <see cref="RaptureLogModule.ShowLogMessage"/>.
    /// Suppresses error log messages (IDs 5855-5860) generated when a CharaCard
    /// request fails (e.g. player has adventure plate disabled), and signals a
    /// failed lookup via the <see cref="OnCharaCardReceived"/> event.
    /// Uses the same exclusive range (> 5854 and &lt; 5861) as OpenRadar.
    /// </summary>
    private unsafe void ShowLogMessageDetour(RaptureLogModule* thisPtr, uint logMessageId)
    {
        if (logMessageId is > 5854 and < 5861)
        {
            OnCharaCardReceived?.Invoke(null);
            return;
        }

        showLogMessageHook!.Original(thisPtr, logMessageId);
    }

    /// <summary>
    /// Sends a CharaCard (adventure plate) request for the given content ID and
    /// waits for the response. Returns the resolved name and world, or <c>null</c>
    /// if the request fails or times out. Throttled to one request per
    /// <see cref="CharaCardThrottleMs"/> milliseconds, serialised via semaphore.
    /// </summary>
    private async Task<(ulong ContentId, string Name, ushort WorldId)?> RequestCharaCardAsync(
        ulong contentId, CancellationToken ct)
    {
        await charaCardRequestGate.WaitAsync(ct);

        try
        {
            // Throttle between requests
            await Task.Delay(CharaCardThrottleMs, ct);

            var tcs = new TaskCompletionSource<(ulong ContentId, string Name, ushort WorldId)?>();

            void Handler((ulong ContentId, string Name, ushort WorldId)? info)
            {
                if (info == null || info.Value.ContentId == contentId)
                    tcs.TrySetResult(info);
            }

            OnCharaCardReceived += Handler;

            await PassportCheckerReborn.Framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    CharaCard.Instance()->RequestCharaCardForContentId(contentId);
                }
            });

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            finally
            {
                OnCharaCardReceived -= Handler;
            }
        }
        finally
        {
            charaCardRequestGate.Release();
        }
    }

    /// <summary>
    /// Asynchronously resolves names for members that could not be resolved from
    /// the PF listing cache. Uses the CharaCard (adventure plate) lookup.
    /// Updates the member list in-place when a name is resolved.
    /// </summary>
    private async Task ResolveUnresolvedMembersAsync(CancellationToken ct)
    {
        for (var i = 0; i < currentMembers.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return;

            var member = currentMembers[i];
            if (member.ContentId == 0 || !member.Name.StartsWith(UnresolvedNamePrefix))
                continue;

            try
            {
                var info = await RequestCharaCardAsync(member.ContentId, ct);
                if (ct.IsCancellationRequested)
                    return;

                if (info is { } resolved && !string.IsNullOrEmpty(resolved.Name))
                {
                    var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();
                    var worldName = worldSheet?.GetRowOrDefault(resolved.WorldId)?.Name.ToString() ?? string.Empty;

                    // Update the PF listing cache so future lookups are instant
                    pfListingPlayerCache[member.ContentId] = (resolved.Name, resolved.WorldId);

                    // Update the member in-place (the overlay re-reads CurrentMembers each frame)
                    if (i < currentMembers.Count && currentMembers[i].ContentId == member.ContentId)
                    {
                        currentMembers[i] = member with { Name = resolved.Name, World = worldName };
                    }
                }
                else
                {
                    // Player has adventure plate hidden - treat as private and remove
                    if (i < currentMembers.Count && currentMembers[i].ContentId == member.ContentId)
                    {
                        currentMembers.RemoveAt(i);
                        i--;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Warning(ex,
                    $"[PartyFinderManager] Error resolving CID: {member.ContentId:X16} via CharaCard.");
            }
        }
    }

    private void OnPFDetailSetup(AddonEvent type, AddonArgs args)
    {
        IsDetailOpen = true;
        DetailOpenGeneration++;

        // Pause auto-refresh while the detail pane is open (matches DailyRoutines pattern)
        StopAutoRefreshTimer();

        // Read duty name from the addon, then detect duty and populate members
        ReadDutyNameFromAddon();
        DetectCurrentDuty();
        RefreshMembers();

        // Auto-show overlay when config enables it
        if (plugin.Configuration.ShowMemberInfoOverlay)
            plugin.OverlayWindow.IsOpen = true;
    }

    private void OnPFDetailRefresh(AddonEvent type, AddonArgs args)
    {
        ReadDutyNameFromAddon();
        DetectCurrentDuty();
        RefreshMembers();
    }

    private void OnPFDetailFinalize(AddonEvent type, AddonArgs args)
    {
        // Cancel any in-progress CharaCard resolution
        resolveCts?.Cancel();
        resolveCts?.Dispose();
        resolveCts = null;

        IsDetailOpen = false;
        currentMembers.Clear();
        currentDetailedPost = null;
        CurrentDutyId = 0;
        CurrentDutyName = string.Empty;
        IsHighEndDuty = false;

        // Resume auto-refresh
        if (IsListOpen)
            StartAutoRefreshTimer();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // PF List Handlers
    // ═════════════════════════════════════════════════════════════════════════

    private void OnPFListSetup(AddonEvent type, AddonArgs args)
    {
        IsListOpen = true;
        StartAutoRefreshTimer();
    }

    private void OnPFListRefresh(AddonEvent type, AddonArgs args)
    {
    }

    private void OnPFListFinalize(AddonEvent type, AddonArgs args)
    {
        IsListOpen = false;
        StopAutoRefreshTimer();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Auto-Refresh (TODO item 5 – functional logic, needs game-client testing)
    // ═════════════════════════════════════════════════════════════════════════

    private void StartAutoRefreshTimer()
    {
        if (!plugin.Configuration.EnableAutomaticRefresh)
            return;

        autoRefreshCountdown = plugin.Configuration.AutoRefreshIntervalSeconds;

        autoRefreshTimer?.Dispose();
        autoRefreshTimer = new System.Timers.Timer(1000); // tick every second
        autoRefreshTimer.AutoReset = true;
        autoRefreshTimer.Elapsed += OnAutoRefreshTick;
        autoRefreshTimer.Start();
    }

    private void StopAutoRefreshTimer()
    {
        if (autoRefreshTimer is not null)
        {
            autoRefreshTimer.Elapsed -= OnAutoRefreshTick;
            autoRefreshTimer.Stop();
            autoRefreshTimer.Dispose();
            autoRefreshTimer = null;
        }
    }

    private unsafe void OnAutoRefreshTick(object? sender, ElapsedEventArgs e)
    {
        // Don't refresh while detail pane is open
        if (IsDetailOpen || !IsListOpen)
        {
            StopAutoRefreshTimer();
            return;
        }

        if (autoRefreshCountdown > 1)
        {
            autoRefreshCountdown--;
            return;
        }

        // Reset countdown
        autoRefreshCountdown = plugin.Configuration.AutoRefreshIntervalSeconds;

        // Request PF listing update on the framework thread (game API calls must
        // happen on the main thread). Uses AgentLookingForGroup as shown in the
        // DailyRoutines AutoRefreshPartyFinder reference.
        PassportCheckerReborn.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                AgentLookingForGroup.Instance()->RequestListingsUpdate();
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Auto-refresh failed.");
            }
        });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Context Menu – "View Recruitment" (TODO item 10)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Registers the context-menu entry for "View Recruitment".</summary>
    public void RegisterContextMenu()
    {
        UnregisterContextMenu();
        PassportCheckerReborn.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    /// <summary>Unregisters the context-menu entry.</summary>
    public void UnregisterContextMenu()
    {
        PassportCheckerReborn.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
    }

    private void OnContextMenuOpened(Dalamud.Game.Gui.ContextMenu.IMenuOpenedArgs args)
    {
        // Only add the menu item when right-clicking on a player character
        if (args.Target is not Dalamud.Game.Gui.ContextMenu.MenuTargetDefault target)
            return;

        if (target.TargetContentId == 0)
            return;

        args.AddMenuItem(new Dalamud.Game.Gui.ContextMenu.MenuItem
        {
            Name = new SeStringBuilder().AddText("View Recruitment").Build(),
            OnClicked = OnViewRecruitmentClicked,
            IsEnabled = true,
        });
    }

    private void OnViewRecruitmentClicked(Dalamud.Game.Gui.ContextMenu.IMenuItemClickedArgs args)
    {
        if (args.Target is Dalamud.Game.Gui.ContextMenu.MenuTargetDefault target)
        {
            PassportCheckerReborn.Log.Information(
                $"[PartyFinderManager] View Recruitment requested for ContentId {target.TargetContentId}");
            //TODO: Need to finish this, gotta make it open the party finder the is in
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // High-End Duty Detection (TODO item 13)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the duty name directly from the LookingForGroupDetail addon's
    /// AtkValue array. Value at index 15 contains the duty name as a string.
    /// </summary>
    private unsafe void ReadDutyNameFromAddon()
    {
        CurrentDutyName = string.Empty;
        try
        {
            var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroupDetail", 1);
            if (addonPtr.IsNull) return;

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon->AtkValuesCount > 15)
            {
                var atkValue = addon->AtkValues[15];
                // ValueType 6 = String, 8 = AllocatedString
                var typeId = (uint)atkValue.Type;
                if ((typeId == 6 || typeId == 8) && atkValue.String.HasValue)
                {
                    CurrentDutyName = atkValue.String.ToString() ?? string.Empty;
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Attempts to detect the duty from the current PF listing and checks whether
    /// it is a high-end duty (Savage, Ultimate, Extreme, Criterion).
    /// </summary>
    private unsafe void DetectCurrentDuty()
    {
        CurrentDutyId = 0;
        IsHighEndDuty = false;

        try
        {
            // Try to read the duty ID from AgentLookingForGroup.
            // This is the most reliable source but requires the agent to be active.
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
            {
                // Fall back to duty-name-based detection
                DetectHighEndFromName();
                return;
            }

            // If we have a duty ID, look it up in the ContentFinderCondition sheet
            if (CurrentDutyId > 0)
            {
                IsHighEndDuty = CheckHighEndDuty(CurrentDutyId);
            }
            else
            {
                // Fall back to duty-name-based detection
                DetectHighEndFromName();
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to detect duty.");
            DetectHighEndFromName();
        }
    }

    /// <summary>
    /// Checks whether the duty name (from AtkValue[15]) indicates a high-end duty.
    /// Used as a fallback when <see cref="CurrentDutyId"/> is not available.
    /// </summary>
    private void DetectHighEndFromName()
    {
        if (string.IsNullOrEmpty(CurrentDutyName))
            return;

        var name = CurrentDutyName.ToLowerInvariant();
        IsHighEndDuty = name.Contains("savage") || name.Contains("ultimate") ||
                        name.Contains("extreme") || name.Contains("criterion") ||
                        name.Contains("unreal");
    }

    /// <summary>
    /// Checks whether the given duty ID corresponds to a high-end duty
    /// (Savage, Ultimate, Extreme, Criterion) using the game's ContentFinderCondition sheet.
    /// </summary>
    public static bool CheckHighEndDuty(uint dutyId)
    {
        try
        {
            var sheet = PassportCheckerReborn.DataManager.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null)
                return false;

            var row = sheet.GetRowOrDefault(dutyId);
            if (row == null)
                return false;

            var cfc = row.Value;

            // HighEndDuty flag is the primary indicator
            if (cfc.HighEndDuty)
                return true;

            // Also check ContentType for known high-end categories:
            // ContentType 5 = Raids, ContentType 28 = Ultimate Raids
            // We also check the name for keywords as a fallback
            var name = cfc.Name.ToString().ToLowerInvariant();
            if (name.Contains("savage") || name.Contains("ultimate") ||
                name.Contains("extreme") || name.Contains("criterion") ||
                name.Contains("unreal"))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to check high-end duty.");
            return false;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Member Discovery
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Populates the member list from the intercepted <see cref="AgentLookingForGroup.Detailed"/>
    /// data. Uses <c>MemberContentIds</c> and <c>Jobs</c> arrays to identify party members,
    /// then resolves names/worlds from the PF listing cache (populated via
    /// <see cref="IPartyFinderGui.ReceiveListing"/>).
    ///
    /// <para>
    /// Falls back to stub data when the PopulateListingData hook hasn't fired
    /// (e.g. during development outside the game client).
    /// </para>
    /// </summary>
    private void RefreshMembers()
    {
        // Cancel any in-progress CharaCard resolution from a previous call
        resolveCts?.Cancel();
        resolveCts?.Dispose();
        resolveCts = null;

        currentMembers.Clear();

        try
        {
            if (currentDetailedPost is not { } post)
            {
                PopulateStubMembers();
                return;
            }

            var classJobSheet = PassportCheckerReborn.DataManager.GetExcelSheet<ClassJob>();

            for (var i = 0; i < 8; i++)
            {
                var contentId = post.MemberContentIds[i];
                if (contentId == 0)
                    continue;

                var jobId = post.Jobs[i];
                var jobAbbreviation = classJobSheet?.GetRowOrDefault(jobId)?.Abbreviation.ToString() ?? "???";

                // Try to resolve name + world from the PF listing cache
                var resolved = ResolvePlayerFromCache(contentId);
                var name = resolved?.Name ?? $"{UnresolvedNamePrefix}{contentId:X16}";
                var world = resolved?.World ?? string.Empty;

                currentMembers.Add(new Windows.PartyMemberInfo(name, world, jobAbbreviation, contentId));
            }

            if (currentMembers.Count > 0)
            {
                // If any members were not resolved from cache, try CharaCard lookup
                var hasUnresolved = false;
                for (var i = 0; i < currentMembers.Count; i++)
                {
                    if (currentMembers[i].Name.StartsWith(UnresolvedNamePrefix))
                    {
                        hasUnresolved = true;
                        break;
                    }
                }

                if (hasUnresolved && charaCardPacketHandlerHook is { IsEnabled: true })
                {
                    resolveCts = new CancellationTokenSource();
                    _ = ResolveUnresolvedMembersAsync(resolveCts.Token);
                }
            }
            else
            {
                PopulateStubMembers();
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex,
                "[PartyFinderManager] Failed to read member data, using stub members.");
            PopulateStubMembers();
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the string looks like a plausible FFXIV name
    /// part (only letters, hyphens, and apostrophes).
    /// </summary>
    private static bool IsPlausibleNamePart(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsLetter(c) && c != '-' && c != '\'')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Inserts synthetic members so the overlay is visible during development
    /// when the game client is not running.
    /// </summary>
    private void PopulateStubMembers()
    {
        currentMembers.Clear();
        currentMembers.Add(new Windows.PartyMemberInfo("Ehl'tee Es", "Kraken", "SCH"));
    }

    /// <summary>Manually refresh the member list (e.g. on overlay open).</summary>
    public void RequestRefresh() => RefreshMembers();

    /// <summary>
    /// Checks if a player is in the known-players set.
    /// </summary>
    public bool IsKnownPlayer(string name, string world)
    {
        return KnownPlayers.ContainsKey($"{name}@{world}");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Dispose
    // ═════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        // Cancel any in-progress CharaCard resolution
        resolveCts?.Cancel();
        resolveCts?.Dispose();

        StopAutoRefreshTimer();
        UnregisterContextMenu();

        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFDetailSetup);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFDetailRefresh);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFDetailFinalize);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListSetup);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListRefresh);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListFinalize);

        PassportCheckerReborn.PartyFinderGui.ReceiveListing -= OnReceiveListing;

        populateListingHook?.Dispose();
        charaCardPacketHandlerHook?.Dispose();
        showLogMessageHook?.Dispose();
        charaCardRequestGate.Dispose();

        currentMembers.Clear();
        pfListingPlayerCache.Clear();
    }
}
