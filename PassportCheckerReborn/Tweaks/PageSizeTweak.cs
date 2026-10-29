using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using System;

namespace PassportCheckerReborn.Tweaks;

public sealed unsafe class PageSizeTweak : IDisposable
{
    private const int PageSizeOffset = 0x480;
    private const int DefaultPageSize = 50;

    private const int ExpandedPageSize = 100;

    public PageSizeTweak()
    {
        Plugin.ClientState.Login += ApplySetting;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "LookingForGroup", OnPFListPreSetup);

        ApplySetting();
    }

    public bool Unavailable { get; private set; }

    public void Dispose()
    {
        Plugin.ClientState.Login -= ApplySetting;
        Plugin.AddonLifecycle.UnregisterListener(OnPFListPreSetup);

        WritePageSize(DefaultPageSize);
    }

    public void ApplySetting()
    {
        WritePageSize(Plugin.Configuration.ExpandListingsTo100PerPage ? ExpandedPageSize : DefaultPageSize);
    }

    private void OnPFListPreSetup(AddonEvent type, AddonArgs args)
    {
        ApplySetting();
    }

    private void WritePageSize(int pageSize)
    {
        if (Unavailable)
        {
            return;
        }

        var proxy = InfoProxyCrossRealm.Instance();
        if (proxy == null)
        {
            return;
        }

        var current = (int*)((byte*)proxy + PageSizeOffset);
        if (*current == pageSize)
        {
            return;
        }

        if (*current != DefaultPageSize && *current != ExpandedPageSize)
        {
            Plugin.Log.Warning($"[PageSizeTweak] Unexpected Party Finder page size {*current}; leaving it alone.");
            Unavailable = true;
            return;
        }

        *current = pageSize;
    }
}
