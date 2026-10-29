using Dalamud.Game.Gui.PartyFinder.Types;
using System;
using System.Collections.Generic;

namespace PassportCheckerReborn.Tweaks;

public sealed class JobFilterTweak : IDisposable
{
    private readonly Plugin plugin;

    private Dictionary<uint, JobFlags>? jobFlagsByClassJob;
    private uint abbreviationJobId;
    private string? abbreviation;

    public JobFilterTweak(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.PartyFinderGui.ReceiveListing += OnReceiveListing;
    }

    public bool Active { get; private set; }

    public string? JobAbbreviation
    {
        get
        {
            if (!TryGetCurrentJobFlag(out _))
            {
                return null;
            }

            var classJob = Plugin.PlayerState.ClassJob;
            if (classJob.RowId != abbreviationJobId)
            {
                abbreviationJobId = classJob.RowId;
                abbreviation = classJob.IsValid ? classJob.Value.Abbreviation.ToString() : null;
            }

            return abbreviation;
        }
    }

    public void Dispose()
    {
        Plugin.PartyFinderGui.ReceiveListing -= OnReceiveListing;
    }

    public void Toggle()
    {
        Active = !Active;
        plugin.PartyFinderManager.RefreshListings();
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (!Active
            || !Plugin.Configuration.EnableOneClickJobFilter
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
        if (!Plugin.PlayerState.IsLoaded)
        {
            return false;
        }

        jobFlagsByClassJob ??= BuildJobFlagLookup();
        return jobFlagsByClassJob.TryGetValue(Plugin.PlayerState.ClassJob.RowId, out job);
    }

    private static Dictionary<uint, JobFlags> BuildJobFlagLookup()
    {
        var lookup = new Dictionary<uint, JobFlags>();
        foreach (var flag in Enum.GetValues<JobFlags>())
        {
            if (flag.ClassJob(Plugin.DataManager) is { } classJob)
            {
                lookup[classJob.RowId] = flag;
            }
        }

        return lookup;
    }
}
