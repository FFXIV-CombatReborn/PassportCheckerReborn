namespace PassportCheckerReborn.Integrations.FFLogs;

public record EncounterParseResult(
    bool HasData,
    bool IsEncounterSpecific,
    int TotalKills,
    double? BestParse,
    double? LowestBossHpPct,
    double? AverageParsePercent)
{
    public static readonly EncounterParseResult NoLogs = new(false, true, 0, null, null, null);
    public static readonly EncounterParseResult NotFound = NoLogs with { CharacterNotFound = true };
    public bool CharacterNotFound { get; init; }
    public double? Phase1BestParse { get; init; }
    public double? Phase2BestParse { get; init; }
    public double? Phase1LowestBossHpPct { get; init; }
    public double? Phase2LowestBossHpPct { get; init; }
    public int? Phase1TotalKills { get; init; }
    public int? Phase2TotalKills { get; init; }

    public double? CurrentJobBestParse { get; init; }

    public string? BestParseJobAbbreviation { get; init; }
    public uint? BestParseJobIconId { get; init; }
}
