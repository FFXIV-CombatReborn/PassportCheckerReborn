using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using System;
using System.Diagnostics;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly PassportCheckerReborn plugin;
    private Configuration Configuration => plugin.Configuration;

    // Temporary buffers for FFLogs credential input
    private string fFlogsClientIdInput = string.Empty;
    private string fFlogsClientSecretInput = string.Empty;
    private string fFlogsTestResult = string.Empty;
    private bool fFlogsTestInProgress = false;

    // Temporary buffer for Tomestone API key input
    private string tomestoneApiKeyInput = string.Empty;

    public MainWindow(PassportCheckerReborn plugin)
        : base("Passport Checker Reborn – Settings###PassportCheckerRebornSettings",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 480),
            MaximumSize = new Vector2(800, 900)
        };

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 260),
            MaximumSize = new Vector2(600, 600)
        };

        TitleBarButtons.Add(new TitleBarButton()
        {
            Icon = FontAwesomeIcon.MugHot,
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.Text("Support the developer on Ko-fi");
                ImGui.EndTooltip();
            },
            Priority = 2,
            Click = _ =>
            {
                try
                {
                    Util.OpenLink("https://ko-fi.com/ltscombatreborn");
                }
                catch
                {
                    // ignored
                }
            },
            AvailableClickthrough = true
        });

        // Populate credential buffers from saved config
        fFlogsClientIdInput = Configuration.FFLogsClientId;
        fFlogsClientSecretInput = Configuration.FFLogsClientSecret;
        tomestoneApiKeyInput = Configuration.TomestoneApiKey;
    }

    public void Dispose() 
    { 
        GC.SuppressFinalize(this);
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##SettingsTabs"))
            return;

        DrawGeneralTab();
        DrawOverlayTab();
        DrawFFLogsTab();
        DrawTomestoneTab();
        DrawAboutTab();

        ImGui.EndTabBar();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // General Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawGeneralTab()
    {
        if (!ImGui.BeginTabItem("General"))
            return;

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), "Party Finder Detail Optimizations");
        ImGui.Separator();
        ImGui.Spacing();

        var specialBorder = Configuration.SpecialBorderColorForKnownPlayers;
        if (ImGui.Checkbox("Special Border Color For Known Players", ref specialBorder))
        {
            Configuration.SpecialBorderColorForKnownPlayers = specialBorder;
            Configuration.Save();
        }

        if (Configuration.SpecialBorderColorForKnownPlayers)
        {
            ImGui.Indent(20f);
            var borderColor = Configuration.KnownPlayerBorderColor;
            if (ImGui.ColorEdit4("Border Color##knownplayer", ref borderColor, ImGuiColorEditFlags.NoInputs))
            {
                Configuration.KnownPlayerBorderColor = borderColor;
                Configuration.Save();
            }
            ImGui.Unindent(20f);
        }

        var showJobIcons = Configuration.ShowPartyJobIcons;
        if (ImGui.Checkbox("Show Party Job Icons", ref showJobIcons))
        {
            Configuration.ShowPartyJobIcons = showJobIcons;
            Configuration.Save();
        }

        ImGui.BeginDisabled();
        var preventAutoClose = Configuration.PreventAutoClosingOnPartyChanges;
        if (ImGui.Checkbox("Prevent Party Finder Window from Auto-Closing on Party Changes", ref preventAutoClose))
        {
            Configuration.PreventAutoClosingOnPartyChanges = preventAutoClose;
            Configuration.Save();
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), "Party Finder List Optimizations");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.BeginDisabled();
        var timeSorting = Configuration.EnableTrueTimeBasedSorting;
        if (ImGui.Checkbox("Enable True Time-Based Sorting for Party Finder", ref timeSorting))
        {
            Configuration.EnableTrueTimeBasedSorting = timeSorting;
            Configuration.Save();
        }
        ImGui.EndDisabled();

        ImGui.BeginDisabled();
        var expand100 = Configuration.ExpandListingsTo100PerPage;
        if (ImGui.Checkbox("Expand Party Finder Listings to Show 100 Per Page", ref expand100))
        {
            Configuration.ExpandListingsTo100PerPage = expand100;
            Configuration.Save();
        }
        ImGui.EndDisabled();

        var autoRefresh = Configuration.EnableAutomaticRefresh;
        if (ImGui.Checkbox("Enable Automatic Refresh for Party Finder Listings", ref autoRefresh))
        {
            Configuration.EnableAutomaticRefresh = autoRefresh;
            Configuration.Save();
        }

        if (Configuration.EnableAutomaticRefresh)
        {
            ImGui.Indent(20f);
            var interval = Configuration.AutoRefreshIntervalSeconds;
            if (ImGui.SliderInt("Refresh Interval (seconds)##refresh", ref interval, 10, 120))
            {
                Configuration.AutoRefreshIntervalSeconds = interval;
                Configuration.Save();
            }
            ImGui.Unindent(20f);
        }

        ImGui.BeginDisabled();
        var oneClickFilter = Configuration.EnableOneClickJobFilter;
        if (ImGui.Checkbox("Enable One-Click Job Filter Button (High-End Duties Only)", ref oneClickFilter))
        {
            Configuration.EnableOneClickJobFilter = oneClickFilter;
            Configuration.Save();
        }
        ImGui.EndDisabled();

        ImGui.BeginDisabled();
        var rightClick = Configuration.RightClickPlayerNameForRecruitment3;
        if (ImGui.Checkbox("Right-Click Player Name to View Their Recruitment", ref rightClick))
        {
            Configuration.RightClickPlayerNameForRecruitment3 = rightClick;
            Configuration.Save();
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), "Blacklist");
        ImGui.Separator();
        ImGui.Spacing();

        var enableBlacklist = Configuration.EnableBlacklistFeature;
        if (ImGui.Checkbox("Enable Blacklist Feature", ref enableBlacklist))
        {
            Configuration.EnableBlacklistFeature = enableBlacklist;
            Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, players on your in-game blacklist are marked with [BL] in the overlay.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh##bl_refresh"))
            plugin.PartyFinderManager.ForceRefreshBlacklist();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-reads the blacklist from the game and saves the result.");

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Overlay Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawOverlayTab()
    {
        if (!ImGui.BeginTabItem("Overlay"))
            return;

        ImGui.Spacing();

        var showOverlay = Configuration.ShowMemberInfoOverlay;
        if (ImGui.Checkbox("Show Member Info Overlay in PF Details", ref showOverlay))
        {
            Configuration.ShowMemberInfoOverlay = showOverlay;
            Configuration.Save();
        }

        ImGui.Spacing();

        using var disabled = new ImGuiDisabledScope(!Configuration.ShowMemberInfoOverlay);

        var highEndOnly = Configuration.OnlyShowOverlayForHighEndDuties;
        if (ImGui.Checkbox("Only Show Overlay for High-End Duties", ref highEndOnly))
        {
            Configuration.OnlyShowOverlayForHighEndDuties = highEndOnly;
            Configuration.Save();
        }

        var leftSide = Configuration.ShowOverlayOnLeftSide;
        if (ImGui.Checkbox("Show Overlay on Left Side (Right if Unchecked)", ref leftSide))
        {
            Configuration.ShowOverlayOnLeftSide = leftSide;
            Configuration.Save();
        }

        var showResolvedNames = Configuration.ShowResolvedPlayerNames;
        if (ImGui.Checkbox("Show Resolved Player Names in Member Info Overlay", ref showResolvedNames))
        {
            Configuration.ShowResolvedPlayerNames = showResolvedNames;
            Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, displays the actual player name (Name@World) once resolved, instead of \"Player X\".");

        var fflogsOverlay = Configuration.EnableFFLogsIntegrationOverlay;
        if (ImGui.Checkbox(
                "Enable FFLogs Integration (configure in FFLogs Integration tab)",
                ref fflogsOverlay))
        {
            Configuration.EnableFFLogsIntegrationOverlay = fflogsOverlay;
            Configuration.Save();
        }

        var tomestone = Configuration.EnableTomestoneIntegration;
        if (ImGui.Checkbox(
                "Enable Tomestone Integration (configure API key in Tomestone Integration tab)",
                ref tomestone))
        {
            Configuration.EnableTomestoneIntegration = tomestone;
            Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), "Party List Overlay");
        ImGui.Spacing();

        var partyListOverlay = Configuration.ShowPartyListOverlay;
        if (ImGui.Checkbox("Show Info for Current Party Members", ref partyListOverlay))
        {
            Configuration.ShowPartyListOverlay = partyListOverlay;
            Configuration.Save();
        }

        if (Configuration.ShowPartyListOverlay)
        {
            ImGui.Indent(20f);
            ImGui.TextWrapped(
                "Shows FFLogs and Tomestone data for your current party members in an overlay " +
                "attached to the Party Members UI element. " +
                "Requires FFLogs Integration and/or Tomestone Integration to be enabled. " +
                "Includes a duty selector dropdown for encounter-specific lookups.");

            var positionNames = Enum.GetNames(typeof(PartyListOverlayPosition));
            var currentPosition = (int)Configuration.PartyListOverlayPosition;
            ImGui.TextUnformatted("Overlay Position:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f);
            if (ImGui.Combo("##party_list_position", ref currentPosition, positionNames, positionNames.Length))
            {
                Configuration.PartyListOverlayPosition = (PartyListOverlayPosition)currentPosition;
                Configuration.Save();
            }

            var hideInDuty = Configuration.HidePartyListInDuty;
            if (ImGui.Checkbox("Hide Party List Overlay while in a duty", ref hideInDuty))
            {
                Configuration.HidePartyListInDuty = hideInDuty;
                Configuration.Save();
            }

            var hideInCombat = Configuration.HidePartyListInCombat;
            if (ImGui.Checkbox("Hide Party List Overlay while in combat", ref hideInCombat))
            {
                Configuration.HidePartyListInCombat = hideInCombat;
                Configuration.Save();
            }

            ImGui.Unindent(20f);
        }

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FFLogs Integration Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawFFLogsTab()
    {
        if (!ImGui.BeginTabItem("FFLogs Integration"))
            return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), "FFLogs API Configuration");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Client ID");
        ImGui.SetNextItemWidth(360f);
        if (ImGui.InputText("##fflogs_client_id", ref fFlogsClientIdInput, 128))
        {
            // don't save on every keystroke – save on button press
        }

        ImGui.Spacing();

        ImGui.TextUnformatted("Client Secret");
        ImGui.SetNextItemWidth(360f);
        if (ImGui.InputText("##fflogs_client_secret", ref fFlogsClientSecretInput, 128,
                            ImGuiInputTextFlags.Password))
        {
            // don't save on every keystroke – save on button press
        }

        ImGui.Spacing();

        // Capture state before the button so BeginDisabled/EndDisabled are always balanced.
        var wasTestInProgress = fFlogsTestInProgress;
        if (wasTestInProgress) ImGui.BeginDisabled();

        if (ImGui.Button("Save & Test Credentials"))
        {
            Configuration.FFLogsClientId = fFlogsClientIdInput;
            Configuration.FFLogsClientSecret = fFlogsClientSecretInput;
            Configuration.Save();
            fFlogsTestResult = string.Empty;
            fFlogsTestInProgress = true;

            _ = TestFFLogsCredentialsAsync().ContinueWith(
                t => PassportCheckerReborn.Log.Warning(t.Exception, "[PassportCheckerReborn] Unhandled error in credential test."),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        }

        if (wasTestInProgress)
        {
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextUnformatted("Testing…");
        }

        if (!string.IsNullOrEmpty(fFlogsTestResult))
        {
            ImGui.Spacing();
            var success = fFlogsTestResult.StartsWith("OK");
            ImGui.TextColored(
                success ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f) : new Vector4(0.9f, 0.2f, 0.2f, 1.0f),
                fFlogsTestResult);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Collapsible "How to obtain credentials" guide
        if (ImGui.CollapsingHeader("How to obtain FFLogs API credentials"))
        {
            const string fflogsApiUrl = "https://www.fflogs.com/api/clients/";
            const string exampleClientName = "PassportCheckerReborn";
            const string exampleRedirectUrl = "https://example.com/";

            ImGui.Spacing();
            ImGui.TextUnformatted("1. Navigate to FFLogs API portal:");
            ImGui.SameLine();
            if (ImGui.Button("Open FFLogs API Portal"))
            {
                OpenUrl(fflogsApiUrl);
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("2. Click 'Create Client' in the top-right corner.");
            ImGui.Spacing();

            ImGui.TextUnformatted("3. Enter a client name (e.g. 'PassportCheckerReborn').");
            ImGui.SameLine();
            if (ImGui.Button("Copy Client Name"))
            {
                ImGui.SetClipboardText(exampleClientName);
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("4. Provide any Redirect URL (e.g. 'https://example.com/').");
            ImGui.SameLine();
            if (ImGui.Button("Copy Redirect URL"))
            {
                ImGui.SetClipboardText(exampleRedirectUrl);
            }

            ImGui.Spacing();
            ImGui.TextWrapped("5. Leave 'Public Client' unchecked. ");
            ImGui.Spacing();
            ImGui.TextWrapped("6. Copy the generated Client ID/Secret to the fields above. ");
            ImGui.Spacing();
            ImGui.TextWrapped("7. Click 'Save & Test Credentials' to verify token status. ");
            ImGui.Spacing();
            ImGui.TextWrapped("Note: The Client Secret is only shown once. Keep it private.");
        }

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tomestone Integration Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawTomestoneTab()
    {
        if (!ImGui.BeginTabItem("Tomestone Integration"))
            return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), "Tomestone API Configuration");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "When enabled, clicking 'Tomestone' in the overlay will fetch prog point and " +
            "activity data for the current duty from the Tomestone.gg API.");
        ImGui.Spacing();

        ImGui.TextUnformatted("API Key (Bearer token)");
        ImGui.SetNextItemWidth(360f);
        if (ImGui.InputText("##tomestone_api_key", ref tomestoneApiKeyInput, 256,
                            ImGuiInputTextFlags.Password))
        {
            // Saved when the user clicks the Save button below
        }

        ImGui.SameLine();
        if (ImGui.Button("Save##ts_save"))
        {
            Configuration.TomestoneApiKey = tomestoneApiKeyInput;
            Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Collapsible "How to obtain your API key" guide
        if (ImGui.CollapsingHeader("How to obtain a Tomestone API key"))
        {
            const string tomestoneAccountUrl = "https://tomestone.gg/profile/account";

            ImGui.Spacing();
            ImGui.TextUnformatted("1. Navigate to Tomestone Account Settings:");
            ImGui.SameLine();
            if (ImGui.Button("Open Tomestone Account Page"))
            {
                OpenUrl(tomestoneAccountUrl);
            }

            ImGui.Spacing();
            ImGui.TextWrapped("2. Scroll down to the \"API access token\" section.");
            ImGui.Spacing();
            ImGui.TextWrapped("3. Click \"Generate access token\".");
            ImGui.Spacing();
            ImGui.TextWrapped("4. Copy the generated token and paste it into the field above.");
            ImGui.Spacing();
            ImGui.TextWrapped("5. Click 'Save' to store your API key.");
            ImGui.Spacing();
            ImGui.TextWrapped("Note: Keep your API token private. It grants access to your Tomestone account data.");
        }

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // About Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawAboutTab()
    {
        if (!ImGui.BeginTabItem("About"))
            return;

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Passport Checker Reborn");
        ImGui.TextUnformatted("An open-source Party Finder enhancement plugin for Final Fantasy XIV.");
        ImGui.Spacing();

        ImGui.TextUnformatted($"Version: {PassportCheckerReborn.Version}");
        ImGui.TextUnformatted("Author:  The Combat Reborn Team - LTS");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Passport Checker Reborn is an open-source alternative to the PFFinder plugin. " +
            "It shows a member-info overlay alongside party finder listings, integrates with " +
            "Tomestone.gg for quick prog-point lookups, and offers quality-of-life improvements " +
            "to the party finder UI.");

        ImGui.Spacing();

        ImGui.TextWrapped("Source code: https://github.com/FFXIV-CombatReborn/PassportCheckerReborn");
        ImGui.Spacing();

        ImGui.TextWrapped("Tomestone.gg integration inspired by the TomestoneGG Dalamud plugin.");
        ImGui.TextWrapped("Source: https://github.com/TomestoneGG/Dalamud.Tomestone");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Commands:\n" +
            "  /pfcheck           – Toggle the main overlay / status window.\n" +
            "  /pcrparty           – Toggle the party list overlay window.\n");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Cache Statistics");
        ImGui.Spacing();
        ImGui.TextUnformatted($"Resolved CIDs:          {plugin.CidCache.Count}");
        ImGui.TextUnformatted($"Blacklisted players:    {plugin.BlacklistCache.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear Cache##bl_clear"))
        {
            plugin.BlacklistCache.Clear();
            plugin.PartyFinderManager.ForceRefreshBlacklist();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clears the persisted blacklist cache, then re-reads from the game.");

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private async System.Threading.Tasks.Task TestFFLogsCredentialsAsync()
    {
        try
        {
            var result = await plugin.FFLogsService.TestCredentialsAsync(
                Configuration.FFLogsClientId,
                Configuration.FFLogsClientSecret);

            fFlogsTestResult = result ? "OK – Credentials accepted by FFLogs!" : "ERROR – Invalid credentials.";
        }
        catch (Exception ex)
        {
            fFlogsTestResult = $"ERROR – {ex.Message}";
            PassportCheckerReborn.Log.Warning(ex, "[PassportCheckerReborn] FFLogs credential test failed.");
        }
        finally
        {
            fFlogsTestInProgress = false;
        }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, $"[PassportCheckerReborn] Failed to open URL {url}");
        }
    }

    // Minimal scope wrapper so we can use `using var` without bringing in Raii
    private sealed class ImGuiDisabledScope : IDisposable
    {
        public ImGuiDisabledScope(bool disabled) { if (disabled) ImGui.BeginDisabled(); this.disabled = disabled; }
        private readonly bool disabled;
        public void Dispose() { if (disabled) ImGui.EndDisabled(); }
    }
}
