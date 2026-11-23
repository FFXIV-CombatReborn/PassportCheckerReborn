using Dalamud.Interface.Windowing;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private const string DiscordUrl = "https://discord.gg/p54TZMPnC9";
    private const string KofiUrl = "https://ko-fi.com/ltscombatreborn";

    private static readonly Vector2 DefaultSize = new(820f, 600f);

    private static readonly WindowSizeConstraints DefaultSizeConstraints = new()
    {
        MinimumSize = new Vector2(440, 360),
        MaximumSize = new Vector2(1600, 1400),
    };

    private static readonly M3WindowAction[] BarActions =
    [
        new("##window_discord", FontAwesomeIcon.Comments, "Join the Discord"),
        new("##window_kofi", FontAwesomeIcon.MugHot, "Support the developer on Ko-fi"),
    ];

    protected override M3WindowAction[] WindowActions => BarActions;

    protected override void OnWindowAction(int index)
    {
        switch (index)
        {
            case 0:
                OpenUrl(DiscordUrl);
                break;
            case 1:
                OpenUrl(KofiUrl);
                break;
        }
    }
}
