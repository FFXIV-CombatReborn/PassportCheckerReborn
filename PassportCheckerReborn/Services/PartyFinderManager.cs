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
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
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

    // ── Prevent auto-close on party changes ──────────────────────────────────
    private Hook<AtkUnitBase.Delegates.Close>? closeAddonHook;
    private int trackedPartyMemberCount;

    /// <summary>
    /// When <c>true</c>, a party-change close was already detected this frame.
    /// Remains active until the next framework tick so that ALL PF addon close
    /// calls within the same game event are suppressed (the game fires Close on
    /// both LookingForGroup and LookingForGroupDetail in a single pass).
    /// </summary>
    private bool partyChangeSuppressionActive;

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

    // ── Blacklist cache ───────────────────────────────────────────────────────
    /// <summary>
    /// Set of "Name@World" strings read from the game's BlackList addon (node #9).
    /// Refreshed whenever the BlackList addon is opened/refreshed, and also when
    /// a PF detail pane is opened.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> blacklistedPlayers = new(StringComparer.OrdinalIgnoreCase);

    // ── PF Listing cache (from IPartyFinderGui.ReceiveListing) ──────────────
    /// <summary>
    /// Cache of player info collected from PF listing packets.
    /// Maps ContentId → (Name, HomeWorldId, ListingId) for PF listing hosts.
    /// Populated via <see cref="IPartyFinderGui.ReceiveListing"/> events,
    /// following the same pattern as OpenRadar's Network.ListingHostExtract.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, (string Name, ushort WorldId, uint ListingId)> pfListingPlayerCache = new();

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
    public const string UnresolvedNamePrefix = "Player ";

    // ── Context-menu "View Recruitment" pending selection ─────────────────
    /// <summary>
    /// When non-zero, the content ID of a player whose PF listing should be
    /// auto-selected once the LookingForGroup addon is ready.
    /// </summary>
    private ulong pendingRecruitmentContentId;

    // ── Context-menu entry ───────────────────────────────────────────────────
    public PartyFinderManager(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        RegisterHooks();

        // Seed the in-memory dict from the persisted JSON cache so the blacklist
        // is ready immediately, before the BlackList addon has been opened.
        SeedBlacklistFromCache();

        // Eagerly read the live string array – it's always in memory even without
        // the BlackList addon being open, so this should succeed straight away and
        // will overwrite the cache seed with the current game state.
        ReadBlacklistFromAddon();
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

        // ── BlackList addon ──────────────────────────────────────────────────
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup, "BlackList", OnBlacklistAddonUpdated);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostRefresh, "BlackList", OnBlacklistAddonUpdated);

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
        catch (Exception)
        {
            //PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to hook PopulateListingData.");
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
        catch (Exception)
        {
            //PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to hook CharaCard/ShowLogMessage.");
        }

        // ── AtkUnitBase.Close hook (prevent PF close on party changes) ────────
        // Close is a virtual function (vtable index 4, offset 32) so there is no
        // static Addresses entry. Read the function pointer from the base vtable.
        try
        {
            var closeAddress = (nint)AtkUnitBase.StaticVirtualTablePointer->Close;
            PassportCheckerReborn.Log.Information(
                $"[PartyFinderManager] Hooking AtkUnitBase.Close at 0x{closeAddress:X16}");
            closeAddonHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<AtkUnitBase.Delegates.Close>(
                closeAddress,
                CloseAddonDetour);
            closeAddonHook.Enable();
            //PassportCheckerReborn.Log.Information(
            //    "[PartyFinderManager] AtkUnitBase.Close hook installed and enabled.");
        }
        catch (Exception)
        {
           //PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to hook AtkUnitBase.Close.");
        }

        // ── Track party member count (initial snapshot for close-suppression) ──
        trackedPartyMemberCount = GetEffectivePartyCount();
        //PassportCheckerReborn.Log.Information(
        //    $"[PartyFinderManager] Initial tracked party count: {trackedPartyMemberCount} " +
        //    $"(IPartyList={PassportCheckerReborn.PartyList.Length})");

        // ── Context menu (right-click → "View Recruitment") ─────────────────
        if (plugin.Configuration.RightClickPlayerNameForRecruitment3)
            RegisterContextMenu();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Prevent Auto-Close on Party Changes
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Detour for <see cref="AtkUnitBase.Close"/>. When the setting is enabled,
    /// suppresses close calls on LookingForGroup / LookingForGroupDetail that
    /// are triggered by a party composition change rather than by the user.
    /// </summary>
    private unsafe bool CloseAddonDetour(AtkUnitBase* unitBase, bool a1)
    {
        try
        {
            var addonName = unitBase->NameString;

            if (addonName is "LookingForGroupDetail" or "LookingForGroup")
            {
                var currentCount = GetEffectivePartyCount();
                //PassportCheckerReborn.Log.Information(
                //    $"[PartyFinderManager] Close called on {addonName} " +
                //    $"(a1={a1}, config={plugin.Configuration.PreventAutoClosingOnPartyChanges}, " +
                //    $"detailOpen={IsDetailOpen}, listOpen={IsListOpen}, " +
                //    $"trackedCount={trackedPartyMemberCount}, effectiveCount={currentCount}, " +
                //    $"suppressionActive={partyChangeSuppressionActive})");

                if (plugin.Configuration.PreventAutoClosingOnPartyChanges
                    && IsDetailOpen)
                {
                    // Suppress if the party count changed OR if another PF addon
                    // close in the same frame already triggered suppression (the
                    // game fires Close on both addons in one pass).
                    // Never suppress when the effective count is 0 — that means the
                    // party disbanded or the player left entirely, so there is no
                    // active party left to justify keeping PF open.  Continued
                    // suppression in that state would lock the user out of closing
                    // the window manually.
                    if (currentCount > 0
                        && (currentCount != trackedPartyMemberCount || partyChangeSuppressionActive))
                    {
                        if (!partyChangeSuppressionActive)
                        {
                            // First detection this frame — start the suppression
                            // window and schedule a deferred count sync + state
                            // verification on the next framework tick.
                            partyChangeSuppressionActive = true;
                            PassportCheckerReborn.Framework.Update += OnDeferredPartyCountSync;
                        }

                        //PassportCheckerReborn.Log.Information(
                        //    $"[PartyFinderManager] SUPPRESSING close for {addonName} " +
                        //    $"(party: {trackedPartyMemberCount}\u2192{currentCount}).");
                        return false;
                    }

                    // If suppression was active but the party is now gone, clean up
                    // the deferred sync so it doesn't fire stale.
                    if (partyChangeSuppressionActive && currentCount == 0)
                    {
                        PassportCheckerReborn.Framework.Update -= OnDeferredPartyCountSync;
                        partyChangeSuppressionActive = false;
                    }

                    //PassportCheckerReborn.Log.Information($"[PartyFinderManager] Allowing close for {addonName} (currentCount={currentCount}, tracked={trackedPartyMemberCount}).");
                    trackedPartyMemberCount = currentCount;
                }
                else
                {
                    //PassportCheckerReborn.Log.Information($"[PartyFinderManager] Config disabled or no PF windows open \u2014 allowing close for {addonName}.");
                }
            }
        }
        catch (Exception)
        {
            //PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Error in CloseAddonDetour.");
        }

        return closeAddonHook!.Original(unitBase, a1);
    }

    /// <summary>
    /// Fires on the next framework tick after a party-change suppression.
    /// Ends the suppression window, syncs the tracked count, and verifies
    /// that suppressed addons are still alive (the game may have destroyed
    /// them through another code path).
    /// </summary>
    private unsafe void OnDeferredPartyCountSync(IFramework framework)
    {
        PassportCheckerReborn.Framework.Update -= OnDeferredPartyCountSync;
        partyChangeSuppressionActive = false;
        trackedPartyMemberCount = GetEffectivePartyCount();

        //PassportCheckerReborn.Log.Information($"[PartyFinderManager] Deferred sync: trackedCount={trackedPartyMemberCount}");

        // Verify that suppressed addons are still alive. If the game destroyed
        // one despite our suppression (e.g. via a different Close override or a
        // secondary teardown path), clean up our state so the UI doesn't get stuck.
        if (IsDetailOpen)
        {
            var ptr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroupDetail");
            if (ptr.IsNull)
            {
                //PassportCheckerReborn.Log.Warning("[PartyFinderManager] Detail was suppressed but addon is gone \u2014 cleaning up.");
                resolveCts?.Cancel();
                resolveCts?.Dispose();
                resolveCts = null;
                IsDetailOpen = false;
                currentMembers.Clear();
                currentDetailedPost = null;
                CurrentDutyId = 0;
                CurrentDutyName = string.Empty;
                IsHighEndDuty = false;
                plugin.CidCache.Save();
            }
        }

        if (IsListOpen)
        {
            var ptr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroup");
            if (ptr.IsNull)
            {
                //PassportCheckerReborn.Log.Warning("[PartyFinderManager] List was suppressed but addon is gone \u2014 cleaning up.");
                IsListOpen = false;
                pendingRecruitmentContentId = 0;
                StopAutoRefreshTimer();
            }
        }
    }

    /// <summary>
    /// Returns the current party member count using the best available source.
    /// <see cref="IPartyList.Length"/> returns 0 for cross-world parties (which PF
    /// always creates), so we read from <see cref="InfoProxyCrossRealm"/> first.
    /// </summary>
    private static unsafe int GetEffectivePartyCount()
    {
        try
        {
            var cwProxy = InfoProxyCrossRealm.Instance();
            if (cwProxy != null && cwProxy->IsInCrossRealmParty)
            {
                var localIndex = cwProxy->LocalPlayerGroupIndex;
                return InfoProxyCrossRealm.GetGroupMemberCount(localIndex);
            }
        }
        catch
        {
            // Fall through to IPartyList
        }

        return PassportCheckerReborn.PartyList.Length;
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
        var listingId = listing.Id;
        pfListingPlayerCache[listing.ContentId] = (name, worldId, listingId);

        // Persist to disk so the name is available in future sessions without
        // needing another CharaCard request. Always overwrite: PF packet data is
        // the freshest available source and handles name/world changes.
        var worldName = PassportCheckerReborn.DataManager.GetExcelSheet<World>()
            ?.GetRowOrDefault(worldId)?.Name.ToString() ?? string.Empty;
        plugin.CidCache.Set(listing.ContentId, name, worldId, worldName);
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

        // Check the premade (plugin-bundled) CID list first. This ships with the
        // plugin and is updated together with plugin releases, providing instant
        // lookups for well-known players.
        if (plugin.PremadeCidCache.TryGet(contentId, out var premade) && premade != null
            && !string.IsNullOrEmpty(premade.Name))
        {
            return (premade.Name, premade.WorldName);
        }

        // Prefer the in-memory PF listing cache (always fresh from network packets).
        if (pfListingPlayerCache.TryGetValue(contentId, out var cached))
        {
            var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();
            var worldName = worldSheet?.GetRowOrDefault(cached.WorldId)?.Name.ToString() ?? string.Empty;
            return (cached.Name, worldName);
        }

        // Fall back to the persistent CID cache for CIDs seen in previous sessions.
        // This avoids a CharaCard round-trip for already-known players.
        // Note: the entry may be stale if the player changed their name or world;
        // it will be overwritten the next time fresh data arrives.
        if (plugin.CidCache.TryGet(contentId, out var persisted) && persisted != null
            && !string.IsNullOrEmpty(persisted.Name))
        {
            return (persisted.Name, persisted.WorldName);
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
        var resolved = false;

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

                if (info is { } resolvedInfo && !string.IsNullOrEmpty(resolvedInfo.Name))
                {
                    var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();
                    var worldName = worldSheet?.GetRowOrDefault(resolvedInfo.WorldId)?.Name.ToString() ?? string.Empty;

                    // Update the in-memory PF listing cache so future lookups within the
                    // same session are instant.
                    var existingListingId = pfListingPlayerCache.TryGetValue(member.ContentId, out var prev) ? prev.ListingId : 0u;
                    pfListingPlayerCache[member.ContentId] = (resolvedInfo.Name, resolvedInfo.WorldId, existingListingId);

                    // Persist to the CID cache so this player is recognised in future
                    // sessions without another CharaCard round-trip.
                    plugin.CidCache.Set(member.ContentId, resolvedInfo.Name, resolvedInfo.WorldId, worldName);
                    resolved = true;

                    // Update the member in-place (the overlay re-reads CurrentMembers each frame).
                    if (i < currentMembers.Count && currentMembers[i].ContentId == member.ContentId)
                    {
                        currentMembers[i] = member with { Name = resolvedInfo.Name, World = worldName };
                    }
                }
                else
                {
                    // Player has adventure plate hidden — treat as private and remove.
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

        // Flush any newly-resolved names to disk in one shot at the end of the batch.
        if (resolved)
            plugin.CidCache.Save();
    }

    private void OnPFDetailSetup(AddonEvent type, AddonArgs args)
    {
        IsDetailOpen = true;
        DetailOpenGeneration++;
        trackedPartyMemberCount = GetEffectivePartyCount();
        //PassportCheckerReborn.Log.Information($"[PartyFinderManager] Detail OPENED (gen={DetailOpenGeneration}, partyCount={trackedPartyMemberCount}).");

        // Pause auto-refresh while the detail pane is open (matches DailyRoutines pattern)
        StopAutoRefreshTimer();

        // Refresh the blacklist cache in case entries changed since last open
        ReadBlacklistFromAddon();

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
        //PassportCheckerReborn.Log.Information($"[PartyFinderManager] Detail CLOSED (partyCount={GetEffectivePartyCount()}, tracked={trackedPartyMemberCount}).");
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

        // Persist any names that were cached during this detail-pane session.
        plugin.CidCache.Save();

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
        trackedPartyMemberCount = GetEffectivePartyCount();
        //PassportCheckerReborn.Log.Information($"[PartyFinderManager] List OPENED (partyCount={trackedPartyMemberCount}).");
        StartAutoRefreshTimer();
        TryProcessPendingRecruitment();
    }

    private void OnPFListRefresh(AddonEvent type, AddonArgs args)
    {
        TryProcessPendingRecruitment();
    }

    private void OnPFListFinalize(AddonEvent type, AddonArgs args)
    {
        //PassportCheckerReborn.Log.Information($"[PartyFinderManager] List CLOSED (partyCount={GetEffectivePartyCount()}, tracked={trackedPartyMemberCount}).");
        IsListOpen = false;
        pendingRecruitmentContentId = 0;
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

    private unsafe void OnViewRecruitmentClicked(Dalamud.Game.Gui.ContextMenu.IMenuItemClickedArgs args)
    {
        if (args.Target is not Dalamud.Game.Gui.ContextMenu.MenuTargetDefault target)
            return;

        var contentId = target.TargetContentId;
        PassportCheckerReborn.Log.Information(
            $"[PartyFinderManager] View Recruitment requested for ContentId {contentId:X16}");

        // Check if this player has a known PF listing
        if (!pfListingPlayerCache.TryGetValue(contentId, out var cached) || cached.ListingId == 0)
        {
            PassportCheckerReborn.ChatGui.Print(
                "[PassportChecker] No active Party Finder listing found for this player.");
            return;
        }

        // Store the pending selection
        pendingRecruitmentContentId = contentId;

        PassportCheckerReborn.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var agent = AgentLookingForGroup.Instance();
                if (agent == null)
                {
                    PassportCheckerReborn.ChatGui.Print(
                        "[PassportChecker] Unable to open Party Finder.");
                    pendingRecruitmentContentId = 0;
                    return;
                }

                // If PF is already open, try to select the listing immediately
                if (IsListOpen)
                {
                    // Request a refresh first so the listing data is current
                    agent->RequestListingsUpdate();
                    // Selection will be attempted in OnPFListRefresh via TryProcessPendingRecruitment
                }
                else
                {
                    // Open the PF window; selection will be attempted in OnPFListSetup
                    agent->Show();
                }
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] View Recruitment failed.");
                pendingRecruitmentContentId = 0;
            }
        });
    }

    /// <summary>
    /// Attempts to process a pending "View Recruitment" selection by finding
    /// the matching listing in the currently displayed PF list and selecting it.
    /// Called from PF list setup/refresh handlers.
    /// </summary>
    private unsafe void TryProcessPendingRecruitment()
    {
        var targetContentId = pendingRecruitmentContentId;
        if (targetContentId == 0)
            return;

        // Only attempt once per click
        pendingRecruitmentContentId = 0;

        if (!pfListingPlayerCache.TryGetValue(targetContentId, out var cached) || cached.ListingId == 0)
            return;

        var targetListingId = cached.ListingId;

        try
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
                return;

            // Scan the agent's displayed listing IDs to find the target
            var listingIds = agent->Listings.ListingIds;
            var foundIndex = -1;
            for (var i = 0; i < listingIds.Length; i++)
            {
                if (listingIds[i] == targetListingId)
                {
                    foundIndex = i;
                    break;
                }
            }

            if (foundIndex < 0)
            {
                PassportCheckerReborn.ChatGui.Print(
                    $"[PassportChecker] Listing by {cached.Name} not found on the current page. " +
                    "It may have expired or be on a different category/page.");
                return;
            }

            // Fire a callback on the LookingForGroup addon to select this listing row.
            // The addon's callback handler expects: [0] = 3 (select listing action), [1] = listing index.
            var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroup");
            if (addonPtr.IsNull)
                return;

            var addon = (AtkUnitBase*)addonPtr.Address;
            var atkValues = stackalloc AtkValue[2];
            atkValues[0].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atkValues[0].Int = 3;
            atkValues[1].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            atkValues[1].Int = foundIndex;
            addon->FireCallback(2, atkValues);

            PassportCheckerReborn.Log.Information(
                $"[PartyFinderManager] Selected listing {targetListingId} by {cached.Name} at index {foundIndex}.");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex,
                "[PartyFinderManager] Failed to select listing for View Recruitment.");
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

    /// <summary>Manually triggers a live re-read of the blacklist string array and saves the result.</summary>
    public void ForceRefreshBlacklist() => ReadBlacklistFromAddon();

    /// <summary>
    /// Checks if a player is in the known-players set.
    /// </summary>
    public bool IsKnownPlayer(string name, string world)
    {
        return KnownPlayers.ContainsKey($"{name}@{world}");
    }

    /// <summary>
    /// Checks if a player is on the local user's blacklist.
    /// </summary>
    public bool IsBlacklisted(string name, string world)
    {
        if (!plugin.Configuration.EnableBlacklistFeature)
            return false;
        if (string.IsNullOrEmpty(name))
            return false;
        var result = (!string.IsNullOrEmpty(world) && blacklistedPlayers.ContainsKey($"{name}@{world}"))
                     || blacklistedPlayers.ContainsKey(name);
        //PassportCheckerReborn.Log.Debug($"[PartyFinderManager] IsBlacklisted(\"{name}\"@\"{world}\") = {result}  (cache size={blacklistedPlayers.Count})");
        return result;
    }

    /// <summary>
    /// Seeds <see cref="blacklistedPlayers"/> from the persisted <see cref="BlacklistCache"/>
    /// so the blacklist is available immediately on plugin load, before the first live
    /// read from <c>BlackListStringArray</c>.
    /// </summary>
    private void SeedBlacklistFromCache()
    {
        foreach (var key in plugin.BlacklistCache.GetAllKeys())
            blacklistedPlayers[key] = true;

        //PassportCheckerReborn.Log.Debug($"[PartyFinderManager] Seeded {blacklistedPlayers.Count} entries from BlacklistCache.");
    }

    /// <summary>
    /// Reads all blacklisted player entries from <see cref="BlackListStringArray"/> and
    /// caches them as "Name@World" keys for fast per-frame lookup.
    /// The string array is always present in memory; the BlackList addon does not need
    /// to be open.
    /// </summary>
    private unsafe void ReadBlacklistFromAddon()
    {
        //PassportCheckerReborn.Log.Debug("[PartyFinderManager] ReadBlacklistFromAddon called.");
        var newEntries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var array = BlackListStringArray.Instance();
            if (array == null)
            {
                //PassportCheckerReborn.Log.Debug("[PartyFinderManager] BlackListStringArray.Instance() returned null – skipping update.");
                return;
            }

            //PassportCheckerReborn.Log.Debug($"[PartyFinderManager] BlackListStringArray obtained. PlayerNames.Length={array->PlayerNames.Length}");

            var names = array->PlayerNames;
            var worlds = array->Homeworlds;

            for (var i = 0; i < names.Length; i++)
            {
                var name = names[i].ToString();
                if (string.IsNullOrEmpty(name))
                    break;

                var world = worlds[i].ToString() ?? string.Empty;
                var key = string.IsNullOrEmpty(world) ? name : $"{name}@{world}";
                //PassportCheckerReborn.Log.Debug($"[PartyFinderManager] Blacklist entry [{i}]: \"{key}\"");
                newEntries[key] = true;
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to read BlackList string array.");
        }

        blacklistedPlayers.Clear();
        foreach (var (k, v) in newEntries)
            blacklistedPlayers[k] = v;

        PassportCheckerReborn.Log.Debug($"[PartyFinderManager] Blacklist cache updated: {blacklistedPlayers.Count} entr{(blacklistedPlayers.Count == 1 ? "y" : "ies")}.");

        // Persist the live snapshot to disk so it survives plugin reloads.
        plugin.BlacklistCache.ReplaceAll(newEntries.Keys);
    }

    private void OnBlacklistAddonUpdated(AddonEvent type, AddonArgs args)
    {
        //PassportCheckerReborn.Log.Debug($"[PartyFinderManager] OnBlacklistAddonUpdated fired (event={type}).");
        ReadBlacklistFromAddon();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Dispose
    // ═════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        // Cancel any in-progress CharaCard resolution
        resolveCts?.Cancel();
        resolveCts?.Dispose();

        // Remove deferred sync callback if pending
        PassportCheckerReborn.Framework.Update -= OnDeferredPartyCountSync;
        partyChangeSuppressionActive = false;

        StopAutoRefreshTimer();
        UnregisterContextMenu();

        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFDetailSetup);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFDetailRefresh);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFDetailFinalize);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListSetup);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListRefresh);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListFinalize);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnBlacklistAddonUpdated);

        PassportCheckerReborn.PartyFinderGui.ReceiveListing -= OnReceiveListing;

        populateListingHook?.Dispose();
        charaCardPacketHandlerHook?.Dispose();
        showLogMessageHook?.Dispose();
        closeAddonHook?.Dispose();
        charaCardRequestGate.Dispose();

        currentMembers.Clear();
        pfListingPlayerCache.Clear();
    }
}
