using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;

namespace PassportCheckerReborn.Tweaks;

public sealed class RecruitmentMenuTweak : IDisposable
{
    private const ushort ViewRecruitmentPrefixColor = 37;

    public RecruitmentMenuTweak()
    {
        ApplySetting();
    }

    public void Dispose()
    {
        Plugin.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
    }

    public void ApplySetting()
    {
        Plugin.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        if (Plugin.Configuration.RightClickPlayerNameForRecruitment3)
        {
            Plugin.ContextMenu.OnMenuOpened += OnContextMenuOpened;
        }
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault { TargetContentId: not 0 })
        {
            return;
        }

        args.AddMenuItem(new MenuItem
        {
            Name = new SeStringBuilder().AddText("View Recruitment").Build(),
            PrefixChar = 'P',
            PrefixColor = ViewRecruitmentPrefixColor,
            OnClicked = OnViewRecruitmentClicked,
            IsEnabled = true,
        });
    }

    private unsafe void OnViewRecruitmentClicked(IMenuItemClickedArgs args)
    {
        if (args.Target is not MenuTargetDefault { TargetContentId: not 0 } target)
        {
            return;
        }

        var agent = AgentLookingForGroup.Instance();
        if (agent == null || !agent->OpenListingByContentId(target.TargetContentId))
        {
            Plugin.ChatGui.Print("[PassportChecker] Unable to request this player's Party Finder listing.");
        }
    }
}
