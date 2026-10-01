using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PassportCheckerReborn.UI;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

// The one-click job filter: a chip just above the Party Finder list.
public class PFListFilterWindow(PassportCheckerReborn plugin) : Window("PF Job Filter##PFCheckerJobFilter",
           ImGuiWindowFlags.NoTitleBar |
               ImGuiWindowFlags.NoResize |
               ImGuiWindowFlags.NoMove |
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoFocusOnAppearing |
               ImGuiWindowFlags.AlwaysAutoResize)
{
    // Unscaled gap between the chip's window and the Party Finder.
    private const float AddonGap = 4f;

    private readonly PassportCheckerReborn plugin = plugin;

    // Where the Party Finder list is this frame, in screen coordinates.
    private Vector2 addonPosition;
    private float addonHeight;

    // From the previous frame, to keep the window on screen.
    private Vector2 lastWindowSize;

    private M3Style.Scope? theme;

    public override unsafe bool DrawConditions()
    {
        var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroup", 1);
        if (addonPtr.IsNull)
        {
            return false;
        }

        var addon = (AtkUnitBase*)addonPtr.Address;
        if (!addon->IsVisible)
        {
            return false;
        }

        addonPosition = ImGui.GetMainViewport().Pos + new Vector2(addon->X, addon->Y);
        addonHeight = addon->GetScaledHeight(true);
        return true;
    }

    public override void PreDraw()
    {
        theme = M3Style.Push(compact: true);

        // Above the Party Finder's top-left corner, or below its bottom-left one when there is no room above.
        var gap = AddonGap * M3.Scale;
        var above = addonPosition.Y - gap;
        if (above - lastWindowSize.Y >= ImGui.GetMainViewport().Pos.Y)
        {
            ImGui.SetNextWindowPos(new Vector2(addonPosition.X, above), ImGuiCond.Always, new Vector2(0f, 1f));
        }
        else
        {
            ImGui.SetNextWindowPos(new Vector2(addonPosition.X, addonPosition.Y + addonHeight + gap), ImGuiCond.Always);
        }
    }

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    public override void Draw()
    {
        var tweaks = plugin.PartyFinderListTweaks;
        var job = tweaks.JobFilterJobAbbreviation;
        var active = tweaks.JobFilterActive;

        var label = job == null ? "Job filter"
            : active ? $"Open {job} slots only"
            : $"Filter for {job}";
        var tooltip = job == null ? "Switch to a combat class or job to filter listings."
            : active ? $"Hiding high-end duty listings with no open slot for {job}. Click to show them again."
            : $"Hide high-end duty listings with no open slot for {job}.";

        // With no job to filter for, the chip can still be clicked to turn an active filter off.
        if (M3Widgets.Chip("##pcr_job_filter", label, active, FontAwesomeIcon.Filter, tooltip) && (job != null || active))
        {
            tweaks.ToggleJobFilter();
        }

        lastWindowSize = ImGui.GetWindowSize();
    }
}
