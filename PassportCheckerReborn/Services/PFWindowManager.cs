using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace PassportCheckerReborn.Services
{
    // Keeps the Party Finder open when the party changes. The game prints log message 947 and then
    // calls Hide on the Party Finder agent, so the message is what marks the next Hide as the game's
    // doing rather than the player's. Follows DailyRoutines' NoAutoClosePartyFinder.
    public static unsafe class PFWindowManager
    {
        // "The Party Finder has closed due to changes within the party."
        public const uint PartyFinderClosedLogMessageId = 947;

        // Long enough to outlive the Hide that follows the message, short enough that an unused skip
        // cannot swallow a close the player asks for later.
        private const long SuppressionWindowMs = 250;

        private static readonly TimeSpan ListingReloadDelay = TimeSpan.FromMilliseconds(100);

        private static Hook<AgentHideDelegate>? AgentHideHook;
        private static long SuppressHideUntil;

        private delegate void AgentHideDelegate(AgentLookingForGroup* agent);

        // True once hooking the game failed, so the setting cannot do anything.
        public static bool Unavailable { get; private set; }

        // Hooks or unhooks the game to match the setting.
        public static void ApplySetting()
        {
            if (!PassportCheckerReborn.Config.PreventAutoClosingOnPartyChanges2)
            {
                AgentHideHook?.Disable();
                SuppressHideUntil = 0;
                return;
            }

            if (AgentHideHook == null && !Unavailable)
            {
                InitializeHideHook();
            }

            AgentHideHook?.Enable();
        }

        public static void Dispose()
        {
            AgentHideHook?.Dispose();
            AgentHideHook = null;
            SuppressHideUntil = 0;
        }

        private static void InitializeHideHook()
        {
            try
            {
                // Hide is virtual, so its address comes from the agent's vtable rather than a signature.
                var agent = AgentLookingForGroup.Instance();
                var hideAddress = agent == null || agent->VirtualTable == null ? 0 : (nint)agent->VirtualTable->Hide;
                if (hideAddress == 0)
                {
                    PassportCheckerReborn.Log.Warning("[PFWindowManager] Party Finder agent is not available.");
                    Unavailable = true;
                    return;
                }

                AgentHideHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<AgentHideDelegate>(hideAddress, AgentHideDetour);
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Error(ex, "[PFWindowManager] Failed to hook the Party Finder agent.");
                Unavailable = true;
            }
        }

        // Called for log message 947. Returns true when the close will be skipped, in which case the
        // message would be untrue and should not be shown.
        public static bool TryInterceptClosedMessage()
        {
            if (AgentHideHook is not { IsEnabled: true })
            {
                return false;
            }

            SuppressHideUntil = Environment.TickCount64 + SuppressionWindowMs;
            return true;
        }

        private static void AgentHideDetour(AgentLookingForGroup* agent)
        {
            if (Environment.TickCount64 > SuppressHideUntil)
            {
                AgentHideHook!.Original(agent);
                return;
            }

            // One message arms one skip.
            SuppressHideUntil = 0;

            try
            {
                ReloadOpenListing(agent);
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Warning(ex, "[PFWindowManager] Failed to reload the open listing.");
            }
        }

        // The listing on screen is the one most likely to have just changed, so it is closed and
        // requested again rather than left showing the party as it was.
        private static void ReloadOpenListing(AgentLookingForGroup* agent)
        {
            var listingId = agent->LastViewedListing.ListingId;
            var detailPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroupDetail", 1);
            if (listingId == 0 || detailPtr.IsNull)
            {
                return;
            }

            ((AtkUnitBase*)detailPtr.Address)->Close(true);

            PassportCheckerReborn.Framework.RunOnTick(() =>
            {
                var current = AgentLookingForGroup.Instance();
                if (current != null && current->IsAgentActive())
                {
                    current->OpenListing(listingId);
                }
            }, ListingReloadDelay);
        }
    }
}
