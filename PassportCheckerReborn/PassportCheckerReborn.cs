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

    internal const string Version = "0.1.0";

    private const string CommandName = "/pfchecker";
    public const string ALTCOMMAND = "/pcr";
    private const string PartyListCommandName = "/pcrparty";

    /// <summary>The loaded configuration, for code with no plugin instance to hand (such as the UI theme).</summary>
    internal static Configuration Config { get; private set; } = null!;

    public Configuration Configuration => Config;

    public readonly WindowSystem WindowSystem = new("PassportCheckerReborn");
    private MainWindow MainWindow { get; set; } = null!;
    internal PFWindow PFWindow { get; set; } = null!;
    internal PartyListWindow PartyListWindow { get; set; } = null!;

    internal TomestoneService TomestoneService { get; private set; } = null!;
    internal FFLogsService FFLogsService { get; private set; } = null!;
    internal CidCache CidCache { get; private set; } = null!;
    internal BlacklistCache BlacklistCache { get; private set; } = null!;
    internal PartyFinderManager PartyFinderManager { get; private set; } = null!;
    internal PartyListMonitorService PartyListMonitorService { get; private set; } = null!;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        TomestoneService = new TomestoneService(this);
        FFLogsService = new FFLogsService(this);
        CidCache = new CidCache();
        BlacklistCache = new BlacklistCache();
        PartyListMonitorService = new PartyListMonitorService(this);

        // Hook registration and addon event subscriptions must run on the main thread.
        await Framework.RunOnFrameworkThread(() =>
        {
            PartyFinderManager = new PartyFinderManager(this);
            PFWindowManager.Enable(this, PartyFinderManager);

            MainWindow = new MainWindow(this);
            PFWindow = new PFWindow(this);
            PartyListWindow = new PartyListWindow(this);

            WindowSystem.AddWindow(MainWindow);
            WindowSystem.AddWindow(PFWindow);
            WindowSystem.AddWindow(PartyListWindow);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Passport Check Reborn menu."
            });
            CommandManager.AddHandler(ALTCOMMAND, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Passport Check Reborn menu."
            });
            CommandManager.AddHandler(PartyListCommandName, new CommandInfo(OnPartyListCommand)
            {
                HelpMessage = "Toggle the Party List Overlay on or off."
            });

            // Register framework update for party list monitoring
            Framework.Update += PartyListMonitorService.OnFrameworkUpdate;

            PluginInterface.UiBuilder.Draw += ManageWindowStates;
            PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        });

        Log.Information($"[PassportCheckerReborn] Plugin loaded.");
    }

    public async ValueTask DisposeAsync()
    {
        // Hook teardown and UI deregistration must run on the main thread.
        await Framework.RunOnFrameworkThread(() =>
        {
            // Unregister framework update for party list monitoring
            Framework.Update -= PartyListMonitorService.OnFrameworkUpdate;

            PluginInterface.UiBuilder.Draw -= ManageWindowStates;
            PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

            WindowSystem.RemoveAllWindows();

            MainWindow?.Dispose();
            PFWindow?.Dispose();
            PartyListWindow?.Dispose();

            PartyFinderManager?.Dispose();
            FontManager.DisposeAll();

            CommandManager.RemoveHandler(CommandName);
            CommandManager.RemoveHandler(ALTCOMMAND);
            CommandManager.RemoveHandler(PartyListCommandName);
        });

        PFWindowManager.Disable();

        PartyListMonitorService?.Dispose();
        CidCache?.Dispose();
        BlacklistCache?.Dispose();
        TomestoneService?.Dispose();
        FFLogsService?.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

    private void OnPartyListCommand(string command, string args)
    {
        Configuration.ShowPartyListOverlay = !Configuration.ShowPartyListOverlay;
        Configuration.Save();
        ChatGui.Print($"[PassportChecker] Party List Overlay {(Configuration.ShowPartyListOverlay ? "shown" : "hidden")}.");
    }

    public void ToggleMainUi() => MainWindow.Toggle();
    public void ToggleOverlay() => PFWindow.Toggle();

    /// <summary>
    /// Runs every frame before WindowSystem.Draw to manage auto-open/close
    /// state for windows that depend on external conditions.
    /// </summary>
    private unsafe void ManageWindowStates()
    {
        // PartyListWindow: open when config enabled, and at least one integration enabled
        if (!Configuration.ShowPartyListOverlay
            || (!Configuration.EnableFFLogsIntegrationOverlay && !Configuration.EnableTomestoneIntegration))
        {
            PartyListWindow.IsOpen = false;
            return;
        }

        // Check if party has members via IPartyList (works for regular parties)
        var hasPartyMembers = PartyList.Length > 0;

        // Fallback: check InfoProxyCrossRealm for crossworld parties where IPartyList may be empty
        // (follows the same pattern as ReadyCheckHelper for detecting cross-realm parties)
        if (!hasPartyMembers)
        {
            try
            {
                var cwProxy = InfoProxyCrossRealm.Instance();
                if (cwProxy != null && cwProxy->IsInCrossRealmParty)
                {
                    hasPartyMembers = true;
                }
            }
            catch
            {
                // Ignore failures reading cross-realm state
            }
        }

        if (!hasPartyMembers)
        {
            PartyListWindow.IsOpen = false;
            return;
        }

        // Hide in duty and/or combat based on individual settings
        var hideNow = (Configuration.HidePartyListInDuty && Condition[ConditionFlag.BoundByDuty])
                   || (Configuration.HidePartyListInCombat && Condition[ConditionFlag.InCombat]);
        PartyListWindow.IsOpen = !hideNow;
    }
}
