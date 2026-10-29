using Dalamud.Interface.Utility.Raii;
using System;
using System.Diagnostics;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public partial class SettingsWindow
{
    private const string Unavailable = "Not available on this game version; it needs a plugin update.";

    private const double CopiedFeedbackSeconds = 1.5;

    private string? copiedId;
    private double copiedAt;

    private static Vector4 Muted => M3.Alpha(M3.Scheme.OnSurfaceVariant, 0.9f);

    private static float RowInset => 12f * M3.Scale;

    private static int SegmentedRow(string label, string? supporting, string id, M3Segment[] segments, int selectedIndex)
    {
        var width = M3Widgets.SegmentedWidth(segments);
        var row = M3SettingRow.Begin(label, supporting, new Vector2(width, M3Widgets.SegmentedHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var picked = M3Widgets.SegmentedButtons(id, segments, selectedIndex, width);
        M3SettingRow.End(row);
        return picked;
    }

    private static bool ButtonRow(string label, string? supporting, string id, string buttonLabel, FontAwesomeIcon icon)
    {
        var width = M3Widgets.ButtonWidth(icon, buttonLabel);
        var row = M3SettingRow.Begin(label, supporting, new Vector2(width, M3Widgets.ButtonHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var pressed = M3Widgets.Button(id, buttonLabel, M3ButtonStyle.Tonal, icon, width);
        M3SettingRow.End(row);
        return pressed;
    }

    private static void TextFieldRow(string label, string id, string hint, ref string value, int maxLength, bool password = false)
    {
        var scale = M3.Scale;
        var width = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.55f, 180f * scale, 340f * scale);
        var row = M3SettingRow.Begin(label, null, new Vector2(width, M3TextField.Height));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        M3TextField.Draw(id, hint, ref value, width, maxLength, password);
        M3SettingRow.End(row);
    }

    private static void TextRow(string label, string? supporting)
    {
        var row = M3SettingRow.Begin(label, supporting, Vector2.Zero);
        M3SettingRow.End(row);
    }

    private static void ValueRow(string label, string? supporting, string value)
    {
        var row = M3SettingRow.Begin(label, supporting, ImGui.CalcTextSize(value));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        using (ImRaii.PushColor(ImGuiCol.Text, M3.Scheme.OnSurfaceVariant))
        {
            ImGui.TextUnformatted(value);
        }

        M3SettingRow.End(row);
    }

    private static void StepRow(int number, string text)
    {
        TextRow($"{number}.  {text}", null);
    }

    private static bool StepRow(int number, string text, string id, string action, FontAwesomeIcon icon, float? width = null)
    {
        var buttonWidth = width ?? M3Widgets.ButtonWidth(icon, action);
        var row = M3SettingRow.Begin($"{number}.  {text}", null, new Vector2(buttonWidth, M3Widgets.ButtonHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var pressed = M3Widgets.Button(id, action, M3ButtonStyle.Tonal, icon, buttonWidth);
        M3SettingRow.End(row);
        return pressed;
    }

    private void CopyStepRow(int number, string text, string id, string value)
    {
        var copied = copiedId == id && ImGui.GetTime() - copiedAt < CopiedFeedbackSeconds;
        var width = MathF.Max(
            M3Widgets.ButtonWidth(FontAwesomeIcon.Copy, "Copy"),
            M3Widgets.ButtonWidth(FontAwesomeIcon.Check, "Copied"));

        if (StepRow(number, text, id, copied ? "Copied" : "Copy", copied ? FontAwesomeIcon.Check : FontAwesomeIcon.Copy, width))
        {
            ImGui.SetClipboardText(value);
            copiedId = id;
            copiedAt = ImGui.GetTime();
        }
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
