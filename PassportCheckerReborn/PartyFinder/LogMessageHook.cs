using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System;
using System.Collections.Generic;

namespace PassportCheckerReborn.PartyFinder;

public sealed unsafe class LogMessageHook : IDisposable
{
    private readonly List<Func<uint, bool>> interceptors = [];
    private readonly Hook<RaptureLogModule.Delegates.ShowLogMessage>? showLogMessageHook;

    public LogMessageHook()
    {
        try
        {
            showLogMessageHook = Plugin.GameInteropProvider.HookFromAddress<RaptureLogModule.Delegates.ShowLogMessage>(
                RaptureLogModule.Addresses.ShowLogMessage.Value,
                ShowLogMessageDetour);
            showLogMessageHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[LogMessageHook] Failed to hook ShowLogMessage.");
        }
    }

    public void Dispose()
    {
        showLogMessageHook?.Dispose();
    }

    public void Intercept(Func<uint, bool> interceptor)
    {
        interceptors.Add(interceptor);
    }

    private void ShowLogMessageDetour(RaptureLogModule* thisPtr, uint logMessageId)
    {
        foreach (var interceptor in interceptors)
        {
            if (interceptor(logMessageId))
            {
                return;
            }
        }

        showLogMessageHook!.Original(thisPtr, logMessageId);
    }
}
