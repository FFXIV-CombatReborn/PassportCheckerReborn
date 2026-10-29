using System;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private static readonly M3Segment[] TimeSortSegments =
    [
        new("Newest first", FontAwesomeIcon.SortAmountDown),
        new("Oldest first", FontAwesomeIcon.SortAmountUp),
    ];

    private void DrawGeneralPage()
    {
        var cfg = Configuration;

        using (M3Card.Begin("general_details", "Listing details", FontAwesomeIcon.AddressCard))
        {
            var highlight = cfg.SpecialBorderColorForKnownPlayers;
            if (M3Widgets.RowSwitch("Highlight known players", ref highlight, "Tints known players' rows in the member info overlay."))
            {
                cfg.SpecialBorderColorForKnownPlayers = highlight;
                cfg.Save();
            }

            if (cfg.SpecialBorderColorForKnownPlayers)
            {
                using var group = M3SubGroup.Begin();
                var color = cfg.KnownPlayerBorderColor;
                if (M3Widgets.RowColor("Highlight color", ref color, Configuration.DefaultKnownPlayerBorderColor, alpha: false))
                {
                    cfg.KnownPlayerBorderColor = color;
                    savePending = true;
                }
            }

            var jobIcons = cfg.ShowPartyJobIcons;
            if (M3Widgets.RowSwitch("Show job icons", ref jobIcons, "Shows each player's job icon in the member info and party list overlays."))
            {
                cfg.ShowPartyJobIcons = jobIcons;
                cfg.Save();
            }

            var keepOpen = cfg.PreventAutoClosingOnPartyChanges2;
            if (M3Widgets.RowSwitch("Keep the listing open when your party changes", ref keepOpen,
                    WhenAvailable("Stops the Party Finder closing itself when your party changes, and reloads the listing you were viewing.",
                        plugin.KeepOpen.Unavailable),
                    enabled: !plugin.KeepOpen.Unavailable))
            {
                cfg.PreventAutoClosingOnPartyChanges2 = keepOpen;
                cfg.Save();
                plugin.KeepOpen.ApplySetting();
            }
        }

        using (M3Card.Begin("general_listings", "Listings", FontAwesomeIcon.ListUl))
        {
            var timeSort = plugin.TimeSort;
            var pageSize = plugin.PageSize;
            var partyFinder = plugin.PartyFinderManager;

            var sorting = cfg.EnableTrueTimeBasedSorting;
            if (M3Widgets.RowSwitch("True time-based sorting", ref sorting,
                    WhenAvailable("Orders each page by the time listings have left, instead of grouping them by duty.",
                        timeSort.Unavailable),
                    enabled: !timeSort.Unavailable))
            {
                cfg.EnableTrueTimeBasedSorting = sorting;
                cfg.Save();
                timeSort.ApplySetting();
                partyFinder.RefreshListings();
            }

            if (cfg.EnableTrueTimeBasedSorting && !timeSort.Unavailable)
            {
                using var group = M3SubGroup.Begin();
                var order = SegmentedRow("Order", "The Party Finder's own sort button flips it.",
                    "time_sort_order", TimeSortSegments, cfg.TimeSortNewestFirst ? 0 : 1);
                if (order >= 0)
                {
                    cfg.TimeSortNewestFirst = order == 0;
                    cfg.Save();
                    partyFinder.RefreshListings();
                }
            }

            var expand = cfg.ExpandListingsTo100PerPage;
            if (M3Widgets.RowSwitch("Show 100 listings per page", ref expand,
                    WhenAvailable("Asks the server for 100 listings at a time instead of 50.", pageSize.Unavailable),
                    enabled: !pageSize.Unavailable))
            {
                cfg.ExpandListingsTo100PerPage = expand;
                cfg.Save();
                pageSize.ApplySetting();
                partyFinder.RefreshListings();
            }

            var autoRefresh = cfg.EnableAutomaticRefresh;
            if (M3Widgets.RowSwitch("Refresh listings automatically", ref autoRefresh, "Reloads the Party Finder list on a timer while you're browsing it."))
            {
                cfg.EnableAutomaticRefresh = autoRefresh;
                cfg.Save();
            }

            if (cfg.EnableAutomaticRefresh)
            {
                using var group = M3SubGroup.Begin();
                var interval = (float)cfg.AutoRefreshIntervalSeconds;
                if (M3Widgets.RowDragFloat("Refresh interval", ref interval, 10f, 120f, "%.0f s", step: 1f))
                {
                    cfg.AutoRefreshIntervalSeconds = (int)MathF.Round(interval);
                    savePending = true;
                }
            }

            var jobFilter = cfg.EnableOneClickJobFilter;
            if (M3Widgets.RowSwitch("One-click job filter button", ref jobFilter,
                    "Adds a button above the Party Finder that hides high-end duty listings with no open slot for your current job."))
            {
                cfg.EnableOneClickJobFilter = jobFilter;
                cfg.Save();

                if (!jobFilter && plugin.JobFilter.Active)
                {
                    plugin.JobFilter.Toggle();
                }
            }

            var rightClick = cfg.RightClickPlayerNameForRecruitment3;
            if (M3Widgets.RowSwitch("Right-click a name to view their recruitment", ref rightClick,
                    "Adds View Recruitment to a player's context menu, which opens their Party Finder listing."))
            {
                cfg.RightClickPlayerNameForRecruitment3 = rightClick;
                cfg.Save();
                plugin.RecruitmentMenu.ApplySetting();
            }
        }

        using (M3Card.Begin("general_blacklist", "Blacklist", FontAwesomeIcon.UserSlash))
        {
            var blacklist = cfg.EnableBlacklistFeature;
            if (M3Widgets.RowSwitch("Flag blacklisted players", ref blacklist, "Marks players on your in-game blacklist with a BL tag in the member info overlay."))
            {
                cfg.EnableBlacklistFeature = blacklist;
                cfg.Save();
            }

            if (ButtonRow("Refresh blacklist", "Re-reads your blacklist from the game and saves it.",
                    "##blacklist_refresh", "Refresh", FontAwesomeIcon.Sync))
            {
                plugin.BlacklistMonitor.Refresh();
            }
        }
    }
}
