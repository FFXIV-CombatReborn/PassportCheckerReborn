using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PassportCheckerReborn.Services;

// Changes to the Party Finder list: time-based sorting, 100 listings per page, automatic refresh and
// the one-click job filter. Sorting and page size follow DailyRoutines' BetterPartyFinderSort and
// PFPageSizeCustomize; their offsets were checked against client 2026.09.15.0000.0000 (7.56), and each
// is guarded so that a game update which moves it turns the feature off instead of corrupting memory.
public sealed unsafe class PartyFinderListTweaks : IDisposable
{
    // The "less than" the game sorts a page of listings with. It orders by duty, never by time.
    private const string ListingComparerSignature = "40 53 48 83 EC 20 0F B6 82 ?? ?? ?? ?? 48 8B DA 38 81 ?? ?? ?? ??";

    // Where the comparer reads the Party Finder's ascending/descending toggle:
    // movzx eax, byte ptr [rip+disp32].
    private const int SortToggleInstructionOffset = 0x49;
    private const int SortToggleInstructionLength = 7;

    // Listings requested per page, in InfoProxyCrossRealm. FFXIVClientStructs does not map it.
    private const int PageSizeOffset = 0x480;
    private const int DefaultPageSize = 50;

    // The agent's listing buffer holds exactly this many.
    private const int ExpandedPageSize = 100;

    private const int MinAutoRefreshSeconds = 10;

    // The game compares these of ListingEntry.LeadingKeys, in this order, before anything else.
    private static readonly int[] LeadingKeyOrder = [0, 1, 3, 2];

    private readonly PassportCheckerReborn plugin;

    private Hook<ListingComparerDelegate>? listingComparerHook;
    private byte* sortToggle;
    private long nextAutoRefreshAt;
    private Dictionary<uint, JobFlags>? jobFlagsByClassJob;
    private uint abbreviationJobId;
    private string? abbreviation;

    private delegate byte ListingComparerDelegate(ListingEntry* left, ListingEntry* right);

    public PartyFinderListTweaks(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;

        PassportCheckerReborn.PartyFinderGui.ReceiveListing += OnReceiveListing;
        PassportCheckerReborn.Framework.Update += OnFrameworkUpdate;
        PassportCheckerReborn.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroup", OnPFListRefresh);

        // The page size is reapplied on login and as the list opens, in case the game has put its default back.
        PassportCheckerReborn.ClientState.Login += ApplyPageSize;
        PassportCheckerReborn.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "LookingForGroup", OnPFListPreSetup);

        ApplySorting();
        ApplyPageSize();
    }

    private Configuration Configuration => plugin.Configuration;

    // True once hooking the game's comparer failed.
    public bool SortingUnavailable { get; private set; }

    // True once the page size turned out not to be where it is expected.
    public bool PageSizeUnavailable { get; private set; }

    // Not saved between sessions.
    public bool JobFilterActive { get; private set; }

    public void Dispose()
    {
        PassportCheckerReborn.PartyFinderGui.ReceiveListing -= OnReceiveListing;
        PassportCheckerReborn.Framework.Update -= OnFrameworkUpdate;
        PassportCheckerReborn.ClientState.Login -= ApplyPageSize;
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListRefresh, OnPFListPreSetup);

        listingComparerHook?.Dispose();
        listingComparerHook = null;

        WritePageSize(DefaultPageSize);
    }

    public void RefreshListings()
    {
        if (!plugin.PartyFinderManager.IsListOpen)
        {
            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent != null)
        {
            agent->RequestListingsUpdate();
        }
    }

    private void OnPFListPreSetup(AddonEvent type, AddonArgs args)
    {
        ApplyPageSize();
    }

    #region Automatic refresh

    private void OnFrameworkUpdate(IFramework framework)
    {
        var partyFinder = plugin.PartyFinderManager;
        if (!Configuration.EnableAutomaticRefresh || !partyFinder.IsListOpen || partyFinder.IsDetailOpen)
        {
            nextAutoRefreshAt = 0;
            return;
        }

        var now = Environment.TickCount64;
        if (nextAutoRefreshAt == 0)
        {
            nextAutoRefreshAt = now + (Math.Max(MinAutoRefreshSeconds, Configuration.AutoRefreshIntervalSeconds) * 1000L);
        }
        else if (now >= nextAutoRefreshAt)
        {
            nextAutoRefreshAt = 0;
            RefreshListings();
        }
    }

    // Any refresh, the player's own included, starts the countdown again.
    private void OnPFListRefresh(AddonEvent type, AddonArgs args)
    {
        nextAutoRefreshAt = 0;
    }

    #endregion

    #region True time-based sorting

    // Hooks or unhooks the game's comparer to match the setting.
    public void ApplySorting()
    {
        if (!Configuration.EnableTrueTimeBasedSorting)
        {
            listingComparerHook?.Disable();
            return;
        }

        if (listingComparerHook == null && !SortingUnavailable)
        {
            InitializeComparerHook();
        }

        listingComparerHook?.Enable();
    }

    private void InitializeComparerHook()
    {
        try
        {
            listingComparerHook = PassportCheckerReborn.GameInteropProvider.HookFromSignature<ListingComparerDelegate>(
                ListingComparerSignature, ListingComparerDetour);
            sortToggle = ResolveSortToggle(listingComparerHook.Address);
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderListTweaks] Failed to hook the Party Finder sort.");
            SortingUnavailable = true;
        }
    }

    // Null if the instruction is not the expected one, in which case the toggle is not followed.
    private static byte* ResolveSortToggle(nint comparer)
    {
        var instruction = (byte*)comparer + SortToggleInstructionOffset;
        if (instruction[0] != 0x0F || instruction[1] != 0xB6 || instruction[2] != 0x05)
        {
            PassportCheckerReborn.Log.Warning("[PartyFinderListTweaks] Party Finder sort toggle not found; its sort button will not flip the order.");
            return null;
        }

        return instruction + SortToggleInstructionLength + *(int*)(instruction + 3);
    }

    // Called from native code many times per sort, so it neither allocates nor throws. It must stay a
    // strict ordering: the game's sort reads out of bounds if it is not.
    private byte ListingComparerDetour(ListingEntry* left, ListingEntry* right)
    {
        foreach (var key in LeadingKeyOrder)
        {
            var leftKey = left->LeadingKeys[key];
            var rightKey = right->LeadingKeys[key];
            if (leftKey != rightKey)
            {
                return ToByte(leftKey < rightKey);
            }
        }

        var newestFirst = Configuration.TimeSortNewestFirst;
        if (sortToggle != null && *sortToggle != 0)
        {
            newestFirst = !newestFirst;
        }

        // A listing counts down from when it is posted or updated, so the most time left is the newest.
        var leftTime = left->Listing.TimeLeft;
        var rightTime = right->Listing.TimeLeft;
        if (leftTime != rightTime)
        {
            return ToByte(newestFirst ? leftTime > rightTime : leftTime < rightTime);
        }

        // Ties fall back to the listing ID so the order does not shuffle between refreshes.
        var leftId = left->Listing.ListingId;
        var rightId = right->Listing.ListingId;
        return ToByte(newestFirst ? leftId > rightId : leftId < rightId);
    }

    private static byte ToByte(bool value)
    {
        return value ? (byte)1 : (byte)0;
    }

    // One listing as the agent stores it: the packet's listing, then what the client worked out about it.
    [StructLayout(LayoutKind.Explicit, Size = 0x1A0)]
    private struct ListingEntry
    {
        [FieldOffset(0x00)] public CrossRealmListingSegmentPacket.CrossRealmListing Listing;

        // Flags such as a blacklisted host or a locked duty, which the game sorts on first.
        [FieldOffset(0x198)] public fixed byte LeadingKeys[4];
    }

    #endregion

    #region 100 listings per page

    public void ApplyPageSize()
    {
        WritePageSize(Configuration.ExpandListingsTo100PerPage ? ExpandedPageSize : DefaultPageSize);
    }

    private void WritePageSize(int pageSize)
    {
        if (PageSizeUnavailable)
        {
            return;
        }

        var proxy = InfoProxyCrossRealm.Instance();
        if (proxy == null)
        {
            return;
        }

        var current = (int*)((byte*)proxy + PageSizeOffset);
        if (*current == pageSize)
        {
            return;
        }

        // Anything but the two sizes this ever writes means a game update moved the field.
        if (*current != DefaultPageSize && *current != ExpandedPageSize)
        {
            PassportCheckerReborn.Log.Warning($"[PartyFinderListTweaks] Unexpected Party Finder page size {*current}; leaving it alone.");
            PageSizeUnavailable = true;
            return;
        }

        *current = pageSize;
    }

    #endregion

    #region One-click job filter

    // Null when the current job cannot join a duty party.
    public string? JobFilterJobAbbreviation
    {
        get
        {
            if (!TryGetCurrentJobFlag(out _))
            {
                return null;
            }

            // Read every frame by the filter button, so the string is only rebuilt when the job changes.
            var classJob = PassportCheckerReborn.PlayerState.ClassJob;
            if (classJob.RowId != abbreviationJobId)
            {
                abbreviationJobId = classJob.RowId;
                abbreviation = classJob.IsValid ? classJob.Value.Abbreviation.ToString() : null;
            }

            return abbreviation;
        }
    }

    public void ToggleJobFilter()
    {
        JobFilterActive = !JobFilterActive;
        RefreshListings();
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (!JobFilterActive
            || !Configuration.EnableOneClickJobFilter
            || listing.Category != DutyCategory.HighEndDuty
            || !TryGetCurrentJobFlag(out var job))
        {
            return;
        }

        if (!HasOpenSlotFor(listing, job))
        {
            args.Visible = false;
        }
    }

    private static bool HasOpenSlotFor(IPartyFinderListing listing, JobFlags job)
    {
        // Slots and the jobs filling them line up by index; an empty slot has no job.
        var index = 0;
        using var present = listing.RawJobsPresent.GetEnumerator();
        foreach (var slot in listing.Slots)
        {
            if (index++ >= listing.SlotsAvailable)
            {
                break;
            }

            var filled = present.MoveNext() && present.Current != 0;
            if (!filled && slot[job])
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetCurrentJobFlag(out JobFlags job)
    {
        job = default;
        if (!PassportCheckerReborn.PlayerState.IsLoaded)
        {
            return false;
        }

        jobFlagsByClassJob ??= BuildJobFlagLookup();
        return jobFlagsByClassJob.TryGetValue(PassportCheckerReborn.PlayerState.ClassJob.RowId, out job);
    }

    private static Dictionary<uint, JobFlags> BuildJobFlagLookup()
    {
        var lookup = new Dictionary<uint, JobFlags>();
        foreach (var flag in Enum.GetValues<JobFlags>())
        {
            if (flag.ClassJob(PassportCheckerReborn.DataManager) is { } classJob)
            {
                lookup[classJob.RowId] = flag;
            }
        }

        return lookup;
    }

    #endregion
}
