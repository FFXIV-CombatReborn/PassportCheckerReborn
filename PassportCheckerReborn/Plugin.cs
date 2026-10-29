using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PassportCheckerReborn.Integrations.FFLogs;
using PassportCheckerReborn.Integrations.Tomestone;
using PassportCheckerReborn.PartyFinder;
using PassportCheckerReborn.Players;
using PassportCheckerReborn.Tweaks;
using PassportCheckerReborn.Windows;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn;

public sealed class Plugin : IAsyncDalamudPlugin
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

    internal static readonly string Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    private const string CommandName = "/pfchecker";
    private const string AltCommandName = "/pcr";
    private const string PartyListCommandName = "/pcrparty";

    internal static Configuration Configuration { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("PassportCheckerReborn");
    private SettingsWindow SettingsWindow { get; set; } = null!;
    private MemberInfoWindow MemberInfoWindow { get; set; } = null!;
    private PartyListWindow PartyListWindow { get; set; } = null!;
    private JobFilterWindow JobFilterWindow { get; set; } = null!;

    internal TomestoneService TomestoneService { get; private set; } = null!;
    internal FFLogsService FFLogsService { get; private set; } = null!;
    internal CidCache CidCache { get; private set; } = null!;
    internal BlacklistCache BlacklistCache { get; private set; } = null!;

    internal LogMessageHook LogMessages { get; private set; } = null!;
    internal CharaCardRequester CharaCards { get; private set; } = null!;
    internal PartyMonitor PartyMonitor { get; private set; } = null!;
    internal BlacklistMonitor BlacklistMonitor { get; private set; } = null!;
    internal PartyFinderManager PartyFinderManager { get; private set; } = null!;

    internal TimeSortTweak TimeSort { get; private set; } = null!;
    internal PageSizeTweak PageSize { get; private set; } = null!;
    internal AutoRefreshTweak AutoRefresh { get; private set; } = null!;
    internal JobFilterTweak JobFilter { get; private set; } = null!;
    internal KeepOpenTweak KeepOpen { get; private set; } = null!;
    internal RecruitmentMenuTweak RecruitmentMenu { get; private set; } = null!;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        M3.Initialize(PluginInterface, Configuration);

        TomestoneService = new TomestoneService();
        FFLogsService = new FFLogsService();
        CidCache = new CidCache();
        BlacklistCache = new BlacklistCache();

        await Framework.RunOnFrameworkThread(() =>
        {
            LogMessages = new LogMessageHook();
            CharaCards = new CharaCardRequester(this);
            PartyMonitor = new PartyMonitor(this);
            BlacklistMonitor = new BlacklistMonitor(this);
            PartyFinderManager = new PartyFinderManager(this);

            TimeSort = new TimeSortTweak();
            PageSize = new PageSizeTweak();
            AutoRefresh = new AutoRefreshTweak(this);
            JobFilter = new JobFilterTweak(this);
            KeepOpen = new KeepOpenTweak(this);
            RecruitmentMenu = new RecruitmentMenuTweak();

            SettingsWindow = new SettingsWindow(this);
            MemberInfoWindow = new MemberInfoWindow(this);
            PartyListWindow = new PartyListWindow(this);
            JobFilterWindow = new JobFilterWindow(this);

            WindowSystem.AddWindow(SettingsWindow);
            WindowSystem.AddWindow(MemberInfoWindow);
            WindowSystem.AddWindow(PartyListWindow);
            WindowSystem.AddWindow(JobFilterWindow);

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

            RecruitmentMenu?.Dispose();
            KeepOpen?.Dispose();
            JobFilter?.Dispose();
            AutoRefresh?.Dispose();
            PageSize?.Dispose();
            TimeSort?.Dispose();

            PartyFinderManager?.Dispose();
            BlacklistMonitor?.Dispose();
            PartyMonitor?.Dispose();
            CharaCards?.Dispose();
            LogMessages?.Dispose();
            M3.Dispose();

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
        if (SettingsWindow is { IsOpen: true, IsMinimized: true })
        {
            SettingsWindow.Restore();
            return;
        }

        SettingsWindow.Toggle();
    }

    private void DrawWindows()
    {
        M3.BeginFrame();
        WindowSystem.Draw();
    }

    private void ManageWindowStates()
    {
        var cfg = Configuration;

        MemberInfoWindow.IsOpen = cfg.ShowMemberInfoOverlay && PartyFinderManager.IsDetailOpen;
        JobFilterWindow.IsOpen = cfg.EnableOneClickJobFilter && PartyFinderManager.IsListOpen;

        PartyListWindow.IsOpen = cfg.ShowPartyListOverlay
            && (cfg.EnableFFLogsIntegrationOverlay || cfg.EnableTomestoneIntegration)
            && PartyMonitor.IsInParty()
            && !(cfg.HidePartyListInDuty && Condition[ConditionFlag.BoundByDuty])
            && !(cfg.HidePartyListInCombat && Condition[ConditionFlag.InCombat]);
    }
}
