using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace PassportCheckerReborn.Services
{
    public static class PFWindowManager
    {
        private static PassportCheckerReborn? Plugin;
        private static PartyFinderManager? PartyFinderManager;
        private static Hook<AtkUnitBase.Delegates.Close>? CloseAddonHook;

        private unsafe delegate bool CloseAddonDelegate(AtkUnitBase* unitBase, bool a1);

        public static void Enable(PassportCheckerReborn pluginInstance, PartyFinderManager pfManager)
        {
            Plugin = pluginInstance;
            PartyFinderManager = pfManager;

            // Initialize hooks
            InitializeCloseHooks();
        }

        public static void Disable()
        {
            // Dispose hooks
            DisposeCloseHooks();
        }

        private static unsafe void InitializeCloseHooks()
        {
            try
            {
                if (AtkUnitBase.StaticVirtualTablePointer == null)
                {
                    PassportCheckerReborn.Log.Warning("[PFWindowManager] StaticVirtualTablePointer is null, skipping hook initialization");
                    return;
                }

                var closeAddress = (nint)AtkUnitBase.StaticVirtualTablePointer->Close;
                if (closeAddress == 0)
                {
                    PassportCheckerReborn.Log.Warning("[PFWindowManager] Close address is null, skipping hook initialization");
                    return;
                }

                CloseAddonHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<AtkUnitBase.Delegates.Close>(
                closeAddress,
                CloseAddonDetour);

                CloseAddonHook?.Enable();

                PassportCheckerReborn.Log.Debug("[PFWindowManager] Action interception hooks initialized");
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Error($"[PFWindowManager] Failed to initialize action hooks: {ex}");
            }
        }

        private static void DisposeCloseHooks()
        {
            try
            {
                CloseAddonHook?.Disable();
                CloseAddonHook?.Dispose();
                CloseAddonHook = null;

                PassportCheckerReborn.Log.Debug("[PFWindowManager] Action interception hooks disposed");
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Error($"[PFWindowManager] Failed to dispose action hooks: {ex}");
            }
        }

        private static int TrackedPartyMemberCount;

        private static int SuppressionObservedCount;

        private static void OnDeferredPartyCountSync(IFramework framework)
        {
            PassportCheckerReborn.Framework.Update -= OnDeferredPartyCountSync;

            // Prefer a fresh count, but fall back to the count that was observed when
            // suppression triggered
            var freshCount = PartyFinderManager.GetEffectivePartyCount();
            TrackedPartyMemberCount = freshCount > 0 ? freshCount : SuppressionObservedCount;
        }

        private static unsafe bool CloseAddonDetour(AtkUnitBase* unitBase, bool a1)
        {
            if (Player.Available)
            {
                try
                {
                    var addonName = unitBase->NameString;

                    // User-initiated close: always allow and clean up suppression state
                    if (a1 == false && (addonName == "LookingForGroupDetail" || addonName == "LookingForGroup"))
                    {
                        // Sync tracked count to current count to prevent suppression loops
                        var currentCount = PartyFinderManager.GetEffectivePartyCount();
                        TrackedPartyMemberCount = currentCount;
                        SuppressionObservedCount = 0;

                        // Clean up any pending deferred sync callbacks
                        PassportCheckerReborn.Framework.Update -= OnDeferredPartyCountSync;

                        PassportCheckerReborn.Log.Information(
                            $"[PFWindowManager] User-initiated close on {addonName} " +
                            $"(synced trackedCount to {currentCount}).");

                        return CloseAddonHook!.Original(unitBase, a1);
                    }

                    // Game-triggered close: potentially suppress if party changed
                    if (a1 == true && (addonName == "LookingForGroupDetail" || addonName == "LookingForGroup"))
                    {
                        var currentCount = PartyFinderManager.GetEffectivePartyCount();
                        PassportCheckerReborn.Log.Information(
                            $"[PFWindowManager] Close called on {addonName} " +
                            $"(a1={a1}, config={Plugin?.Configuration.PreventAutoClosingOnPartyChanges2}, " +
                            $"detailOpen={PartyFinderManager?.IsDetailOpen}, listOpen={PartyFinderManager?.IsListOpen}, " +
                            $"trackedCount={TrackedPartyMemberCount}, effectiveCount={currentCount})");

                        if (Plugin?.Configuration.PreventAutoClosingOnPartyChanges2 == true
                            && PartyFinderManager?.IsDetailOpen == true)
                        {
                            // Suppress if the party count changed OR if another PF addon
                            // close in the same frame already triggered suppression (the
                            // game fires Close on both addons in one pass).
                            // Never suppress when the effective count is 0 — that means the
                            // party disbanded or the player left entirely, so there is no
                            // active party left to justify keeping PF open.  Continued
                            // suppression in that state would lock the user out of closing
                            // the window manually.
                            if (currentCount > 0 && currentCount != TrackedPartyMemberCount)
                            {
                                SuppressionObservedCount = currentCount;
                                PassportCheckerReborn.Framework.Update -= OnDeferredPartyCountSync;
                                PassportCheckerReborn.Framework.Update += OnDeferredPartyCountSync;

                                PassportCheckerReborn.Log.Information(
                                    $"[PFWindowManager] SUPPRESSING close for {addonName} " +
                                    $"(party: {TrackedPartyMemberCount} → {currentCount}).");
                                return false;
                            }

                            // If suppression was active but the party is now gone, clean up
                            // the deferred sync so it doesn't fire stale.
                            if (currentCount == 0)
                            {
                                SuppressionObservedCount = 0;
                                PassportCheckerReborn.Framework.Update -= OnDeferredPartyCountSync;
                            }

                            PassportCheckerReborn.Log.Information($"[PFWindowManager] Allowing close for {addonName} (currentCount={currentCount}, tracked={TrackedPartyMemberCount}).");
                            TrackedPartyMemberCount = currentCount;
                            return CloseAddonHook!.Original(unitBase, a1);
                        }
                    }

                }
                catch (Exception)
                {

                }
            }

            if (CloseAddonHook?.Original != null)
            {
                return CloseAddonHook.Original(unitBase, a1);
            }

            return true;
        }
    }
}
