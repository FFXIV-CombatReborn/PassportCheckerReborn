using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public class JobFilterWindow(Plugin plugin) : Window("PF Job Filter##PFCheckerJobFilter",
           ImGuiWindowFlags.NoTitleBar |
               ImGuiWindowFlags.NoResize |
               ImGuiWindowFlags.NoMove |
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoFocusOnAppearing |
               ImGuiWindowFlags.AlwaysAutoResize)
{
    private const float AddonGap = 4f;

    private readonly Plugin plugin = plugin;

    private Vector2 addonPosition;
    private float addonHeight;

    private Vector2 lastWindowSize;

    private M3Style.Scope? theme;

    public override unsafe bool DrawConditions()
    {
        var addonPtr = Plugin.GameGui.GetAddonByName("LookingForGroup", 1);
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
        theme = M3Style.Push(M3Density.Compact);

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
        var jobFilter = plugin.JobFilter;
        var job = jobFilter.JobAbbreviation;
        var active = jobFilter.Active;

        var label = job == null ? "Job filter"
            : active ? $"Open {job} slots only"
            : $"Filter for {job}";
        var tooltip = job == null ? "Switch to a combat class or job to filter listings."
            : active ? $"Hiding high-end duty listings with no open slot for {job}. Click to show them again."
            : $"Hide high-end duty listings with no open slot for {job}.";

        if (M3Widgets.Chip("##pcr_job_filter", label, active, FontAwesomeIcon.Filter, tooltip) && (job != null || active))
        {
            jobFilter.Toggle();
        }

        lastWindowSize = ImGui.GetWindowSize();
    }
}
