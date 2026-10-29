using PassportCheckerReborn.Integrations.FFLogs;
using PassportCheckerReborn.Integrations.Tomestone;
using PassportCheckerReborn.Players;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Windows;

public partial class PartyListWindow
{
    private const string NoDuty = "(None)";

    private static readonly string[] DutyNames = BuildDutyNames();

    private string? SelectedDutyName => selectedDutyIndex > 0 ? DutyNames[selectedDutyIndex] : null;

    private string? TomestoneDutyName => SelectedDutyName ?? plugin.PartyFinderManager.LookupDutyName;

    private string? FFLogsDutyName
    {
        get
        {
            var partyFinder = plugin.PartyFinderManager;
            return SelectedDutyName ?? (partyFinder.CurrentDutyId > 0 ? partyFinder.LookupDutyName : partyFinder.CurrentDutyName);
        }
    }

    private static string[] BuildDutyNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(FFLogsEncounters.SupportedDutyNames);
        names.UnionWith(TomestoneEncounters.SupportedDutyNames);
        return [NoDuty, .. names];
    }

    private void StartLookups(Configuration cfg)
    {
        var serial = ++lookupSerial;
        fflogsResults = [];
        tomestoneResults = [];

        fflogsLoading = cfg.EnableFFLogsIntegrationOverlay && cfg.HasFFLogsCredentials();
        if (fflogsLoading)
        {
            _ = LookUpFFLogsAsync(members, FFLogsDutyName, serial);
        }

        tomestoneLoading = cfg.EnableTomestoneIntegration && cfg.HasTomestoneKey();
        if (tomestoneLoading)
        {
            _ = LookUpTomestoneAsync(members, TomestoneDutyName, serial);
        }
    }

    private async Task LookUpFFLogsAsync(IReadOnlyList<PartyMemberInfo> party, string? dutyName, int serial)
    {
        var results = new Dictionary<int, EncounterParseResult?>();
        try
        {
            var service = plugin.FFLogsService;
            var lookup = FFLogsEncounters.GetEncounterIdsForDuty(dutyName) is { } encounterIds
                ? service.GetEncounterDataAsync(party, encounterIds)
                : service.GetOverallParsesAsync(party);
            foreach (var (index, result) in await lookup)
            {
                results[index] = result;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PartyListWindow] FFLogs lookup failed.");
        }

        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (serial == lookupSerial)
            {
                fflogsResults = results;
                fflogsLoading = false;
            }
        });
    }

    private async Task LookUpTomestoneAsync(IReadOnlyList<PartyMemberInfo> party, string? dutyName, int serial)
    {
        var results = new Dictionary<int, TomestoneCharacterInfo?>();
        try
        {
            var infos = await Task.WhenAll(party.Select(member =>
                plugin.TomestoneService.GetCharacterInfoAsync(member.Name, member.World, dutyName)));
            for (var i = 0; i < infos.Length; i++)
            {
                results[i] = infos[i];
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PartyListWindow] Tomestone lookup failed.");
        }

        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (serial == lookupSerial)
            {
                tomestoneResults = results;
                tomestoneLoading = false;
            }
        });
    }
}
