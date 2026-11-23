using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow : M3FoldWindow
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

    private const string BrandName = "Passport Checker";
    private const float BrandLogoSize = 40f;
    private const float DrawerLayoutWidth = 600f;
    private const float DrawerMinWidth = 190f;
    private const float RailWidth = 84f;

    private readonly Plugin plugin;

    private Page page = Page.General;

    private bool savePending;

    public SettingsWindow(Plugin plugin)
        : base("Passport Checker Reborn – Settings###PassportCheckerRebornSettings", "PCR", DefaultSize, DefaultSizeConstraints)
    {
        this.plugin = plugin;

        AllowPinning = false;
        AllowClickthrough = false;
    }

    private Configuration Configuration => Plugin.Configuration;

    private static float IconStripWidth => MathF.Max(44f * M3.Scale, (UiBuilder.IconFont.FontSize * 1.25f) + (8f * M3.Scale));

    public override void OnOpen()
    {
        IsPinned = false;
        IsClickthrough = false;
    }

    public override void OnClose()
    {
        FlushSave();

        M3Motion.Reset();
        M3CardHost.Reset();

        base.OnClose();
    }

    protected override void DrawLast()
    {
        if (!ImGui.IsAnyItemActive())
        {
            FlushSave();
        }
    }

    private void FlushSave()
    {
        if (!savePending)
        {
            return;
        }

        Configuration.Save();
        savePending = false;
    }

    protected override void DrawOpen()
    {
        var scale = M3.Scale;
        var available = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var items = BuildNavItems();
        var iconsOnly = Configuration.UiIconSidebar;
        var expanded = !iconsOnly && available.X >= DrawerLayoutWidth * scale;
        var navWidth = iconsOnly ? MathF.Min(IconStripWidth, available.X * 0.3f)
            : expanded ? MathF.Min(DrawerWidth(items), available.X * 0.4f)
            : MathF.Min(RailWidth * scale, available.X * 0.3f);

        var origin = ImGui.GetCursorScreenPos();
        var dividerX = origin.X + navWidth + (spacing * 0.5f);
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(dividerX, origin.Y),
            new Vector2(dividerX, origin.Y + available.Y),
            M3.U32(M3.Scheme.OutlineVariant, 0.45f), 1f * scale);

        DrawNavigation(items, navWidth, expanded, iconsOnly);
        ImGui.SameLine(0f, spacing);
        DrawBody();
    }

    private static float DrawerWidth(List<M3NavItem> items)
    {
        var scale = M3.Scale;
        var label = 0f;
        foreach (var item in items)
        {
            var badge = item.Badge == null ? 0f : ImGui.CalcTextSize(item.Badge).X + (20f * scale);
            label = MathF.Max(label, ImGui.CalcTextSize(item.Label).X + badge);
        }

        var labelWidth = label + (44f * scale) + M3Style.Spacing(16f, 12f) + M3Style.Spacing(14f, 10f);
        var brandWidth = MeasureText(M3.TitleMedium, BrandName).X + ((BrandLogoSize + 26f) * scale);
        return MathF.Max(DrawerMinWidth * scale, MathF.Max(labelWidth, brandWidth));
    }

    private static Vector2 MeasureText(ImFontPtr font, string text)
    {
        using var pushed = ImRaii.PushFont(font);
        return ImGui.CalcTextSize(text);
    }

    private void DrawNavigation(List<M3NavItem> items, float width, bool expanded, bool iconsOnly)
    {
        using var child = ImRaii.Child("##pcr_nav", new Vector2(width, -1f), false, ImGuiWindowFlags.NoScrollbar);
        if (!child)
        {
            return;
        }

        DrawBrand(expanded);
        ImGui.Dummy(new Vector2(0f, M3.Space1));

        var clicked = M3Navigation.Draw("pcr_nav", items, expanded, iconsOnly);
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
                "Accent color, sizes, spacing, animations and layout"),
            new(nameof(Page.About), "About", FontAwesomeIcon.InfoCircle, page == Page.About,
                "Commands, caches and links"),
        ];
    }

    private void DrawBrand(bool expanded)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var logoSize = MathF.Min(BrandLogoSize * scale, width);
        var height = logoSize + (8f * scale);

        var gap = 1f * scale;
        var tagline = $"v{Plugin.Version}";
        var nameHeight = 0f;
        var textHeight = 0f;
        if (expanded)
        {
            nameHeight = MeasureText(M3.TitleMedium, BrandName).Y;
            textHeight = nameHeight + gap + MeasureText(M3.LabelSmall, tagline).Y;
            height = MathF.Max(height, textHeight + (4f * scale));
        }

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

        var logoX = expanded ? min.X + (8f * scale) : min.X + ((width - logoSize) * 0.5f);
        M3Brand.DrawLogo(drawList, new Vector2(logoX, min.Y + ((height - logoSize) * 0.5f)), logoSize);

        if (expanded)
        {
            var textX = logoX + logoSize + (10f * scale);
            var textWidth = MathF.Max(16f * scale, max.X - textX - (4f * scale));
            var textTop = min.Y + ((height - textHeight) * 0.5f);
            using (ImRaii.PushFont(M3.TitleMedium))
            {
                drawList.AddText(new Vector2(textX, textTop), M3.U32(s.OnSurface, 0.98f), M3Navigation.Truncate(BrandName, textWidth));
            }

            using (ImRaii.PushFont(M3.LabelSmall))
            {
                drawList.AddText(new Vector2(textX, textTop + nameHeight + gap), M3.U32(s.Primary), tagline);
            }
        }

        if (hovered)
        {
            M3Tooltip.Show("About Passport Checker Reborn");
        }

        if (pressed)
        {
            page = Page.About;
        }
    }

    private void DrawVersionFooter()
    {
        var label = $"v{Plugin.Version}";
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
        var (title, subtitle) = PageHeading(page);
        using var bar = M3TopAppBar.Begin(title, subtitle, WindowActions.Length, Brand);
        SetActionBar(bar.ShownActions, bar.BarTop);
    }
}
