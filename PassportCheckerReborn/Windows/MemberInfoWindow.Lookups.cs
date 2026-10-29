using PassportCheckerReborn.Integrations.FFLogs;
using PassportCheckerReborn.Integrations.Tomestone;
using PassportCheckerReborn.Players;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Windows;

public partial class MemberInfoWindow
{
    private async Task FetchFFLogsAsync(PartyMemberInfo[] members, ConcurrentDictionary<ulong, EncounterParseResult?> results)
    {
        try
        {
            var encounterIds = FFLogsEncounters.GetEncounterIdsForDuty(plugin.PartyFinderManager.LookupDutyName);
            await LookUpFFLogsAsync(members, encounterIds, results);

            var renamed = false;
            for (var i = 0; i < members.Length; i++)
            {
                if (results.TryGetValue(members[i].ContentId, out var result)
                    && result is { CharacterNotFound: true }
                    && await plugin.PartyFinderManager.RefreshMemberAsync(members[i]) is { } member)
                {
                    members[i] = member;
                    renamed = true;
                }
            }

            if (renamed)
            {
                await LookUpFFLogsAsync(members, encounterIds, results);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[MemberInfoWindow] FFLogs lookup failed.");
        }
        finally
        {
            if (ReferenceEquals(results, fflogsResults))
            {
                fflogsLoading = false;
            }
        }
    }

    private async Task LookUpFFLogsAsync(
        PartyMemberInfo[] members, (int Primary, int? Secondary)? encounterIds, ConcurrentDictionary<ulong, EncounterParseResult?> results)
    {
        var service = plugin.FFLogsService;
        if (encounterIds is not { } ids)
        {
            foreach (var (index, result) in await service.GetOverallParsesAsync(members))
            {
                results[members[index].ContentId] = result;
            }

            return;
        }

        var encounterResults = await service.GetEncounterDataAsync(members, ids);
        var withoutData = new List<int>();
        foreach (var (index, result) in encounterResults)
        {
            results[members[index].ContentId] = result;
            if (!result.CharacterNotFound
                && result.TotalKills == 0
                && result.LowestBossHpPct is null
                && result.Phase1BestParse is null
                && result.Phase2BestParse is null
                && result.Phase1LowestBossHpPct is null
                && result.Phase2LowestBossHpPct is null)
            {
                withoutData.Add(index);
            }
        }

        foreach (var (index, average) in await service.GetOverallAveragesAsync(members, withoutData))
        {
            if (average.HasValue)
            {
                results[members[index].ContentId] = encounterResults[index] with { AverageParsePercent = average };
            }
        }
    }

    private async Task FetchTomestoneAsync(PartyMemberInfo[] members, ConcurrentDictionary<ulong, TomestoneCharacterInfo?> results)
    {
        try
        {
            var dutyName = plugin.PartyFinderManager.LookupDutyName;
            var service = plugin.TomestoneService;
            await Task.WhenAll(members.Select(async member =>
            {
                if (!member.IsResolved)
                {
                    results[member.ContentId] = null;
                    return;
                }

                var info = results[member.ContentId] = await service.GetCharacterInfoAsync(member.Name, member.World, dutyName);
                if (info is { NotFound: true } && await plugin.PartyFinderManager.RefreshMemberAsync(member) is { } renamed)
                {
                    results[member.ContentId] = await service.GetCharacterInfoAsync(renamed.Name, renamed.World, dutyName);
                }
            }));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[MemberInfoWindow] Tomestone lookup failed.");
        }
        finally
        {
            if (ReferenceEquals(results, tomestoneResults))
            {
                tomestoneLoading = false;
            }
        }
    }
}
