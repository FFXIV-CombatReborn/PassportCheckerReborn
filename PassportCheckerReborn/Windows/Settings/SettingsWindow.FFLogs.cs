using System;
using System.Numerics;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private const string FFLogsClientsUrl = "https://www.fflogs.com/api/clients/";
    private const string FFLogsExampleClientName = "PassportCheckerReborn";
    private const string FFLogsExampleRedirectUrl = "https://example.com/";

    private string fflogsClientIdInput = Plugin.Configuration.FFLogsClientId;
    private string fflogsClientSecretInput = Plugin.Configuration.FFLogsClientSecret;
    private string fflogsTestMessage = string.Empty;
    private bool fflogsTestSucceeded;
    private bool fflogsTestInProgress;
    private bool fflogsGuideExpanded;

    private void DrawFFLogsPage()
    {
        var cfg = Configuration;
        var s = M3.Scheme;

        if (cfg.EnableFFLogsIntegrationOverlay && !cfg.HasFFLogsCredentials())
        {
            M3Widgets.Banner("##fflogs_missing", "FFLogs is turned on, but no API client is saved yet. Add one below.",
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        using (M3Card.Begin("fflogs_integration", "Integration", FontAwesomeIcon.ChartBar))
        {
            var enabled = cfg.EnableFFLogsIntegrationOverlay;
            if (M3Widgets.RowSwitch("Show FFLogs data in overlays", ref enabled,
                    "Adds an FFLogs column with clears and parses to the member info and party list overlays."))
            {
                cfg.EnableFFLogsIntegrationOverlay = enabled;
                cfg.Save();
            }
        }

        using (M3Card.Begin("fflogs_client", "API client", FontAwesomeIcon.Key,
                   subtitle: "Create a client on FFLogs, then paste its ID and secret here."))
        {
            TextFieldRow("Client ID", "##fflogs_client_id", "Paste your client ID", ref fflogsClientIdInput, 128);
            TextFieldRow("Client secret", "##fflogs_client_secret", "Paste your client secret", ref fflogsClientSecretInput, 128, password: true);

            var testing = fflogsTestInProgress;
            BeginActionRow();
            if (M3Widgets.Button("##fflogs_save", testing ? "Testing…" : "Save & test", M3ButtonStyle.Filled,
                    testing ? FontAwesomeIcon.HourglassHalf : FontAwesomeIcon.Plug, enabled: !testing))
            {
                SaveAndTestFFLogsCredentials();
            }

            var dirty = fflogsClientIdInput != cfg.FFLogsClientId || fflogsClientSecretInput != cfg.FFLogsClientSecret;
            if (testing)
            {
                StatusLine(FontAwesomeIcon.HourglassHalf, "Checking your credentials with FFLogs…", Muted);
            }
            else if (dirty)
            {
                StatusLine(FontAwesomeIcon.PencilAlt, "Unsaved changes.", s.Warning);
            }
            else if (!string.IsNullOrEmpty(fflogsTestMessage))
            {
                StatusLine(fflogsTestSucceeded ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.TimesCircle,
                    fflogsTestMessage, fflogsTestSucceeded ? s.Success : s.Error);
            }
        }

        using (var guide = M3ExpandableCard.Begin("fflogs_guide", "How to get API credentials", ref fflogsGuideExpanded,
                   FontAwesomeIcon.QuestionCircle))
        {
            if (guide.Expanded)
            {
                if (StepRow(1, "Open the FFLogs API clients page.", "##fflogs_step_open", "Open", FontAwesomeIcon.ExternalLinkAlt))
                {
                    OpenUrl(FFLogsClientsUrl);
                }

                StepRow(2, "Click \"Create Client\" in the top-right corner.");
                CopyStepRow(3, $"Enter a client name, such as {FFLogsExampleClientName}.", "##fflogs_step_name", FFLogsExampleClientName);
                CopyStepRow(4, $"Enter any redirect URL, such as {FFLogsExampleRedirectUrl}", "##fflogs_step_redirect", FFLogsExampleRedirectUrl);
                StepRow(5, "Leave \"Public Client\" unchecked.");
                StepRow(6, "Copy the generated client ID and secret into the fields above.");
                StepRow(7, "Click Save & test to check them.");
                StatusLine(FontAwesomeIcon.ShieldAlt, "The client secret is only shown once. Keep it private.", s.Warning);
            }
        }
    }

    private void SaveAndTestFFLogsCredentials()
    {
        fflogsClientIdInput = fflogsClientIdInput.Trim();
        fflogsClientSecretInput = fflogsClientSecretInput.Trim();
        Configuration.FFLogsClientId = fflogsClientIdInput;
        Configuration.FFLogsClientSecret = fflogsClientSecretInput;
        Configuration.Save();
        fflogsTestMessage = string.Empty;
        fflogsTestInProgress = true;

        _ = TestFFLogsCredentialsAsync();
    }

    private async Task TestFFLogsCredentialsAsync()
    {
        try
        {
            var accepted = await plugin.FFLogsService.TestCredentialsAsync(
                Configuration.FFLogsClientId,
                Configuration.FFLogsClientSecret);

            fflogsTestSucceeded = accepted;
            fflogsTestMessage = accepted ? "FFLogs accepted these credentials." : "FFLogs rejected these credentials.";
        }
        catch (Exception ex)
        {
            fflogsTestSucceeded = false;
            fflogsTestMessage = $"The test failed: {ex.Message}";
            Plugin.Log.Warning(ex, "[SettingsWindow] FFLogs credential test failed.");
        }
        finally
        {
            fflogsTestInProgress = false;
        }
    }
}
