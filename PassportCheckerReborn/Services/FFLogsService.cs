using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Services;

// The FFLogs V2 API: an OAuth client-credentials token, then GraphQL queries.
public sealed class FFLogsService : IDisposable
{
    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string ApiUrl = "https://www.fflogs.com/api/v2/client";

    private const int DifficultyNormal = 100;
    private const int DifficultySavage = 101;

    // English duty name to FFLogs encounter. Needs a new entry each content tier.
    private static readonly Dictionary<string, (int EncounterId, int Difficulty)> Encounters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAC Light-heavyweight M1 (Savage)"] = (93, DifficultySavage),
        ["AAC Light-heavyweight M2 (Savage)"] = (94, DifficultySavage),
        ["AAC Light-heavyweight M3 (Savage)"] = (95, DifficultySavage),
        ["AAC Light-heavyweight M4 (Savage)"] = (96, DifficultySavage),

        ["AAC Cruiserweight M1 (Savage)"] = (97, DifficultySavage),
        ["AAC Cruiserweight M2 (Savage)"] = (98, DifficultySavage),
        ["AAC Cruiserweight M3 (Savage)"] = (99, DifficultySavage),
        ["AAC Cruiserweight M4 (Savage)"] = (100, DifficultySavage),

        ["AAC Heavyweight M1 (Savage)"] = (101, DifficultySavage),
        ["AAC Heavyweight M2 (Savage)"] = (102, DifficultySavage),
        ["AAC Heavyweight M3 (Savage)"] = (103, DifficultySavage),
        ["AAC Heavyweight M4 (Savage) P1"] = (104, DifficultySavage),
        ["AAC Heavyweight M4 (Savage) P2"] = (105, DifficultySavage),

        ["The Cloud of Darkness (Chaotic)"] = (2061, DifficultyNormal),

        ["Tsukuyomi's Pain (Unreal)"] = (3012, DifficultyNormal),
        ["Shinryu's Domain (Unreal)"] = (3013, DifficultyNormal),

        ["Worqor Lar Dor (Extreme)"] = (1071, DifficultyNormal),
        ["Everkeep (Extreme)"] = (1072, DifficultyNormal),
        ["The Minstrel's Ballad: Sphene's Burden"] = (1078, DifficultyNormal),
        ["Recollection (Extreme)"] = (1080, DifficultyNormal),
        ["The Minstrel's Ballad: Necron's Embrace"] = (1081, DifficultyNormal),
        ["The Windward Wilds (Extreme)"] = (1082, DifficultyNormal),
        ["Hell on Rails (Extreme)"] = (1083, DifficultyNormal),
        ["The Unmaking (Extreme)"] = (1084, DifficultyNormal),

        ["The Unending Coil of Bahamut (Ultimate)"] = (1073, DifficultyNormal),
        ["The Weapon's Refrain (Ultimate)"] = (1074, DifficultyNormal),
        ["The Epic of Alexander (Ultimate)"] = (1075, DifficultyNormal),
        ["Dragonsong's Reprise (Ultimate)"] = (1076, DifficultyNormal),
        ["The Omega Protocol (Ultimate)"] = (1077, DifficultyNormal),
        ["Futures Rewritten (Ultimate)"] = (1079, DifficultyNormal),
        ["Dancing Mad (Ultimate)"] = (1085, DifficultyNormal),
    };

    // Duties the Party Finder lists once but FFLogs splits into two encounters.
    private static readonly Dictionary<string, (int Phase1, int Phase2)> MultiPartEncounters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAC Heavyweight M4 (Savage)"] = (104, 105),
    };

    private static readonly Dictionary<int, int> DifficultyByEncounter =
        Encounters.Values.ToDictionary(encounter => encounter.EncounterId, encounter => encounter.Difficulty);

    private static readonly (string Job, string Spec, uint ClassJobId)[] Jobs =
    [
        ("PLD", "Paladin", 19), ("WAR", "Warrior", 21), ("DRK", "DarkKnight", 32), ("GNB", "Gunbreaker", 37),
        ("WHM", "WhiteMage", 24), ("SCH", "Scholar", 28), ("AST", "Astrologian", 33), ("SGE", "Sage", 40),
        ("MNK", "Monk", 20), ("DRG", "Dragoon", 22), ("NIN", "Ninja", 30), ("SAM", "Samurai", 34),
        ("RPR", "Reaper", 39), ("VPR", "Viper", 41),
        ("BRD", "Bard", 23), ("MCH", "Machinist", 31), ("DNC", "Dancer", 38),
        ("BLM", "BlackMage", 25), ("SMN", "Summoner", 27), ("RDM", "RedMage", 35), ("PCT", "Pictomancer", 42),
    ];

    private static readonly Dictionary<string, string> SpecByJob =
        Jobs.ToDictionary(job => job.Job, job => job.Spec, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> JobBySpec =
        Jobs.ToDictionary(job => job.Spec, job => job.Job, StringComparer.OrdinalIgnoreCase);

    // Job icons are game icon 62100 plus the ClassJob row.
    private static readonly Dictionary<string, uint> IconByJob =
        Jobs.ToDictionary(job => job.Job, job => 62100 + job.ClassJobId, StringComparer.OrdinalIgnoreCase);

    // Worlds by FFLogs region. Any world not listed is taken to be in NA.
    private static readonly HashSet<string> EuWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan",
        "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark",
        "Innocence", "Pixie", "Titania",
    };

    private static readonly HashSet<string> JpWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon",
        "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima",
        "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan",
        "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus",
    };

    private static readonly HashSet<string> OcWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan",
    };

    private readonly PassportCheckerReborn plugin;
    private readonly HttpClient httpClient;

    private string? cachedToken;
    private DateTime tokenExpiry = DateTime.MinValue;

    public FFLogsService(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PassportCheckerReborn/{PassportCheckerReborn.Version}");
    }

    // The phase entries of a multi-part duty are listed; its combined name is not.
    public static IEnumerable<string> SupportedDutyNames => Encounters.Keys;

    public void Dispose()
    {
        httpClient.Dispose();
    }

    public static uint? GetJobIconId(string? jobAbbreviation)
    {
        return !string.IsNullOrWhiteSpace(jobAbbreviation) && IconByJob.TryGetValue(jobAbbreviation, out var iconId) ? iconId : null;
    }

    public static (int Primary, int? Secondary)? GetEncounterIdsForDuty(string? dutyName)
    {
        if (string.IsNullOrWhiteSpace(dutyName))
        {
            return null;
        }

        if (MultiPartEncounters.TryGetValue(dutyName, out var parts))
        {
            return (parts.Phase1, parts.Phase2);
        }

        return Encounters.TryGetValue(dutyName, out var encounter) ? (encounter.EncounterId, null) : null;
    }

    #region Token

    public async Task<bool> TestCredentialsAsync(string clientId, string clientSecret)
    {
        return !string.IsNullOrWhiteSpace(clientId)
            && !string.IsNullOrWhiteSpace(clientSecret)
            && await FetchTokenAsync(clientId, clientSecret) is not null;
    }

    private async Task<string?> FetchTokenAsync(string clientId, string clientSecret)
    {
        // A token issued to other credentials must not outlive them.
        cachedToken = null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            request.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded");

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                PassportCheckerReborn.Log.Warning($"[FFLogsService] Token request failed: {(int)response.StatusCode}");
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl))
            {
                return null;
            }

            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expEl) ? expEl.GetInt32() : 3600;
            tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60);
            cachedToken = tokenEl.GetString();
            return cachedToken;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Exception during token fetch.");
            return null;
        }
    }

    private async Task<string?> GetTokenAsync()
    {
        var cfg = plugin.Configuration;
        if (!cfg.HasFFLogsCredentials())
        {
            return null;
        }

        if (cachedToken is not null && DateTime.UtcNow < tokenExpiry)
        {
            return cachedToken;
        }

        return await FetchTokenAsync(cfg.FFLogsClientId, cfg.FFLogsClientSecret);
    }

    #endregion

    #region Queries

    // The raw JSON response, or null on failure.
    private async Task<string?> QueryAsync(string graphqlQuery)
    {
        var token = await GetTokenAsync();
        if (token is null)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(new { query = graphqlQuery }), Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                PassportCheckerReborn.Log.Warning($"[FFLogsService] GraphQL request failed: {(int)response.StatusCode}");
                return null;
            }

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Exception during GraphQL request.");
            return null;
        }
    }

    // A player's overall standing in the current zone, for duties with no encounter mapping.
    public async Task<EncounterParseResult> GetOverallParseAsync(string playerName, string worldName)
    {
        var average = await GetBestPerfAvgAsync(playerName, worldName);
        return new EncounterParseResult(average.HasValue, false, 0, average, null, null);
    }

    public async Task<double?> GetBestPerfAvgAsync(string playerName, string worldName)
    {
        if (GetFFLogsServer(worldName) is not { } server)
        {
            return null;
        }

        var json = await QueryAsync($"{{ characterData {{ {CharacterSelector(playerName, server)} {{ zoneRankings }} }} }}");
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPath(doc.RootElement, out var rankings, "data", "characterData", "character", "zoneRankings"))
            {
                return null;
            }

            return TryGetNumber(rankings, "bestPerformanceAverage") ?? TryGetNumber(rankings, "medianPerformanceAverage");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse zone rankings response.");
            return null;
        }
    }

    // One result per member, by index. A two-part duty is looked up per part and combined.
    public async Task<Dictionary<int, EncounterParseResult>> GetEncounterDataAsync(
        IReadOnlyList<PartyMemberInfo> members, (int Primary, int? Secondary) encounterIds)
    {
        var phase1 = await GetEncounterDataAsync(members, encounterIds.Primary);
        if (encounterIds.Secondary is not { } secondary)
        {
            return phase1;
        }

        var phase2 = await GetEncounterDataAsync(members, secondary);
        var combined = new Dictionary<int, EncounterParseResult>();
        for (var i = 0; i < members.Count; i++)
        {
            combined[i] = Combine(phase1[i], phase2[i]);
        }

        return combined;
    }

    private static EncounterParseResult Combine(EncounterParseResult phase1, EncounterParseResult phase2)
    {
        // The job shown is the one from the last phase that was cleared.
        var best = phase2 is { HasData: true, TotalKills: > 0 } ? phase2
            : phase1 is { HasData: true, TotalKills: > 0 } ? phase1
            : phase2;

        var currentJobBest = phase1.CurrentJobBestParse.HasValue && phase2.CurrentJobBestParse.HasValue
            ? Math.Max(phase1.CurrentJobBestParse.Value, phase2.CurrentJobBestParse.Value)
            : phase2.CurrentJobBestParse ?? phase1.CurrentJobBestParse;

        // A full clear needs a kill of each phase.
        var fullClears = Math.Min(phase1.TotalKills, phase2.TotalKills);
        return new EncounterParseResult(phase1.HasData || phase2.HasData, true, fullClears, best.BestParse, null, null)
        {
            Phase1BestParse = phase1.BestParse,
            Phase2BestParse = phase2.BestParse,
            Phase2LowestBossHpPct = phase2.LowestBossHpPct,
            Phase1TotalKills = phase1.TotalKills,
            Phase2TotalKills = phase2.TotalKills,
            CurrentJobBestParse = currentJobBest,
            BestParseJobAbbreviation = best.BestParseJobAbbreviation,
            BestParseJobIconId = best.BestParseJobIconId,
        };
    }

    // Rankings for everyone in one query, then, for those with no kill, how far their recent logs got.
    private async Task<Dictionary<int, EncounterParseResult>> GetEncounterDataAsync(IReadOnlyList<PartyMemberInfo> members, int encounterId)
    {
        var results = new Dictionary<int, EncounterParseResult>();
        for (var i = 0; i < members.Count; i++)
        {
            results[i] = EncounterParseResult.NoLogs;
        }

        var selectors = BuildCharacterSelectors(members, Enumerable.Range(0, members.Count));
        if (selectors.Count == 0)
        {
            return results;
        }

        var difficulty = DifficultyByEncounter.TryGetValue(encounterId, out var value) ? $", difficulty: {value}" : string.Empty;
        var json = await QueryAsync(BuildCharacterQuery(selectors, $"encounterRankings(encounterID: {encounterId}{difficulty})"));
        if (json is null)
        {
            return results;
        }

        var noKillIndices = new List<int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPath(doc.RootElement, out var characters, "data", "characterData"))
            {
                PassportCheckerReborn.Log.Warning("[FFLogsService] Encounter batch response missing data/characterData.");
                return results;
            }

            foreach (var i in selectors.Keys)
            {
                if (!TryGetPath(characters, out var rankings, $"p{i}", "encounterRankings"))
                {
                    continue;
                }

                var totalKills = (int)(TryGetNumber(rankings, "totalKills") ?? 0);
                if (totalKills > 0)
                {
                    results[i] = ParseRankings(rankings, totalKills, members[i].JobAbbreviation);
                }
                else
                {
                    noKillIndices.Add(i);
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse encounter rankings batch response.");
            return results;
        }

        if (noKillIndices.Count > 0)
        {
            await FetchProgressionDataAsync(members, encounterId, noKillIndices, results);
        }

        return results;
    }

    private static EncounterParseResult ParseRankings(JsonElement rankings, int totalKills, string jobAbbreviation)
    {
        double? bestParse = null;
        string? bestSpec = null;
        double? currentJobBest = null;
        var currentSpec = SpecByJob.GetValueOrDefault(jobAbbreviation);

        if (TryGetPath(rankings, out var ranks, "ranks") && ranks.ValueKind == JsonValueKind.Array)
        {
            foreach (var rank in ranks.EnumerateArray())
            {
                if (TryGetNumber(rank, "rankPercent") is not { } percent)
                {
                    continue;
                }

                var spec = rank.TryGetProperty("spec", out var specEl) ? specEl.GetString() : null;
                if (bestParse is null || percent > bestParse)
                {
                    bestParse = percent;
                    bestSpec = spec;
                }

                if (currentSpec != null
                    && string.Equals(spec, currentSpec, StringComparison.OrdinalIgnoreCase)
                    && (currentJobBest is null || percent > currentJobBest))
                {
                    currentJobBest = percent;
                }
            }
        }

        var bestJob = bestSpec == null ? null : JobBySpec.GetValueOrDefault(bestSpec);
        return new EncounterParseResult(true, true, totalKills, bestParse, null, null)
        {
            CurrentJobBestParse = currentJobBest,
            BestParseJobAbbreviation = bestJob,
            BestParseJobIconId = GetJobIconId(bestJob),
        };
    }

    // For players with no kill: the lowest boss HP reached across their ten most recent reports.
    private async Task FetchProgressionDataAsync(
        IReadOnlyList<PartyMemberInfo> members, int encounterId, List<int> noKillIndices, Dictionary<int, EncounterParseResult> results)
    {
        var selectors = BuildCharacterSelectors(members, noKillIndices);
        var reportsJson = selectors.Count == 0
            ? null
            : await QueryAsync(BuildCharacterQuery(selectors, "recentReports(limit: 10) { data { code } }"));
        if (reportsJson is null)
        {
            return;
        }

        // Report code to the players who appear in it.
        var playersByReport = new Dictionary<string, List<int>>();
        try
        {
            using var doc = JsonDocument.Parse(reportsJson);
            if (TryGetPath(doc.RootElement, out var characters, "data", "characterData"))
            {
                foreach (var i in selectors.Keys)
                {
                    if (!TryGetPath(characters, out var reports, $"p{i}", "recentReports", "data") || reports.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var report in reports.EnumerateArray())
                    {
                        var code = report.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                        if (string.IsNullOrEmpty(code))
                        {
                            continue;
                        }

                        if (!playersByReport.TryGetValue(code, out var players))
                        {
                            playersByReport[code] = players = [];
                        }

                        players.Add(i);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse recent reports response.");
        }

        if (playersByReport.Count == 0)
        {
            return;
        }

        var reportCodes = playersByReport.Keys.ToList();
        var fightQuery = string.Join(" ", reportCodes.Select((code, index) =>
            $@"r{index}: report(code: ""{EscapeGraphQL(code)}"") {{ fights(encounterID: {encounterId}) {{ kill percentage }} }}"));
        var fightJson = await QueryAsync($"{{ reportData {{ {fightQuery} }} }}");
        if (fightJson is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(fightJson);
            if (!TryGetPath(doc.RootElement, out var reportData, "data", "reportData"))
            {
                return;
            }

            var lowestHp = new Dictionary<int, double>();
            for (var index = 0; index < reportCodes.Count; index++)
            {
                if (!TryGetPath(reportData, out var fights, $"r{index}", "fights") || fights.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var fight in fights.EnumerateArray())
                {
                    // Only wipes say how far the boss got.
                    if (fight.TryGetProperty("kill", out var killEl) && killEl.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    if (TryGetNumber(fight, "percentage") is not { } percent)
                    {
                        continue;
                    }

                    foreach (var player in playersByReport[reportCodes[index]])
                    {
                        if (!lowestHp.TryGetValue(player, out var current) || percent < current)
                        {
                            lowestHp[player] = percent;
                        }
                    }
                }
            }

            foreach (var (player, percent) in lowestHp)
            {
                results[player] = new EncounterParseResult(true, true, 0, null, percent, null);
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse fight percentages response.");
        }
    }

    #endregion

    #region Helpers

    // Member index to GraphQL character selector, for the members FFLogs can be asked about.
    private static Dictionary<int, string> BuildCharacterSelectors(IReadOnlyList<PartyMemberInfo> members, IEnumerable<int> indices)
    {
        var selectors = new Dictionary<int, string>();
        foreach (var i in indices)
        {
            if (members[i].IsResolved && GetFFLogsServer(members[i].World) is { } server)
            {
                selectors[i] = CharacterSelector(members[i].Name, server);
            }
        }

        return selectors;
    }

    private static string BuildCharacterQuery(Dictionary<int, string> selectors, string fields)
    {
        var characters = string.Join(" ", selectors.Select(selector => $"p{selector.Key}: {selector.Value} {{ {fields} }}"));
        return $"{{ characterData {{ {characters} }} }}";
    }

    private static string CharacterSelector(string name, (string Slug, string Region) server)
    {
        return $@"character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(server.Slug)}"", serverRegion: ""{server.Region}"")";
    }

    // FFLogs' server slug is the lower-cased world name.
    private static (string Slug, string Region)? GetFFLogsServer(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return null;
        }

        var region = EuWorlds.Contains(worldName) ? "EU"
            : JpWorlds.Contains(worldName) ? "JP"
            : OcWorlds.Contains(worldName) ? "OC"
            : "NA";
        return (worldName.ToLowerInvariant(), region);
    }

    private static string EscapeGraphQL(string input)
    {
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // False, rather than throwing, when a step of the path is missing or null.
    private static bool TryGetPath(JsonElement element, out JsonElement result, params ReadOnlySpan<string> path)
    {
        result = element;
        foreach (var name in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out result))
            {
                return false;
            }
        }

        return result.ValueKind != JsonValueKind.Null;
    }

    private static double? TryGetNumber(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
    }

    #endregion
}

// A player's FFLogs standing for one encounter or, when IsEncounterSpecific is false, overall.
public record EncounterParseResult(
    bool HasData,
    bool IsEncounterSpecific,
    int TotalKills,
    double? BestParse,
    double? LowestBossHpPct,
    double? AverageParsePercent)
{
    public static readonly EncounterParseResult NoLogs = new(false, true, 0, null, null, null);

    public double? Phase1BestParse { get; init; }
    public double? Phase2BestParse { get; init; }
    public double? Phase2LowestBossHpPct { get; init; }
    public int? Phase1TotalKills { get; init; }
    public int? Phase2TotalKills { get; init; }

    public double? CurrentJobBestParse { get; init; }

    // The job the overall best parse was on.
    public string? BestParseJobAbbreviation { get; init; }
    public uint? BestParseJobIconId { get; init; }
}
