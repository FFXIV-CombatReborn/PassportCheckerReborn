using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using System;

namespace PassportCheckerReborn.PartyFinder;

public sealed partial class PartyFinderManager
{
    private static readonly string[] HighEndKeywords = ["savage", "ultimate", "extreme", "criterion", "unreal", "minstrel's ballad"];

    public uint CurrentDutyId { get; private set; }

    public bool IsHighEndDuty { get; private set; }

    public string CurrentDutyName { get; private set; } = string.Empty;

    public string CurrentDutyNameEnglish { get; private set; } = string.Empty;

    public string LookupDutyName => string.IsNullOrWhiteSpace(CurrentDutyNameEnglish) ? CurrentDutyName : CurrentDutyNameEnglish;

    private unsafe void ReadDutyNameFromAddon()
    {
        CurrentDutyName = string.Empty;

        var addonPtr = Plugin.GameGui.GetAddonByName("LookingForGroupDetail", 1);
        if (addonPtr.IsNull)
        {
            return;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (addon->AtkValuesCount <= 15)
        {
            return;
        }

        var value = addon->AtkValues[15];
        if (value.Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString && value.String.HasValue)
        {
            CurrentDutyName = new ReadOnlySeStringSpan(value.String.AsSpan()).ExtractText();
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

        var duty = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>().GetRowOrDefault(post.DutyId);
        IsHighEndDuty = duty?.HighEndDuty == true
            || post.Category.HasFlag(AgentLookingForGroup.DutyCategory.HighEndDuty)
            || HasHighEndKeyword(CurrentDutyNameEnglish);
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
            var name = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>(language)
                .GetRowOrDefault(dutyId)?.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[PartyFinderManager] Failed to get the duty name for id {dutyId}.");
            return null;
        }
    }
}
