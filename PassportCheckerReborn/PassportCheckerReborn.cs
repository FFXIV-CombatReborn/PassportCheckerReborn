using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.Windows;

namespace PassportCheckerReborn;

public sealed class PassportCheckerReborn : IDalamudPlugin
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

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PassportCheckerReborn");
    private MainWindow MainWindow { get; init; }
    internal OverlayWindow OverlayWindow { get; init; }
    internal PartyListWindow PartyListWindow { get; init; }

    internal TomestoneService TomestoneService { get; init; }
    internal FFLogsService FFLogsService { get; init; }
    internal CidCache CidCache { get; init; }
    internal PremadeCidCache PremadeCidCache { get; init; }
    internal BlacklistCache BlacklistCache { get; init; }
    internal PartyFinderManager PartyFinderManager { get; init; }

    public PassportCheckerReborn()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        TomestoneService = new TomestoneService(this);
        FFLogsService = new FFLogsService(this);
        CidCache = new CidCache();
        PremadeCidCache = new PremadeCidCache();
        BlacklistCache = new BlacklistCache();
        PartyFinderManager = new PartyFinderManager(this);

        MainWindow = new MainWindow(this);
        OverlayWindow = new OverlayWindow(this);
        PartyListWindow = new PartyListWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(OverlayWindow);
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

        PluginInterface.UiBuilder.Draw += ManageWindowStates;
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"[PassportCheckerReborn] Plugin loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= ManageWindowStates;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();
        OverlayWindow.Dispose();
        PartyListWindow.Dispose();

        PartyFinderManager.Dispose();
        CidCache.Dispose();
        BlacklistCache.Dispose();
        TomestoneService.Dispose();
        FFLogsService.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ALTCOMMAND);
        CommandManager.RemoveHandler(PartyListCommandName);
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
    public void ToggleOverlay() => OverlayWindow.Toggle();

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
                    hasPartyMembers = true;
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
