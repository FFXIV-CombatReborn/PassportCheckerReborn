using System;
using System.Collections.Generic;
using System.Linq;

namespace PassportCheckerReborn.Players;

internal static class Jobs
{
    private static readonly (string Job, string Spec, uint ClassJobId)[] All =
    [
        ("PLD", "Paladin", 19), ("WAR", "Warrior", 21), ("DRK", "DarkKnight", 32), ("GNB", "Gunbreaker", 37),
        ("WHM", "WhiteMage", 24), ("SCH", "Scholar", 28), ("AST", "Astrologian", 33), ("SGE", "Sage", 40),
        ("MNK", "Monk", 20), ("DRG", "Dragoon", 22), ("NIN", "Ninja", 30), ("SAM", "Samurai", 34),
        ("RPR", "Reaper", 39), ("VPR", "Viper", 41),
        ("BRD", "Bard", 23), ("MCH", "Machinist", 31), ("DNC", "Dancer", 38),
        ("BLM", "BlackMage", 25), ("SMN", "Summoner", 27), ("RDM", "RedMage", 35), ("PCT", "Pictomancer", 42),
    ];

    private static readonly Dictionary<string, string> SpecByJob =
        All.ToDictionary(job => job.Job, job => job.Spec, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> JobBySpec =
        All.ToDictionary(job => job.Spec, job => job.Job, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, uint> IconByJob =
        All.ToDictionary(job => job.Job, job => 62100 + job.ClassJobId, StringComparer.OrdinalIgnoreCase);

    public static uint? GetIconId(string? jobAbbreviation)
    {
        return !string.IsNullOrWhiteSpace(jobAbbreviation) && IconByJob.TryGetValue(jobAbbreviation, out var iconId) ? iconId : null;
    }

    public static string? GetFFLogsSpec(string jobAbbreviation)
    {
        return SpecByJob.GetValueOrDefault(jobAbbreviation);
    }

    public static string? FromFFLogsSpec(string? spec)
    {
        return spec == null ? null : JobBySpec.GetValueOrDefault(spec);
    }
}
