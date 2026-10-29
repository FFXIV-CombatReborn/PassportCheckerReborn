using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private const string RepoUrl = "https://github.com/FFXIV-CombatReborn/PassportCheckerReborn";

    private void DrawAboutPage()
    {
        var s = M3.Scheme;

        using (M3Card.Begin("about_hero", null, style: M3CardStyle.Elevated))
        {
            using (ImRaii.PushFont(M3.TitleLarge))
            using (ImRaii.PushColor(ImGuiCol.Text, s.Primary))
            {
                ImGui.TextWrapped("Passport Checker Reborn");
            }

            ImGui.Dummy(new Vector2(0f, M3.Space1));

            using (ImRaii.PushColor(ImGuiCol.Text, M3.Alpha(s.OnSurfaceVariant, 0.95f)))
            {
                ImGui.TextWrapped(
                    "An open-source alternative to the PFFinder plugin. It shows a member info overlay beside " +
                    "Party Finder listings, looks up prog points on Tomestone.gg and FFLogs, and adds " +
                    "quality-of-life improvements to the Party Finder.");
            }

            ImGui.Dummy(new Vector2(0f, M3.Space2));

            _ = M3Widgets.Pill("##about_version", $"v{Plugin.Version}", s.Primary, FontAwesomeIcon.CodeBranch);
            ImGui.SameLine(0f, M3.Space1);
            _ = M3Widgets.Pill("##about_author", "The Combat Reborn Team - LTS", s.Tertiary, FontAwesomeIcon.Users);

            ImGui.Dummy(new Vector2(0f, M3.Space2));

            if (M3Widgets.Button("##about_kofi", "Support on Ko-fi", M3ButtonStyle.Tonal, FontAwesomeIcon.MugHot))
            {
                OpenUrl(KofiUrl);
            }

            ImGui.SameLine(0f, M3.Space2);

            if (M3Widgets.Button("##about_repo", "Source code", M3ButtonStyle.Outlined, FontAwesomeIcon.CodeBranch))
            {
                OpenUrl(RepoUrl);
            }
        }

        using (M3Card.Begin("about_commands", "Commands", FontAwesomeIcon.Terminal))
        {
            TextRow("/pcr", "Opens or closes this window. /pfchecker does the same.");
            TextRow("/pcrparty", "Shows or hides the party list overlay.");
        }

        using (M3Card.Begin("about_caches", "Caches", FontAwesomeIcon.Database))
        {
            ValueRow("Resolved players", "Content IDs already matched to a name and world.", plugin.CidCache.Count.ToString());

            const string clearLabel = "Clear";
            var countText = plugin.BlacklistCache.Count.ToString();
            var countSize = ImGui.CalcTextSize(countText);
            var buttonWidth = M3Widgets.ButtonWidth(FontAwesomeIcon.TrashAlt, clearLabel);
            var controlSize = new Vector2(countSize.X + M3.Space3 + buttonWidth, M3Widgets.ButtonHeight);

            var row = M3SettingRow.Begin("Blacklisted players", "Your in-game blacklist, as last read from the game.", controlSize);
            var origin = row.ControlPosition;

            ImGui.SetCursorScreenPos(origin + new Vector2(0f, (controlSize.Y - countSize.Y) * 0.5f));
            using (ImRaii.PushColor(ImGuiCol.Text, s.OnSurfaceVariant))
            {
                ImGui.TextUnformatted(countText);
            }

            ImGui.SetCursorScreenPos(origin + new Vector2(countSize.X + M3.Space3, 0f));
            if (M3Widgets.Button("##blacklist_clear", clearLabel, M3ButtonStyle.Danger, FontAwesomeIcon.TrashAlt, buttonWidth,
                    tooltip: "Clears the saved blacklist cache, then re-reads it from the game."))
            {
                plugin.BlacklistCache.Clear();
                plugin.BlacklistMonitor.Refresh();
            }

            M3SettingRow.End(row);
        }
    }
}
