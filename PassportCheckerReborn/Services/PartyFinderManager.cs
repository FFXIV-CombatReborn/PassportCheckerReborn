using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Services;

// Tracks the Party Finder list and detail pane, and works out who is in the listing being viewed.
// The member lookups follow OpenRadar. Everything here runs on the framework thread unless noted.
public sealed class PartyFinderManager : IDisposable
{
    private const int MaxListingSlots = 48;
    private const int CharaCardThrottleMs = 900;

    // UIColor row tinting the boxed "P" in front of the "View Recruitment" context-menu entry.
    private const ushort ViewRecruitmentPrefixColor = 37;

    private static readonly TimeSpan CharaCardTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] HighEndKeywords = ["savage", "ultimate", "extreme", "criterion", "unreal"];

    private readonly PassportCheckerReborn plugin;
    private readonly List<PartyMemberInfo> currentMembers = [];

    private Hook<AgentLookingForGroup.Delegates.PopulateListingData>? populateListingHook;
    private Hook<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>? charaCardPacketHook;
    private Hook<RaptureLogModule.Delegates.ShowLogMessage>? showLogMessageHook;

    // What the game last filled the detail pane from: every slot's content ID and job.
    private AgentLookingForGroup.Detailed? currentDetailedPost;

    // Adventure plate requests go out one at a time, because the game's "plate hidden" answer does
    // not say whose plate it was.
    private readonly SemaphoreSlim charaCardRequestGate = new(1, 1);
    private volatile CharaCardRequest? charaCardRequest;
    private CancellationTokenSource? resolveCts;

    public PartyFinderManager(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;

        var lifecycle = PassportCheckerReborn.AddonLifecycle;
        lifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroupDetail", OnPFDetailSetup);
        lifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroupDetail", OnPFDetailRefresh);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroupDetail", OnPFDetailFinalize);
        lifecycle.RegisterListener(AddonEvent.PostSetup, "LookingForGroup", OnPFListSetup);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, "LookingForGroup", OnPFListFinalize);
        lifecycle.RegisterListener(AddonEvent.PostSetup, "BlackList", OnBlacklistAddonUpdated);
        lifecycle.RegisterListener(AddonEvent.PostRefresh, "BlackList", OnBlacklistAddonUpdated);

        PassportCheckerReborn.PartyFinderGui.ReceiveListing += OnReceiveListing;

        RegisterHooks();
        ApplyContextMenuSetting();
        RefreshBlacklist();
    }

    public IReadOnlyList<PartyMemberInfo> CurrentMembers => currentMembers;

    public bool IsDetailOpen { get; private set; }

    public bool IsListOpen { get; private set; }

    public uint CurrentDutyId { get; private set; }

    public bool IsHighEndDuty { get; private set; }

    public string CurrentDutyName { get; private set; } = string.Empty;

    public string CurrentDutyNameEnglish { get; private set; } = string.Empty;

    // The FFLogs and Tomestone duty maps are keyed in English whatever the client's language.
    public string LookupDutyName => string.IsNullOrWhiteSpace(CurrentDutyNameEnglish) ? CurrentDutyName : CurrentDutyNameEnglish;

    // Goes up each time a detail pane opens, so the overlay knows to drop what it looked up.
    public int DetailOpenGeneration { get; private set; }

    public bool HasPendingMembers => currentMembers.Exists(static member => member.NameState == MemberNameState.Pending);

    // Never populated yet: nothing records who the player has grouped with.
    public ConcurrentDictionary<string, bool> KnownPlayers { get; } = new();

    public void Dispose()
    {
        CancelNameResolution();
        PassportCheckerReborn.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        PassportCheckerReborn.PartyFinderGui.ReceiveListing -= OnReceiveListing;
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(
            OnPFDetailSetup, OnPFDetailRefresh, OnPFDetailFinalize, OnPFListSetup, OnPFListFinalize, OnBlacklistAddonUpdated);

        populateListingHook?.Dispose();
        charaCardPacketHook?.Dispose();
        showLogMessageHook?.Dispose();

        currentMembers.Clear();
    }

    private unsafe void RegisterHooks()
    {
        try
        {
            populateListingHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<AgentLookingForGroup.Delegates.PopulateListingData>(
                AgentLookingForGroup.Addresses.PopulateListingData.Value,
                PopulateListingDataDetour);
            populateListingHook.Enable();
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to hook PopulateListingData.");
        }

        try
        {
            charaCardPacketHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>(
                CharaCard.Addresses.HandleCurrentCharaCardDataPacket.Value,
                CharaCardPacketDetour);
            charaCardPacketHook.Enable();

            showLogMessageHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<RaptureLogModule.Delegates.ShowLogMessage>(
                RaptureLogModule.Addresses.ShowLogMessage.Value,
                ShowLogMessageDetour);
            showLogMessageHook.Enable();
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to hook CharaCard/ShowLogMessage.");
        }
    }

    #region Addon events

    private void OnPFListSetup(AddonEvent type, AddonArgs args)
    {
        IsListOpen = true;
    }

    private void OnPFListFinalize(AddonEvent type, AddonArgs args)
    {
        IsListOpen = false;
    }

    private void OnPFDetailSetup(AddonEvent type, AddonArgs args)
    {
        IsDetailOpen = true;
        DetailOpenGeneration++;

        RefreshBlacklist();
        RefreshDetail();

        if (plugin.Configuration.ShowMemberInfoOverlay)
        {
            plugin.PFWindow.IsOpen = true;
        }
    }

    private void OnPFDetailRefresh(AddonEvent type, AddonArgs args)
    {
        RefreshDetail();
    }

    private void OnPFDetailFinalize(AddonEvent type, AddonArgs args)
    {
        CancelNameResolution();

        IsDetailOpen = false;
        plugin.PFWindow.IsOpen = false;
        currentMembers.Clear();
        currentDetailedPost = null;
        CurrentDutyId = 0;
        CurrentDutyName = string.Empty;
        IsHighEndDuty = false;

        plugin.CidCache.Save();
    }

    private void OnBlacklistAddonUpdated(AddonEvent type, AddonArgs args)
    {
        RefreshBlacklist();
    }

    private void RefreshDetail()
    {
        ReadDutyNameFromAddon();
        DetectCurrentDuty();
        RefreshMembers();
    }

    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        var worldId = (ushort)listing.HomeWorld.RowId;
        plugin.CidCache.Set(listing.ContentId, listing.Name.TextValue, worldId, GetWorldName(worldId));
    }

    private unsafe void PopulateListingDataDetour(AgentLookingForGroup* thisPtr, AgentLookingForGroup.Detailed* listingData)
    {
        if (listingData != null)
        {
            currentDetailedPost = *listingData;
        }

        populateListingHook!.Original(thisPtr, listingData);
    }

    #endregion

    #region Duty

    // Fallback for when the hook has not delivered a duty ID: AtkValue 15 holds the duty's name.
    private unsafe void ReadDutyNameFromAddon()
    {
        CurrentDutyName = string.Empty;

        var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroupDetail", 1);
        if (addonPtr.IsNull)
        {
            return;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon->AtkValuesCount <= 15)
        {
            return;
        }

        // Value types 6 and 8 are the string ones.
        var value = addon->AtkValues[15];
        if ((uint)value.Type is 6 or 8 && value.String.HasValue)
        {
            CurrentDutyName = value.String.ToString() ?? string.Empty;
        }
    }

    private void DetectCurrentDuty()
    {
        CurrentDutyId = 0;
        CurrentDutyNameEnglish = string.Empty;

        if (currentDetailedPost is not { DutyId: > 0 } post)
        {
            IsHighEndDuty = HasHighEndKeyword(CurrentDutyName);
            return;
        }

        CurrentDutyId = post.DutyId;
        CurrentDutyName = GetDutyName(post.DutyId) ?? CurrentDutyName;
        CurrentDutyNameEnglish = GetDutyName(post.DutyId, ClientLanguage.English) ?? CurrentDutyName;

        var duty = PassportCheckerReborn.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(post.DutyId);
        IsHighEndDuty = duty?.HighEndDuty == true
            || post.Category.HasFlag(AgentLookingForGroup.DutyCategory.HighEndDuty)
            || HasHighEndKeyword(CurrentDutyName);
    }

    private static bool HasHighEndKeyword(string dutyName)
    {
        foreach (var keyword in HighEndKeywords)
        {
            if (dutyName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetDutyName(uint dutyId, ClientLanguage? language = null)
    {
        try
        {
            var name = PassportCheckerReborn.DataManager.GetExcelSheet<ContentFinderCondition>(language)
                .GetRowOrDefault(dutyId)?.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            // A client without the asked-for language has no such sheet.
            PassportCheckerReborn.Log.Warning(ex, $"[PartyFinderManager] Failed to get the duty name for id {dutyId}.");
            return null;
        }
    }

    private static string GetWorldName(uint worldId)
    {
        return PassportCheckerReborn.DataManager.GetExcelSheet<World>().GetRowOrDefault(worldId)?.Name.ToString() ?? string.Empty;
    }

    #endregion

    #region Members

    // The leader first, then the rest in slot order.
    private void RefreshMembers()
    {
        CancelNameResolution();
        currentMembers.Clear();

        // A leader ID of 0 means the listing has expired.
        if (currentDetailedPost is not { LeaderContentId: not 0 } post)
        {
            return;
        }

        try
        {
            var classJobSheet = PassportCheckerReborn.DataManager.GetExcelSheet<ClassJob>();
            var canResolve = charaCardPacketHook is { IsEnabled: true };
            var leaderJob = "???";

            // Slots past TotalSlots hold garbage, and members can sit in any slot before it.
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

            // The listing carries the leader's own name and world.
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
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to read member data.");
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
                var result = await RequestCharaCardAsync(contentId, ct);
                await PassportCheckerReborn.Framework.RunOnFrameworkThread(() => ApplyCharaCardResult(contentId, result, ct));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to resolve member names.");
        }
    }

    private void ApplyCharaCardResult(ulong contentId, CharaCardResult? result, CancellationToken ct)
    {
        // Cancelled means the members this lookup was for have been replaced.
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
                // No answer at all, so the slot is dropped.
                currentMembers.RemoveAt(index);
                break;

            case { IsHidden: true }:
                currentMembers[index] = member with { NameState = MemberNameState.Private };
                break;

            case { Name.Length: > 0 } resolved:
                var world = GetWorldName(resolved.WorldId);
                plugin.CidCache.Set(contentId, resolved.Name, resolved.WorldId, world);
                currentMembers[index] = member with
                {
                    Name = resolved.Name,
                    World = world,
                    WorldId = resolved.WorldId,
                    NameState = MemberNameState.Resolved,
                };
                break;

            default:
                // Some plates, cross-data-center ones among them, come back without a name.
                currentMembers[index] = member with { NameState = MemberNameState.Unresolved };
                break;
        }
    }

    // Runs off the framework thread. Null means the game never answered.
    private async Task<CharaCardResult?> RequestCharaCardAsync(ulong contentId, CancellationToken ct)
    {
        await charaCardRequestGate.WaitAsync(ct);
        try
        {
            await Task.Delay(CharaCardThrottleMs, ct);

            // Completed from the hooks, which must not run this method's continuation themselves.
            var request = new CharaCardRequest(contentId, new(TaskCreationOptions.RunContinuationsAsynchronously));
            charaCardRequest = request;

            await PassportCheckerReborn.Framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    var charaCard = CharaCard.Instance();
                    if (charaCard != null)
                    {
                        charaCard->RequestCharaCardForContentId(contentId);
                    }
                }
            });

            try
            {
                return await request.Completion.Task.WaitAsync(CharaCardTimeout, ct);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
        finally
        {
            charaCardRequest = null;
            charaCardRequestGate.Release();
        }
    }

    private unsafe void CharaCardPacketDetour(CharaCard* thisPtr, CharaCardPacket* packet)
    {
        try
        {
            if (charaCardRequest is { } request && request.ContentId == packet->ContentId)
            {
                var rawName = System.Text.Encoding.UTF8.GetString(packet->Name).TrimEnd('\0');

                // The name is sometimes preceded by non-printable or symbol bytes, such as icon
                // glyphs. Future me: don't delete this.
                var nameStart = 0;
                while (nameStart < rawName.Length && !char.IsLetter(rawName[nameStart]) && rawName[nameStart] != ' ')
                {
                    nameStart++;
                }

                request.Completion.TrySetResult(new CharaCardResult(rawName[nameStart..].Trim(), packet->WorldId, false));
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] CharaCard detour error.");
        }

        charaCardPacketHook!.Original(thisPtr, packet);
    }

    private unsafe void ShowLogMessageDetour(RaptureLogModule* thisPtr, uint logMessageId)
    {
        // The game announces that it is closing the Party Finder just before it does. If that close
        // is going to be skipped, the announcement would be untrue.
        if (logMessageId == PFWindowManager.PartyFinderClosedLogMessageId && PFWindowManager.TryInterceptClosedMessage())
        {
            return;
        }

        // 5855-5860: the adventure plate asked for is hidden or unavailable. Only swallowed while one
        // of the plugin's own lookups is waiting, which this also answers.
        if (logMessageId is > 5854 and < 5861 && charaCardRequest is { } request)
        {
            request.Completion.TrySetResult(new CharaCardResult(string.Empty, 0, IsHidden: true));
            return;
        }

        showLogMessageHook!.Original(thisPtr, logMessageId);
    }

    private sealed record CharaCardRequest(ulong ContentId, TaskCompletionSource<CharaCardResult?> Completion);

    private readonly record struct CharaCardResult(string Name, ushort WorldId, bool IsHidden);

    #endregion

    #region Context menu

    public void ApplyContextMenuSetting()
    {
        PassportCheckerReborn.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        if (plugin.Configuration.RightClickPlayerNameForRecruitment3)
        {
            PassportCheckerReborn.ContextMenu.OnMenuOpened += OnContextMenuOpened;
        }
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault { TargetContentId: not 0 })
        {
            return;
        }

        args.AddMenuItem(new MenuItem
        {
            Name = new SeStringBuilder().AddText("View Recruitment").Build(),
            PrefixChar = 'P',
            PrefixColor = ViewRecruitmentPrefixColor,
            OnClicked = OnViewRecruitmentClicked,
            IsEnabled = true,
        });
    }

    // The server answers with the listing, or with its own error if the player has none.
    private unsafe void OnViewRecruitmentClicked(IMenuItemClickedArgs args)
    {
        if (args.Target is not MenuTargetDefault { TargetContentId: not 0 } target)
        {
            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null || !agent->OpenListingByContentId(target.TargetContentId))
        {
            PassportCheckerReborn.ChatGui.Print("[PassportChecker] Unable to request this player's Party Finder listing.");
        }
    }

    #endregion

    #region Known players and blacklist

    public bool IsKnownPlayer(string name, string world)
    {
        return KnownPlayers.ContainsKey($"{name}@{world}");
    }

    public bool IsBlacklisted(string name, string world)
    {
        if (!plugin.Configuration.EnableBlacklistFeature || string.IsNullOrEmpty(name))
        {
            return false;
        }

        var blacklist = plugin.BlacklistCache;
        return (!string.IsNullOrEmpty(world) && blacklist.Contains($"{name}@{world}")) || blacklist.Contains(name);
    }

    // Reads the game's blacklist, which is in memory whether or not its window is open.
    public unsafe void RefreshBlacklist()
    {
        // Logged out, the game's list is empty, and reading it would wipe the saved one.
        if (!PassportCheckerReborn.ClientState.IsLoggedIn)
        {
            return;
        }

        try
        {
            var array = BlackListStringArray.Instance();
            if (array == null)
            {
                return;
            }

            var names = array->PlayerNames;
            var worlds = array->Homeworlds;
            var keys = new List<string>();
            for (var i = 0; i < names.Length; i++)
            {
                var name = names[i].ToString();
                if (string.IsNullOrEmpty(name))
                {
                    break;
                }

                var world = worlds[i].ToString();
                keys.Add(string.IsNullOrEmpty(world) ? name : $"{name}@{world}");
            }

            plugin.BlacklistCache.ReplaceAll(keys);
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to read the blacklist.");
        }
    }

    #endregion
}
