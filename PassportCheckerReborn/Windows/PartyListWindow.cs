using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PassportCheckerReborn.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Windows;

// FFLogs and Tomestone data for the current party, attached to the game's party list or free-floating.
public class PartyListWindow(PassportCheckerReborn plugin) : Window("Party Member Info##PFCheckerPartyList", LockedFlags)
{
    private const ImGuiWindowFlags FreeFlags = ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.AlwaysAutoResize;

    private const ImGuiWindowFlags LockedFlags = FreeFlags | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;

    private const string NoDuty = "(None)";

    private static readonly string[] PartyListAddons = ["_PartyList", "_CrossWorldPartyList"];
    private static readonly string[] DutyNames = BuildDutyNames();

    private readonly PassportCheckerReborn plugin = plugin;

    private IReadOnlyList<PartyMemberInfo> members = [];
    private int membersVersion = -1;

    // Results by member index. Each lookup replaces the whole dictionary when it finishes.
    private Dictionary<int, EncounterParseResult?> fflogsResults = [];
    private Dictionary<int, TomestoneCharacterInfo?> tomestoneResults = [];
    private bool fflogsLoading;
    private bool tomestoneLoading;

    // Goes up with every lookup started, so one that a newer lookup has overtaken drops its results.
    private int lookupSerial;

    private int selectedDutyIndex;

    // From the previous frame: the window's size, for placing it above the party list, and the width
    // of its widest content, which the header's hide button aligns to.
    private Vector2 lastWindowSize = new(310, 200);
    private float lastContentWidth;

    private M3Style.Scope? theme;

    private string? SelectedDutyName => selectedDutyIndex > 0 ? DutyNames[selectedDutyIndex] : null;

    public override unsafe bool DrawConditions()
    {
        return plugin.PartyListMonitorService.Members.Count > 0 || FindPartyListAddon() != null;
    }

    public override void PreDraw()
    {
        theme = M3Style.Push(M3Density.Compact);

        var position = plugin.Configuration.PartyListOverlayPosition;
        if (position == PartyListOverlayPosition.Unbound)
        {
            Flags = FreeFlags;
            Position = null;
            return;
        }

        Flags = LockedFlags;
        PositionBeside(position);
    }

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    private static unsafe AtkUnitBase* FindPartyListAddon()
    {
        foreach (var name in PartyListAddons)
        {
            var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName(name, 1);
            if (!addonPtr.IsNull && ((AtkUnitBase*)addonPtr.Address)->IsVisible)
            {
                return (AtkUnitBase*)addonPtr.Address;
            }
        }

        return null;
    }

    private unsafe void PositionBeside(PartyListOverlayPosition position)
    {
        var addon = FindPartyListAddon();
        if (addon == null)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        var left = viewport.Pos.X + addon->X;
        var top = viewport.Pos.Y + addon->Y;

        switch (position)
        {
            case PartyListOverlayPosition.Left:
                // Anchored by its top-right corner, so it grows leftwards and never covers the party list.
                var right = MathF.Min(MathF.Max(left - 10f, viewport.Pos.X + lastWindowSize.X), viewport.Pos.X + viewport.Size.X);
                ImGui.SetNextWindowPos(new Vector2(right, top), ImGuiCond.Always, new Vector2(1f, 0f));
                Position = null;
                break;

            case PartyListOverlayPosition.Right:
                Position = new Vector2(left + addon->GetScaledWidth(true) + 5f, top);
                break;

            case PartyListOverlayPosition.Above:
                Position = new Vector2(left, MathF.Max(top - lastWindowSize.Y - 5f, viewport.Pos.Y));
                break;

            case PartyListOverlayPosition.Below:
                Position = new Vector2(left, top + addon->GetScaledHeight(true) + 5f);
                break;
        }
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        var monitor = plugin.PartyListMonitorService;

        if (monitor.Members.Count == 0)
        {
            OverlayWidgets.EmptyState(FontAwesomeIcon.HourglassHalf, "Waiting for party data…");
            return;
        }

        if (monitor.Version != membersVersion)
        {
            membersVersion = monitor.Version;
            members = monitor.Members;
            StartLookups(cfg);
        }

        var contentStartX = ImGui.GetCursorScreenPos().X;
        if (!DrawHeader(cfg, contentStartX, out var contentRight))
        {
            return;
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        contentRight = MathF.Max(contentRight, DrawMemberTable(cfg));

        if (fflogsLoading || tomestoneLoading)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            OverlayWidgets.StatusLine(FontAwesomeIcon.HourglassHalf,
                fflogsLoading && tomestoneLoading ? "Loading FFLogs & Tomestone data…"
                : fflogsLoading ? "Loading FFLogs data…"
                : "Loading Tomestone data…");
            contentRight = MathF.Max(contentRight, ImGui.GetItemRectMax().X);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
        if (DrawDutySelector(out var selectorRight))
        {
            StartLookups(cfg);
        }

        contentRight = MathF.Max(contentRight, selectorRight);

        // Floored so a fractional global scale can never nudge the hide button past the content and
        // grow the window by a sub-pixel each frame.
        lastContentWidth = MathF.Floor(contentRight - contentStartX);
        lastWindowSize = ImGui.GetWindowSize();
    }

    // False when the user hid the overlay. contentRight is the title's right edge, not the button's.
    private bool DrawHeader(Configuration cfg, float contentStartX, out float contentRight)
    {
        var dutyName = TomestoneDutyName;
        OverlayWidgets.Header(FontAwesomeIcon.UserFriends, "Party Members", string.IsNullOrWhiteSpace(dutyName) ? null : dutyName);
        var headerMin = ImGui.GetItemRectMin();
        var headerMax = ImGui.GetItemRectMax();
        contentRight = headerMax.X;

        // Aligned to last frame's widest content rather than the window's edge: the window auto-resizes,
        // so anchoring to its edge would stop it ever shrinking.
        var buttonSize = 26f * M3.Scale;
        ImGui.SameLine();
        var buttonX = MathF.Max(ImGui.GetCursorScreenPos().X, contentStartX + lastContentWidth - buttonSize);
        ImGui.SetCursorScreenPos(new Vector2(buttonX, headerMin.Y + ((headerMax.Y - headerMin.Y - buttonSize) * 0.5f)));

        if (M3Widgets.IconButton("##hide_party_overlay", FontAwesomeIcon.EyeSlash,
                "Hide the party list overlay. Turn it back on in settings or with /pcrparty.", diameter: buttonSize))
        {
            cfg.ShowPartyListOverlay = false;
            cfg.Save();
            return false;
        }

        return true;
    }

    // True when the selection changed.
    private bool DrawDutySelector(out float right)
    {
        const string label = "Duty";
        var width = 240f * M3.Scale;
        var start = ImGui.GetCursorScreenPos();
        var labelSize = ImGui.CalcTextSize(label);

        ImGui.GetWindowDrawList().AddText(
            new Vector2(start.X, start.Y + ((M3Widgets.ComboHeight - labelSize.Y) * 0.5f)),
            M3.U32(OverlayWidgets.Muted), label);

        var comboX = start.X + labelSize.X + M3.Space2;
        ImGui.SetCursorScreenPos(new Vector2(comboX, start.Y));
        right = comboX + width;
        return M3Widgets.Combo("##party_duty_select", ref selectedDutyIndex, DutyNames, width);
    }

    // Returns the right edge of the widest cell. The table's own item rect will not do: ImGui clips it
    // to the window's previous size, and feeding that back into the header made the window oscillate.
    private float DrawMemberTable(Configuration cfg)
    {
        var hasTomestone = cfg.EnableTomestoneIntegration && cfg.HasTomestoneKey();
        var hasFFLogs = cfg.EnableFFLogsIntegrationOverlay && cfg.HasFFLogsCredentials();
        if (!OverlayWidgets.BeginMemberTable("##PartyMemberTable", hasTomestone, hasFFLogs))
        {
            return 0f;
        }

        var right = ImGui.GetItemRectMax().X;
        for (var i = 0; i < members.Count; i++)
        {
            DrawMemberRow(members[i], i, cfg, hasTomestone, hasFFLogs);
            right = MathF.Max(right, ImGui.GetItemRectMax().X);
        }

        ImGui.EndTable();
        return right;
    }

    private void DrawMemberRow(PartyMemberInfo member, int index, Configuration cfg, bool hasTomestone, bool hasFFLogs)
    {
        using var id = ImRaii.PushId(index);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        if (cfg.ShowPartyJobIcons)
        {
            OverlayWidgets.JobIcon(member.JobAbbreviation);
            ImGui.SameLine();
        }

        if (string.IsNullOrEmpty(member.World))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(member.Name);
        }
        else
        {
            OverlayWidgets.PlayerLink($"{member.Name}@{member.World}", member);
        }

        if (hasTomestone)
        {
            ImGui.TableNextColumn();
            if (tomestoneResults.TryGetValue(index, out var tomestone))
            {
                OverlayWidgets.TomestoneCell(tomestone);
            }
            else
            {
                OverlayWidgets.Pending();
            }
        }

        if (hasFFLogs)
        {
            ImGui.TableNextColumn();
            if (fflogsResults.TryGetValue(index, out var fflogs))
            {
                OverlayWidgets.FFLogsCell(fflogs, member);
            }
            else
            {
                OverlayWidgets.Pending();
            }
        }
    }

    private static string[] BuildDutyNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(FFLogsService.SupportedDutyNames);
        names.UnionWith(TomestoneService.SupportedDutyNames);
        return [NoDuty, .. names];
    }

    // The duty picked in the overlay, or else the last one viewed in the Party Finder.
    private string? TomestoneDutyName => SelectedDutyName ?? plugin.PartyFinderManager.LookupDutyName;

    // As above, but only while that Party Finder listing is still open.
    private string? FFLogsDutyName
    {
        get
        {
            var partyFinder = plugin.PartyFinderManager;
            return SelectedDutyName ?? (partyFinder.CurrentDutyId > 0 ? partyFinder.LookupDutyName : partyFinder.CurrentDutyName);
        }
    }

    private void StartLookups(Configuration cfg)
    {
        var serial = ++lookupSerial;
        fflogsResults = [];
        tomestoneResults = [];

        fflogsLoading = cfg.EnableFFLogsIntegrationOverlay && cfg.HasFFLogsCredentials();
        if (fflogsLoading)
        {
            _ = LookUpFFLogsAsync(members, FFLogsDutyName, serial);
        }

        tomestoneLoading = cfg.EnableTomestoneIntegration && cfg.HasTomestoneKey();
        if (tomestoneLoading)
        {
            _ = LookUpTomestoneAsync(members, TomestoneDutyName, serial);
        }
    }

    private async Task LookUpFFLogsAsync(IReadOnlyList<PartyMemberInfo> party, string? dutyName, int serial)
    {
        var results = new Dictionary<int, EncounterParseResult?>();
        try
        {
            var service = plugin.FFLogsService;
            var lookup = FFLogsService.GetEncounterIdsForDuty(dutyName) is { } encounterIds
                ? service.GetEncounterDataAsync(party, encounterIds)
                : service.GetOverallParsesAsync(party);
            foreach (var (index, result) in await lookup)
            {
                results[index] = result;
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyListWindow] FFLogs lookup failed.");
        }

        await PassportCheckerReborn.Framework.RunOnFrameworkThread(() =>
        {
            if (serial == lookupSerial)
            {
                fflogsResults = results;
                fflogsLoading = false;
            }
        });
    }

    private async Task LookUpTomestoneAsync(IReadOnlyList<PartyMemberInfo> party, string? dutyName, int serial)
    {
        var results = new Dictionary<int, TomestoneCharacterInfo?>();
        try
        {
            var infos = await Task.WhenAll(party.Select(member =>
                plugin.TomestoneService.GetCharacterInfoAsync(member.Name, member.World, dutyName)));
            for (var i = 0; i < infos.Length; i++)
            {
                results[i] = infos[i];
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyListWindow] Tomestone lookup failed.");
        }

        await PassportCheckerReborn.Framework.RunOnFrameworkThread(() =>
        {
            if (serial == lookupSerial)
            {
                tomestoneResults = results;
                tomestoneLoading = false;
            }
        });
    }
}
