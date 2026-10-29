using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private static readonly M3Segment[] OverlaySideSegments =
    [
        new("Left", FontAwesomeIcon.ArrowLeft),
        new("Right", FontAwesomeIcon.ArrowRight),
    ];

    private void DrawOverlaysPage()
    {
        var cfg = Configuration;

        using (M3Card.Begin("overlays_member_info", "Member info overlay", FontAwesomeIcon.Users,
                   subtitle: "Appears beside a Party Finder listing's details."))
        {
            var show = cfg.ShowMemberInfoOverlay;
            if (M3Widgets.RowSwitch("Show member info overlay", ref show))
            {
                cfg.ShowMemberInfoOverlay = show;
                cfg.Save();
            }

            if (cfg.ShowMemberInfoOverlay)
            {
                using var group = M3SubGroup.Begin();

                var highEnd = cfg.OnlyShowOverlayForHighEndDuties;
                if (M3Widgets.RowSwitch("High-end duties only", ref highEnd, "Hides the overlay for listings that aren't high-end duties."))
                {
                    cfg.OnlyShowOverlayForHighEndDuties = highEnd;
                    cfg.Save();
                }

                var side = SegmentedRow("Side", "Which side of the listing details the overlay sits on.",
                    "overlay_side", OverlaySideSegments, cfg.ShowOverlayOnLeftSide ? 0 : 1);
                if (side >= 0)
                {
                    cfg.ShowOverlayOnLeftSide = side == 0;
                    cfg.Save();
                }

                var resolvedNames = cfg.ShowResolvedPlayerNames;
                if (M3Widgets.RowSwitch("Show resolved player names", ref resolvedNames,
                        "Shows Name@World once a player is resolved, instead of \"Player 1\"."))
                {
                    cfg.ShowResolvedPlayerNames = resolvedNames;
                    cfg.Save();
                }
            }
        }

        if (cfg.ShowPartyListOverlay && !cfg.EnableFFLogsIntegrationOverlay && !cfg.EnableTomestoneIntegration)
        {
            M3Widgets.Banner("##party_list_needs_source",
                "The party list overlay stays hidden until FFLogs or Tomestone is turned on.",
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        using (M3Card.Begin("overlays_party_list", "Party list overlay", FontAwesomeIcon.UserFriends,
                   subtitle: "FFLogs and Tomestone data for your current party, next to the party list. Includes a duty picker for encounter-specific lookups."))
        {
            var show = cfg.ShowPartyListOverlay;
            if (M3Widgets.RowSwitch("Show party list overlay", ref show, "You can also toggle it with /pcrparty."))
            {
                cfg.ShowPartyListOverlay = show;
                cfg.Save();
            }

            if (cfg.ShowPartyListOverlay)
            {
                using var group = M3SubGroup.Begin();

                var position = cfg.PartyListOverlayPosition;
                if (M3Widgets.RowCombo("Position", ref position,
                        "Where the overlay sits relative to the party list. Unbound lets you drag it anywhere."))
                {
                    cfg.PartyListOverlayPosition = position;
                    cfg.Save();
                }

                var hideInDuty = cfg.HidePartyListInDuty;
                if (M3Widgets.RowSwitch("Hide in duties", ref hideInDuty))
                {
                    cfg.HidePartyListInDuty = hideInDuty;
                    cfg.Save();
                }

                var hideInCombat = cfg.HidePartyListInCombat;
                if (M3Widgets.RowSwitch("Hide in combat", ref hideInCombat))
                {
                    cfg.HidePartyListInCombat = hideInCombat;
                    cfg.Save();
                }
            }
        }
    }
}
