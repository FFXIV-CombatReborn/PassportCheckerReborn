using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using System;

namespace PassportCheckerReborn.Tweaks;

public sealed class AutoRefreshTweak : IDisposable
{
    private const int MinAutoRefreshSeconds = 10;

    private readonly Plugin plugin;
    private long nextAutoRefreshAt;

    public AutoRefreshTweak(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "LookingForGroup", OnPFListRefresh);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.AddonLifecycle.UnregisterListener(OnPFListRefresh);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var partyFinder = plugin.PartyFinderManager;
        if (!Plugin.Configuration.EnableAutomaticRefresh || !partyFinder.IsListOpen || partyFinder.IsDetailOpen)
        {
            nextAutoRefreshAt = 0;
            return;
        }

        var now = Environment.TickCount64;
        if (nextAutoRefreshAt == 0)
        {
            nextAutoRefreshAt = now + (Math.Max(MinAutoRefreshSeconds, Plugin.Configuration.AutoRefreshIntervalSeconds) * 1000L);
        }
        else if (now >= nextAutoRefreshAt)
        {
            nextAutoRefreshAt = 0;
            partyFinder.RefreshListings();
        }
    }

    private void OnPFListRefresh(AddonEvent type, AddonArgs args)
    {
        nextAutoRefreshAt = 0;
    }
}
