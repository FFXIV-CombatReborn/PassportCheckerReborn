using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Services;

/// <summary>
/// Provides integration with <see href="https://tomestone.gg"/>.
///
/// <para>
/// Browser-based lookups open the player's profile in the system default browser.
/// API-based lookups use the documented Tomestone public API to fetch prog data
/// and activity data for a given duty.
/// </para>
/// </summary>
public sealed partial class TomestoneService : IDisposable
{
    private readonly PassportCheckerReborn plugin;
    private readonly HttpClient httpClient;

    private const string BaseUrl = "https://tomestone.gg";
    private const string ApiBaseUrl = "https://tomestone.gg/api";

    [GeneratedRegex(@"[^a-z0-9\s-]", RegexOptions.Compiled)]
    private static partial Regex SlugStripRegex();

    [GeneratedRegex(@"/lodestone/character/(\d+)/", RegexOptions.Compiled)]
    private static partial Regex LodestoneIdRegex();

    /// <summary>
    /// Maps PF duty names to Tomestone API query parameters
    /// (expansion, zone, encounter) for the progression-graph and activity endpoints.
    /// Values are sourced from the TomestoneViewer plugin's Location definitions.
    /// </summary>
    private static readonly Dictionary<string, TomestoneEncounterParams> DutyNameToTomestoneParams =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Dawntrail Ultimates ──────────────────────────────────────────────
        ["Futures Rewritten (Ultimate)"] =
            new("dawntrail", "ultimates", "futures-rewritten-ultimate"),

        // ── Endwalker Ultimates ──────────────────────────────────────────────
        ["The Omega Protocol (Ultimate)"] =
            new("endwalker", "ultimates", "the-omega-protocol-ultimate"),
        ["Dragonsong's Reprise (Ultimate)"] =
            new("endwalker", "ultimates", "dragonsongs-reprise-ultimate"),

        // ── Shadowbringers Ultimates ─────────────────────────────────────────
        ["The Epic of Alexander (Ultimate)"] =
            new("shadowbringers", "ultimates", "the-epic-of-alexander-ultimate"),

        // ── Stormblood Ultimates ─────────────────────────────────────────────
        ["The Weapon's Refrain (Ultimate)"] =
            new("stormblood", "ultimates", "the-weapons-refrain-ultimate"),
        ["The Unending Coil of Bahamut (Ultimate)"] =
            new("stormblood", "ultimates", "the-unending-coil-of-bahamut-ultimate"),

        // ── Dawntrail Savage – AAC Heavyweight ───────────────────────────────
        ["AAC Heavyweight M1 (Savage)"] =
            new("dawntrail", "aac-heavyweight-savage", "vamp-fatale"),
        ["AAC Heavyweight M2 (Savage)"] =
            new("dawntrail", "aac-heavyweight-savage", "red-hot-deep-blue"),
        ["AAC Heavyweight M3 (Savage)"] =
            new("dawntrail", "aac-heavyweight-savage", "the-tyrant"),
        ["AAC Heavyweight M4 (Savage) P1"] =
            new("dawntrail", "aac-heavyweight-savage", "lindwurm"),
        ["AAC Heavyweight M4 (Savage) P2"] =
            new("dawntrail", "aac-heavyweight-savage", "lindwurm-ii"),

        // ── Dawntrail Savage – AAC Cruiserweight ─────────────────────────────
        ["AAC Cruiserweight M1 (Savage)"] =
            new("dawntrail", "aac-cruiserweight-savage", "dancing-green"),
        ["AAC Cruiserweight M2 (Savage)"] =
            new("dawntrail", "aac-cruiserweight-savage", "honey-b-lovely"),
        ["AAC Cruiserweight M3 (Savage)"] =
            new("dawntrail", "aac-cruiserweight-savage", "brute-bomber"),
        ["AAC Cruiserweight M4 (Savage)"] =
            new("dawntrail", "aac-cruiserweight-savage", "wicked-thunder"),

        // ── Dawntrail Savage – AAC Light-heavyweight ─────────────────────────
        ["AAC Light-heavyweight M1 (Savage)"] =
            new("dawntrail", "aac-light-heavyweight-savage", "black-cat"),
        ["AAC Light-heavyweight M2 (Savage)"] =
            new("dawntrail", "aac-light-heavyweight-savage", "honey-b-lovely"),
        ["AAC Light-heavyweight M3 (Savage)"] =
            new("dawntrail", "aac-light-heavyweight-savage", "brute-bomber"),
        ["AAC Light-heavyweight M4 (Savage)"] =
            new("dawntrail", "aac-light-heavyweight-savage", "wicked-thunder"),
    };

    /// <summary>
    /// Maps base duty names for multi-part encounters to their preferred
    /// Tomestone encounter parameters.  These are used for auto-detection
    /// (e.g. when PF reports "AAC Heavyweight M4 (Savage)") but do NOT
    /// appear in the duty dropdown – only the per-phase entries above are shown.
    /// For multi-phase fights the preferred phase is P2, which represents the
    /// full clear.
    /// </summary>
    private static readonly Dictionary<string, TomestoneEncounterParams> MultiPartDutyToTomestoneParams =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAC Heavyweight M4 (Savage)"] =
            new("dawntrail", "aac-heavyweight-savage", "lindwurm-ii"),
    };

    /// <summary>
    /// Returns all duty names that have Tomestone encounter mappings.
    /// Multi-part base names (e.g. "AAC Heavyweight M4 (Savage)") are excluded
    /// because the per-phase entries (P1 / P2) are already present.
    /// </summary>
    public static IReadOnlyCollection<string> GetAllSupportedDutyNames()
        => DutyNameToTomestoneParams.Keys;

    public TomestoneService(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PassportCheckerReborn/{PassportCheckerReborn.Version}");
        httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Browser-based lookup (always available)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the player's Tomestone.gg profile in the system default browser.
    /// </summary>
    public static void OpenTomestonePage(string playerName, string world, string? characterId = null)
    {
        var slug = BuildTomestoneSlug(playerName);
        var url = !string.IsNullOrWhiteSpace(characterId)
            ? $"{BaseUrl}/character/{characterId}/{slug}"
            : $"{BaseUrl}/character/{Uri.EscapeDataString(world)}/{slug}";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            PassportCheckerReborn.Log.Information($"[TomestoneService] Opened {url}");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, $"[TomestoneService] Failed to open browser for {url}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API – Encounter-specific lookup
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches Tomestone data for a player for a specific duty.
    /// Uses the profile-by-ID endpoint to get parse and prog information
    /// for all encounters in a single call.  Falls back to the legacy
    /// progression-graph and activity endpoints when the profile-by-ID
    /// response is unavailable.
    /// </summary>
    public async Task<TomestoneCharacterInfo?> GetCharacterInfoAsync(
        string playerName, string world, string? dutyName = null)
    {
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(world))
            return null;

        var info = new TomestoneCharacterInfo
        {
            Name = playerName,
            World = world,
        };

        // Resolve Tomestone encounter parameters for the duty
        TomestoneEncounterParams? encounterParams = null;
        if (!string.IsNullOrWhiteSpace(dutyName))
        {
            if (!DutyNameToTomestoneParams.TryGetValue(dutyName, out encounterParams))
                MultiPartDutyToTomestoneParams.TryGetValue(dutyName, out encounterParams);
        }

        var server = Uri.EscapeDataString(world);
        var name = Uri.EscapeDataString(playerName);

        // ── Profile (Lodestone ID for browser links) ─────────────────────────
        await FetchProfileAsync(info, server, name);

        // ── Full profile by Lodestone ID (parse + prog for all encounters) ───
        if (!string.IsNullOrWhiteSpace(info.CharacterId))
        {
            await FetchFullProfileByIdAsync(info, info.CharacterId, dutyName, encounterParams);
        }

        // ── Fallback to old endpoints if profile-by-ID didn't yield data ─────
        if (encounterParams != null && info.ProgPoint == null && !info.TotalClears.HasValue)
        {
            await FetchProgressionGraphAsync(info, server, name, encounterParams);
            await FetchActivityAsync(info, server, name, encounterParams);
        }
        else if (!string.IsNullOrWhiteSpace(dutyName) && encounterParams == null)
        {
        }

        return info;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lodestone ID resolution (fallback)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches the FFXIV Lodestone for a character by name and world and
    /// returns the Lodestone character ID, or <c>null</c> if not found.
    /// </summary>
    public async Task<string?> ResolveLodestoneIdAsync(string playerName, string world)
    {
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(world))
            return null;

        try
        {
            var encodedName = Uri.EscapeDataString(playerName);
            var encodedWorld = Uri.EscapeDataString(world);
            var url = $"https://na.finalfantasyxiv.com/lodestone/character/?q={encodedName}&worldname={encodedWorld}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd($"PassportCheckerReborn/{PassportCheckerReborn.Version}");

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync();

            var match = LodestoneIdRegex().Match(html);
            if (match.Success)
            {
                var lodestoneId = match.Groups[1].Value;
                PassportCheckerReborn.Log.Information(
                    $"[TomestoneService] Resolved Lodestone ID {lodestoneId} for {playerName}@{world}");
                return lodestoneId;
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the Tomestone encounter parameters for a given duty name,
    /// or <c>null</c> if the duty is not mapped.
    /// </summary>
    public static TomestoneEncounterParams? GetEncounterParamsForDuty(string? dutyName)
    {
        if (string.IsNullOrWhiteSpace(dutyName))
            return null;
        if (DutyNameToTomestoneParams.TryGetValue(dutyName, out var p))
            return p;
        MultiPartDutyToTomestoneParams.TryGetValue(dutyName, out p);
        return p;
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private – API calls
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the progression graph for a character/encounter from
    /// <c>GET /api/character/progression-graph/{server}/{name}?expansion=…&amp;zone=…&amp;encounter=…</c>
    /// and populates <see cref="TomestoneCharacterInfo.ProgPoint"/>.
    /// </summary>
    private async Task FetchProgressionGraphAsync(
        TomestoneCharacterInfo info, string server, string name,
        TomestoneEncounterParams ep)
    {
        try
        {
            var url = $"{ApiBaseUrl}/character/progression-graph/{server}/{name}"
                + $"?expansion={Uri.EscapeDataString(ep.Expansion)}"
                + $"&zone={Uri.EscapeDataString(ep.Zone)}"
                + $"&encounter={Uri.EscapeDataString(ep.Encounter)}";

            using var request = CreateAuthenticatedRequest(url);
            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try to extract the furthest prog point from the graph data.
            // The response contains a "data.graph" array of {duration, mechanic:{name,number}}.
            info.ProgPoint = ParseProgPointFromGraph(root);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Fetches activity data for a character/encounter from
    /// <c>GET /api/character/activity/{server}/{name}?expansion=…&amp;zone=…&amp;encounter=…</c>
    /// and populates clears / best parse info.
    /// </summary>
    private async Task FetchActivityAsync(
        TomestoneCharacterInfo info, string server, string name,
        TomestoneEncounterParams ep)
    {
        try
        {
            var url = $"{ApiBaseUrl}/character/activity/{server}/{name}"
                + $"?expansion={Uri.EscapeDataString(ep.Expansion)}"
                + $"&zone={Uri.EscapeDataString(ep.Zone)}"
                + $"&encounter={Uri.EscapeDataString(ep.Encounter)}";

            using var request = CreateAuthenticatedRequest(url);
            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            ParseActivityResponse(info, root);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Fetches the character profile from
    /// <c>GET /api/character/profile/{server}/{name}</c>
    /// and populates the Lodestone character ID.
    /// </summary>
    private async Task FetchProfileAsync(
        TomestoneCharacterInfo info, string server, string name)
    {
        try
        {
            var url = $"{ApiBaseUrl}/character/profile/{server}/{name}";

            using var request = CreateAuthenticatedRequest(url);
            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Extract Lodestone ID from various possible field names
            if (root.TryGetProperty("lodestoneId", out var idEl))
                info.CharacterId = ReadIdValue(idEl);
            else if (root.TryGetProperty("lodestone_id", out var idEl2))
                info.CharacterId = ReadIdValue(idEl2);
            else if (root.TryGetProperty("id", out var idEl3))
                info.CharacterId = ReadIdValue(idEl3);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Fetches the full character profile from
    /// <c>GET /api/character/profile/{lodestoneId}?update=true</c>
    /// and parses encounter-specific data (clears and progression) for the
    /// requested duty.
    /// </summary>
    private async Task FetchFullProfileByIdAsync(
        TomestoneCharacterInfo info, string lodestoneId,
        string? dutyName, TomestoneEncounterParams? encounterParams)
    {
        if (string.IsNullOrWhiteSpace(dutyName) && encounterParams == null)
            return;

        try
        {
            var url = $"{ApiBaseUrl}/character/profile/{lodestoneId}?update=false";

            using var request = CreateAuthenticatedRequest(url);
            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            ParseProfileEncounters(info, root, dutyName, encounterParams);
        }
        catch (Exception)
        {
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private – Response parsing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the progression-graph response to find the furthest mechanic reached.
    /// The response is expected to contain a graph array with duration and mechanic info.
    /// </summary>
    private static string? ParseProgPointFromGraph(JsonElement root)
    {
        // Try nested "data.graph" path (TomestoneViewer pattern)
        if (root.TryGetProperty("data", out var dataEl) &&
            dataEl.TryGetProperty("graph", out var graph))
        {
            // found
        }
        // Try top-level "graph" array
        else if (root.TryGetProperty("graph", out graph))
        {
            // found
        }
        else
        {
            // Not a graph structure – try direct percentage fields
            return ParseDirectProgPoint(root);
        }

        if (graph.ValueKind != JsonValueKind.Array)
            return ParseDirectProgPoint(root);

        // Find the entry with the highest duration → the furthest mechanic reached
        var bestDuration = 0;
        string? lastMechanic = null;

        foreach (var point in graph.EnumerateArray())
        {
            if (!point.TryGetProperty("duration", out var durEl))
                continue;
            var dur = durEl.GetInt32();
            if (dur <= bestDuration)
                continue;
            bestDuration = dur;

            if (point.TryGetProperty("mechanic", out var mechEl))
            {
                var mechName = mechEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var mechNum = mechEl.TryGetProperty("number", out var numEl) && numEl.TryGetInt32(out var num) ? num : 0;
                if (!string.IsNullOrWhiteSpace(mechName))
                {
                    lastMechanic = mechNum > 1 ? $"{mechName} #{mechNum}" : mechName;
                }
            }
        }

        return lastMechanic;
    }

    /// <summary>
    /// Tries to extract a prog point from direct percentage / phase fields.
    /// </summary>
    private static string? ParseDirectProgPoint(JsonElement root)
    {
        // Try "percent" or "bestPercent" as a direct value
        if (root.TryGetProperty("percent", out var pctEl))
            return pctEl.ToString();
        if (root.TryGetProperty("bestPercent", out var bestPctEl))
            return bestPctEl.ToString();
        if (root.TryGetProperty("progPoint", out var progEl))
            return progEl.GetString();
        if (root.TryGetProperty("prog_point", out var prog2El))
            return prog2El.GetString();
        return null;
    }

    /// <summary>
    /// Parses the activity response to extract clears and best parse data.
    /// </summary>
    private static void ParseActivityResponse(TomestoneCharacterInfo info, JsonElement root)
    {
        // Try various JSON structures the activity endpoint may return

        // ── Paginated results pattern: { results: [...], hasNextPage: bool } ──
        if (root.TryGetProperty("results", out var resultsEl) &&
            resultsEl.ValueKind == JsonValueKind.Array)
        {
            ParseActivityArray(info, resultsEl);
            return;
        }

        // ── Nested data pattern: data.paginator.data or activities.paginator.data ──
        if (root.TryGetProperty("data", out var dataEl))
        {
            if (dataEl.TryGetProperty("paginator", out var pagEl) &&
                pagEl.TryGetProperty("data", out var pagDataEl) &&
                pagDataEl.ValueKind == JsonValueKind.Array)
            {
                ParseActivityArray(info, pagDataEl);
                return;
            }

            if (dataEl.ValueKind == JsonValueKind.Array)
            {
                ParseActivityArray(info, dataEl);
                return;
            }
        }

        // ── Flat array at root ──
        if (root.ValueKind == JsonValueKind.Array)
        {
            ParseActivityArray(info, root);
            return;
        }

        // ── Single-object response (might contain summary fields) ──
        ParseActivitySummary(info, root);
    }

    /// <summary>
    /// Walks an array of activity entries looking for clears and best percent.
    /// </summary>
    private static void ParseActivityArray(TomestoneCharacterInfo info, JsonElement array)
    {
        var totalKills = 0;
        double? bestPercent = null;

        foreach (var entry in array.EnumerateArray())
        {
            // The entry might have the fields directly, or under an "activity" sub-object
            var activity = entry.TryGetProperty("activity", out var actEl) ? actEl : entry;

            // Check for kills
            if (activity.TryGetProperty("killsCount", out var killsEl) &&
                killsEl.TryGetInt32(out var kills) && kills > 0)
            {
                totalKills += kills;
            }

            // Check for best percent
            if (activity.TryGetProperty("bestPercent", out var bpEl))
            {
                if (bpEl.TryGetDouble(out var pct))
                {
                    if (!bestPercent.HasValue || pct > bestPercent.Value)
                        bestPercent = pct;
                }
                else if (bpEl.ValueKind == JsonValueKind.String)
                {
                    var raw = bpEl.GetString();
                    if (raw != null && double.TryParse(raw.TrimEnd('%'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var pctFromStr))
                    {
                        if (!bestPercent.HasValue || pctFromStr > bestPercent.Value)
                            bestPercent = pctFromStr;
                    }
                }
            }
        }

        if (totalKills > 0)
            info.TotalClears = totalKills;

        if (bestPercent.HasValue)
            info.BestPercent = bestPercent.Value;
    }

    /// <summary>
    /// Parses a single summary object for aggregate activity data.
    /// </summary>
    private static void ParseActivitySummary(TomestoneCharacterInfo info, JsonElement root)
    {
        if (root.TryGetProperty("clears", out var clearsEl) &&
            clearsEl.TryGetInt32(out var clears))
            info.TotalClears = clears;
        else if (root.TryGetProperty("killsCount", out var kEl) &&
                 kEl.TryGetInt32(out var k))
            info.TotalClears = k;

        if (root.TryGetProperty("bestPercent", out var bpEl) &&
            bpEl.TryGetDouble(out var bp))
            info.BestPercent = bp;
    }

    /// <summary>
    /// Parses encounter data from the full profile response
    /// (<c>/api/character/profile/{id}?update=true</c>).
    /// Searches through all encounter categories (savage, ultimate, extremes, etc.)
    /// to find the requested duty and extract clear/progression data.
    /// </summary>
    private static void ParseProfileEncounters(
        TomestoneCharacterInfo info, JsonElement root,
        string? dutyName, TomestoneEncounterParams? encounterParams)
    {
        if (!root.TryGetProperty("encounters", out var encountersEl) ||
            encountersEl.ValueKind != JsonValueKind.Object)
            return;

        // Encounter categories that may contain grouped or flat encounters
        foreach (var category in new[] { "savage", "ultimate", "extremes", "criterion", "chaotic", "quantum" })
        {
            if (!encountersEl.TryGetProperty(category, out var catEl) ||
                catEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var group in catEl.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                    continue;

                // Savage / ultimate groups have a nested "encounters" array
                if (group.TryGetProperty("encounters", out var subEncounters) &&
                    subEncounters.ValueKind == JsonValueKind.Array)
                {
                    foreach (var enc in subEncounters.EnumerateArray())
                    {
                        if (MatchesEncounter(enc, dutyName, encounterParams))
                        {
                            ExtractEncounterData(info, enc);
                            return;
                        }
                    }
                }
                else
                {
                    // Flat structure (extremes, criterion, etc.)
                    if (MatchesEncounter(group, dutyName, encounterParams))
                    {
                        ExtractEncounterData(info, group);
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Determines whether the given JSON encounter element matches the
    /// requested duty by <paramref name="encounterParams"/> (preferred) or
    /// <paramref name="dutyName"/> (fallback).
    /// When <paramref name="encounterParams"/> is provided the slug-based match
    /// is used exclusively so that multi-phase encounters sharing the same
    /// <c>zoneName</c> (e.g. Lindwurm / Lindwurm II) are disambiguated.
    /// </summary>
    private static bool MatchesEncounter(
        JsonElement enc, string? dutyName, TomestoneEncounterParams? encounterParams)
    {
        // Prefer precise slug matching when encounter params are known
        if (encounterParams != null)
        {
            var matchesGroup = enc.TryGetProperty("encounterGroupCanonicalName", out var groupEl) &&
                string.Equals(groupEl.GetString(), encounterParams.Zone, StringComparison.OrdinalIgnoreCase);
            var matchesExpansion = enc.TryGetProperty("expansionCanonicalName", out var expEl) &&
                string.Equals(expEl.GetString(), encounterParams.Expansion, StringComparison.OrdinalIgnoreCase);

            if (matchesGroup && matchesExpansion &&
                enc.TryGetProperty("name", out var nameEl))
            {
                var slug = BuildTomestoneSlug(nameEl.GetString() ?? string.Empty);
                if (string.Equals(slug, encounterParams.Encounter, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // Fallback: match by zoneName when no encounter params are available
        if (!string.IsNullOrWhiteSpace(dutyName) &&
            enc.TryGetProperty("zoneName", out var zoneNameEl))
        {
            var zoneName = zoneNameEl.GetString();
            if (zoneName != null && zoneName.Equals(dutyName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts clear / progression data from a matched encounter element
    /// within the profile response.
    /// </summary>
    private static void ExtractEncounterData(TomestoneCharacterInfo info, JsonElement enc)
    {
        // ── Activity present → encounter has been cleared ────────────────────
        // The profile endpoint's activity object only provides timestamps, not
        // a kill count, so we record 1 to indicate the encounter is cleared.
        if (enc.TryGetProperty("activity", out var actEl) &&
            actEl.ValueKind == JsonValueKind.Object)
        {
            info.TotalClears = 1;

            if (actEl.TryGetProperty("completionWeek", out var cwEl))
            {
                var cw = cwEl.GetString();
                if (!string.IsNullOrWhiteSpace(cw))
                    info.CompletionWeek = cw;
            }
        }

        // ── Progression present → still progging ─────────────────────────────
        if (enc.TryGetProperty("progression", out var progEl) &&
            progEl.ValueKind == JsonValueKind.Object)
        {
            // Mechanic name (e.g. "Splattershed")
            if (progEl.TryGetProperty("mechanic", out var mechEl) &&
                mechEl.ValueKind == JsonValueKind.Object &&
                mechEl.TryGetProperty("name", out var mechNameEl))
            {
                var mechName = mechNameEl.GetString();

                // Only suffix the mechanic number when > 1 (i.e. "Splattershed #2"),
                // since a single occurrence doesn't need disambiguation.
                var mechNum = 0;
                if (mechEl.TryGetProperty("number", out var numEl))
                    numEl.TryGetInt32(out mechNum);

                if (!string.IsNullOrWhiteSpace(mechName))
                    info.ProgPoint = mechNum > 1 ? $"{mechName} #{mechNum}" : mechName;
            }

            // Use "displayPercent" from the API response as the shown percentage.
            // The API returns a pre-formatted string (e.g. "35.82%") or empty.
            if (progEl.TryGetProperty("displayPercent", out var displayPctEl))
            {
                var dp = displayPctEl.GetString();
                if (!string.IsNullOrWhiteSpace(dp))
                    info.DisplayPercent = dp;
            }

            // Fallback: use the percentage string (e.g. "57%")
            if (string.IsNullOrWhiteSpace(info.ProgPoint) &&
                progEl.TryGetProperty("percent", out var pctEl))
            {
                info.ProgPoint = pctEl.GetString();
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private – Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="HttpRequestMessage"/> with Bearer token auth
    /// if a Tomestone API key is configured.
    /// </summary>
    private HttpRequestMessage CreateAuthenticatedRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var apiKey = plugin.Configuration.TomestoneApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    private static string BuildTomestoneSlug(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        // Tomestone slugs are lowercase, with spaces replaced by hyphens and
        // all non-alphanumeric / non-hyphen characters stripped.
        // e.g. "Jo'hn Fantasy" → "jo-hn-fantasy"
        var lower = playerName.Trim().ToLowerInvariant();
        var slug = SlugStripRegex().Replace(lower, string.Empty);
        var parts = slug.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("-", parts);
    }

    private static string? ReadIdValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var idValue))
            return idValue.ToString();

        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        return null;
    }
}

/// <summary>
/// Parameters needed to query Tomestone.gg API for a specific encounter.
/// </summary>
public record TomestoneEncounterParams(string Expansion, string Zone, string Encounter);

/// <summary>Data returned from a Tomestone.gg character lookup.</summary>
public class TomestoneCharacterInfo
{
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public string? CharacterId { get; set; }
    public string? ProgPoint { get; set; }
    public string? DisplayPercent { get; set; }
    public int? TotalClears { get; set; }
    public string? CompletionWeek { get; set; }
    public double? BestPercent { get; set; }
}
