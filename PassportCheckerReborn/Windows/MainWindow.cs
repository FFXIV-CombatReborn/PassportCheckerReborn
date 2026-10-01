using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Windows;

public partial class MainWindow : Window
{
    private enum Page
    {
        General,
        Overlays,
        FFLogs,
        Tomestone,
        Appearance,
        About,
    }

    private const string KofiUrl = "https://ko-fi.com/ltscombatreborn";
    private const string RepoUrl = "https://github.com/FFXIV-CombatReborn/PassportCheckerReborn";
    private const string FFLogsClientsUrl = "https://www.fflogs.com/api/clients/";
    private const string FFLogsExampleClientName = "PassportCheckerReborn";
    private const string FFLogsExampleRedirectUrl = "https://example.com/";
    private const string TomestoneAccountUrl = "https://tomestone.gg/profile/account";
    private const string Unavailable = "Not available on this game version; it needs a plugin update.";

    // Unscaled content width below which the navigation drawer collapses to an icon rail.
    private const float DrawerLayoutWidth = 620f;
    private const float DrawerWidth = 188f;
    private const float RailWidth = 84f;

    private const double CopiedFeedbackSeconds = 1.5;

    private static readonly string[] OverlayPositionNames = Enum.GetNames<PartyListOverlayPosition>();

    private static readonly M3Segment[] OverlaySideSegments =
    [
        new("Left", FontAwesomeIcon.ArrowLeft),
        new("Right", FontAwesomeIcon.ArrowRight),
    ];

    private static readonly M3Segment[] TimeSortSegments =
    [
        new("Newest first", FontAwesomeIcon.SortAmountDown),
        new("Oldest first", FontAwesomeIcon.SortAmountUp),
    ];

    private static readonly (string Name, Vector4 Color)[] AccentPresets =
    [
        ("Crimson (default)", M3.DefaultSeed),
        ("Sapphire", M3ColorMath.FromRgb(0x2F6DB5)),
        ("Teal", M3ColorMath.FromRgb(0x00897B)),
        ("Forest", M3ColorMath.FromRgb(0x3E8E41)),
        ("Amber", M3ColorMath.FromRgb(0xC98A00)),
        ("Violet", M3ColorMath.FromRgb(0x7E57C2)),
        ("Rose", M3ColorMath.FromRgb(0xC2185B)),
    ];

    private readonly PassportCheckerReborn plugin;

    private M3Style.Scope? theme;
    private Page page = Page.General;

    // Sliders and colour pickers change the config every frame while dragged, so the save waits
    // until they are let go.
    private bool savePending;

    private string fflogsClientIdInput;
    private string fflogsClientSecretInput;
    private string fflogsTestMessage = string.Empty;
    private bool fflogsTestSucceeded;
    private bool fflogsTestInProgress;
    private bool fflogsGuideExpanded;
    private string tomestoneApiKeyInput;
    private bool tomestoneGuideExpanded;

    private string? copiedId;
    private double copiedAt;

    public MainWindow(PassportCheckerReborn plugin)
        : base("Passport Checker Reborn – Settings###PassportCheckerRebornSettings", BaseFlags)
    {
        this.plugin = plugin;

        Size = DefaultSize;
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = DefaultSizeConstraints;

        // Dalamud offers these from the title bar, which this window no longer has.
        // TODO: add these to the top app bar element maybe
        AllowPinning = false;
        AllowClickthrough = false;

        fflogsClientIdInput = Configuration.FFLogsClientId;
        fflogsClientSecretInput = Configuration.FFLogsClientSecret;
        tomestoneApiKeyInput = Configuration.TomestoneApiKey;
    }

    private Configuration Configuration => plugin.Configuration;

    private static Vector4 Muted => M3.Alpha(M3.Scheme.OnSurfaceVariant, 0.9f);

    // M3SettingRow's horizontal padding, to line other content up with row text.
    private static float RowInset => 12f * M3.Scale;

    public override void OnOpen()
    {
        // A pin or click-through set back when the title bar offered them could no longer be undone.
        IsPinned = false;
        IsClickthrough = false;
    }

    // The theme goes on before ImGui.Begin, which draws the window's own background and rounding.
    public override void PreDraw()
    {
        theme = M3Style.Push();
        PrepareFold();
    }

    public override void PostDraw()
    {
        // Draw pops these as soon as Begin has them, but Draw is skipped when Begin returns false.
        PopFoldStyle();
        theme?.Dispose();
        theme = null;
    }

    public override void OnClose()
    {
        if (savePending)
        {
            Configuration.Save();
            savePending = false;
        }

        M3Motion.Reset();
        M3CardHost.Reset();
        RestoreOnClose();
    }

    public override void Draw()
    {
        PopFoldStyle();
        windowPos = ImGui.GetWindowPos();
        windowSize = ImGui.GetWindowSize();

        var folded = Folded;
        if (folded < 1f)
        {
            using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (1f - MathF.Min(1f, folded * 1.4f)));
            DrawBackdrop(windowRounding);

            var (openPos, openSize) = OpenRect();
            DrawContent(openPos, openSize);
        }

        DrawWindowBar();

        if (savePending && !ImGui.IsAnyItemActive())
        {
            Configuration.Save();
            savePending = false;
        }
    }

    // Laid out at the window's open rect even while it folds, so the window shrinks over the page
    // rather than the page reflowing to fit it.
    private void DrawContent(Vector2 openPos, Vector2 openSize)
    {
        ImGui.SetCursorScreenPos(openPos + openPadding);
        using var content = ImRaii.Child("##pcr_window_content", Vector2.Max(Vector2.One, openSize - (openPadding * 2f)), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground);
        if (!content)
        {
            return;
        }

        var scale = M3.Scale;
        var available = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var navWidth = (available.X >= DrawerLayoutWidth * scale ? DrawerWidth : RailWidth) * scale;

        var origin = ImGui.GetCursorScreenPos();
        var dividerX = origin.X + navWidth + (spacing * 0.5f);
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(dividerX, origin.Y),
            new Vector2(dividerX, origin.Y + available.Y),
            M3.U32(M3.Scheme.OutlineVariant, 0.45f), 1f * scale);

        DrawNavigation(navWidth);
        ImGui.SameLine(0f, spacing);
        DrawBody();
    }

    private static void DrawBackdrop(float rounding)
    {
        var s = M3.Scheme;
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var top = M3.Alpha(s.SurfaceContainer, 0.65f);

        var min = pos;
        var max = new Vector2(pos.X + size.X, pos.Y + MathF.Min(200f * M3.Scale, size.Y));
        rounding = MathF.Min(rounding, (max.Y - min.Y) * 0.5f);

        drawList.PushClipRect(pos, pos + size, false);
        if (rounding > 0f)
        {
            drawList.AddRectFilled(min, new Vector2(max.X, min.Y + rounding), M3.U32(top), rounding, ImDrawFlags.RoundCornersTop);
        }

        M3Draw.VerticalGradient(drawList, new Vector2(min.X, min.Y + rounding), max, top, M3.Alpha(s.Surface, 0f));
        drawList.PopClipRect();
    }

    private void DrawNavigation(float width)
    {
        using var child = ImRaii.Child("##pcr_nav", new Vector2(width, -1f), false, ImGuiWindowFlags.NoScrollbar);
        if (!child)
        {
            return;
        }

        var expanded = ImGui.GetContentRegionAvail().X >= M3Navigation.DrawerBreakpoint * M3.Scale;

        DrawBrand(expanded);
        ImGui.Dummy(new Vector2(0f, M3.Space2));

        var clicked = M3Navigation.Draw("pcr_nav", BuildNavItems(), expanded);
        if (clicked != null && Enum.TryParse<Page>(clicked, out var target))
        {
            page = target;
        }

        if (expanded)
        {
            DrawVersionFooter();
        }
    }

    private List<M3NavItem> BuildNavItems()
    {
        var cfg = Configuration;
        var fflogsNeedsSetup = cfg.EnableFFLogsIntegrationOverlay && !cfg.HasFFLogsCredentials();
        var tomestoneNeedsSetup = cfg.EnableTomestoneIntegration && !cfg.HasTomestoneKey();

        return
        [
            new(nameof(Page.General), "General", FontAwesomeIcon.SlidersH, page == Page.General,
                "Party Finder quality-of-life options"),
            new(nameof(Page.Overlays), "Overlays", FontAwesomeIcon.LayerGroup, page == Page.Overlays,
                "Member info and party list overlays"),
            new(nameof(Page.FFLogs), "FFLogs", FontAwesomeIcon.ChartBar, page == Page.FFLogs,
                "FFLogs integration and API client", Badge: fflogsNeedsSetup ? "!" : null),
            new(nameof(Page.Tomestone), "Tomestone", FontAwesomeIcon.Gem, page == Page.Tomestone,
                "Tomestone.gg integration and API key", Badge: tomestoneNeedsSetup ? "!" : null, SeparatorAfter: true),
            new(nameof(Page.Appearance), "Appearance", FontAwesomeIcon.Palette, page == Page.Appearance,
                "Accent color, text and element size"),
            new(nameof(Page.About), "About", FontAwesomeIcon.InfoCircle, page == Page.About,
                "Commands, caches and links"),
        ];
    }

    private void DrawBrand(bool expanded)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var badge = 40f * scale;
        var height = badge + (8f * scale);

        var pressed = ImGui.InvisibleButton("##pcr_brand", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        if (hovered)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, M3.StateHover), M3.ShapeMedium);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var center = expanded
            ? new Vector2(min.X + (8f * scale) + (badge * 0.5f), min.Y + (height * 0.5f))
            : new Vector2(min.X + (width * 0.5f), min.Y + (height * 0.5f));
        var half = new Vector2(badge * 0.5f);
        if (Logo is { } logo)
        {
            drawList.AddImage(logo.Handle, center - half, center + half, Vector2.Zero, Vector2.One, M3.U32(Vector4.One));
        }
        else
        {
            drawList.AddCircleFilled(center, badge * 0.5f, M3.U32(s.PrimaryContainer), 32);
            M3Draw.IconCentered(drawList, FontAwesomeIcon.Passport, center - half, center + half, s.OnPrimaryContainer);
        }

        if (expanded)
        {
            const string tagline = "REBORN";
            var textX = center.X + (badge * 0.5f) + (10f * scale);
            var textWidth = MathF.Max(16f * scale, max.X - textX - (4f * scale));
            var gap = 1f * scale;

            string name;
            Vector2 nameSize;
            using (ImRaii.PushFont(M3.TitleMedium))
            {
                name = M3Navigation.Truncate("Passport Checker", textWidth);
                nameSize = ImGui.CalcTextSize(name);
            }

            Vector2 taglineSize;
            using (ImRaii.PushFont(M3.LabelSmall))
            {
                taglineSize = ImGui.CalcTextSize(tagline);
            }

            var textTop = min.Y + ((height - nameSize.Y - gap - taglineSize.Y) * 0.5f);
            using (ImRaii.PushFont(M3.TitleMedium))
            {
                drawList.AddText(new Vector2(textX, textTop), M3.U32(s.OnSurface, 0.98f), name);
            }

            using (ImRaii.PushFont(M3.LabelSmall))
            {
                drawList.AddText(new Vector2(textX, textTop + nameSize.Y + gap), M3.U32(s.Primary), tagline);
            }
        }

        if (hovered)
        {
            ImguiTooltips.ShowTooltip("About Passport Checker Reborn");
        }

        if (pressed)
        {
            page = Page.About;
        }
    }

    private void DrawVersionFooter()
    {
        var label = $"v{PassportCheckerReborn.Version}";
        var size = M3Widgets.PillSize(label, FontAwesomeIcon.CodeBranch);
        var bottom = ImGui.GetCursorPosY() + ImGui.GetContentRegionAvail().Y - size.Y;
        ImGui.SetCursorPosY(MathF.Max(ImGui.GetCursorPosY() + M3.Space2, bottom));
        _ = M3Widgets.Pill("##pcr_version", label, M3.Scheme.OnSurfaceVariant, FontAwesomeIcon.CodeBranch, "Installed version");
    }

    private void DrawBody()
    {
        using var body = ImRaii.Child("##pcr_body", new Vector2(-1f, -1f), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!body)
        {
            return;
        }

        DrawTopAppBar();

        // One child per page, so each page keeps its own scroll position.
        using var content = ImRaii.Child($"##pcr_page_{page}", new Vector2(-1f, -1f), false);
        if (!content)
        {
            return;
        }

        switch (page)
        {
            case Page.General:
                DrawGeneralPage();
                break;
            case Page.Overlays:
                DrawOverlaysPage();
                break;
            case Page.FFLogs:
                DrawFFLogsPage();
                break;
            case Page.Tomestone:
                DrawTomestonePage();
                break;
            case Page.Appearance:
                DrawAppearancePage();
                break;
            case Page.About:
                DrawAboutPage();
                break;
        }

        ImGui.Dummy(new Vector2(0f, M3.Space2));
    }

    private static (string Title, string Subtitle) PageHeading(Page page) => page switch
    {
        Page.General => ("General", "Party Finder quality-of-life options"),
        Page.Overlays => ("Overlays", "What the member info and party list overlays show"),
        Page.FFLogs => ("FFLogs", "Clears and parses from FFLogs"),
        Page.Tomestone => ("Tomestone", "Prog points and clears from Tomestone.gg"),
        Page.Appearance => ("Appearance", "How the plugin's windows look"),
        _ => ("About", "Commands, caches and links"),
    };

    private void DrawTopAppBar()
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var (title, subtitle) = PageHeading(page);
        var fullWidth = MathF.Max(64f * scale, ImGui.GetContentRegionAvail().X);

        // Minimize and close always show; the other actions leave room for a few characters of the title.
        var shown = WindowActions.Length;
        while (shown > 0 && M3Widgets.WindowActionsSize(shown, Brand, 0f).X + (96f * scale) > fullWidth)
        {
            shown--;
        }

        var barSize = M3Widgets.WindowActionsSize(shown, Brand, 0f);
        var width = MathF.Max(32f * scale, fullWidth - barSize.X - (12f * scale));

        // Measured rather than placed at fixed offsets: the game font's line height does not track the UI scale.
        var padTop = 6f * scale;
        var padBottom = 8f * scale;
        var lineGap = 2f * scale;

        string clippedTitle;
        Vector2 titleSize;
        using (ImRaii.PushFont(M3.HeadlineSmall))
        {
            clippedTitle = M3Navigation.Truncate(title, width);
            titleSize = ImGui.CalcTextSize(clippedTitle);
        }

        string clippedSubtitle;
        Vector2 subtitleSize;
        using (ImRaii.PushFont(M3.LabelSmall))
        {
            clippedSubtitle = M3Navigation.Truncate(subtitle, width);
            subtitleSize = ImGui.CalcTextSize(clippedSubtitle);
        }

        var contentHeight = titleSize.Y + lineGap + subtitleSize.Y;
        var height = MathF.Max(52f * scale, padTop + contentHeight + padBottom);
        ImGui.Dummy(new Vector2(fullWidth, height));

        shownActions = shown;
        barTop = (height - barSize.Y) * 0.5f;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var textTop = min.Y + ((height - contentHeight) * 0.5f);

        using (ImRaii.PushFont(M3.HeadlineSmall))
        {
            drawList.AddText(new Vector2(min.X, textTop), M3.U32(s.OnSurface, 0.98f), clippedTitle);
        }

        using (ImRaii.PushFont(M3.LabelSmall))
        {
            drawList.AddText(new Vector2(min.X, textTop + titleSize.Y + lineGap), M3.U32(s.OnSurfaceVariant, 0.88f), clippedSubtitle);
        }

        drawList.AddLine(new Vector2(min.X, max.Y), max, M3.U32(s.OutlineVariant, 0.5f), 1f * scale);
        ImGui.Dummy(new Vector2(fullWidth, M3.Space2));
    }

    private void DrawGeneralPage()
    {
        var cfg = Configuration;

        using (M3Card.Begin("general_details", "Listing details", FontAwesomeIcon.AddressCard))
        {
            var highlight = cfg.SpecialBorderColorForKnownPlayers;
            if (SwitchRow("Highlight known players", "Tints known players' rows in the member info overlay.", ref highlight))
            {
                cfg.SpecialBorderColorForKnownPlayers = highlight;
                cfg.Save();
            }

            if (cfg.SpecialBorderColorForKnownPlayers)
            {
                using var group = M3SubGroup.Begin();
                var color = cfg.KnownPlayerBorderColor;
                if (ColorRow("Highlight color", "##known_player_color", ref color))
                {
                    cfg.KnownPlayerBorderColor = color;
                    savePending = true;
                }
            }

            var jobIcons = cfg.ShowPartyJobIcons;
            if (SwitchRow("Show job icons", "Shows each player's job icon in the member info and party list overlays.", ref jobIcons))
            {
                cfg.ShowPartyJobIcons = jobIcons;
                cfg.Save();
            }

            var keepOpen = cfg.PreventAutoClosingOnPartyChanges2;
            if (SwitchRow("Keep the listing open when your party changes",
                    WhenAvailable("Stops the Party Finder closing itself when your party changes, and reloads the listing you were viewing.",
                        PFWindowManager.Unavailable),
                    ref keepOpen, enabled: !PFWindowManager.Unavailable))
            {
                cfg.PreventAutoClosingOnPartyChanges2 = keepOpen;
                cfg.Save();
                PFWindowManager.ApplySetting();
            }
        }

        using (M3Card.Begin("general_listings", "Listings", FontAwesomeIcon.ListUl))
        {
            var tweaks = plugin.PartyFinderListTweaks;

            var sorting = cfg.EnableTrueTimeBasedSorting;
            if (SwitchRow("True time-based sorting",
                    WhenAvailable("Orders each page by the time listings have left, instead of grouping them by duty.",
                        tweaks.SortingUnavailable),
                    ref sorting, enabled: !tweaks.SortingUnavailable))
            {
                cfg.EnableTrueTimeBasedSorting = sorting;
                cfg.Save();
                tweaks.ApplySorting();
                tweaks.RefreshListings();
            }

            if (cfg.EnableTrueTimeBasedSorting && !tweaks.SortingUnavailable)
            {
                using var group = M3SubGroup.Begin();
                var order = SegmentedRow("Order", "The Party Finder's own sort button flips it.",
                    "time_sort_order", TimeSortSegments, cfg.TimeSortNewestFirst ? 0 : 1);
                if (order >= 0)
                {
                    cfg.TimeSortNewestFirst = order == 0;
                    cfg.Save();
                    tweaks.RefreshListings();
                }
            }

            var expand = cfg.ExpandListingsTo100PerPage;
            if (SwitchRow("Show 100 listings per page",
                    WhenAvailable("Asks the server for 100 listings at a time instead of 50.", tweaks.PageSizeUnavailable),
                    ref expand, enabled: !tweaks.PageSizeUnavailable))
            {
                cfg.ExpandListingsTo100PerPage = expand;
                cfg.Save();
                tweaks.ApplyPageSize();
                tweaks.RefreshListings();
            }

            var autoRefresh = cfg.EnableAutomaticRefresh;
            if (SwitchRow("Refresh listings automatically", "Reloads the Party Finder list on a timer while you're browsing it.", ref autoRefresh))
            {
                cfg.EnableAutomaticRefresh = autoRefresh;
                cfg.Save();
            }

            if (cfg.EnableAutomaticRefresh)
            {
                using var group = M3SubGroup.Begin();
                var interval = cfg.AutoRefreshIntervalSeconds;
                if (SliderRow("Refresh interval", ref interval, 10, 120, " s"))
                {
                    cfg.AutoRefreshIntervalSeconds = interval;
                    savePending = true;
                }
            }

            var jobFilter = cfg.EnableOneClickJobFilter;
            if (SwitchRow("One-click job filter button",
                    "Adds a button above the Party Finder that hides high-end duty listings with no open slot for your current job.",
                    ref jobFilter))
            {
                cfg.EnableOneClickJobFilter = jobFilter;
                cfg.Save();

                // A filter left on would keep hiding listings with no button left to turn it off.
                if (!jobFilter && tweaks.JobFilterActive)
                {
                    tweaks.ToggleJobFilter();
                }
            }

            var rightClick = cfg.RightClickPlayerNameForRecruitment3;
            if (SwitchRow("Right-click a name to view their recruitment",
                    "Adds View Recruitment to a player's context menu, which opens their Party Finder listing.",
                    ref rightClick))
            {
                cfg.RightClickPlayerNameForRecruitment3 = rightClick;
                cfg.Save();
                plugin.PartyFinderManager.ApplyContextMenuSetting();
            }
        }

        using (M3Card.Begin("general_blacklist", "Blacklist", FontAwesomeIcon.UserSlash))
        {
            var blacklist = cfg.EnableBlacklistFeature;
            if (SwitchRow("Flag blacklisted players", "Marks players on your in-game blacklist with a BL tag in the member info overlay.", ref blacklist))
            {
                cfg.EnableBlacklistFeature = blacklist;
                cfg.Save();
            }

            if (ButtonRow("Refresh blacklist", "Re-reads your blacklist from the game and saves it.",
                    "##blacklist_refresh", "Refresh", FontAwesomeIcon.Sync))
            {
                plugin.PartyFinderManager.RefreshBlacklist();
            }
        }
    }

    private void DrawOverlaysPage()
    {
        var cfg = Configuration;

        using (M3Card.Begin("overlays_member_info", "Member info overlay", FontAwesomeIcon.Users,
                   subtitle: "Appears beside a Party Finder listing's details."))
        {
            var show = cfg.ShowMemberInfoOverlay;
            if (SwitchRow("Show member info overlay", null, ref show))
            {
                cfg.ShowMemberInfoOverlay = show;
                cfg.Save();
            }

            if (cfg.ShowMemberInfoOverlay)
            {
                using var group = M3SubGroup.Begin();

                var highEnd = cfg.OnlyShowOverlayForHighEndDuties;
                if (SwitchRow("High-end duties only", "Hides the overlay for listings that aren't high-end duties.", ref highEnd))
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
                if (SwitchRow("Show resolved player names",
                        "Shows Name@World once a player is resolved, instead of \"Player 1\".", ref resolvedNames))
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
            if (SwitchRow("Show party list overlay", "You can also toggle it with /pcrparty.", ref show))
            {
                cfg.ShowPartyListOverlay = show;
                cfg.Save();
            }

            if (cfg.ShowPartyListOverlay)
            {
                using var group = M3SubGroup.Begin();

                var position = (int)cfg.PartyListOverlayPosition;
                if (ComboRow("Position", "Where the overlay sits relative to the party list. Unbound lets you drag it anywhere.",
                        "##party_list_position", ref position, OverlayPositionNames, 150f * M3.Scale))
                {
                    cfg.PartyListOverlayPosition = (PartyListOverlayPosition)position;
                    cfg.Save();
                }

                var hideInDuty = cfg.HidePartyListInDuty;
                if (SwitchRow("Hide in duties", null, ref hideInDuty))
                {
                    cfg.HidePartyListInDuty = hideInDuty;
                    cfg.Save();
                }

                var hideInCombat = cfg.HidePartyListInCombat;
                if (SwitchRow("Hide in combat", null, ref hideInCombat))
                {
                    cfg.HidePartyListInCombat = hideInCombat;
                    cfg.Save();
                }
            }
        }
    }

    private void DrawFFLogsPage()
    {
        var cfg = Configuration;
        var s = M3.Scheme;

        if (cfg.EnableFFLogsIntegrationOverlay && !cfg.HasFFLogsCredentials())
        {
            M3Widgets.Banner("##fflogs_missing", "FFLogs is turned on, but no API client is saved yet. Add one below.",
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        using (M3Card.Begin("fflogs_integration", "Integration", FontAwesomeIcon.ChartBar))
        {
            var enabled = cfg.EnableFFLogsIntegrationOverlay;
            if (SwitchRow("Show FFLogs data in overlays",
                    "Adds an FFLogs column with clears and parses to the member info and party list overlays.", ref enabled))
            {
                cfg.EnableFFLogsIntegrationOverlay = enabled;
                cfg.Save();
            }
        }

        using (M3Card.Begin("fflogs_client", "API client", FontAwesomeIcon.Key,
                   subtitle: "Create a client on FFLogs, then paste its ID and secret here."))
        {
            TextFieldRow("Client ID", "##fflogs_client_id", "Paste your client ID", ref fflogsClientIdInput, 128);
            TextFieldRow("Client secret", "##fflogs_client_secret", "Paste your client secret", ref fflogsClientSecretInput, 128, password: true);

            var testing = fflogsTestInProgress;
            BeginActionRow();
            if (M3Widgets.Button("##fflogs_save", testing ? "Testing…" : "Save & test", M3ButtonStyle.Filled,
                    testing ? FontAwesomeIcon.HourglassHalf : FontAwesomeIcon.Plug, enabled: !testing))
            {
                SaveAndTestFFLogsCredentials();
            }

            var dirty = fflogsClientIdInput != cfg.FFLogsClientId || fflogsClientSecretInput != cfg.FFLogsClientSecret;
            if (testing)
            {
                StatusLine(FontAwesomeIcon.HourglassHalf, "Checking your credentials with FFLogs…", Muted);
            }
            else if (dirty)
            {
                StatusLine(FontAwesomeIcon.PencilAlt, "Unsaved changes.", s.Warning);
            }
            else if (!string.IsNullOrEmpty(fflogsTestMessage))
            {
                StatusLine(fflogsTestSucceeded ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.TimesCircle,
                    fflogsTestMessage, fflogsTestSucceeded ? s.Success : s.Error);
            }
        }

        using (var guide = M3ExpandableCard.Begin("fflogs_guide", "How to get API credentials", ref fflogsGuideExpanded,
                   FontAwesomeIcon.QuestionCircle))
        {
            if (guide.Expanded)
            {
                if (StepRow(1, "Open the FFLogs API clients page.", "##fflogs_step_open", "Open", FontAwesomeIcon.ExternalLinkAlt))
                {
                    OpenUrl(FFLogsClientsUrl);
                }

                StepRow(2, "Click \"Create Client\" in the top-right corner.");
                CopyStepRow(3, $"Enter a client name, such as {FFLogsExampleClientName}.", "##fflogs_step_name", FFLogsExampleClientName);
                CopyStepRow(4, $"Enter any redirect URL, such as {FFLogsExampleRedirectUrl}", "##fflogs_step_redirect", FFLogsExampleRedirectUrl);
                StepRow(5, "Leave \"Public Client\" unchecked.");
                StepRow(6, "Copy the generated client ID and secret into the fields above.");
                StepRow(7, "Click Save & test to check them.");
                StatusLine(FontAwesomeIcon.ShieldAlt, "The client secret is only shown once. Keep it private.", s.Warning);
            }
        }
    }

    private void SaveAndTestFFLogsCredentials()
    {
        Configuration.FFLogsClientId = fflogsClientIdInput;
        Configuration.FFLogsClientSecret = fflogsClientSecretInput;
        Configuration.Save();
        fflogsTestMessage = string.Empty;
        fflogsTestInProgress = true;

        _ = TestFFLogsCredentialsAsync();
    }

    private async Task TestFFLogsCredentialsAsync()
    {
        try
        {
            var accepted = await plugin.FFLogsService.TestCredentialsAsync(
                Configuration.FFLogsClientId,
                Configuration.FFLogsClientSecret);

            // The outcome is written before the message: the UI only reads the outcome once a message exists.
            fflogsTestSucceeded = accepted;
            fflogsTestMessage = accepted ? "FFLogs accepted these credentials." : "FFLogs rejected these credentials.";
        }
        catch (Exception ex)
        {
            fflogsTestSucceeded = false;
            fflogsTestMessage = $"The test failed: {ex.Message}";
            PassportCheckerReborn.Log.Warning(ex, "[PassportCheckerReborn] FFLogs credential test failed.");
        }
        finally
        {
            fflogsTestInProgress = false;
        }
    }

    private void DrawTomestonePage()
    {
        var cfg = Configuration;
        var s = M3.Scheme;

        if (cfg.EnableTomestoneIntegration && !cfg.HasTomestoneKey())
        {
            M3Widgets.Banner("##tomestone_missing", "Tomestone is turned on, but no API key is saved yet. Add one below.",
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        using (M3Card.Begin("tomestone_integration", "Integration", FontAwesomeIcon.Gem))
        {
            var enabled = cfg.EnableTomestoneIntegration;
            if (SwitchRow("Show Tomestone data in overlays",
                    "Adds a Tomestone column with prog points and clears. In the member info overlay, click Tomestone to look up the listing's duty.",
                    ref enabled))
            {
                cfg.EnableTomestoneIntegration = enabled;
                cfg.Save();
            }
        }

        using (M3Card.Begin("tomestone_key", "API access", FontAwesomeIcon.Key,
                   subtitle: "Your Tomestone.gg access token, sent with each lookup."))
        {
            TextFieldRow("API key", "##tomestone_api_key", "Paste your access token", ref tomestoneApiKeyInput, 256, password: true);

            BeginActionRow();
            if (M3Widgets.Button("##tomestone_save", "Save", M3ButtonStyle.Filled, FontAwesomeIcon.Save))
            {
                cfg.TomestoneApiKey = tomestoneApiKeyInput;
                cfg.Save();
            }

            if (tomestoneApiKeyInput != cfg.TomestoneApiKey)
            {
                StatusLine(FontAwesomeIcon.PencilAlt, "Unsaved changes.", s.Warning);
            }
            else if (cfg.HasTomestoneKey())
            {
                StatusLine(FontAwesomeIcon.CheckCircle, "API key saved.", s.Success);
            }
        }

        using var guide = M3ExpandableCard.Begin("tomestone_guide", "How to get an API key", ref tomestoneGuideExpanded,
                   FontAwesomeIcon.QuestionCircle);
        if (guide.Expanded)
        {
            if (StepRow(1, "Open your Tomestone account settings.", "##tomestone_step_open", "Open", FontAwesomeIcon.ExternalLinkAlt))
            {
                OpenUrl(TomestoneAccountUrl);
            }

            StepRow(2, "Scroll down to the \"API access token\" section.");
            StepRow(3, "Click \"Generate access token\".");
            StepRow(4, "Paste the token into the field above.");
            StepRow(5, "Click Save.");
            StatusLine(FontAwesomeIcon.ShieldAlt, "Keep your token private. It grants access to your Tomestone account data.", s.Warning);
        }
    }

    private void DrawAppearancePage()
    {
        var cfg = Configuration;
        var scale = M3.Scale;

        using (M3Card.Begin("appearance_theme", "Theme", FontAwesomeIcon.Palette,
                   subtitle: "Every color in the plugin's windows is generated from one accent color."))
        {
            var swatch = 28f * scale;
            var reset = M3Widgets.IconButtonSize;
            var controlSize = new Vector2(swatch + M3.Space2 + reset, MathF.Max(swatch, reset));

            var row = M3SettingRow.Begin("Accent color", "Very dark or grey colors fall back to the default.", controlSize);
            var origin = row.ControlPosition;

            ImGui.SetCursorScreenPos(origin + new Vector2(0f, (controlSize.Y - swatch) * 0.5f));
            var accent = cfg.UiAccentColor;
            if (M3Widgets.ColorSwatch("##accent_color", ref accent))
            {
                cfg.UiAccentColor = accent with { W = 1f };
                savePending = true;
            }

            ImGui.SetCursorScreenPos(origin + new Vector2(swatch + M3.Space2, (controlSize.Y - reset) * 0.5f));
            if (M3Widgets.IconButton("##accent_reset", FontAwesomeIcon.Undo, "Reset to the default accent"))
            {
                cfg.UiAccentColor = M3.DefaultSeed;
                cfg.Save();
            }

            M3SettingRow.End(row);

            DrawAccentPresets();
        }

        using (M3Card.Begin("appearance_size", "Size", FontAwesomeIcon.TextHeight,
                   subtitle: "Applies to every window of the plugin, on top of Dalamud's global scale."))
        {
            var textPercent = (int)MathF.Round(cfg.UiTextScale * 100f);
            if (SliderRow("Text size", ref textPercent, 75, 175, "%",
                    "Scales the text, on top of Dalamud's own font settings."))
            {
                cfg.UiTextScale = textPercent / 100f;
                savePending = true;
            }

            var elementPercent = (int)MathF.Round(cfg.UiElementScale * 100f);
            if (SliderRow("Element size", ref elementPercent, 75, 175, "%",
                    "Scales the padding, spacing and controls. Turn it down for a more compact layout."))
            {
                cfg.UiElementScale = elementPercent / 100f;
                savePending = true;
            }
        }
    }

    private void DrawAccentPresets()
    {
        var cfg = Configuration;
        var s = M3.Scheme;
        var scale = M3.Scale;
        var diameter = 28f * scale;
        var gap = M3.Space2;
        var controlWidth = (AccentPresets.Length * diameter) + ((AccentPresets.Length - 1) * gap);

        var row = M3SettingRow.Begin("Presets", null, new Vector2(controlWidth, diameter));
        var origin = row.ControlPosition;
        var drawList = ImGui.GetWindowDrawList();

        for (var i = 0; i < AccentPresets.Length; i++)
        {
            var (name, color) = AccentPresets[i];
            ImGui.SetCursorScreenPos(origin + new Vector2(i * (diameter + gap), 0f));
            var pressed = ImGui.InvisibleButton($"##accent_preset_{i}", new Vector2(diameter));
            var hovered = ImGui.IsItemHovered();
            var center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;

            drawList.AddCircleFilled(center, diameter * 0.5f, M3.U32(color), 32);
            if (IsSameColor(cfg.UiAccentColor, color))
            {
                drawList.AddCircle(center, (diameter * 0.5f) + (3f * scale), M3.U32(s.OnSurface), 32, 2f * scale);
            }
            else if (hovered)
            {
                drawList.AddCircle(center, (diameter * 0.5f) + (2f * scale), M3.U32(s.Outline), 32, 1.5f * scale);
            }

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImguiTooltips.ShowTooltip(name);
            }

            if (pressed)
            {
                cfg.UiAccentColor = color;
                cfg.Save();
            }
        }

        M3SettingRow.End(row);
    }

    private static bool IsSameColor(Vector4 a, Vector4 b)
    {
        const float tolerance = 0.002f;
        return MathF.Abs(a.X - b.X) < tolerance
            && MathF.Abs(a.Y - b.Y) < tolerance
            && MathF.Abs(a.Z - b.Z) < tolerance;
    }

    private void DrawAboutPage()
    {
        var s = M3.Scheme;

        using (M3Card.Begin("about_hero", null, style: M3CardStyle.Elevated))
        {
            using (ImRaii.PushFont(M3.TitleLarge))
            using (ImRaii.PushColor(ImGuiCol.Text, s.Primary))
            {
                ImGui.TextWrapped("Passport Checker Reborn");
            }

            ImGui.Dummy(new Vector2(0f, M3.Space1));

            using (ImRaii.PushColor(ImGuiCol.Text, M3.Alpha(s.OnSurfaceVariant, 0.95f)))
            {
                ImGui.TextWrapped(
                    "An open-source alternative to the PFFinder plugin. It shows a member info overlay beside " +
                    "Party Finder listings, looks up prog points on Tomestone.gg and FFLogs, and adds " +
                    "quality-of-life improvements to the Party Finder.");
            }

            ImGui.Dummy(new Vector2(0f, M3.Space2));

            _ = M3Widgets.Pill("##about_version", $"v{PassportCheckerReborn.Version}", s.Primary, FontAwesomeIcon.CodeBranch);
            ImGui.SameLine(0f, M3.Space1);
            _ = M3Widgets.Pill("##about_author", "The Combat Reborn Team - LTS", s.Tertiary, FontAwesomeIcon.Users);

            ImGui.Dummy(new Vector2(0f, M3.Space2));

            if (M3Widgets.Button("##about_kofi", "Support on Ko-fi", M3ButtonStyle.Tonal, FontAwesomeIcon.MugHot))
            {
                OpenUrl(KofiUrl);
            }

            ImGui.SameLine(0f, M3.Space2);

            if (M3Widgets.Button("##about_repo", "Source code", M3ButtonStyle.Outlined, FontAwesomeIcon.CodeBranch))
            {
                OpenUrl(RepoUrl);
            }
        }

        using (M3Card.Begin("about_commands", "Commands", FontAwesomeIcon.Terminal))
        {
            TextRow("/pcr", "Opens or closes this window. /pfchecker does the same.");
            TextRow("/pcrparty", "Shows or hides the party list overlay.");
        }

        using (M3Card.Begin("about_caches", "Caches", FontAwesomeIcon.Database))
        {
            ValueRow("Resolved players", "Content IDs already matched to a name and world.", plugin.CidCache.Count.ToString());

            const string clearLabel = "Clear";
            var countText = plugin.BlacklistCache.Count.ToString();
            var countSize = ImGui.CalcTextSize(countText);
            var buttonWidth = M3Widgets.ButtonWidth(FontAwesomeIcon.TrashAlt, clearLabel);
            var controlSize = new Vector2(countSize.X + M3.Space3 + buttonWidth, M3Widgets.ButtonHeight);

            var row = M3SettingRow.Begin("Blacklisted players", "Your in-game blacklist, as last read from the game.", controlSize);
            var origin = row.ControlPosition;

            ImGui.SetCursorScreenPos(origin + new Vector2(0f, (controlSize.Y - countSize.Y) * 0.5f));
            using (ImRaii.PushColor(ImGuiCol.Text, s.OnSurfaceVariant))
            {
                ImGui.TextUnformatted(countText);
            }

            ImGui.SetCursorScreenPos(origin + new Vector2(countSize.X + M3.Space3, 0f));
            if (M3Widgets.Button("##blacklist_clear", clearLabel, M3ButtonStyle.Danger, FontAwesomeIcon.TrashAlt, buttonWidth,
                    tooltip: "Clears the saved blacklist cache, then re-reads it from the game."))
            {
                plugin.BlacklistCache.Clear();
                plugin.PartyFinderManager.RefreshBlacklist();
            }

            M3SettingRow.End(row);
        }
    }

    private static bool SwitchRow(string label, string? supporting, ref bool value, bool enabled = true)
    {
        var row = M3SettingRow.Begin(label, supporting, M3Widgets.SwitchSize(), disabled: !enabled);
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var changed = M3Widgets.Switch($"##{label}_switch", ref value, enabled);
        M3SettingRow.End(row);
        return changed;
    }

    private static bool SliderRow(string label, ref int value, int min, int max, string suffix, string? supporting = null)
    {
        var trackWidth = 150f * M3.Scale;
        var controlSize = new Vector2(trackWidth + M3Widgets.SliderValueGutter($"{max}{suffix}"), M3Widgets.ButtonHeight);

        var row = M3SettingRow.Begin(label, supporting, controlSize);
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var changed = M3Widgets.SliderInt($"##{label}_slider", ref value, min, max, $"{value}{suffix}", trackWidth);
        M3SettingRow.End(row);
        return changed;
    }

    private static bool ComboRow(string label, string? supporting, string id, ref int index, IReadOnlyList<string> items, float width)
    {
        var row = M3SettingRow.Begin(label, supporting, new Vector2(width, M3Widgets.ComboHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var changed = M3Widgets.Combo(id, ref index, items, width);
        M3SettingRow.End(row);
        return changed;
    }

    // Returns the index picked this frame, or -1 when the selection did not change.
    private static int SegmentedRow(string label, string? supporting, string id, M3Segment[] segments, int selectedIndex)
    {
        var width = M3Widgets.SegmentedWidth(segments);
        var row = M3SettingRow.Begin(label, supporting, new Vector2(width, M3Widgets.SegmentedHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var picked = M3Widgets.SegmentedButtons(id, segments, selectedIndex, width);
        M3SettingRow.End(row);
        return picked;
    }

    private static bool ColorRow(string label, string id, ref Vector4 color)
    {
        var diameter = 28f * M3.Scale;
        var row = M3SettingRow.Begin(label, null, new Vector2(diameter, diameter));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var changed = M3Widgets.ColorSwatch(id, ref color);
        M3SettingRow.End(row);
        return changed;
    }

    private static bool ButtonRow(string label, string? supporting, string id, string buttonLabel, FontAwesomeIcon icon)
    {
        var width = M3Widgets.ButtonWidth(icon, buttonLabel);
        var row = M3SettingRow.Begin(label, supporting, new Vector2(width, M3Widgets.ButtonHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var pressed = M3Widgets.Button(id, buttonLabel, M3ButtonStyle.Tonal, icon, width);
        M3SettingRow.End(row);
        return pressed;
    }

    private static void TextFieldRow(string label, string id, string hint, ref string value, int maxLength, bool password = false)
    {
        var scale = M3.Scale;
        var width = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.55f, 180f * scale, 340f * scale);
        var row = M3SettingRow.Begin(label, null, new Vector2(width, M3TextField.Height));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        M3TextField.Draw(id, hint, ref value, width, maxLength, password);
        M3SettingRow.End(row);
    }

    private static void TextRow(string label, string? supporting)
    {
        var row = M3SettingRow.Begin(label, supporting, Vector2.Zero);
        M3SettingRow.End(row);
    }

    private static void ValueRow(string label, string? supporting, string value)
    {
        var row = M3SettingRow.Begin(label, supporting, ImGui.CalcTextSize(value));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        using (ImRaii.PushColor(ImGuiCol.Text, M3.Scheme.OnSurfaceVariant))
        {
            ImGui.TextUnformatted(value);
        }

        M3SettingRow.End(row);
    }

    private static void StepRow(int number, string text)
    {
        TextRow($"{number}.  {text}", null);
    }

    private static bool StepRow(int number, string text, string id, string action, FontAwesomeIcon icon, float? width = null)
    {
        var buttonWidth = width ?? M3Widgets.ButtonWidth(icon, action);
        var row = M3SettingRow.Begin($"{number}.  {text}", null, new Vector2(buttonWidth, M3Widgets.ButtonHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var pressed = M3Widgets.Button(id, action, M3ButtonStyle.Tonal, icon, buttonWidth);
        M3SettingRow.End(row);
        return pressed;
    }

    private void CopyStepRow(int number, string text, string id, string value)
    {
        var copied = copiedId == id && ImGui.GetTime() - copiedAt < CopiedFeedbackSeconds;
        var width = MathF.Max(
            M3Widgets.ButtonWidth(FontAwesomeIcon.Copy, "Copy"),
            M3Widgets.ButtonWidth(FontAwesomeIcon.Check, "Copied"));

        if (StepRow(number, text, id, copied ? "Copied" : "Copy", copied ? FontAwesomeIcon.Check : FontAwesomeIcon.Copy, width))
        {
            ImGui.SetClipboardText(value);
            copiedId = id;
            copiedAt = ImGui.GetTime();
        }
    }

    private static void BeginActionRow()
    {
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + RowInset);
    }

    private static void StatusLine(FontAwesomeIcon icon, string message, Vector4 color)
    {
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        var start = ImGui.GetCursorScreenPos() + new Vector2(RowInset, 0f);
        var iconSize = M3Draw.MeasureIcon(icon);
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon,
            new Vector2(start.X, start.Y + ((ImGui.GetTextLineHeight() - iconSize.Y) * 0.5f)), color);

        ImGui.SetCursorScreenPos(new Vector2(start.X + iconSize.X + M3.Space2, start.Y));
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(message);
    }

    private static string WhenAvailable(string supporting, bool unavailable)
    {
        return unavailable ? Unavailable : supporting;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, $"[PassportCheckerReborn] Failed to open URL {url}");
        }
    }
}
