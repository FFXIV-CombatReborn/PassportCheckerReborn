using System;
using System.Collections.Generic;
using System.Globalization;
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

    private readonly PassportCheckerReborn plugin;
    private readonly HttpClient httpClient;

    public TomestoneService(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PassportCheckerReborn/{PassportCheckerReborn.Version}");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // The phase entries of a multi-part duty are listed; its combined name is not.
    public static IEnumerable<string> SupportedDutyNames => Encounters.Keys;

    [GeneratedRegex(@"[^a-z0-9\s-]")]
    private static partial Regex SlugStripRegex();

    public void Dispose()
    {
        httpClient.Dispose();
    }

    public async Task<TomestoneCharacterInfo?> GetCharacterInfoAsync(string playerName, string world, string? dutyName = null)
    {
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(world))
        {
            return null;
        }

        TomestoneEncounterParams? encounter = null;
        TomestoneEncounterParams? fallback = null;
        if (!string.IsNullOrWhiteSpace(dutyName)
            && !Encounters.TryGetValue(dutyName, out encounter)
            && MultiPartEncounters.TryGetValue(dutyName, out var parts))
        {
            encounter = parts.Preferred;
            fallback = parts.Fallback;
        }

        var info = new TomestoneCharacterInfo();
        var server = Uri.EscapeDataString(world);
        var name = Uri.EscapeDataString(playerName);

        await FetchAsync(info, $"{ApiBaseUrl}/character/profile/{server}/{name}", root =>
        {
            if (root.TryGetProperty("lodestoneId", out var id)
                || root.TryGetProperty("lodestone_id", out id)
                || root.TryGetProperty("id", out id))
            {
                info.CharacterId = id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString() : id.GetString();
            }
        });

        await FetchEncounterAsync(info, server, name, dutyName, encounter);
        if (fallback != null && !HasEncounterData(info))
        {
            await FetchEncounterAsync(info, server, name, dutyName, fallback);
        }

        return info;
    }

    private static bool HasEncounterData(TomestoneCharacterInfo info)
    {
        return info.ProgPoint != null || info.TotalClears.HasValue;
    }

    // The profile by Lodestone ID covers every encounter in one call. The per-encounter endpoints are
    // older, and only asked when it came back without the duty.
    private async Task FetchEncounterAsync(
        TomestoneCharacterInfo info, string server, string name, string? dutyName, TomestoneEncounterParams? encounter)
    {
        if (encounter == null && string.IsNullOrWhiteSpace(dutyName))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(info.CharacterId))
        {
            await FetchAsync(info, $"{ApiBaseUrl}/character/profile/{info.CharacterId}?update=false",
                root => ParseProfileEncounters(info, root, dutyName, encounter));
        }

        if (encounter == null || HasEncounterData(info))
        {
            return;
        }

        var query = $"{server}/{name}"
            + $"?expansion={Uri.EscapeDataString(encounter.Expansion)}"
            + $"&zone={Uri.EscapeDataString(encounter.Zone)}"
            + $"&encounter={Uri.EscapeDataString(encounter.Encounter)}";

        await FetchAsync(info, $"{ApiBaseUrl}/character/progression-graph/{query}", root => info.ProgPoint = ParseProgPointFromGraph(root));
        await FetchAsync(info, $"{ApiBaseUrl}/character/activity/{query}", root => ParseActivityResponse(info, root));
    }

    // Requests a URL and hands the JSON to parse. A failed request or a parse error leaves info as it was.
    private async Task FetchAsync(TomestoneCharacterInfo info, string url, Action<JsonElement> parse)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var apiKey = plugin.Configuration.TomestoneApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            // A hidden profile is answered with an empty array.
            var json = await response.Content.ReadAsStringAsync();
            if (json.Trim() == "[]")
            {
                info.NoLogs = true;
                return;
            }

            using var doc = JsonDocument.Parse(json);
            parse(doc.RootElement);
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Debug(ex, $"[TomestoneService] Request failed: {url}");
        }
    }

    #region Response parsing

    // The furthest mechanic reached, from a graph of { duration, mechanic: { name, number } } points.
    private static string? ParseProgPointFromGraph(JsonElement root)
    {
        if (!((root.TryGetProperty("data", out var data) && data.TryGetProperty("graph", out var graph))
              || root.TryGetProperty("graph", out graph))
            || graph.ValueKind != JsonValueKind.Array)
        {
            return ParseDirectProgPoint(root);
        }

        var longest = 0;
        string? mechanic = null;
        foreach (var point in graph.EnumerateArray())
        {
            if (!point.TryGetProperty("duration", out var durationEl))
            {
                continue;
            }

            var duration = durationEl.GetInt32();
            if (duration <= longest)
            {
                continue;
            }

            longest = duration;
            if (point.TryGetProperty("mechanic", out var mechanicEl))
            {
                mechanic = FormatMechanic(mechanicEl) ?? mechanic;
            }
        }

        return mechanic;
    }

    private static string? ParseDirectProgPoint(JsonElement root)
    {
        if (root.TryGetProperty("percent", out var percent) || root.TryGetProperty("bestPercent", out percent))
        {
            return percent.ToString();
        }

        if (root.TryGetProperty("progPoint", out var progPoint) || root.TryGetProperty("prog_point", out progPoint))
        {
            return progPoint.GetString();
        }

        return null;
    }

    // "Splattershed", or "Splattershed #2" for a mechanic that comes round more than once.
    private static string? FormatMechanic(JsonElement mechanic)
    {
        if (mechanic.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = mechanic.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var number = mechanic.TryGetProperty("number", out var numberEl) && numberEl.TryGetInt32(out var value) ? value : 0;
        return number > 1 ? $"{name} #{number}" : name;
    }

    // The activity endpoint has answered in several shapes; each is tried in turn.
    private static void ParseActivityResponse(TomestoneCharacterInfo info, JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            ParseActivityArray(info, root);
            return;
        }

        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            ParseActivityArray(info, results);
            return;
        }

        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Array)
            {
                ParseActivityArray(info, data);
                return;
            }

            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("paginator", out var paginator)
                && paginator.TryGetProperty("data", out var page)
                && page.ValueKind == JsonValueKind.Array)
            {
                ParseActivityArray(info, page);
                return;
            }
        }

        if ((root.TryGetProperty("clears", out var clears) || root.TryGetProperty("killsCount", out clears))
            && clears.TryGetInt32(out var count))
        {
            info.TotalClears = count;
        }

        if (root.TryGetProperty("bestPercent", out var best) && best.TryGetDouble(out var bestPercent))
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
            var activity = entry.TryGetProperty("activity", out var nested) ? nested : entry;

            if (activity.TryGetProperty("killsCount", out var killsEl) && killsEl.TryGetInt32(out var kills) && kills > 0)
            {
                totalKills += kills;
            }

            if (activity.TryGetProperty("bestPercent", out var bestEl)
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
        TomestoneCharacterInfo info, JsonElement root, string? dutyName, TomestoneEncounterParams? encounter)
    {
        if (!root.TryGetProperty("encounters", out var encounters) || encounters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var category in EncounterCategories)
        {
            if (!encounters.TryGetProperty(category, out var groups) || groups.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!group.TryGetProperty("encounters", out var nested) || nested.ValueKind != JsonValueKind.Array)
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

    private static bool MatchesEncounter(JsonElement candidate, string? dutyName, TomestoneEncounterParams? encounter)
    {
        var zoneName = candidate.TryGetProperty("zoneName", out var zoneEl) ? zoneEl.GetString() : null;
        if (encounter == null)
        {
            return !string.IsNullOrWhiteSpace(dutyName) && string.Equals(zoneName, dutyName, StringComparison.OrdinalIgnoreCase);
        }

        // The slug is that of the zone name for ultimates, and of the boss name for everything else.
        var name = candidate.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        return string.Equals(Slugify(zoneName), encounter.Encounter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Slugify(name), encounter.Encounter, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractEncounterData(TomestoneCharacterInfo info, JsonElement encounter)
    {
        // The profile only gives timestamps for a cleared encounter, not a kill count.
        if (encounter.TryGetProperty("activity", out var activity) && activity.ValueKind == JsonValueKind.Object)
        {
            info.TotalClears = 1;

            var week = activity.TryGetProperty("completionWeek", out var weekEl) ? weekEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(week))
            {
                info.CompletionWeek = week;
            }
        }

        if (!encounter.TryGetProperty("progression", out var progression) || progression.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (progression.TryGetProperty("mechanic", out var mechanicEl) && FormatMechanic(mechanicEl) is { } mechanic)
        {
            info.ProgPoint = mechanic;
        }

        // Already formatted by the API, such as "35.82%".
        var display = progression.TryGetProperty("displayPercent", out var displayEl) ? displayEl.GetString() : null;
        if (!string.IsNullOrWhiteSpace(display))
        {
            info.DisplayPercent = display;
        }

        if (string.IsNullOrWhiteSpace(info.ProgPoint) && progression.TryGetProperty("percent", out var percent))
        {
            info.ProgPoint = percent.GetString();
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
    // The Lodestone ID.
    public string? CharacterId { get; set; }
    public string? ProgPoint { get; set; }
    public string? DisplayPercent { get; set; }
    public int? TotalClears { get; set; }
    public string? CompletionWeek { get; set; }
    public double? BestPercent { get; set; }
    public bool NoLogs { get; set; }
}
