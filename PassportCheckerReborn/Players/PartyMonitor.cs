using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace PassportCheckerReborn.Players;

public sealed class PartyMonitor : IDisposable
{
    private const long CheckIntervalMs = 1000;

    private readonly Plugin plugin;
    private long nextCheckAt;
    private string compositionKey = string.Empty;

    public PartyMonitor(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public IReadOnlyList<PartyMemberInfo> Members { get; private set; } = [];

    public int Version { get; private set; }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    public static unsafe bool IsInParty()
    {
        if (Plugin.PartyList.Length > 0)
        {
            return true;
        }

        var crossRealm = InfoProxyCrossRealm.Instance();
        return crossRealm != null && crossRealm->IsInCrossRealmParty;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now < nextCheckAt)
        {
            return;
        }

        nextCheckAt = now + CheckIntervalMs;

        try
        {
            var members = Plugin.PlayerState.IsLoaded ? ReadPartyMembers() : [];
            var key = BuildCompositionKey(members);
            if (key == compositionKey)
            {
                return;
            }

            compositionKey = key;
            Members = members;
            Version++;

            foreach (var member in members)
            {
                if (member.ContentId != 0 && !string.IsNullOrEmpty(member.World))
                {
                    plugin.CidCache.Set(member.ContentId, member.Name, member.WorldId, member.World);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PartyMonitor] Failed to read the party.");
        }
    }

    private static List<PartyMemberInfo> ReadPartyMembers()
    {
        var partyList = Plugin.PartyList;
        if (partyList.Length == 0)
        {
            return ReadCrossRealmPartyMembers();
        }

        var result = new List<PartyMemberInfo>(partyList.Length);
        for (var i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            var name = member?.Name.TextValue;
            if (member == null || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var world = member.World.ValueNullable?.Name.ToString() ?? string.Empty;
            var job = member.ClassJob.ValueNullable?.Abbreviation.ToString() ?? "???";
            result.Add(new PartyMemberInfo(name, world, job, member.ContentId, (ushort)member.World.RowId));
        }

        return result;
    }

    private static unsafe List<PartyMemberInfo> ReadCrossRealmPartyMembers()
    {
        var result = new List<PartyMemberInfo>();
        var proxy = InfoProxyCrossRealm.Instance();
        if (proxy == null || !proxy->IsInCrossRealmParty)
        {
            return result;
        }

        var worldSheet = Plugin.DataManager.GetExcelSheet<World>();
        var classJobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();

        var groupIndex = proxy->LocalPlayerGroupIndex;
        var memberCount = InfoProxyCrossRealm.GetGroupMemberCount(groupIndex);
        for (var i = 0; i < memberCount; i++)
        {
            var memberPtr = InfoProxyCrossRealm.GetGroupMember((uint)i, groupIndex);
            if (memberPtr == null)
            {
                continue;
            }

            var member = *memberPtr;
            if (member.HomeWorld == -1 || string.IsNullOrEmpty(member.NameString))
            {
                continue;
            }

            var world = worldSheet.GetRowOrDefault((uint)member.HomeWorld)?.Name.ToString() ?? string.Empty;
            var job = classJobSheet.GetRowOrDefault(member.ClassJobId)?.Abbreviation.ToString() ?? "???";
            result.Add(new PartyMemberInfo(member.NameString, world, job, member.ContentId, (ushort)member.HomeWorld));
        }

        return result;
    }

    private static string BuildCompositionKey(List<PartyMemberInfo> members)
    {
        var parts = new List<string>(members.Count);
        foreach (var member in members)
        {
            parts.Add($"{member.ContentId}:{member.Name}@{member.World}:{member.JobAbbreviation}");
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("|", parts);
    }
}
