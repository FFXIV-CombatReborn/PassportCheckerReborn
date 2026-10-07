using Dalamud.Configuration;
using System;
using System.Numerics;

namespace PassportCheckerReborn;

public enum PartyListOverlayPosition
{
    Left,
    Right,
    Above,
    Below,
    Unbound,
}

[Serializable]
public class Configuration : IPluginConfiguration, IM3Settings
{
    public static readonly Vector4 DefaultKnownPlayerBorderColor = new(0.2f, 0.8f, 0.2f, 1.0f);

    public int Version { get; set; } = 1;

    public bool SpecialBorderColorForKnownPlayers { get; set; } = false;
    public Vector4 KnownPlayerBorderColor { get; set; } = DefaultKnownPlayerBorderColor;
    public bool ShowPartyJobIcons { get; set; } = true;
    public bool PreventAutoClosingOnPartyChanges2 { get; set; } = false;

    public bool EnableTrueTimeBasedSorting { get; set; } = false;
    public bool TimeSortNewestFirst { get; set; } = true;
    public bool ExpandListingsTo100PerPage { get; set; } = false;
    public bool EnableAutomaticRefresh { get; set; } = false;
    public int AutoRefreshIntervalSeconds { get; set; } = 30;
    public bool EnableOneClickJobFilter { get; set; } = false;
    public bool RightClickPlayerNameForRecruitment3 { get; set; } = false;

    public bool ShowMemberInfoOverlay { get; set; } = true;
    public bool OnlyShowOverlayForHighEndDuties { get; set; } = true;
    public bool ShowOverlayOnLeftSide { get; set; } = true;
    public bool ShowResolvedPlayerNames { get; set; } = false;
    public bool EnableFFLogsIntegrationOverlay { get; set; } = false;
    public bool EnableTomestoneIntegration { get; set; } = true;
    public bool ShowPartyListOverlay { get; set; } = false;
    public PartyListOverlayPosition PartyListOverlayPosition { get; set; } = PartyListOverlayPosition.Left;
    public bool HidePartyListInDuty { get; set; } = true;
    public bool HidePartyListInCombat { get; set; } = true;

    public bool EnableBlacklistFeature { get; set; } = true;

    public string TomestoneApiKey { get; set; } = string.Empty;

    public string FFLogsClientId { get; set; } = string.Empty;
    public string FFLogsClientSecret { get; set; } = string.Empty;

    public Vector4 UiAccentColor { get; set; } = M3.DefaultSeed;
    public float UiTextScale { get; set; } = 1f;
    public float UiElementScale { get; set; } = 1f;
    public float UiPaddingScale { get; set; } = 1f;

    // Methods rather than properties, so they are not written to the config file.
    public bool HasFFLogsCredentials()
    {
        return !string.IsNullOrEmpty(FFLogsClientId) && !string.IsNullOrEmpty(FFLogsClientSecret);
    }

    public bool HasTomestoneKey()
    {
        return !string.IsNullOrEmpty(TomestoneApiKey);
    }

    public void Save()
    {
        PassportCheckerReborn.PluginInterface.SavePluginConfig(this);
    }
}
