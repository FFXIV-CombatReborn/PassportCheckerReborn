using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Services;

/// <summary>
/// Provides integration with the
/// <see href="https://www.fflogs.com/api/v2">FFLogs V2 API</see>.
///
/// <para>
/// Authentication uses the OAuth 2.0 Client-Credentials flow:
/// POST https://www.fflogs.com/oauth/token
/// with grant_type=client_credentials + Basic auth (clientId:clientSecret).
/// </para>
///
/// <para>
/// Queries are sent as GraphQL to https://www.fflogs.com/api/v2/client
/// with the Bearer token obtained above.
/// </para>
/// </summary>
public sealed class FFLogsService : IDisposable
{
    private readonly PassportCheckerReborn plugin;
    private readonly HttpClient httpClient;

    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string ApiUrl = "https://www.fflogs.com/api/v2/client";

    private string? cachedToken;
    private DateTime tokenExpiry = DateTime.MinValue;

    // ── Parse result cache (playerKey → (encounterId → percentile)) ─────────
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, CachedParse>> parseCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public FFLogsService(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PassportCheckerReborn/{PassportCheckerReborn.Version}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Credential testing (called from the ConfigWindow)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to obtain an OAuth token with the supplied credentials.
    /// Returns <c>true</c> when a token is successfully issued.
    /// </summary>
    public async Task<bool> TestCredentialsAsync(string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return false;

        var token = await FetchTokenAsync(clientId, clientSecret);
        return token is not null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Token management
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string?> FetchTokenAsync(string clientId, string clientSecret)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);

            // Basic auth header
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            // Body
            request.Content = new StringContent(
                "grant_type=client_credentials",
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                PassportCheckerReborn.Log.Warning(
                    $"[FFLogsService] Token request failed: {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl))
                return null;

            var token = tokenEl.GetString();

            // Cache the token
            cachedToken = token;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expEl)
                ? expEl.GetInt32() : 3600;
            tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60); // 60-second safety margin

            PassportCheckerReborn.Log.Information("[FFLogsService] Token obtained successfully.");
            return token;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Exception during token fetch.");
            return null;
        }
    }

    /// <summary>Returns a valid Bearer token, refreshing if necessary.</summary>
    private async Task<string?> GetTokenAsync()
    {
        var cfg = plugin.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.FFLogsClientId) ||
            string.IsNullOrWhiteSpace(cfg.FFLogsClientSecret))
            return null;

        if (cachedToken is not null && DateTime.UtcNow < tokenExpiry)
            return cachedToken;

        return await FetchTokenAsync(cfg.FFLogsClientId, cfg.FFLogsClientSecret);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GraphQL query helper
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes an arbitrary GraphQL query against the FFLogs V2 API.
    /// Returns the raw JSON response string, or <c>null</c> on failure.
    /// </summary>
    public async Task<string?> QueryAsync(string graphqlQuery)
    {
        var token = await GetTokenAsync();
        if (token is null)
            return null;

        try
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new { query = graphqlQuery });
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                PassportCheckerReborn.Log.Warning(
                    $"[FFLogsService] GraphQL request failed: {(int)response.StatusCode}");
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

    // ─────────────────────────────────────────────────────────────────────────
    // Character ranking lookup
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the best parse percentile for the given player and encounter.
    /// Results are cached for <see cref="CacheTtl"/> to avoid excessive API calls.
    /// </summary>
    public async Task<double?> GetBestParseAsync(string playerName, string serverName, string serverRegion, int encounterId)
    {
        // Check cache first
        var playerKey = $"{playerName}@{serverName}";
        if (parseCache.TryGetValue(playerKey, out var encounterCache) &&
            encounterCache.TryGetValue(encounterId, out var cached) &&
            DateTime.UtcNow < cached.Expiry)
        {
            return cached.Percentile;
        }

        var difficultyParam = GetDifficultyForEncounter(encounterId) is { } diff
            ? $", difficulty: {diff}"
            : string.Empty;
        var query = $@"
        {{
          characterData {{
            character(name: ""{EscapeGraphQL(playerName)}"", serverSlug: ""{EscapeGraphQL(serverName)}"", serverRegion: ""{EscapeGraphQL(serverRegion)}"") {{
              encounterRankings(encounterID: {encounterId}{difficultyParam})
            }}
          }}
        }}";

        var json = await QueryAsync(query);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);

            // Navigate: data.characterData.character.encounterRankings
            if (!doc.RootElement.TryGetProperty("data", out var dataEl))
                return null;
            if (!dataEl.TryGetProperty("characterData", out var charDataEl))
                return null;
            if (!charDataEl.TryGetProperty("character", out var charEl))
                return null;
            if (charEl.ValueKind == JsonValueKind.Null)
                return null;
            if (!charEl.TryGetProperty("encounterRankings", out var rankingsEl))
                return null;

            // encounterRankings contains a "ranks" array; extract the best percentile
            double? bestParse = null;
            if (rankingsEl.TryGetProperty("ranks", out var ranksEl) &&
                ranksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var rank in ranksEl.EnumerateArray())
                {
                    if (rank.TryGetProperty("rankPercent", out var pctEl))
                    {
                        var pct = pctEl.GetDouble();
                        if (bestParse is null || pct > bestParse.Value)
                            bestParse = pct;
                    }
                }
            }

            // Also try the "bestAmount" field as a fallback (some API versions)
            if (bestParse is null && rankingsEl.TryGetProperty("bestAmount", out var bestAmtEl))
            {
                bestParse = bestAmtEl.GetDouble();
            }

            // Cache the result
            var cache = parseCache.GetOrAdd(playerKey, _ => new ConcurrentDictionary<int, CachedParse>());
            cache[encounterId] = new CachedParse(bestParse, DateTime.UtcNow + CacheTtl);

            return bestParse;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse ranking response.");
        }

        return null;
    }

    /// <summary>
    /// Fetches the FFLogs character ID for the given player and world.
    /// </summary>
    public async Task<long?> GetCharacterIdAsync(string playerName, string worldName)
    {
        var serverInfo = GetFFLogsServer(worldName);
        if (serverInfo is null)
            return null;

        var (serverSlug, serverRegion) = serverInfo.Value;
        var query = $@"
        {{
          characterData {{
            character(name: ""{EscapeGraphQL(playerName)}"", serverSlug: ""{EscapeGraphQL(serverSlug)}"", serverRegion: ""{EscapeGraphQL(serverRegion)}"") {{
              id
            }}
          }}
        }}";

        var json = await QueryAsync(query);
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl))
                return null;
            if (!dataEl.TryGetProperty("characterData", out var charDataEl))
                return null;
            if (!charDataEl.TryGetProperty("character", out var charEl))
                return null;
            if (charEl.ValueKind == JsonValueKind.Null)
                return null;
            if (!charEl.TryGetProperty("id", out var idEl))
                return null;

            if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var idValue))
                return idValue;

            if (idEl.ValueKind == JsonValueKind.String && long.TryParse(idEl.GetString(), out var parsed))
                return parsed;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse character ID response.");
        }

        return null;
    }

    /// <summary>
    /// Fetches the best performance average from the character's latest zone rankings.
    /// This provides an overall parse percentile without requiring a specific encounter ID.
    /// </summary>
    public async Task<double?> GetBestPerfAvgAsync(string playerName, string worldName)
    {
        var serverInfo = GetFFLogsServer(worldName);
        if (serverInfo is null)
            return null;

        var (serverSlug, serverRegion) = serverInfo.Value;
        var query = $@"
        {{
          characterData {{
            character(name: ""{EscapeGraphQL(playerName)}"", serverSlug: ""{EscapeGraphQL(serverSlug)}"", serverRegion: ""{EscapeGraphQL(serverRegion)}"") {{
              zoneRankings
            }}
          }}
        }}";

        var json = await QueryAsync(query);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var dataEl))
                return null;
            if (!dataEl.TryGetProperty("characterData", out var charDataEl))
                return null;
            if (!charDataEl.TryGetProperty("character", out var charEl))
                return null;
            if (charEl.ValueKind == JsonValueKind.Null)
                return null;
            if (!charEl.TryGetProperty("zoneRankings", out var rankingsEl))
                return null;

            // Try bestPerformanceAverage first, fall back to medianPerformanceAverage
            if (rankingsEl.TryGetProperty("bestPerformanceAverage", out var bestAvgEl) &&
                bestAvgEl.ValueKind == JsonValueKind.Number)
            {
                return bestAvgEl.GetDouble();
            }

            if (rankingsEl.TryGetProperty("medianPerformanceAverage", out var medAvgEl) &&
                medAvgEl.ValueKind == JsonValueKind.Number)
            {
                return medAvgEl.GetDouble();
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse zone rankings response.");
        }

        return null;
    }

    /// <summary>Clears the parse result cache.</summary>
    public void ClearCache() => parseCache.Clear();

    // ─────────────────────────────────────────────────────────────────────────
    // Encounter-specific data retrieval
    // ─────────────────────────────────────────────────────────────────────────

    // FFLogs difficulty constants
    private const int DifficultyNormal = 100;
    private const int DifficultyHigh = 101;

    /// <summary>
    /// Maps FFXIV duty names (from ContentFinderCondition / LookingForGroupDetail
    /// AtkValue[15]) to FFLogs encounter IDs and difficulty levels.
    /// Must be updated each content tier.
    /// </summary>
    private static readonly Dictionary<string, (int ZoneId, int EncounterId, int Difficulty)> DutyNameToEncounterInfo = new(StringComparer.OrdinalIgnoreCase)
    {
        // Savage Raids
        ["AAC Light-heavyweight M1 (Savage)"] = (62, 93, DifficultyHigh),
        ["AAC Light-heavyweight M2 (Savage)"] = (62, 94, DifficultyHigh),
        ["AAC Light-heavyweight M3 (Savage)"] = (62, 95, DifficultyHigh),
        ["AAC Light-heavyweight M4 (Savage)"] = (62, 96, DifficultyHigh),

        ["AAC Cruiserweight M1 (Savage)"] = (68, 97, DifficultyHigh),
        ["AAC Cruiserweight M2 (Savage)"] = (68, 98, DifficultyHigh),
        ["AAC Cruiserweight M3 (Savage)"] = (68, 99, DifficultyHigh),
        ["AAC Cruiserweight M4 (Savage)"] = (68, 100, DifficultyHigh),

        ["AAC Heavyweight M1 (Savage)"] = (73, 101, DifficultyHigh),
        ["AAC Heavyweight M2 (Savage)"] = (73, 102, DifficultyHigh),
        ["AAC Heavyweight M3 (Savage)"] = (73, 103, DifficultyHigh),
        ["AAC Heavyweight M4 (Savage) P1"] = (73, 104, DifficultyHigh),
        ["AAC Heavyweight M4 (Savage) P2"] = (73, 105, DifficultyHigh),

        // Chaotic Raids
        ["The Cloud of Darkness (Chaotic)"] = (66, 2061, DifficultyNormal),

        // Unreal Trials
        ["Tsukuyomi's Pain (Unreal)"] = (64, 3012, DifficultyNormal),

        // Extreme Trials
        ["Worqor Lar Dor (Extreme)"] = (58, 1071, DifficultyNormal),
        ["Everkeep (Extreme)"] = (58, 1072, DifficultyNormal),
        ["The Minstrel's Ballad: Sphene's Burden"] = (58, 1078, DifficultyNormal),
        ["Recollection (Extreme)"] = (67, 1080, DifficultyNormal),
        ["The Minstrel's Ballad: Necron's Embrace"] = (67, 1081, DifficultyNormal),
        ["The Windward Wilds (Extreme)"] = (67, 1082, DifficultyNormal),
        ["Hell on Rails (Extreme)"] = (72, 1083, DifficultyNormal),

        // Ultimate Raids
        ["The Unending Coil of Bahamut (Ultimate)"] = (59, 1073, DifficultyNormal),
        ["The Weapon's Refrain (Ultimate)"] = (59, 1074, DifficultyNormal),
        ["The Epic of Alexander (Ultimate)"] = (59, 1075, DifficultyNormal),
        ["Dragonsong's Reprise (Ultimate)"] = (59, 1076, DifficultyNormal),
        ["The Omega Protocol (Ultimate)"] = (59, 1077, DifficultyNormal),
        ["Futures Rewritten (Ultimate)"] = (59, 1079, DifficultyNormal),
    };

    /// <summary>Backwards-compatible helper that returns only the encounter ID.</summary>
    private static readonly Dictionary<string, int> DutyNameToEncounterId;

    /// <summary>Maps encounter IDs to their FFLogs difficulty level.</summary>
    private static readonly Dictionary<int, int> EncounterIdToDifficulty;

    /// <summary>Maps encounter IDs to their FFLogs zone ID.</summary>
    private static readonly Dictionary<int, int> EncounterIdToZoneId;

    static FFLogsService()
    {
        DutyNameToEncounterId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        EncounterIdToDifficulty = [];
        EncounterIdToZoneId = [];
        foreach (var (name, (zoneId, encId, diff)) in DutyNameToEncounterInfo)
        {
            DutyNameToEncounterId[name] = encId;
            EncounterIdToDifficulty[encId] = diff;
            EncounterIdToZoneId[encId] = zoneId;
        }
    }

    /// <summary>
    /// Returns the FFLogs difficulty level for the given encounter ID, or <c>null</c> if unknown.
    /// </summary>
    public static int? GetDifficultyForEncounter(int encounterId)
        => EncounterIdToDifficulty.TryGetValue(encounterId, out var diff) ? diff : null;

    /// <summary>
    /// Returns the FFLogs zone ID for the given encounter ID, or <c>null</c> if unknown.
    /// </summary>
    public static int? GetZoneIdForEncounter(int encounterId)
        => EncounterIdToZoneId.TryGetValue(encounterId, out var zoneId) ? zoneId : null;

    /// <summary>
    /// Returns the FFLogs zone ID for the given FFXIV duty name, or <c>null</c> if not mapped.
    /// For multi-part duties the zone is resolved from the primary (phase 1) encounter ID.
    /// </summary>
    public static int? GetZoneIdForDuty(string? dutyName)
    {
        if (string.IsNullOrWhiteSpace(dutyName))
            return null;

        if (DutyNameToEncounterInfo.TryGetValue(dutyName!, out var info))
            return info.ZoneId;

        if (MultiPartDutyToEncounterIds.TryGetValue(dutyName!, out var multiIds))
            return GetZoneIdForEncounter(multiIds.Phase1EncounterId);

        return null;
    }

    private static readonly Dictionary<string, (int Phase1EncounterId, int Phase2EncounterId)> MultiPartDutyToEncounterIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AAC Heavyweight M4 (Savage)"] = (104, 105),
        };

    /// <summary>
    /// Returns the FFLogs encounter ID for the given FFXIV duty name, or <c>null</c> if not mapped.
    /// </summary>
    public static int? GetEncounterIdForDuty(string? dutyName)
    {
        var ids = GetEncounterIdsForDuty(dutyName);
        return ids?.PrimaryEncounterId;
    }

    /// <summary>
    /// Returns the FFLogs encounter IDs for the given FFXIV duty name, including multi-part encounters.
    /// </summary>
    public static (int PrimaryEncounterId, int? SecondaryEncounterId)? GetEncounterIdsForDuty(string? dutyName)
    {
        if (string.IsNullOrWhiteSpace(dutyName))
            return null;

        if (MultiPartDutyToEncounterIds.TryGetValue(dutyName!, out var multiIds))
            return (multiIds.Phase1EncounterId, multiIds.Phase2EncounterId);

        return DutyNameToEncounterId.TryGetValue(dutyName!, out var id) ? (id, null) : null;
    }

    /// <summary>
    /// Returns all duty names that have FFLogs encounter mappings.
    /// Multi-part base names (e.g. "AAC Heavyweight M4 (Savage)") are excluded
    /// because the per-phase entries (P1 / P2) are already present.
    /// </summary>
    public static IReadOnlyCollection<string> GetAllSupportedDutyNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in DutyNameToEncounterId.Keys)
            names.Add(key);
        return names;
    }

    /// <summary>
    /// Fetches encounter-specific data for all members in a batch.
    /// <para>Phase 1: Query encounterRankings for all characters.</para>
    /// <para>Phase 2: For characters with no kills, query their recent reports.</para>
    /// <para>Phase 3: Get fight percentages from those reports to find best progression.</para>
    /// </summary>
    public async Task<Dictionary<int, EncounterParseResult>> GetEncounterDataForAllAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members, int encounterId)
    {
        var results = new Dictionary<int, EncounterParseResult>();

        // Phase 1: Build a single GraphQL query for encounterRankings for all members
        var queryParts = new List<string>();
        var validIndices = new List<int>();
        var difficultyParam = GetDifficultyForEncounter(encounterId) is { } diff
            ? $", difficulty: {diff}"
            : string.Empty;

        for (var i = 0; i < members.Count; i++)
        {
            var (name, world, _) = members[i];
            var serverInfo = GetFFLogsServer(world);
            if (serverInfo is null)
            {
                results[i] = new EncounterParseResult(false, true, 0, null, null, null);
                continue;
            }

            var (slug, region) = serverInfo.Value;
            validIndices.Add(i);
            queryParts.Add(
                $@"p{i}: character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(slug)}"", serverRegion: ""{EscapeGraphQL(region)}"") {{ encounterRankings(encounterID: {encounterId}{difficultyParam}) }}");
        }

        if (queryParts.Count == 0)
            return results;

        var fullQuery = $"{{ characterData {{ {string.Join(" ", queryParts)} }} }}";
        var json = await QueryAsync(fullQuery);

        if (json is null)
        {
            foreach (var i in validIndices)
                results[i] = new EncounterParseResult(false, true, 0, null, null, null);
            return results;
        }

        var noKillIndices = new List<int>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("characterData", out var charDataEl))
            {
                PassportCheckerReborn.Log.Warning(
                    "[FFLogsService] Encounter batch response missing data/characterData.");
                foreach (var i in validIndices)
                    results[i] = new EncounterParseResult(false, true, 0, null, null, null);
                return results;
            }

            foreach (var i in validIndices)
            {
                if (!charDataEl.TryGetProperty($"p{i}", out var charEl) ||
                    charEl.ValueKind == JsonValueKind.Null)
                {
                    results[i] = new EncounterParseResult(false, true, 0, null, null, null);
                    continue;
                }

                if (!charEl.TryGetProperty("encounterRankings", out var rankingsEl) ||
                    rankingsEl.ValueKind == JsonValueKind.Null)
                {
                    results[i] = new EncounterParseResult(false, true, 0, null, null, null);
                    continue;
                }

                var totalKills = 0;
                if (rankingsEl.TryGetProperty("totalKills", out var tkEl) &&
                    tkEl.ValueKind == JsonValueKind.Number)
                    totalKills = tkEl.GetInt32();

                if (totalKills > 0)
                {
                    double? bestParse = null;
                    string? bestParseSpec = null;
                    double? currentJobBestParse = null;

                    var currentJobSpec = GetSpecForJob(members[i].JobAbbreviation);

                    if (rankingsEl.TryGetProperty("ranks", out var ranksEl) &&
                        ranksEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rank in ranksEl.EnumerateArray())
                        {
                            if (rank.TryGetProperty("rankPercent", out var pctEl) &&
                                pctEl.ValueKind == JsonValueKind.Number)
                            {
                                var pct = pctEl.GetDouble();
                                var spec = rank.TryGetProperty("spec", out var specEl)
                                    ? specEl.GetString() : null;

                                // Track overall best parse (any job)
                                if (!bestParse.HasValue || pct > bestParse.Value)
                                {
                                    bestParse = pct;
                                    bestParseSpec = spec;
                                }

                                // Track best parse for the player's current job
                                if (currentJobSpec != null && spec != null &&
                                    string.Equals(spec, currentJobSpec, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!currentJobBestParse.HasValue || pct > currentJobBestParse.Value)
                                        currentJobBestParse = pct;
                                }
                            }
                        }
                    }

                    results[i] = new EncounterParseResult(true, true, totalKills, bestParse, null, null)
                    {
                        CurrentJobBestParse = currentJobBestParse,
                        BestParseSpec = bestParseSpec,
                        BestParseJobAbbreviation = GetJobAbbrevForSpec(bestParseSpec),
                        BestParseJobIconId = GetJobIconIdForSpec(bestParseSpec),
                    };
                }
                else
                {
                    noKillIndices.Add(i);
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex,
                "[FFLogsService] Failed to parse encounter rankings batch response.");
            foreach (var i in validIndices)
            {
                if (!results.ContainsKey(i))
                    results[i] = new EncounterParseResult(false, true, 0, null, null, null);
            }
            return results;
        }

        // Phases 2 & 3: Get progression data for players with no kills
        if (noKillIndices.Count > 0)
            await FetchProgressionDataAsync(members, encounterId, noKillIndices, results);

        return results;
    }

    /// <summary>
    /// Fetches encounter-specific data for multi-part encounters (e.g., P1/P2) for all members.
    /// </summary>
    public async Task<Dictionary<int, EncounterParseResult>> GetMultiEncounterDataForAllAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members, int phase1EncounterId, int phase2EncounterId)
    {
        var phase1Results = await GetEncounterDataForAllAsync(members, phase1EncounterId);
        var phase2Results = await GetEncounterDataForAllAsync(members, phase2EncounterId);

        var combined = new Dictionary<int, EncounterParseResult>();
        for (var i = 0; i < members.Count; i++)
        {
            phase1Results.TryGetValue(i, out var phase1);
            phase2Results.TryGetValue(i, out var phase2);

            var phase1Kills = phase1?.TotalKills ?? 0;
            var phase2Kills = phase2?.TotalKills ?? 0;
            var fullClears = Math.Min(phase1Kills, phase2Kills);
            var hasData = (phase1?.HasData ?? false) || (phase2?.HasData ?? false);

            // Determine best job info from phase results (prefer P2, fall back to P1)
            var bestPhase = (phase2?.HasData == true && phase2.TotalKills > 0) ? phase2
                          : (phase1?.HasData == true && phase1.TotalKills > 0) ? phase1
                          : phase2 ?? phase1;

            // Determine CurrentJobBestParse: take the value from whichever phase has it
            double? currentJobBestParse;
            if (phase2?.CurrentJobBestParse.HasValue == true && phase1?.CurrentJobBestParse.HasValue == true)
                currentJobBestParse = Math.Max(phase1.CurrentJobBestParse!.Value, phase2.CurrentJobBestParse!.Value);
            else
                currentJobBestParse = phase2?.CurrentJobBestParse ?? phase1?.CurrentJobBestParse;

            // BestParse for the combined result – the overall best parse (any job) from
            // the most relevant phase so DrawBestParseOnDifferentJob can display it.
            var combinedBestParse = bestPhase?.BestParse;

            combined[i] = new EncounterParseResult(hasData, true, fullClears, combinedBestParse, null, null)
            {
                Phase1BestParse = phase1?.BestParse,
                Phase2BestParse = phase2?.BestParse,
                Phase2LowestBossHpPct = phase2?.LowestBossHpPct,
                Phase1TotalKills = phase1Kills,
                Phase2TotalKills = phase2Kills,
                CurrentJobBestParse = currentJobBestParse,
                BestParseSpec = bestPhase?.BestParseSpec,
                BestParseJobAbbreviation = bestPhase?.BestParseJobAbbreviation,
                BestParseJobIconId = bestPhase?.BestParseJobIconId,
            };
        }

        return combined;
    }

    /// <summary>
    /// Phases 2 &amp; 3: Fetches progression data (lowest boss HP %) for players
    /// who have no kills of the encounter by querying their recent reports.
    /// </summary>
    private async Task FetchProgressionDataAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members,
        int encounterId,
        List<int> noKillIndices,
        Dictionary<int, EncounterParseResult> results)
    {
        // Phase 2: Get recent report codes for each no-kill player
        var queryParts = new List<string>();
        var phase2ValidIndices = new List<int>();

        foreach (var i in noKillIndices)
        {
            var (name, world, _) = members[i];
            var serverInfo = GetFFLogsServer(world);
            if (serverInfo is null) continue;

            var (slug, region) = serverInfo.Value;
            phase2ValidIndices.Add(i);
            queryParts.Add(
                $@"p{i}: character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(slug)}"", serverRegion: ""{EscapeGraphQL(region)}"") {{ recentReports(limit: 10) {{ data {{ code }} }} }}");
        }

        if (queryParts.Count == 0)
        {
            foreach (var i in noKillIndices)
                results[i] = new EncounterParseResult(false, true, 0, null, null, null);
            return;
        }

        var reportsQuery = $"{{ characterData {{ {string.Join(" ", queryParts)} }} }}";
        var reportsJson = await QueryAsync(reportsQuery);

        if (reportsJson is null)
        {
            PassportCheckerReborn.Log.Warning(
                "[FFLogsService] Recent reports query failed; marking no-kill players as no logs.");
            foreach (var i in noKillIndices)
                if (!results.ContainsKey(i))
                    results[i] = new EncounterParseResult(false, true, 0, null, null, null);
            return;
        }

        // Parse report codes per player
        var playerReportCodes = new Dictionary<int, List<string>>();
        try
        {
            using var doc = JsonDocument.Parse(reportsJson);
            if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.TryGetProperty("characterData", out var charDataEl))
            {
                foreach (var i in phase2ValidIndices)
                {
                    if (!charDataEl.TryGetProperty($"p{i}", out var charEl) ||
                        charEl.ValueKind == JsonValueKind.Null)
                        continue;

                    if (!charEl.TryGetProperty("recentReports", out var reportsEl) ||
                        !reportsEl.TryGetProperty("data", out var dataArr) ||
                        dataArr.ValueKind != JsonValueKind.Array)
                        continue;

                    var codes = new List<string>();
                    foreach (var report in dataArr.EnumerateArray())
                    {
                        if (report.TryGetProperty("code", out var codeEl))
                        {
                            var code = codeEl.GetString();
                            if (!string.IsNullOrEmpty(code))
                                codes.Add(code!);
                        }
                    }

                    if (codes.Count > 0)
                    {
                        playerReportCodes[i] = codes;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex,
                "[FFLogsService] Failed to parse recent reports response.");
        }

        // Mark players with no report codes as "no logs"
        foreach (var i in noKillIndices)
        {
            if (!playerReportCodes.ContainsKey(i) && !results.ContainsKey(i))
            {
                results[i] = new EncounterParseResult(false, true, 0, null, null, null);
            }
        }

        if (playerReportCodes.Count == 0)
            return;

        // Phase 3: Get fight percentages from reports
        var codeToPlayers = new Dictionary<string, HashSet<int>>();
        foreach (var (playerIdx, codes) in playerReportCodes)
        {
            foreach (var code in codes)
            {
                if (!codeToPlayers.TryGetValue(code, out var set))
                {
                    set = [];
                    codeToPlayers[code] = set;
                }
                set.Add(playerIdx);
            }
        }

        var fightQueryParts = new List<string>();
        var aliasToCode = new Dictionary<string, string>();
        var aliasIdx = 0;
        foreach (var code in codeToPlayers.Keys)
        {
            var alias = $"r{aliasIdx}";
            aliasToCode[alias] = code;
            fightQueryParts.Add(
                $@"{alias}: report(code: ""{EscapeGraphQL(code)}"") {{ fights(encounterID: {encounterId}) {{ kill percentage }} }}");
            aliasIdx++;
        }

        if (fightQueryParts.Count == 0)
            return;

        var fightQuery = $"{{ reportData {{ {string.Join(" ", fightQueryParts)} }} }}";
        var fightJson = await QueryAsync(fightQuery);

        if (fightJson is null)
        {
            PassportCheckerReborn.Log.Warning(
                "[FFLogsService] Fight percentages query failed; marking no-kill players as no logs.");
            foreach (var (playerIdx, _) in playerReportCodes)
                if (!results.ContainsKey(playerIdx))
                    results[playerIdx] = new EncounterParseResult(false, true, 0, null, null, null);
            return;
        }

        // Parse fight percentages – find the lowest boss HP % per player
        var playerBestPct = new Dictionary<int, double>();
        try
        {
            using var doc = JsonDocument.Parse(fightJson);
            if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.TryGetProperty("reportData", out var reportDataEl))
            {
                foreach (var (alias, code) in aliasToCode)
                {
                    if (!reportDataEl.TryGetProperty(alias, out var reportEl) ||
                        reportEl.ValueKind == JsonValueKind.Null)
                        continue;

                    if (!reportEl.TryGetProperty("fights", out var fightsEl) ||
                        fightsEl.ValueKind != JsonValueKind.Array)
                        continue;

                    var playersForCode = codeToPlayers[code];
                    foreach (var fight in fightsEl.EnumerateArray())
                    {
                        // Skip kills – we only want wipe data for boss HP %
                        if (fight.TryGetProperty("kill", out var killEl) &&
                            killEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                            killEl.GetBoolean())
                            continue;

                        if (fight.TryGetProperty("percentage", out var pctEl) &&
                            pctEl.ValueKind == JsonValueKind.Number)
                        {
                            var pct = pctEl.GetDouble();
                            foreach (var playerIdx in playersForCode)
                            {
                                if (!playerBestPct.TryGetValue(playerIdx, out var current) ||
                                    pct < current)
                                    playerBestPct[playerIdx] = pct;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex,
                "[FFLogsService] Failed to parse fight percentages response.");
        }

        // Store final results for players with progression data
        foreach (var (playerIdx, _) in playerReportCodes)
        {
            if (playerBestPct.TryGetValue(playerIdx, out var bestPct))
            {
                results[playerIdx] = new EncounterParseResult(true, true, 0, null, bestPct, null);
            }
            else if (!results.ContainsKey(playerIdx))
            {
                results[playerIdx] = new EncounterParseResult(false, true, 0, null, null, null);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // World → FFLogs slug/region mapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps an FFXIV home-world name to the FFLogs <c>serverSlug</c> and
    /// <c>serverRegion</c> values.
    /// </summary>
    /// <returns>
    /// A tuple of (serverSlug, serverRegion) or <c>null</c> if the world is unknown.
    /// The serverSlug is the world name lowercased; serverRegion is the data-center region.
    /// </returns>
    public static (string ServerSlug, string ServerRegion)? GetFFLogsServer(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
            return null;

        // FFLogs uses the world name as the slug (lowercased)
        var slug = worldName.ToLowerInvariant();

        // Map known worlds to their data-center region
        if (NaWorlds.Contains(worldName))
            return (slug, "NA");
        if (EuWorlds.Contains(worldName))
            return (slug, "EU");
        if (JpWorlds.Contains(worldName))
            return (slug, "JP");
        if (OcWorlds.Contains(worldName))
            return (slug, "OC");

        // Fallback: attempt NA (most common for English clients)
        return (slug, "NA");
    }

    // NA worlds (Aether, Primal, Crystal, Dynamis)
    private static readonly HashSet<string> NaWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Aether
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren",
        // Primal
        "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",
        // Crystal
        "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera",
        // Dynamis
        "Halicarnassus", "Maduin", "Marilith", "Seraph", "Cuchulainn", "Golem", "Kraken", "Rafflesia",
    };

    // EU worlds (Chaos, Light, Shadow)
    private static readonly HashSet<string> EuWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chaos
        "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan",
        // Light
        "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark",
        // Shadow
        "Innocence", "Pixie", "Titania",
    };

    // JP worlds (Elemental, Gaia, Mana, Meteor)
    private static readonly HashSet<string> JpWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Elemental
        "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon",
        // Gaia
        "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima",
        // Mana
        "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan",
        // Meteor
        "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus",
    };

    // OC worlds (Materia)
    private static readonly HashSet<string> OcWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan",
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Job abbreviation ↔ FFLogs spec mapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Maps FFXIV job abbreviations to FFLogs spec names.</summary>
    private static readonly Dictionary<string, string> JobAbbrevToSpec = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = "Paladin", ["WAR"] = "Warrior", ["DRK"] = "DarkKnight", ["GNB"] = "Gunbreaker",
        ["WHM"] = "WhiteMage", ["SCH"] = "Scholar", ["AST"] = "Astrologian", ["SGE"] = "Sage",
        ["MNK"] = "Monk", ["DRG"] = "Dragoon", ["NIN"] = "Ninja", ["SAM"] = "Samurai",
        ["RPR"] = "Reaper", ["VPR"] = "Viper",
        ["BRD"] = "Bard", ["MCH"] = "Machinist", ["DNC"] = "Dancer",
        ["BLM"] = "BlackMage", ["SMN"] = "Summoner", ["RDM"] = "RedMage", ["PCT"] = "Pictomancer",
    };

    /// <summary>Maps FFLogs spec names to FFXIV job abbreviations.</summary>
    private static readonly Dictionary<string, string> SpecToJobAbbrev = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Paladin"] = "PLD", ["Warrior"] = "WAR", ["DarkKnight"] = "DRK", ["Gunbreaker"] = "GNB",
        ["WhiteMage"] = "WHM", ["Scholar"] = "SCH", ["Astrologian"] = "AST", ["Sage"] = "SGE",
        ["Monk"] = "MNK", ["Dragoon"] = "DRG", ["Ninja"] = "NIN", ["Samurai"] = "SAM",
        ["Reaper"] = "RPR", ["Viper"] = "VPR",
        ["Bard"] = "BRD", ["Machinist"] = "MCH", ["Dancer"] = "DNC",
        ["BlackMage"] = "BLM", ["Summoner"] = "SMN", ["RedMage"] = "RDM", ["Pictomancer"] = "PCT",
    };

    /// <summary>Maps FFLogs spec names to FFXIV game icon IDs (62100 + ClassJob RowId).</summary>
    private static readonly Dictionary<string, uint> SpecToJobIconId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Paladin"] = 62119, ["Warrior"] = 62121, ["DarkKnight"] = 62132, ["Gunbreaker"] = 62137,
        ["WhiteMage"] = 62124, ["Scholar"] = 62128, ["Astrologian"] = 62133, ["Sage"] = 62140,
        ["Monk"] = 62120, ["Dragoon"] = 62122, ["Ninja"] = 62130, ["Samurai"] = 62134,
        ["Reaper"] = 62139, ["Viper"] = 62141,
        ["Bard"] = 62123, ["Machinist"] = 62131, ["Dancer"] = 62138,
        ["BlackMage"] = 62125, ["Summoner"] = 62127, ["RedMage"] = 62135, ["Pictomancer"] = 62142,
    };

    /// <summary>
    /// Resolves a player's current FFXIV job abbreviation to the FFLogs spec name.
    /// Returns <c>null</c> if the abbreviation is unknown.
    /// </summary>
    public static string? GetSpecForJob(string? jobAbbreviation)
    {
        if (string.IsNullOrWhiteSpace(jobAbbreviation))
            return null;
        return JobAbbrevToSpec.GetValueOrDefault(jobAbbreviation!);
    }

    /// <summary>
    /// Resolves an FFLogs spec name to the FFXIV job abbreviation.
    /// Returns <c>null</c> if the spec is unknown.
    /// </summary>
    public static string? GetJobAbbrevForSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;
        return SpecToJobAbbrev.GetValueOrDefault(spec!);
    }

    /// <summary>
    /// Returns the game icon ID for a given FFLogs spec name, or <c>null</c> if unknown.
    /// </summary>
    public static uint? GetJobIconIdForSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;
        return SpecToJobIconId.GetValueOrDefault(spec!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string EscapeGraphQL(string input) =>
        input.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private record CachedParse(double? Percentile, DateTime Expiry);

    public void Dispose()
    {
        httpClient.Dispose();
    }
}

/// <summary>
/// Data representing a player's parse data for a specific encounter (or a general parse fallback).
/// </summary>
public record EncounterParseResult(
    bool HasData,
    bool IsEncounterSpecific,
    int TotalKills,
    double? BestParse,
    double? LowestBossHpPct,
    double? AverageParsePercent)
{
    public double? Phase1BestParse { get; init; }
    public double? Phase2BestParse { get; init; }
    public double? Phase2LowestBossHpPct { get; init; }
    public int? Phase1TotalKills { get; init; }
    public int? Phase2TotalKills { get; init; }

    /// <summary>Best parse on the player's current job for this encounter.</summary>
    public double? CurrentJobBestParse { get; init; }

    /// <summary>FFLogs spec name for the overall best parse (any job).</summary>
    public string? BestParseSpec { get; init; }

    /// <summary>FFXIV job abbreviation for the overall best parse job.</summary>
    public string? BestParseJobAbbreviation { get; init; }

    /// <summary>Icon ID for the overall best parse job.</summary>
    public uint? BestParseJobIconId { get; init; }
}
