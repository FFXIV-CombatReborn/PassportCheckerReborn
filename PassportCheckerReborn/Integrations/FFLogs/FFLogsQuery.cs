using Lumina.Excel.Sheets;
using PassportCheckerReborn.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PassportCheckerReborn.Integrations.FFLogs;

internal static class FFLogsQuery
{
    private static Dictionary<string, string>? RegionByWorld;

    public static Dictionary<int, string> BuildCharacterSelectors(IReadOnlyList<PartyMemberInfo> members, IEnumerable<int> indices)
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

    public static string BuildCharacterQuery(Dictionary<int, string> selectors, string fields)
    {
        var characters = string.Join(" ", selectors.Select(selector => $"p{selector.Key}: {selector.Value} {{ {fields} }}"));
        return $"{{ characterData {{ {characters} }} }}";
    }

    public static string EncounterArguments(int encounterId)
    {
        return FFLogsEncounters.TryGetDifficulty(encounterId, out var difficulty)
            ? $"encounterID: {encounterId}, difficulty: {difficulty}"
            : $"encounterID: {encounterId}";
    }

    public static string EscapeGraphQL(string input)
    {
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string CharacterSelector(string name, (string Slug, string Region) server)
    {
        return $@"character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(server.Slug)}"", serverRegion: ""{server.Region}"")";
    }

    private static (string Slug, string Region)? GetFFLogsServer(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return null;
        }

        var regions = LazyInitializer.EnsureInitialized(ref RegionByWorld, BuildRegionLookup);
        return (worldName.ToLowerInvariant(), regions.GetValueOrDefault(worldName, "NA"));
    }

    private static Dictionary<string, string> BuildRegionLookup()
    {
        var regions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var world in Plugin.DataManager.GetExcelSheet<World>())
        {
            var region = world.DataCenter.ValueNullable?.Region.RowId switch
            {
                1 => "JP",
                3 => "EU",
                4 => "OC",
                _ => null,
            };

            if (region != null)
            {
                regions[world.Name.ToString()] = region;
            }
        }

        return regions;
    }
}
