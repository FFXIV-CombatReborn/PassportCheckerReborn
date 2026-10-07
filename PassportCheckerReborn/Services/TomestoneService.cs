using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Services;

// Prog points and clears from the Tomestone.gg API.
public sealed partial class TomestoneService : IDisposable
{
    private const string ApiBaseUrl = "https://tomestone.gg/api";
    private const int MaxConcurrentRequests = 4;

    private static readonly string[] EncounterCategories = ["savage", "ultimate", "extremes", "criterion", "chaotic", "quantum"];

    // English duty name to Tomestone's expansion, zone and encounter slugs, as TomestoneViewer has them.
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

    // Duties the Party Finder lists once but Tomestone splits in two. The last part stands for the
    // full clear, so it is asked for first; the other is the fallback when it has no data.
    private static readonly Dictionary<string, (TomestoneEncounterParams Preferred, TomestoneEncounterParams Fallback)> MultiPartEncounters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AAC Heavyweight M4 (Savage)"] = (
                new("dawntrail", "aac-heavyweight-savage", "lindwurm-ii"),
                new("dawntrail", "aac-heavyweight-savage", "lindwurm")),
        };

    // The activity endpoint has answered in several shapes; each is tried in turn.
    private static readonly string[][] ActivityArrayPaths = [["results"], ["data"], ["data", "paginator", "data"]];

    private readonly PassportCheckerReborn plugin;
    private readonly ApiHttpClient http = new(MaxConcurrentRequests);
    private readonly ExpiringCache<(string World, string Name, string Duty), TomestoneCharacterInfo> results = new(TimeSpan.FromMinutes(5));
    private readonly ConcurrentDictionary<(string World, string Name, string Duty), Task<TomestoneCharacterInfo>> inFlight = new();

    public TomestoneService(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // The phase entries of a multi-part duty are listed; its combined name is not.
    public static IEnumerable<string> SupportedDutyNames => Encounters.Keys;

    [GeneratedRegex(@"[^a-z0-9\s-]")]
    private static partial Regex SlugStripRegex();

    public void Dispose()
    {
        http.Dispose();
    }

    public async Task<TomestoneCharacterInfo?> GetCharacterInfoAsync(string playerName, string world, string? dutyName = null)
    {
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(world))
        {
            return null;
        }

        // Every endpoint answers per encounter, so without a duty there is nothing to ask for.
        if (string.IsNullOrWhiteSpace(dutyName))
        {
            return new TomestoneCharacterInfo();
        }

        var key = (world, playerName, dutyName);
        if (results.TryGet(key, out var cached))
        {
            return cached;
        }

        // Overlapping lookups of one player share the requests.
        var lookup = inFlight.GetOrAdd(key, _ => LookUpAsync(playerName, world, dutyName));
        try
        {
            return await lookup;
        }
        finally
        {
            inFlight.TryRemove(KeyValuePair.Create(key, lookup));
        }
    }

    private async Task<TomestoneCharacterInfo> LookUpAsync(string playerName, string world, string dutyName)
    {
        TomestoneEncounterParams? fallback = null;
        if (!Encounters.TryGetValue(dutyName, out var encounter) && MultiPartEncounters.TryGetValue(dutyName, out var parts))
        {
            (encounter, fallback) = parts;
        }

        var info = new TomestoneCharacterInfo();
        var server = Uri.EscapeDataString(world);
        var name = Uri.EscapeDataString(playerName);

        // The profile lists every encounter. When it has nothing for the duty, the per-encounter endpoints
        // are asked, unless the profile is unknown, empty, or the player hides their activity.
        var outcome = await FetchAsync(info, $"{ApiBaseUrl}/character/profile/{server}/{name}", root =>
        {
            ParseProfileEncounters(info, root, dutyName, encounter);
            if (fallback != null && !HasEncounterData(info))
            {
                ParseProfileEncounters(info, root, dutyName, fallback);
            }
        });

        info.NotFound = outcome == FetchOutcome.NotFound;
        var complete = outcome != FetchOutcome.Failed;
        if (outcome == FetchOutcome.Answered && !info.NoLogs)
        {
            foreach (var part in new[] { encounter, fallback })
            {
                if (part != null && !info.ActivityHidden && !HasEncounterData(info))
                {
                    complete &= await FetchEncounterEndpointsAsync(info, server, name, part);
                }
            }
        }

        // An empty answer from one endpoint does not outweigh data from another.
        if (HasEncounterData(info) || info.BestPercent.HasValue)
        {
            info.NoLogs = false;
        }

        info.LookupFailed = !complete;
        if (complete)
        {
            results.Set((world, playerName, dutyName), info);
        }

        return info;
    }

    private static bool HasEncounterData(TomestoneCharacterInfo info)
    {
        return info.ProgPoint != null || info.TotalClears.HasValue;
    }

    // The older per-encounter endpoints, for when the profile came back without the duty.
    private async Task<bool> FetchEncounterEndpointsAsync(TomestoneCharacterInfo info, string server, string name, TomestoneEncounterParams encounter)
    {
        var query = $"{server}/{name}"
            + $"?expansion={Uri.EscapeDataString(encounter.Expansion)}"
            + $"&zone={Uri.EscapeDataString(encounter.Zone)}"
            + $"&encounter={Uri.EscapeDataString(encounter.Encounter)}";

        var outcomes = await Task.WhenAll(
            FetchAsync(info, $"{ApiBaseUrl}/character/progression-graph/{query}", root => info.ProgPoint = ParseProgPointFromGraph(root)),
            FetchAsync(info, $"{ApiBaseUrl}/character/activity/{query}", root => ParseActivityResponse(info, root)));
        return Array.TrueForAll(outcomes, outcome => outcome != FetchOutcome.Failed);
    }

    // A failed request is not cached; a 404 is an answer, not a failure.
    private async Task<FetchOutcome> FetchAsync(TomestoneCharacterInfo info, string url, Action<JsonElement> parse)
    {
        try
        {
            using var response = await http.SendAsync(() => CreateRequest(url));
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return FetchOutcome.NotFound;
            }

            // A public profile can still hide its activity: {"error":"Character activity is hidden."}
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                if (error.RootElement.GetStringOrNull("error")?.Contains("hidden", StringComparison.OrdinalIgnoreCase) == true)
                {
                    info.ActivityHidden = true;
                    return FetchOutcome.Answered;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                PassportCheckerReborn.Log.Debug($"[TomestoneService] {(int)response.StatusCode} from {url}");
                return FetchOutcome.Failed;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() == 0)
            {
                info.NoLogs = true;
            }
            else
            {
                parse(doc.RootElement);
            }

            return FetchOutcome.Answered;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Debug(ex, $"[TomestoneService] Request failed: {url}");
            return FetchOutcome.Failed;
        }
    }

    private enum FetchOutcome
    {
        Failed,
        NotFound,
        Answered,
    }

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var apiKey = plugin.Configuration.TomestoneApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    #region Response parsing

    // The furthest mechanic reached, from a graph of { duration, mechanic: { name, number } } points.
    private static string? ParseProgPointFromGraph(JsonElement root)
    {
        if (!(root.TryGetPath(out var graph, "data", "graph") || root.TryGetPath(out graph, "graph"))
            || graph.ValueKind != JsonValueKind.Array)
        {
            return ParseDirectProgPoint(root);
        }

        var longest = 0.0;
        string? mechanic = null;
        foreach (var point in graph.EnumerateArray())
        {
            if (point.GetNumberOrNull("duration") is not { } duration || duration <= longest)
            {
                continue;
            }

            longest = duration;
            if (point.TryGetPath(out var mechanicEl, "mechanic"))
            {
                mechanic = FormatMechanic(mechanicEl) ?? mechanic;
            }
        }

        return mechanic;
    }

    private static string? ParseDirectProgPoint(JsonElement root)
    {
        if (root.TryGetPath(out var percent, "percent") || root.TryGetPath(out percent, "bestPercent"))
        {
            return percent.ToString();
        }

        return root.GetStringOrNull("progPoint") ?? root.GetStringOrNull("prog_point");
    }

    // "Splattershed", or "Splattershed #2" for a mechanic that comes round more than once.
    private static string? FormatMechanic(JsonElement mechanic)
    {
        var name = mechanic.GetStringOrNull("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var number = (int)(mechanic.GetNumberOrNull("number") ?? 0);
        return number > 1 ? $"{name} #{number}" : name;
    }

    private static void ParseActivityResponse(TomestoneCharacterInfo info, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            ParseActivityArray(info, root);
            return;
        }

        foreach (var path in ActivityArrayPaths)
        {
            if (root.TryGetPath(out var array, path) && array.ValueKind == JsonValueKind.Array)
            {
                ParseActivityArray(info, array);
                return;
            }
        }

        if ((root.GetNumberOrNull("clears") ?? root.GetNumberOrNull("killsCount")) is { } clears)
        {
            info.TotalClears = (int)clears;
        }

        if (root.GetNumberOrNull("bestPercent") is { } bestPercent)
        {
            info.BestPercent = bestPercent;
        }
    }

    private static void ParseActivityArray(TomestoneCharacterInfo info, JsonElement array)
    {
        var totalKills = 0;
        double? bestPercent = null;

        foreach (var entry in array.EnumerateArray())
        {
            var activity = entry.TryGetPath(out var nested, "activity") ? nested : entry;

            if (activity.GetNumberOrNull("killsCount") is { } kills and > 0)
            {
                totalKills += (int)kills;
            }

            if (activity.TryGetPath(out var bestEl, "bestPercent")
                && ReadPercent(bestEl) is { } percent
                && (bestPercent is null || percent > bestPercent))
            {
                bestPercent = percent;
            }
        }

        if (totalKills > 0)
        {
            info.TotalClears = totalKills;
        }

        if (bestPercent.HasValue)
        {
            info.BestPercent = bestPercent;
        }
    }

    // A number, or a string such as "57%".
    private static double? ReadPercent(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String when double.TryParse(element.GetString()?.TrimEnd('%'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    // Savage and ultimate categories group their encounters; the others list them flat.
    private static void ParseProfileEncounters(
        TomestoneCharacterInfo info, JsonElement root, string dutyName, TomestoneEncounterParams? encounter)
    {
        if (!root.TryGetPath(out var encounters, "encounters"))
        {
            return;
        }

        foreach (var category in EncounterCategories)
        {
            if (!encounters.TryGetPath(out var groups, category) || groups.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetPath(out var nested, "encounters") || nested.ValueKind != JsonValueKind.Array)
                {
                    if (MatchesEncounter(group, dutyName, encounter))
                    {
                        ExtractEncounterData(info, group);
                        return;
                    }

                    continue;
                }

                foreach (var candidate in nested.EnumerateArray())
                {
                    if (MatchesEncounter(candidate, dutyName, encounter))
                    {
                        ExtractEncounterData(info, candidate);
                        return;
                    }
                }
            }
        }
    }

    private static bool MatchesEncounter(JsonElement candidate, string dutyName, TomestoneEncounterParams? encounter)
    {
        var zoneName = candidate.GetStringOrNull("zoneName");
        if (encounter == null)
        {
            return string.Equals(zoneName, dutyName, StringComparison.OrdinalIgnoreCase);
        }

        // The slug is that of the zone name for ultimates, and of the boss name for everything else.
        return string.Equals(Slugify(zoneName), encounter.Encounter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Slugify(candidate.GetStringOrNull("name")), encounter.Encounter, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractEncounterData(TomestoneCharacterInfo info, JsonElement encounter)
    {
        // The profile only gives a cleared encounter's week, not a kill count. Hidden activity leaves out
        // the logged clear, but the achievement stays public.
        if ((encounter.TryGetPath(out var clear, "activity") || encounter.TryGetPath(out clear, "achievement"))
            && clear.ValueKind == JsonValueKind.Object)
        {
            info.TotalClears = 1;

            var week = clear.GetStringOrNull("completionWeek");
            if (!string.IsNullOrWhiteSpace(week))
            {
                info.CompletionWeek = week;
            }
        }

        if (!encounter.TryGetPath(out var progression, "progression"))
        {
            return;
        }

        if (progression.TryGetPath(out var mechanicEl, "mechanic") && FormatMechanic(mechanicEl) is { } mechanic)
        {
            info.ProgPoint = mechanic;
        }

        // Already formatted by the API, such as "35.82%".
        var display = progression.GetStringOrNull("displayPercent");
        if (!string.IsNullOrWhiteSpace(display))
        {
            info.DisplayPercent = display;
        }

        if (string.IsNullOrWhiteSpace(info.ProgPoint) && progression.TryGetPath(out var percent, "percent"))
        {
            info.ProgPoint = percent.ToString();
        }
    }

    // "Honey B. Lovely" becomes "honey-b-lovely".
    private static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var stripped = SlugStripRegex().Replace(text.Trim().ToLowerInvariant(), string.Empty);
        return string.Join("-", stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    #endregion
}

public record TomestoneEncounterParams(string Expansion, string Zone, string Encounter);

public class TomestoneCharacterInfo
{
    public string? ProgPoint { get; set; }
    public string? DisplayPercent { get; set; }
    public int? TotalClears { get; set; }
    public string? CompletionWeek { get; set; }
    public double? BestPercent { get; set; }
    public bool NoLogs { get; set; }

    // Tomestone has no page for this name and world.
    public bool NotFound { get; set; }

    // The profile is public but its prog and logs are not.
    public bool ActivityHidden { get; set; }

    // A request failed, so missing data may only be missing from this lookup.
    public bool LookupFailed { get; set; }
}
