using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using System;
using System.Collections.Generic;

namespace PassportCheckerReborn.Players;

public sealed class BlacklistMonitor : IDisposable
{
    private readonly Plugin plugin;

    public BlacklistMonitor(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "BlackList", OnBlacklistAddonUpdated);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "BlackList", OnBlacklistAddonUpdated);

        Refresh();
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(OnBlacklistAddonUpdated);
    }

    public bool IsBlacklisted(string name, string world)
    {
        if (!Plugin.Configuration.EnableBlacklistFeature || string.IsNullOrEmpty(name))
        {
            return false;
        }

        var blacklist = plugin.BlacklistCache;
        return (!string.IsNullOrEmpty(world) && blacklist.Contains($"{name}@{world}")) || blacklist.Contains(name);
    }

    public unsafe void Refresh()
    {
        if (!Plugin.ClientState.IsLoggedIn)
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
            Plugin.Log.Warning(ex, "[BlacklistMonitor] Failed to read the blacklist.");
        }
    }

    private void OnBlacklistAddonUpdated(AddonEvent type, AddonArgs args)
    {
        Refresh();
    }
}
