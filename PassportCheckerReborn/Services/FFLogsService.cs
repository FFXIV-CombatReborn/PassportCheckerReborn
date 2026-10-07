using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Services;

// The FFLogs V2 API: an OAuth client-credentials token, then GraphQL queries.
public sealed class FFLogsService : IDisposable
{
    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string ApiUrl = "https://www.fflogs.com/api/v2/client";
    private const int MaxConcurrentRequests = 3;

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

    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(5);

    private readonly PassportCheckerReborn plugin;
    private readonly ApiHttpClient http = new(MaxConcurrentRequests);
    private readonly SemaphoreSlim tokenLock = new(1, 1);

    // Keyed by job too, since the current job's best parse is picked out of the rankings.
    private readonly ExpiringCache<(string Name, string World, string Job, int Primary, int? Secondary), EncounterParseResult> encounterCache = new(ResultLifetime);
    private readonly ExpiringCache<(string Name, string World), (double? Average, bool NotFound)> averageCache = new(ResultLifetime);

    private string? cachedToken;
    private (string ClientId, string ClientSecret) tokenCredentials;
    private DateTime tokenExpiry;

    public FFLogsService(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
    }

    // The phase entries of a multi-part duty are listed; its combined name is not.
    public static IEnumerable<string> SupportedDutyNames => Encounters.Keys;

    public void Dispose()
    {
        http.Dispose();
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
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return false;
        }

        await tokenLock.WaitAsync();
        try
        {
            return await FetchTokenAsync(clientId, clientSecret) is not null;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    // Parallel lookups share one token fetch. A token issued to other credentials is not reused.
    private async Task<string?> GetTokenAsync()
    {
        var cfg = plugin.Configuration;
        if (!cfg.HasFFLogsCredentials())
        {
            return null;
        }

        await tokenLock.WaitAsync();
        try
        {
            var credentials = (ClientId: cfg.FFLogsClientId, ClientSecret: cfg.FFLogsClientSecret);
            return cachedToken is not null && tokenCredentials == credentials && DateTime.UtcNow < tokenExpiry
                ? cachedToken
                : await FetchTokenAsync(credentials.ClientId, credentials.ClientSecret);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private async Task<string?> FetchTokenAsync(string clientId, string clientSecret)
    {
        try
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            using var response = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", basic) },
                Content = new StringContent("grant_type=client_credentials", Encoding.UTF8, "application/x-www-form-urlencoded"),
            });

            if (!response.IsSuccessStatusCode)
            {
                PassportCheckerReborn.Log.Warning($"[FFLogsService] Token request failed: {(int)response.StatusCode}");
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (doc.RootElement.GetStringOrNull("access_token") is not { } token)
            {
                return null;
            }

            tokenExpiry = DateTime.UtcNow.AddSeconds((doc.RootElement.GetNumberOrNull("expires_in") ?? 3600) - 60);
            tokenCredentials = (clientId, clientSecret);
            return cachedToken = token;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Exception during token fetch.");
            return null;
        }
    }

    #endregion

    #region Queries

    // Null when the request failed or the response has no data.
    private async Task<JsonDocument?> QueryAsync(string graphqlQuery)
    {
        var token = await GetTokenAsync();
        if (token is null)
        {
            return null;
        }

        try
        {
            var body = JsonSerializer.Serialize(new { query = graphqlQuery });
            using var response = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Interlocked.CompareExchange(ref cachedToken, null, token);
                }

                PassportCheckerReborn.Log.Warning($"[FFLogsService] GraphQL request failed: {(int)response.StatusCode}");
                return null;
            }

            var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var hasErrors = doc.RootElement.TryGetPath(out var errors, "errors");
            if (doc.RootElement.TryGetPath(out _, "data"))
            {
                if (hasErrors)
                {
                    PassportCheckerReborn.Log.Debug($"[FFLogsService] GraphQL errors: {errors.GetRawText()}");
                }

                return doc;
            }

            PassportCheckerReborn.Log.Warning($"[FFLogsService] GraphQL query returned no data: {(hasErrors ? errors.GetRawText() : "no errors given")}");
            doc.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Exception during GraphQL request.");
            return null;
        }
    }

    // Each player's overall standing in the current zone, for duties with no encounter mapping.
    public async Task<Dictionary<int, EncounterParseResult>> GetOverallParsesAsync(IReadOnlyList<PartyMemberInfo> members)
    {
        var averages = await GetZoneAveragesAsync(members, Enumerable.Range(0, members.Count));
        var results = new Dictionary<int, EncounterParseResult>();
        for (var i = 0; i < members.Count; i++)
        {
            var (average, notFound) = averages.GetValueOrDefault(i);
            results[i] = notFound ? EncounterParseResult.NotFound : new EncounterParseResult(average.HasValue, false, 0, average, null, null);
        }

        return results;
    }

    // Best performance averages in the current zone, by member index.
    public async Task<Dictionary<int, double?>> GetOverallAveragesAsync(IReadOnlyList<PartyMemberInfo> members, IEnumerable<int> indices)
    {
        var averages = await GetZoneAveragesAsync(members, indices);
        return averages.ToDictionary(pair => pair.Key, pair => pair.Value.Average);
    }

    private async Task<Dictionary<int, (double? Average, bool NotFound)>> GetZoneAveragesAsync(IReadOnlyList<PartyMemberInfo> members, IEnumerable<int> indices)
    {
        var results = new Dictionary<int, (double? Average, bool NotFound)>();
        var pending = new List<int>();
        foreach (var i in indices)
        {
            if (averageCache.TryGet((members[i].Name, members[i].World), out var cached))
            {
                results[i] = cached;
            }
            else
            {
                pending.Add(i);
            }
        }

        var selectors = BuildCharacterSelectors(members, pending);
        if (selectors.Count == 0)
        {
            return results;
        }

        try
        {
            using var doc = await QueryAsync(BuildCharacterQuery(selectors, "zoneRankings"));
            if (doc is null || !doc.RootElement.TryGetPath(out var characters, "data", "characterData"))
            {
                return results;
            }

            foreach (var i in selectors.Keys)
            {
                (double? Average, bool NotFound) standing = (null, true);
                if (characters.TryGetPath(out var character, $"p{i}"))
                {
                    standing = character.TryGetPath(out var rankings, "zoneRankings")
                        ? (rankings.GetNumberOrNull("bestPerformanceAverage") ?? rankings.GetNumberOrNull("medianPerformanceAverage"), false)
                        : (null, false);
                }

                results[i] = standing;
                averageCache.Set((members[i].Name, members[i].World), standing);
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse zone rankings response.");
        }

        return results;
    }

    // One result per member, by index. A two-part duty is looked up per part and combined.
    public async Task<Dictionary<int, EncounterParseResult>> GetEncounterDataAsync(
        IReadOnlyList<PartyMemberInfo> members, (int Primary, int? Secondary) encounterIds)
    {
        var results = new Dictionary<int, EncounterParseResult>();
        var pending = new List<int>();
        for (var i = 0; i < members.Count; i++)
        {
            if (encounterCache.TryGet(EncounterKey(members[i], encounterIds), out var cached))
            {
                results[i] = cached;
            }
            else
            {
                results[i] = EncounterParseResult.NoLogs;
                pending.Add(i);
            }
        }

        int[] ids = encounterIds.Secondary is { } secondary ? [encounterIds.Primary, secondary] : [encounterIds.Primary];
        try
        {
            foreach (var (i, parts) in await FetchEncounterResultsAsync(members, pending, ids))
            {
                var phase1 = parts[0] ?? EncounterParseResult.NoLogs;
                results[i] = parts.Length == 1 || phase1.CharacterNotFound ? phase1 : Combine(phase1, parts[1] ?? EncounterParseResult.NoLogs);
                if (Array.TrueForAll(parts, part => part is not null))
                {
                    encounterCache.Set(EncounterKey(members[i], encounterIds), results[i]);
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse encounter rankings response.");
        }

        return results;
    }

    private static (string, string, string, int, int?) EncounterKey(PartyMemberInfo member, (int Primary, int? Secondary) encounterIds)
    {
        return (member.Name, member.World, member.JobAbbreviation, encounterIds.Primary, encounterIds.Secondary);
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
            Phase1LowestBossHpPct = phase1.LowestBossHpPct,
            Phase2LowestBossHpPct = phase2.LowestBossHpPct,
            Phase1TotalKills = phase1.TotalKills,
            Phase2TotalKills = phase2.TotalKills,
            CurrentJobBestParse = currentJobBest,
            BestParseJobAbbreviation = best.BestParseJobAbbreviation,
            BestParseJobIconId = best.BestParseJobIconId,
        };
    }

    // Every encounter's rankings for everyone in one query, then, for those with no kill, how far their
    // recent logs got. Indexed by member, then encounter; null where a request failed, so it is not cached.
    private async Task<Dictionary<int, EncounterParseResult?[]>> FetchEncounterResultsAsync(
        IReadOnlyList<PartyMemberInfo> members, IEnumerable<int> indices, int[] encounterIds)
    {
        var results = new Dictionary<int, EncounterParseResult?[]>();
        var selectors = BuildCharacterSelectors(members, indices);
        if (selectors.Count == 0)
        {
            return results;
        }

        var fields = string.Join(" ", encounterIds.Select((id, e) => DifficultyByEncounter.TryGetValue(id, out var difficulty)
            ? $"e{e}: encounterRankings(encounterID: {id}, difficulty: {difficulty})"
            : $"e{e}: encounterRankings(encounterID: {id})"));
        using var doc = await QueryAsync(BuildCharacterQuery(selectors, fields));
        if (doc is null || !doc.RootElement.TryGetPath(out var characters, "data", "characterData"))
        {
            return results;
        }

        var noKills = new List<(int Member, int Encounter)>();
        foreach (var i in selectors.Keys)
        {
            var parts = results[i] = new EncounterParseResult?[encounterIds.Length];
            if (!characters.TryGetPath(out var character, $"p{i}"))
            {
                Array.Fill(parts, EncounterParseResult.NotFound);
                continue;
            }

            for (var e = 0; e < encounterIds.Length; e++)
            {
                parts[e] = EncounterParseResult.NoLogs;
                if (!character.TryGetPath(out var rankings, $"e{e}"))
                {
                    continue;
                }

                var totalKills = (int)(rankings.GetNumberOrNull("totalKills") ?? 0);
                if (totalKills > 0)
                {
                    parts[e] = ParseRankings(rankings, totalKills, members[i].JobAbbreviation);
                }
                else
                {
                    noKills.Add((i, e));
                }
            }
        }

        if (noKills.Count > 0)
        {
            var lowest = await FetchLowestFightPercentAsync(members, noKills, encounterIds);
            foreach (var (i, e) in noKills)
            {
                results[i][e] = lowest is null ? null
                    : lowest.TryGetValue((i, e), out var percent) ? new EncounterParseResult(true, true, 0, null, percent, null)
                    : EncounterParseResult.NoLogs;
            }
        }

        return results;
    }

    private static EncounterParseResult ParseRankings(JsonElement rankings, int totalKills, string jobAbbreviation)
    {
        double? bestParse = null;
        string? bestSpec = null;
        double? currentJobBest = null;
        var currentSpec = SpecByJob.GetValueOrDefault(jobAbbreviation);

        if (rankings.TryGetPath(out var ranks, "ranks") && ranks.ValueKind == JsonValueKind.Array)
        {
            foreach (var rank in ranks.EnumerateArray())
            {
                if (rank.GetNumberOrNull("rankPercent") is not { } percent)
                {
                    continue;
                }

                var spec = rank.GetStringOrNull("spec");
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

    // For players with no kill: the lowest fight percentage reached across their ten most recent reports,
    // by member and encounter. Null when a request failed.
    private async Task<Dictionary<(int Member, int Encounter), double>?> FetchLowestFightPercentAsync(
        IReadOnlyList<PartyMemberInfo> members, List<(int Member, int Encounter)> noKills, int[] encounterIds)
    {
        var selectors = BuildCharacterSelectors(members, noKills.Select(pair => pair.Member).Distinct());
        using var reportsDoc = await QueryAsync(BuildCharacterQuery(selectors, "recentReports(limit: 10) { data { code } }"));
        if (reportsDoc is null || !reportsDoc.RootElement.TryGetPath(out var characters, "data", "characterData"))
        {
            return null;
        }

        // Report code to the players who appear in it.
        var playersByReport = new Dictionary<string, List<int>>();
        foreach (var i in selectors.Keys)
        {
            if (!characters.TryGetPath(out var reports, $"p{i}", "recentReports", "data") || reports.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var report in reports.EnumerateArray())
            {
                if (report.GetStringOrNull("code") is not { Length: > 0 } code)
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

        var lowest = new Dictionary<(int Member, int Encounter), double>();
        if (playersByReport.Count == 0)
        {
            return lowest;
        }

        var wanted = noKills.Select(pair => pair.Encounter).Distinct().ToArray();
        var fightFields = string.Join(" ", wanted.Select(e => $"f{e}: fights(encounterID: {encounterIds[e]}) {{ kill fightPercentage }}"));
        var reportCodes = playersByReport.Keys.ToList();
        var reportQuery = string.Join(" ", reportCodes.Select((code, r) => $@"r{r}: report(code: ""{EscapeGraphQL(code)}"") {{ {fightFields} }}"));
        using var fightsDoc = await QueryAsync($"{{ reportData {{ {reportQuery} }} }}");
        if (fightsDoc is null || !fightsDoc.RootElement.TryGetPath(out var reportData, "data", "reportData"))
        {
            return null;
        }

        var noKillSet = noKills.ToHashSet();
        for (var r = 0; r < reportCodes.Count; r++)
        {
            foreach (var e in wanted)
            {
                if (!reportData.TryGetPath(out var fights, $"r{r}", $"f{e}") || fights.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var fight in fights.EnumerateArray())
                {
                    // Only wipes say how far the fight got.
                    if (fight.TryGetPath(out var kill, "kill") && kill.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    if (fight.GetNumberOrNull("fightPercentage") is not { } percent)
                    {
                        continue;
                    }

                    foreach (var i in playersByReport[reportCodes[r]])
                    {
                        if (noKillSet.Contains((i, e)) && (!lowest.TryGetValue((i, e), out var current) || percent < current))
                        {
                            lowest[(i, e)] = percent;
                        }
                    }
                }
            }
        }

        return lowest;
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
    public static readonly EncounterParseResult NotFound = NoLogs with { CharacterNotFound = true };

    // FFLogs has no character by this name and world.
    public bool CharacterNotFound { get; init; }

    public double? Phase1BestParse { get; init; }
    public double? Phase2BestParse { get; init; }
    public double? Phase1LowestBossHpPct { get; init; }
    public double? Phase2LowestBossHpPct { get; init; }
    public int? Phase1TotalKills { get; init; }
    public int? Phase2TotalKills { get; init; }

    public double? CurrentJobBestParse { get; init; }

    // The job the overall best parse was on.
    public string? BestParseJobAbbreviation { get; init; }
    public uint? BestParseJobIconId { get; init; }
}
