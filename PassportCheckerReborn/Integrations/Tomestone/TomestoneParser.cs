using System;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PassportCheckerReborn.Integrations.Tomestone;

internal static partial class TomestoneParser
{
    private static readonly string[] EncounterCategories = ["savage", "ultimate", "extremes", "criterion", "chaotic", "quantum"];

    private static readonly string[][] ActivityArrayPaths = [["results"], ["data"], ["data", "paginator", "data"]];

    [GeneratedRegex(@"[^a-z0-9\s-]")]
    private static partial Regex SlugStripRegex();

    public static string? ParseProgPointFromGraph(JsonElement root)
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

    public static void ParseActivityResponse(TomestoneCharacterInfo info, JsonElement root)
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

    public static void ParseProfileEncounters(
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

    private static string? ParseDirectProgPoint(JsonElement root)
    {
        if (root.TryGetPath(out var percent, "percent") || root.TryGetPath(out percent, "bestPercent"))
        {
            return percent.ToString();
        }

        return root.GetStringOrNull("progPoint") ?? root.GetStringOrNull("prog_point");
    }

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

    private static bool MatchesEncounter(JsonElement candidate, string dutyName, TomestoneEncounterParams? encounter)
    {
        var zoneName = candidate.GetStringOrNull("zoneName");
        if (encounter == null)
        {
            return string.Equals(zoneName, dutyName, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Slugify(zoneName), encounter.Encounter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Slugify(candidate.GetStringOrNull("name")), encounter.Encounter, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractEncounterData(TomestoneCharacterInfo info, JsonElement encounter)
    {
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

    private static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var stripped = SlugStripRegex().Replace(text.Trim().ToLowerInvariant(), string.Empty);
        return string.Join("-", stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
