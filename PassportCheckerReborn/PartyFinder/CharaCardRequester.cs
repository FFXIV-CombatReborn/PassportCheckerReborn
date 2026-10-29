using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn.PartyFinder;

public sealed class CharaCardRequester : IDisposable
{
    private const int ThrottleMs = 900;

    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly Hook<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>? charaCardPacketHook;
    private volatile CharaCardRequest? currentRequest;

    public unsafe CharaCardRequester(Plugin plugin)
    {
        try
        {
            charaCardPacketHook = Plugin.GameInteropProvider.HookFromAddress<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>(
                CharaCard.Addresses.HandleCurrentCharaCardDataPacket.Value,
                CharaCardPacketDetour);
            charaCardPacketHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[CharaCardRequester] Failed to hook HandleCurrentCharaCardDataPacket.");
        }

        plugin.LogMessages.Intercept(OnLogMessage);
    }

    public bool IsAvailable => charaCardPacketHook is { IsEnabled: true };

    public void Dispose()
    {
        charaCardPacketHook?.Dispose();
    }

    public async Task<CharaCardResult?> RequestAsync(ulong contentId, CancellationToken ct)
    {
        await requestGate.WaitAsync(ct);
        try
        {
            await Task.Delay(ThrottleMs, ct);

            var request = new CharaCardRequest(contentId, new(TaskCreationOptions.RunContinuationsAsynchronously));
            currentRequest = request;

            await Plugin.Framework.RunOnFrameworkThread(() =>
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
                return await request.Completion.Task.WaitAsync(ResponseTimeout, ct);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
        finally
        {
            currentRequest = null;
            requestGate.Release();
        }
    }

    private unsafe void CharaCardPacketDetour(CharaCard* thisPtr, CharaCardPacket* packet)
    {
        try
        {
            if (currentRequest is { } request && request.ContentId == packet->ContentId)
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
            Plugin.Log.Warning(ex, "[CharaCardRequester] CharaCard detour error.");
        }

        charaCardPacketHook!.Original(thisPtr, packet);
    }

    private bool OnLogMessage(uint logMessageId)
    {
        // 5855-5860: the adventure plate asked for is hidden or unavailable
        if (logMessageId is > 5854 and < 5861 && currentRequest is { } request)
        {
            request.Completion.TrySetResult(new CharaCardResult(string.Empty, 0, IsHidden: true));
            return true;
        }

        return false;
    }

    private sealed record CharaCardRequest(ulong ContentId, TaskCompletionSource<CharaCardResult?> Completion);
}
