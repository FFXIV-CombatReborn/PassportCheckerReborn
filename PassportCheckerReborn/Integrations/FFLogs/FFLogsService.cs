using PassportCheckerReborn.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Integrations.FFLogs;

public sealed class FFLogsService : IDisposable
{
    private const string ApiUrl = "https://www.fflogs.com/api/v2/client";
    private const int MaxConcurrentRequests = 3;

    private static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(5);

    private readonly ApiHttpClient http = new(MaxConcurrentRequests);
    private readonly FFLogsTokenProvider tokens;

    private readonly ExpiringCache<(string Name, string World, string Job, int Primary, int? Secondary), EncounterParseResult> encounterCache = new(ResultLifetime);
    private readonly ExpiringCache<(string Name, string World), (double? Average, bool NotFound)> averageCache = new(ResultLifetime);

    public FFLogsService()
    {
        tokens = new FFLogsTokenProvider(http);
    }

    public void Dispose()
    {
        http.Dispose();
    }

    public Task<bool> TestCredentialsAsync(string clientId, string clientSecret)
    {
        return tokens.TestCredentialsAsync(clientId, clientSecret);
    }

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

    public async Task<Dictionary<int, double?>> GetOverallAveragesAsync(IReadOnlyList<PartyMemberInfo> members, IEnumerable<int> indices)
    {
        var averages = await GetZoneAveragesAsync(members, indices);
        return averages.ToDictionary(pair => pair.Key, pair => pair.Value.Average);
    }

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
            Plugin.Log.Warning(ex, "[FFLogsService] Failed to parse encounter rankings response.");
        }

        return results;
    }

    private async Task<JsonDocument?> QueryAsync(string graphqlQuery)
    {
        var token = await tokens.GetTokenAsync();
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
                    tokens.Invalidate(token);
                }

                Plugin.Log.Warning($"[FFLogsService] GraphQL request failed: {(int)response.StatusCode}");
                return null;
            }

            var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var hasErrors = doc.RootElement.TryGetPath(out var errors, "errors");
            if (doc.RootElement.TryGetPath(out _, "data"))
            {
                if (hasErrors)
                {
                    Plugin.Log.Debug($"[FFLogsService] GraphQL errors: {errors.GetRawText()}");
                }

                return doc;
            }

            Plugin.Log.Warning($"[FFLogsService] GraphQL query returned no data: {(hasErrors ? errors.GetRawText() : "no errors given")}");
            doc.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[FFLogsService] Exception during GraphQL request.");
            return null;
        }
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

        var selectors = FFLogsQuery.BuildCharacterSelectors(members, pending);
        if (selectors.Count == 0)
        {
            return results;
        }

        try
        {
            using var doc = await QueryAsync(FFLogsQuery.BuildCharacterQuery(selectors, "zoneRankings"));
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
            Plugin.Log.Warning(ex, "[FFLogsService] Failed to parse zone rankings response.");
        }

        return results;
    }

    private static (string, string, string, int, int?) EncounterKey(PartyMemberInfo member, (int Primary, int? Secondary) encounterIds)
    {
        return (member.Name, member.World, member.JobAbbreviation, encounterIds.Primary, encounterIds.Secondary);
    }

    private static EncounterParseResult Combine(EncounterParseResult phase1, EncounterParseResult phase2)
    {
        var best = phase2 is { HasData: true, TotalKills: > 0 } ? phase2
            : phase1 is { HasData: true, TotalKills: > 0 } ? phase1
            : phase2;

        var currentJobBest = phase1.CurrentJobBestParse.HasValue && phase2.CurrentJobBestParse.HasValue
            ? Math.Max(phase1.CurrentJobBestParse.Value, phase2.CurrentJobBestParse.Value)
            : phase2.CurrentJobBestParse ?? phase1.CurrentJobBestParse;

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

    private async Task<Dictionary<int, EncounterParseResult?[]>> FetchEncounterResultsAsync(
        IReadOnlyList<PartyMemberInfo> members, IEnumerable<int> indices, int[] encounterIds)
    {
        var results = new Dictionary<int, EncounterParseResult?[]>();
        var selectors = FFLogsQuery.BuildCharacterSelectors(members, indices);
        if (selectors.Count == 0)
        {
            return results;
        }

        var fields = string.Join(" ", encounterIds.Select((id, e) => $"e{e}: encounterRankings({FFLogsQuery.EncounterArguments(id)})"));
        using var doc = await QueryAsync(FFLogsQuery.BuildCharacterQuery(selectors, fields));
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
        var currentSpec = Jobs.GetFFLogsSpec(jobAbbreviation);

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

        var bestJob = Jobs.FromFFLogsSpec(bestSpec);
        return new EncounterParseResult(true, true, totalKills, bestParse, null, null)
        {
            CurrentJobBestParse = currentJobBest,
            BestParseJobAbbreviation = bestJob,
            BestParseJobIconId = Jobs.GetIconId(bestJob),
        };
    }

    private async Task<Dictionary<(int Member, int Encounter), double>?> FetchLowestFightPercentAsync(
        IReadOnlyList<PartyMemberInfo> members, List<(int Member, int Encounter)> noKills, int[] encounterIds)
    {
        var selectors = FFLogsQuery.BuildCharacterSelectors(members, noKills.Select(pair => pair.Member).Distinct());
        using var reportsDoc = await QueryAsync(FFLogsQuery.BuildCharacterQuery(selectors, "recentReports(limit: 10) { data { code } }"));
        if (reportsDoc is null || !reportsDoc.RootElement.TryGetPath(out var characters, "data", "characterData"))
        {
            return null;
        }

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
        var fightFields = string.Join(" ", wanted.Select(e =>
            $"f{e}: fights({FFLogsQuery.EncounterArguments(encounterIds[e])}, killType: Wipes, translate: false) {{ fightPercentage }}"));
        var reportCodes = playersByReport.Keys.ToList();
        var reportQuery = string.Join(" ", reportCodes.Select((code, r) => $@"r{r}: report(code: ""{FFLogsQuery.EscapeGraphQL(code)}"") {{ {fightFields} }}"));
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
}
