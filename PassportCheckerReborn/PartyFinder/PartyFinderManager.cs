using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System;

namespace PassportCheckerReborn.PartyFinder;

public sealed partial class PartyFinderManager : IDisposable
{
    private readonly Plugin plugin;

    private Hook<AgentLookingForGroup.Delegates.PopulateListingData>? populateListingHook;

    private AgentLookingForGroup.Detailed? currentDetailedPost;
    private ulong shownListingId;

    public PartyFinderManager(Plugin plugin)
    {
        this.plugin = plugin;

        var lifecycle = Plugin.AddonLifecycle;
        lifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroupDetail", OnPFDetailSetup);
        lifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroupDetail", OnPFDetailRefresh);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnPFDetailFinalize);
        lifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroup", OnPFListSetup);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnPFListFinalize);

        Plugin.PartyFinderGui.ReceiveListing += OnReceiveListing;

        RegisterHooks();
    }

    public bool IsDetailOpen { get; private set; }

    public bool IsListOpen { get; private set; }

    public int ListingGeneration { get; private set; }

    public void Dispose()
    {
        CancelNameResolution();
        Plugin.PartyFinderGui.ReceiveListing -= OnReceiveListing;
        Plugin.AddonLifecycle.UnregisterListener(
            OnPFDetailSetup, OnPFDetailRefresh, OnPFDetailFinalize, OnPFListSetup, OnPFListFinalize);

        populateListingHook?.Dispose();

        currentMembers.Clear();
    }

    public unsafe void RefreshListings()
    {
        if (!IsListOpen)
        {
            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent != null)
        {
            agent->RequestListingsUpdate();
        }
    }

    private unsafe void RegisterHooks()
    {
        try
        {
            populateListingHook = Plugin.GameInteropProvider.HookFromAddress<AgentLookingForGroup.Delegates.PopulateListingData>(
                AgentLookingForGroup.Addresses.PopulateListingData.Value,
                PopulateListingDataDetour);
            populateListingHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PartyFinderManager] Failed to hook PopulateListingData.");
        }
    }

    private void OnPFListSetup(AddonEvent type, AddonArgs args)
    {
        IsListOpen = true;
    }

    private void OnPFListFinalize(AddonEvent type, AddonArgs args)
    {
        IsListOpen = false;
    }

    private void OnPFDetailSetup(AddonEvent type, AddonArgs args)
    {
        IsDetailOpen = true;
        ListingGeneration++;

        plugin.BlacklistMonitor.Refresh();
        RefreshDetail();
    }

    private void OnPFDetailRefresh(AddonEvent type, AddonArgs args)
    {
        RefreshDetail();
    }

    private void OnPFDetailFinalize(AddonEvent type, AddonArgs args)
    {
        CancelNameResolution();

        IsDetailOpen = false;
        currentMembers.Clear();
        currentDetailedPost = null;
        CurrentDutyId = 0;
        CurrentDutyName = string.Empty;
        IsHighEndDuty = false;

        plugin.CidCache.Save();
    }

    private void RefreshDetail()
    {
        var listingId = currentDetailedPost?.ListingId ?? 0;
        if (listingId != shownListingId)
        {
            shownListingId = listingId;
            ListingGeneration++;
        }

        ReadDutyNameFromAddon();
        DetectCurrentDuty();
        RefreshMembers();
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        var worldId = (ushort)listing.HomeWorld.RowId;
        plugin.CidCache.Set(listing.ContentId, listing.Name.TextValue, worldId, GetWorldName(worldId));
    }

    private unsafe void PopulateListingDataDetour(AgentLookingForGroup* thisPtr, AgentLookingForGroup.Detailed* listingData)
    {
        if (listingData != null)
        {
            currentDetailedPost = *listingData;
        }

        populateListingHook!.Original(thisPtr, listingData);
    }

    private static string GetWorldName(uint worldId)
    {
        return Plugin.DataManager.GetExcelSheet<World>().GetRowOrDefault(worldId)?.Name.ToString() ?? string.Empty;
    }
}
