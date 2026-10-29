using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private const string TomestoneAccountUrl = "https://tomestone.gg/profile/account";

    private string tomestoneApiKeyInput = Plugin.Configuration.TomestoneApiKey;
    private bool tomestoneGuideExpanded;

    private void DrawTomestonePage()
    {
        var cfg = Configuration;
        var s = M3.Scheme;

        if (cfg.EnableTomestoneIntegration && !cfg.HasTomestoneKey())
        {
            M3Widgets.Banner("##tomestone_missing", "Tomestone is turned on, but no API key is saved yet. Add one below.",
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        using (M3Card.Begin("tomestone_integration", "Integration", FontAwesomeIcon.Gem))
        {
            var enabled = cfg.EnableTomestoneIntegration;
            if (M3Widgets.RowSwitch("Show Tomestone data in overlays", ref enabled,
                    "Adds a Tomestone column with prog points and clears. In the member info overlay, click Tomestone to look up the listing's duty."))
            {
                cfg.EnableTomestoneIntegration = enabled;
                cfg.Save();
            }
        }

        using (M3Card.Begin("tomestone_key", "API access", FontAwesomeIcon.Key,
                   subtitle: "Your Tomestone.gg access token, sent with each lookup."))
        {
            TextFieldRow("API key", "##tomestone_api_key", "Paste your access token", ref tomestoneApiKeyInput, 256, password: true);

            BeginActionRow();
            if (M3Widgets.Button("##tomestone_save", "Save", M3ButtonStyle.Filled, FontAwesomeIcon.Save))
            {
                tomestoneApiKeyInput = tomestoneApiKeyInput.Trim();
                cfg.TomestoneApiKey = tomestoneApiKeyInput;
                cfg.Save();
            }

            if (tomestoneApiKeyInput != cfg.TomestoneApiKey)
            {
                StatusLine(FontAwesomeIcon.PencilAlt, "Unsaved changes.", s.Warning);
            }
            else if (cfg.HasTomestoneKey())
            {
                StatusLine(FontAwesomeIcon.CheckCircle, "API key saved.", s.Success);
            }
        }

        using var guide = M3ExpandableCard.Begin("tomestone_guide", "How to get an API key", ref tomestoneGuideExpanded,
                   FontAwesomeIcon.QuestionCircle);
        if (guide.Expanded)
        {
            if (StepRow(1, "Open your Tomestone account settings.", "##tomestone_step_open", "Open", FontAwesomeIcon.ExternalLinkAlt))
            {
                OpenUrl(TomestoneAccountUrl);
            }

            StepRow(2, "Scroll down to the \"API access token\" section.");
            StepRow(3, "Click \"Generate access token\".");
            StepRow(4, "Paste the token into the field above.");
            StepRow(5, "Click Save.");
            StatusLine(FontAwesomeIcon.ShieldAlt, "Keep your token private. It grants access to your Tomestone account data.", s.Warning);
        }
    }
}
