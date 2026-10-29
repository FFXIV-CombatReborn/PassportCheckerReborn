using Lumina.Excel.Sheets;
using PassportCheckerReborn.Players;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn.PartyFinder;

public sealed partial class PartyFinderManager
{
    private const int MaxListingSlots = 48;

    private readonly List<PartyMemberInfo> currentMembers = [];
    private readonly ConcurrentDictionary<ulong, Task<CidCacheEntry?>> refreshes = new();
    private CancellationTokenSource? resolveCts;

    public IReadOnlyList<PartyMemberInfo> CurrentMembers => currentMembers;

    public bool HasPendingMembers => currentMembers.Exists(static member => member.NameState == MemberNameState.Pending);

    public ConcurrentDictionary<string, bool> KnownPlayers { get; } = new();

    public bool IsKnownPlayer(string name, string world)
    {
        return KnownPlayers.ContainsKey($"{name}@{world}");
    }

    public async Task<PartyMemberInfo?> RefreshMemberAsync(PartyMemberInfo member)
    {
        if (member.ContentId == 0)
        {
            return null;
        }

        var refresh = refreshes.GetOrAdd(member.ContentId, contentId => Task.Run(() => RefreshEntryAsync(contentId)));
        CidCacheEntry? entry;
        try
        {
            entry = await refresh;
        }
        finally
        {
            refreshes.TryRemove(KeyValuePair.Create(member.ContentId, refresh));
        }

        return entry != null && (entry.Name != member.Name || entry.WorldId != member.WorldId)
            ? member with { Name = entry.Name, World = entry.WorldName, WorldId = entry.WorldId }
            : null;
    }

    private void RefreshMembers()
    {
        CancelNameResolution();
        currentMembers.Clear();

        if (currentDetailedPost is not { LeaderContentId: not 0 } post)
        {
            return;
        }

        try
        {
            var classJobSheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
            var canResolve = plugin.CharaCards.IsAvailable;
            var leaderJob = "???";

            var slots = post.TotalSlots is > 0 and <= MaxListingSlots ? (int)post.TotalSlots : MaxListingSlots;
            for (var i = 0; i < slots; i++)
            {
                var contentId = post.MemberContentIds[i];
                if (contentId == 0)
                {
                    continue;
                }

                var job = classJobSheet.GetRowOrDefault(post.Jobs[i])?.Abbreviation.ToString() ?? "???";
                if (contentId == post.LeaderContentId)
                {
                    leaderJob = job;
                }
                else
                {
                    currentMembers.Add(CreateMember(contentId, job, canResolve));
                }
            }

            var leaderName = post.LeaderString;
            if (string.IsNullOrEmpty(leaderName))
            {
                currentMembers.Insert(0, CreateMember(post.LeaderContentId, leaderJob, canResolve));
            }
            else
            {
                var leaderWorld = GetWorldName(post.HomeWorld);
                plugin.CidCache.Set(post.LeaderContentId, leaderName, post.HomeWorld, leaderWorld);
                currentMembers.Insert(0, new PartyMemberInfo(leaderName, leaderWorld, leaderJob, post.LeaderContentId, post.HomeWorld));
            }

            var pending = new List<ulong>();
            foreach (var member in currentMembers)
            {
                if (member.NameState == MemberNameState.Pending)
                {
                    pending.Add(member.ContentId);
                }
            }

            if (pending.Count > 0)
            {
                resolveCts = new CancellationTokenSource();
                _ = ResolvePendingMembersAsync(pending, resolveCts.Token);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PartyFinderManager] Failed to read member data.");
        }
    }

    private PartyMemberInfo CreateMember(ulong contentId, string job, bool canResolve)
    {
        if (plugin.CidCache.TryGet(contentId, out var known) && known != null && !string.IsNullOrEmpty(known.Name))
        {
            return new PartyMemberInfo(known.Name, known.WorldName, job, contentId, known.WorldId);
        }

        var state = canResolve ? MemberNameState.Pending : MemberNameState.Unresolved;
        return new PartyMemberInfo(string.Empty, string.Empty, job, contentId, NameState: state);
    }

    private void CancelNameResolution()
    {
        resolveCts?.Cancel();
        resolveCts?.Dispose();
        resolveCts = null;
    }

    private async Task ResolvePendingMembersAsync(List<ulong> contentIds, CancellationToken ct)
    {
        try
        {
            foreach (var contentId in contentIds)
            {
                var result = await plugin.CharaCards.RequestAsync(contentId, ct);
                await Plugin.Framework.RunOnFrameworkThread(() => ApplyCharaCardResult(contentId, result, ct));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[PartyFinderManager] Failed to resolve member names.");
        }
    }

    private void ApplyCharaCardResult(ulong contentId, CharaCardResult? result, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var index = currentMembers.FindIndex(member => member.ContentId == contentId);
        if (index < 0)
        {
            return;
        }

        var member = currentMembers[index];
        switch (result)
        {
            case null:
                currentMembers.RemoveAt(index);
                break;

            case { IsHidden: true }:
                currentMembers[index] = member with { NameState = MemberNameState.Private };
                break;

            case { Name.Length: > 0 } resolved:
                StoreResolvedName(contentId, resolved);
                break;

            default:
                currentMembers[index] = member with { NameState = MemberNameState.Unresolved };
                break;
        }
    }

    private void StoreResolvedName(ulong contentId, CharaCardResult resolved)
    {
        var world = GetWorldName(resolved.WorldId);
        plugin.CidCache.Set(contentId, resolved.Name, resolved.WorldId, world);

        var index = currentMembers.FindIndex(member => member.ContentId == contentId);
        if (index >= 0)
        {
            currentMembers[index] = currentMembers[index] with
            {
                Name = resolved.Name,
                World = world,
                WorldId = resolved.WorldId,
                NameState = MemberNameState.Resolved,
            };
        }
    }

    private async Task<CidCacheEntry?> RefreshEntryAsync(ulong contentId)
    {
        var cache = plugin.CidCache;
        var due = await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (!cache.NeedsRefresh(contentId))
            {
                return false;
            }

            cache.MarkRefreshed(contentId);
            return true;
        });

        if (due && await plugin.CharaCards.RequestAsync(contentId, CancellationToken.None) is { Name.Length: > 0 } resolved)
        {
            await Plugin.Framework.RunOnFrameworkThread(() => StoreResolvedName(contentId, resolved));
        }

        return await Plugin.Framework.RunOnFrameworkThread(() => cache.TryGet(contentId, out var entry) ? entry : null);
    }
}
