using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace PassportCheckerReborn;

/// <summary>
/// Determines where the Party List Overlay is positioned relative to the in-game party list.
/// </summary>
public enum PartyListOverlayPosition
{
    Left,
    Right,
    Above,
    Below,
    Unbound,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ── General ─ Party Finder Detail Optimizations ─────────────────────────
    public bool SpecialBorderColorForKnownPlayers { get; set; } = false;
    public Vector4 KnownPlayerBorderColor { get; set; } = new Vector4(0.2f, 0.8f, 0.2f, 1.0f);
    public bool ShowPartyJobIcons { get; set; } = true;
    public bool PreventAutoClosingOnPartyChanges { get; set; } = false;

    // ── General ─ Party Finder List Optimizations ────────────────────────────
    public bool EnableTrueTimeBasedSorting { get; set; } = false;
    public bool ExpandListingsTo100PerPage { get; set; } = false;
    public bool EnableAutomaticRefresh { get; set; } = false;
    public int AutoRefreshIntervalSeconds { get; set; } = 30;
    public bool EnableOneClickJobFilter { get; set; } = false;
    public bool RightClickPlayerNameForRecruitment3 { get; set; } = false;

    // ── Overlay ───────────────────────────────────────────────────────────────
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

    // ── Blacklist ─────────────────────────────────────────────────────────────
    public bool EnableBlacklistFeature { get; set; } = true;

    // ── Tomestone Integration ────────────────────────────────────────────────
    public string TomestoneApiKey { get; set; } = string.Empty;

    // ── FFLogs Integration ───────────────────────────────────────────────────
    public string FFLogsClientId { get; set; } = string.Empty;
    public string FFLogsClientSecret { get; set; } = string.Empty;

    public void Save()
    {
        PassportCheckerReborn.PluginInterface.SavePluginConfig(this);
    }
}
