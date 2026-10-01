using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.UI;
using PassportCheckerReborn.Windows;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn;

public sealed class PassportCheckerReborn : IAsyncDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPartyFinderGui PartyFinderGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    internal static readonly string Version = typeof(PassportCheckerReborn).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    private const string CommandName = "/pfchecker";
    private const string AltCommandName = "/pcr";
    private const string PartyListCommandName = "/pcrparty";

    // Static for code with no plugin instance to hand, such as the UI theme.
    internal static Configuration Config { get; private set; } = null!;

    public Configuration Configuration => Config;

    public readonly WindowSystem WindowSystem = new("PassportCheckerReborn");
    private MainWindow MainWindow { get; set; } = null!;
    internal PFWindow PFWindow { get; set; } = null!;
    internal PartyListWindow PartyListWindow { get; set; } = null!;
    internal PFListFilterWindow PFListFilterWindow { get; set; } = null!;

    internal TomestoneService TomestoneService { get; private set; } = null!;
    internal FFLogsService FFLogsService { get; private set; } = null!;
    internal CidCache CidCache { get; private set; } = null!;
    internal BlacklistCache BlacklistCache { get; private set; } = null!;
    internal PartyFinderManager PartyFinderManager { get; private set; } = null!;
    internal PartyFinderListTweaks PartyFinderListTweaks { get; private set; } = null!;
    internal PartyListMonitorService PartyListMonitorService { get; private set; } = null!;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        TomestoneService = new TomestoneService(this);
        FFLogsService = new FFLogsService(this);
        CidCache = new CidCache();
        BlacklistCache = new BlacklistCache();

        // Hooks, addon listeners and windows must be set up on the framework thread.
        await Framework.RunOnFrameworkThread(() =>
        {
            PartyListMonitorService = new PartyListMonitorService(this);
            PartyFinderManager = new PartyFinderManager(this);
            PartyFinderListTweaks = new PartyFinderListTweaks(this);
            PFWindowManager.ApplySetting();

            MainWindow = new MainWindow(this);
            PFWindow = new PFWindow(this);
            PartyListWindow = new PartyListWindow(this);
            PFListFilterWindow = new PFListFilterWindow(this);

            WindowSystem.AddWindow(MainWindow);
            WindowSystem.AddWindow(PFWindow);
            WindowSystem.AddWindow(PartyListWindow);
            WindowSystem.AddWindow(PFListFilterWindow);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Passport Check Reborn menu."
            });
            CommandManager.AddHandler(AltCommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Passport Check Reborn menu."
            });
            CommandManager.AddHandler(PartyListCommandName, new CommandInfo(OnPartyListCommand)
            {
                HelpMessage = "Toggle the Party List Overlay on or off."
            });

            PluginInterface.UiBuilder.Draw += ManageWindowStates;
            PluginInterface.UiBuilder.Draw += DrawWindows;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Framework.RunOnFrameworkThread(() =>
        {
            PluginInterface.UiBuilder.Draw -= ManageWindowStates;
            PluginInterface.UiBuilder.Draw -= DrawWindows;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

            WindowSystem.RemoveAllWindows();

            PartyFinderListTweaks?.Dispose();
            PartyFinderManager?.Dispose();
            PartyListMonitorService?.Dispose();
            PFWindowManager.Dispose();
            FontManager.DisposeAll();

            CommandManager.RemoveHandler(CommandName);
            CommandManager.RemoveHandler(AltCommandName);
            CommandManager.RemoveHandler(PartyListCommandName);
        });

        CidCache?.Dispose();
        BlacklistCache?.Dispose();
        TomestoneService?.Dispose();
        FFLogsService?.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainUi();
    }

    private void OnPartyListCommand(string command, string args)
    {
        Configuration.ShowPartyListOverlay = !Configuration.ShowPartyListOverlay;
        Configuration.Save();
        ChatGui.Print($"[PassportChecker] Party List Overlay {(Configuration.ShowPartyListOverlay ? "shown" : "hidden")}.");
    }

    public void ToggleMainUi()
    {
        // Like a taskbar button: a minimized window comes back rather than closing.
        if (MainWindow is { IsOpen: true, IsMinimized: true })
        {
            MainWindow.Restore();
            return;
        }

        MainWindow.Toggle();
    }

    private void DrawWindows()
    {
        M3.BeginFrame();
        WindowSystem.Draw();
    }

    // Opens and closes the windows that follow the game's state. Runs before WindowSystem.Draw.
    private void ManageWindowStates()
    {
        var cfg = Configuration;

        PFListFilterWindow.IsOpen = cfg.EnableOneClickJobFilter && PartyFinderManager.IsListOpen;

        PartyListWindow.IsOpen = cfg.ShowPartyListOverlay
            && (cfg.EnableFFLogsIntegrationOverlay || cfg.EnableTomestoneIntegration)
            && IsInParty()
            && !(cfg.HidePartyListInDuty && Condition[ConditionFlag.BoundByDuty])
            && !(cfg.HidePartyListInCombat && Condition[ConditionFlag.InCombat]);
    }

    private static unsafe bool IsInParty()
    {
        if (PartyList.Length > 0)
        {
            return true;
        }

        // A cross-world party is not in IPartyList.
        var crossRealm = InfoProxyCrossRealm.Instance();
        return crossRealm != null && crossRealm->IsInCrossRealmParty;
    }
}
