using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace PassportCheckerReborn.Tweaks;

public sealed unsafe class KeepOpenTweak : IDisposable
{
    private const uint PartyFinderClosedLogMessageId = 947;

    private const long SuppressionWindowMs = 250;

    private static readonly TimeSpan ListingReloadDelay = TimeSpan.FromMilliseconds(100);

    private Hook<AgentHideDelegate>? agentHideHook;
    private long suppressHideUntil;

    private delegate void AgentHideDelegate(AgentLookingForGroup* agent);

    public KeepOpenTweak(Plugin plugin)
    {
        plugin.LogMessages.Intercept(OnLogMessage);
        ApplySetting();
    }

    public bool Unavailable { get; private set; }

    public void Dispose()
    {
        agentHideHook?.Dispose();
        agentHideHook = null;
        suppressHideUntil = 0;
    }

    public void ApplySetting()
    {
        if (!Plugin.Configuration.PreventAutoClosingOnPartyChanges2)
        {
            agentHideHook?.Disable();
            suppressHideUntil = 0;
            return;
        }

        if (agentHideHook == null && !Unavailable)
        {
            InitializeHideHook();
        }

        agentHideHook?.Enable();
    }

    private void InitializeHideHook()
    {
        try
        {
            var agent = AgentLookingForGroup.Instance();
            var hideAddress = agent == null || agent->VirtualTable == null ? 0 : (nint)agent->VirtualTable->Hide;
            if (hideAddress == 0)
            {
                Plugin.Log.Warning("[KeepOpenTweak] Party Finder agent is not available.");
                Unavailable = true;
                return;
            }

            agentHideHook = Plugin.GameInteropProvider.HookFromAddress<AgentHideDelegate>(hideAddress, AgentHideDetour);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[KeepOpenTweak] Failed to hook the Party Finder agent.");
            Unavailable = true;
        }
    }

    private bool OnLogMessage(uint logMessageId)
    {
        return logMessageId == PartyFinderClosedLogMessageId && TryInterceptClosedMessage();
    }

    private bool TryInterceptClosedMessage()
    {
        if (agentHideHook is not { IsEnabled: true })
        {
            return false;
        }

        suppressHideUntil = Environment.TickCount64 + SuppressionWindowMs;
        return true;
    }

    private void AgentHideDetour(AgentLookingForGroup* agent)
    {
        if (Environment.TickCount64 > suppressHideUntil)
        {
            agentHideHook!.Original(agent);
            return;
        }

        // One message arms one skip.
        suppressHideUntil = 0;

        try
        {
            ReloadOpenListing(agent);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[KeepOpenTweak] Failed to reload the open listing.");
        }
    }

    private static void ReloadOpenListing(AgentLookingForGroup* agent)
    {
        var listingId = agent->LastViewedListing.ListingId;
        var detailPtr = Plugin.GameGui.GetAddonByName("LookingForGroupDetail", 1);
        if (listingId == 0 || detailPtr.IsNull)
        {
            return;
        }

        ((AtkUnitBase*)detailPtr.Address)->Close(true);

        Plugin.Framework.RunOnTick(() =>
        {
            var current = AgentLookingForGroup.Instance();
            if (current != null && current->IsAgentActive())
            {
                current->OpenListing(listingId);
            }
        }, ListingReloadDelay);
    }
}
