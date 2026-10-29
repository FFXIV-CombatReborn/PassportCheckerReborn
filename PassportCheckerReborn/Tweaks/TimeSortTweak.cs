using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using System;
using System.Runtime.InteropServices;

namespace PassportCheckerReborn.Tweaks;

public sealed unsafe class TimeSortTweak : IDisposable
{
    private const string ListingComparerSignature = "40 53 48 83 EC 20 0F B6 82 ?? ?? ?? ?? 48 8B DA 38 81 ?? ?? ?? ??";

    private const int SortToggleInstructionOffset = 0x49;
    private const int SortToggleInstructionLength = 7;

    private static readonly int[] LeadingKeyOrder = [0, 1, 3, 2];

    private Hook<ListingComparerDelegate>? listingComparerHook;
    private byte* sortToggle;

    private delegate byte ListingComparerDelegate(ListingEntry* left, ListingEntry* right);

    public TimeSortTweak()
    {
        ApplySetting();
    }

    public bool Unavailable { get; private set; }

    public void Dispose()
    {
        listingComparerHook?.Dispose();
        listingComparerHook = null;
    }

    public void ApplySetting()
    {
        if (!Plugin.Configuration.EnableTrueTimeBasedSorting)
        {
            listingComparerHook?.Disable();
            return;
        }

        if (listingComparerHook == null && !Unavailable)
        {
            InitializeComparerHook();
        }

        listingComparerHook?.Enable();
    }

    private void InitializeComparerHook()
    {
        try
        {
            listingComparerHook = Plugin.GameInteropProvider.HookFromSignature<ListingComparerDelegate>(
                ListingComparerSignature, ListingComparerDetour);
            sortToggle = ResolveSortToggle(listingComparerHook.Address);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[TimeSortTweak] Failed to hook the Party Finder sort.");
            Unavailable = true;
        }
    }

    private static byte* ResolveSortToggle(nint comparer)
    {
        var instruction = (byte*)comparer + SortToggleInstructionOffset;
        if (instruction[0] != 0x0F || instruction[1] != 0xB6 || instruction[2] != 0x05)
        {
            Plugin.Log.Warning("[TimeSortTweak] Party Finder sort toggle not found; its sort button will not flip the order.");
            return null;
        }

        return instruction + SortToggleInstructionLength + *(int*)(instruction + 3);
    }

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

        var newestFirst = Plugin.Configuration.TimeSortNewestFirst;
        if (sortToggle != null && *sortToggle != 0)
        {
            newestFirst = !newestFirst;
        }

        var leftTime = left->Listing.TimeLeft;
        var rightTime = right->Listing.TimeLeft;
        if (leftTime != rightTime)
        {
            return ToByte(newestFirst ? leftTime > rightTime : leftTime < rightTime);
        }

        var leftId = left->Listing.ListingId;
        var rightId = right->Listing.ListingId;
        return ToByte(newestFirst ? leftId > rightId : leftId < rightId);
    }

    private static byte ToByte(bool value)
    {
        return value ? (byte)1 : (byte)0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x1A0)]
    private struct ListingEntry
    {
        [FieldOffset(0x00)] public CrossRealmListingSegmentPacket.CrossRealmListing Listing;

        [FieldOffset(0x198)] public fixed byte LeadingKeys[4];
    }
}
