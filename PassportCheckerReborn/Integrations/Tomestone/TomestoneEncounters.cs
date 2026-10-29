using System;
using System.Collections.Generic;

namespace PassportCheckerReborn.Integrations.Tomestone;

internal static class TomestoneEncounters
{
    private static readonly Dictionary<string, TomestoneEncounterParams> Encounters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Futures Rewritten (Ultimate)"] = new("dawntrail", "ultimates", "futures-rewritten-ultimate"),
        ["Dancing Mad (Ultimate)"] = new("dawntrail", "ultimates", "dancing-mad-ultimate"),
        ["The Omega Protocol (Ultimate)"] = new("endwalker", "ultimates", "the-omega-protocol-ultimate"),
        ["Dragonsong's Reprise (Ultimate)"] = new("endwalker", "ultimates", "dragonsongs-reprise-ultimate"),
        ["The Epic of Alexander (Ultimate)"] = new("shadowbringers", "ultimates", "the-epic-of-alexander-ultimate"),
        ["The Weapon's Refrain (Ultimate)"] = new("stormblood", "ultimates", "the-weapons-refrain-ultimate"),
        ["The Unending Coil of Bahamut (Ultimate)"] = new("stormblood", "ultimates", "the-unending-coil-of-bahamut-ultimate"),

        ["AAC Heavyweight M1 (Savage)"] = new("dawntrail", "aac-heavyweight-savage", "vamp-fatale"),
        ["AAC Heavyweight M2 (Savage)"] = new("dawntrail", "aac-heavyweight-savage", "red-hot-deep-blue"),
        ["AAC Heavyweight M3 (Savage)"] = new("dawntrail", "aac-heavyweight-savage", "the-tyrant"),
        ["AAC Heavyweight M4 (Savage) P1"] = new("dawntrail", "aac-heavyweight-savage", "lindwurm"),
        ["AAC Heavyweight M4 (Savage) P2"] = new("dawntrail", "aac-heavyweight-savage", "lindwurm-ii"),

        ["AAC Light-heavyweight M1 (Savage)"] = new("dawntrail", "aac-light-heavyweight-savage", "black-cat"),
        ["AAC Light-heavyweight M2 (Savage)"] = new("dawntrail", "aac-light-heavyweight-savage", "honey-b-lovely"),
        ["AAC Light-heavyweight M3 (Savage)"] = new("dawntrail", "aac-light-heavyweight-savage", "brute-bomber"),
        ["AAC Light-heavyweight M4 (Savage)"] = new("dawntrail", "aac-light-heavyweight-savage", "wicked-thunder"),

        ["AAC Cruiserweight M1 (Savage)"] = new("dawntrail", "aac-cruiserweight-savage", "dancing-green"),
        ["AAC Cruiserweight M2 (Savage)"] = new("dawntrail", "aac-cruiserweight-savage", "sugar-riot"),
        ["AAC Cruiserweight M3 (Savage)"] = new("dawntrail", "aac-cruiserweight-savage", "brute-abombinator"),
        ["AAC Cruiserweight M4 (Savage)"] = new("dawntrail", "aac-cruiserweight-savage", "howling-blade"),

        ["Worqor Lar Dor (Extreme)"] = new("dawntrail", "trials-extreme", "valigarmanda"),
        ["Everkeep (Extreme)"] = new("dawntrail", "trials-extreme", "everkept"),
        ["The Minstrel's Ballad: Sphene's Burden"] = new("dawntrail", "trials-extreme", "queen-eternal"),
        ["Recollection (Extreme)"] = new("dawntrail", "trials-extreme", "zelenia"),
        ["The Minstrel's Ballad: Necron's Embrace"] = new("dawntrail", "trials-extreme", "necron"),
        ["The Windward Wilds (Extreme)"] = new("dawntrail", "trials-extreme", "guardian-arkveld"),
        ["Hell on Rails (Extreme)"] = new("dawntrail", "trials-extreme", "doomtrain"),
        ["The Unmaking (Extreme)"] = new("dawntrail", "trials-extreme", "enuo"),

        ["Tsukuyomi's Pain (Unreal)"] = new("dawntrail", "unreal", "tsukuyomis-pain"),
        ["Shinryu's Domain (Unreal)"] = new("dawntrail", "unreal", "shinryu"),

        ["The Cloud of Darkness (Chaotic)"] = new("dawntrail", "chaotic", "the-cloud-of-darkness"),
    };

    private static readonly Dictionary<string, (TomestoneEncounterParams Preferred, TomestoneEncounterParams Fallback)> MultiPartEncounters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AAC Heavyweight M4 (Savage)"] = (
                new("dawntrail", "aac-heavyweight-savage", "lindwurm-ii"),
                new("dawntrail", "aac-heavyweight-savage", "lindwurm")),
        };

    public static IEnumerable<string> SupportedDutyNames => Encounters.Keys;

    public static (TomestoneEncounterParams? Encounter, TomestoneEncounterParams? Fallback) Find(string dutyName)
    {
        if (Encounters.TryGetValue(dutyName, out var encounter))
        {
            return (encounter, null);
        }

        return MultiPartEncounters.TryGetValue(dutyName, out var parts) ? (parts.Preferred, parts.Fallback) : (null, null);
    }
}
