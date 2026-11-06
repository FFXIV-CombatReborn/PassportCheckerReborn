using Dalamud.Interface.Utility.Raii;
using System;
using System.Diagnostics;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private const string Unavailable = "Not available on this game version; it needs a plugin update.";

    private static Vector4 Muted => M3.Alpha(M3.Scheme.OnSurfaceVariant, 0.9f);

    private static float RowInset => 12f * M3.Scale;

    private static void TextRow(string label, string? supporting)
    {
        var row = M3SettingRow.Begin(label, supporting, Vector2.Zero);
        M3SettingRow.End(row);
    }

    private static void StepRow(int number, string text)
    {
        TextRow($"{number}.  {text}", null);
    }

    private static bool StepRow(int number, string text, string id, string action, FontAwesomeIcon icon)
    {
        return M3Widgets.RowButton($"{number}.  {text}###{id}", action, icon: icon);
    }

    private static void CopyStepRow(int number, string text, string id, string value)
    {
        _ = M3Widgets.RowCopy($"{number}.  {text}###{id}", value);
    }

    private static void BeginActionRow()
    {
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + RowInset);
    }

    private static void StatusLine(FontAwesomeIcon icon, string message, Vector4 color)
    {
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        var start = ImGui.GetCursorScreenPos() + new Vector2(RowInset, 0f);
        var iconSize = M3Draw.MeasureIcon(icon);
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon,
            new Vector2(start.X, start.Y + ((ImGui.GetTextLineHeight() - iconSize.Y) * 0.5f)), color);

        ImGui.SetCursorScreenPos(new Vector2(start.X + iconSize.X + M3.Space2, start.Y));
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(message);
    }

    private static string WhenAvailable(string supporting, bool unavailable)
    {
        return unavailable ? Unavailable : supporting;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[SettingsWindow] Failed to open URL {url}");
        }
    }
}
