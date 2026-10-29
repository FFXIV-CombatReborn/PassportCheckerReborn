using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow : Window
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
    private const float DrawerLayoutWidth = 620f;
    private const float DrawerWidth = 188f;
    private const float RailWidth = 84f;

    private readonly Plugin plugin;

    private M3Style.Scope? theme;
    private Page page = Page.General;

    private bool savePending;

    public SettingsWindow(Plugin plugin)
        : base("Passport Checker Reborn – Settings###PassportCheckerRebornSettings", BaseFlags)
    {
        this.plugin = plugin;

        Size = DefaultSize;
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = DefaultSizeConstraints;

        AllowPinning = false;
        AllowClickthrough = false;
    }

    private Configuration Configuration => Plugin.Configuration;

    public override void OnOpen()
    {
        IsPinned = false;
        IsClickthrough = false;
    }

    public override void PreDraw()
    {
        theme = M3Style.Push();
        PrepareFold();
    }

    public override void PostDraw()
    {
        fold.PopStyle();
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

        fold.Reset();
    }

    public override void Draw()
    {
        fold.BeginDraw();

        if (fold.Amount < 1f)
        {
            using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, fold.ContentAlpha);
            M3Draw.WindowBackdrop(fold.Rounding);
            DrawContent();
        }

        DrawWindowBar();

        if (savePending && !ImGui.IsAnyItemActive())
        {
            Configuration.Save();
            savePending = false;
        }
    }

    private void DrawContent()
    {
        using var content = fold.BeginContent("##pcr_window_content");
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
                "Accent color, size and spacing"),
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
        shownActions = bar.ShownActions;
        fold.BarTop = bar.BarTop;
    }
}
