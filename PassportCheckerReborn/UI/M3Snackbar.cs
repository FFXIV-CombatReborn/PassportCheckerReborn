using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace PassportCheckerReborn.UI;

internal static class M3Snackbar
{
    public const float ShortDuration = 4f;
    public const float LongDuration = 8f;

    private const int MaxQueued = 8;
    private const float FadeDuration = 0.15f;

    private sealed record Entry(string Message, string? ActionLabel, Action? OnAction, float Duration);

    private static readonly Queue<Entry> Queue = [];
    private static Entry? Current;

    // Both clocks only run on frames the snackbar is drawn, so a message queued while its window is
    // closed still gets its full time on screen.
    private static float Age;
    private static float Remaining;
    private static bool Hovered;
    private static int LastFrame = -1;

    public static void Show(string message, string? actionLabel = null, Action? onAction = null, float duration = ShortDuration)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        // Dropping the oldest keeps a burst of clicks from backing the queue up.
        while (Queue.Count >= MaxQueued)
        {
            _ = Queue.Dequeue();
        }

        Queue.Enqueue(new Entry(message, actionLabel, onAction, MathF.Max(1f, duration)));
    }

    public static void Clear()
    {
        Queue.Clear();
        Current = null;
        Hovered = false;
    }

    public static void Draw(Vector2 areaMin, Vector2 areaMax)
    {
        if (Current == null)
        {
            if (!Queue.TryDequeue(out var next))
            {
                return;
            }

            Current = next;
            Age = 0f;
            Remaining = next.Duration;
        }

        // Advance once per frame, however many windows host the queue.
        var frame = ImGui.GetFrameCount();
        if (frame != LastFrame)
        {
            LastFrame = frame;
            var delta = ImGui.GetIO().DeltaTime;
            Age += delta;
            if (!Hovered)
            {
                Remaining -= delta;
            }
        }

        if (Remaining <= -FadeDuration)
        {
            Current = null;
            Hovered = false;
            return;
        }

        var entry = Current;
        var alpha = MathF.Min(Math.Clamp(Age / FadeDuration, 0f, 1f), Math.Clamp(1f + (Remaining / FadeDuration), 0f, 1f));

        var s = M3.Scheme;
        var scale = M3.Scale;
        var margin = 16f * scale;
        var shadow = 8f * scale;
        var padding = 16f * scale;
        var areaWidth = areaMax.X - areaMin.X;
        var maxWidth = MathF.Min(560f * scale, areaWidth - (margin * 2f));
        if (maxWidth < 96f * scale)
        {
            return;
        }

        var hasAction = !string.IsNullOrEmpty(entry.ActionLabel);
        var actionWidth = hasAction ? M3Widgets.ButtonWidth(FontAwesomeIcon.None, entry.ActionLabel!) : 0f;
        var closeWidth = M3Widgets.IconButtonSize;
        var trailing = actionWidth + closeWidth + (8f * scale);
        var textWrap = MathF.Max(32f * scale, maxWidth - padding - trailing);
        var textSize = ImGui.CalcTextSize(entry.Message, false, textWrap);

        var width = MathF.Min(maxWidth, MathF.Max(280f * scale, padding + textSize.X + (16f * scale) + trailing));
        var height = MathF.Max(48f * scale, textSize.Y + (28f * scale));
        var min = new Vector2(areaMin.X + ((areaWidth - width) * 0.5f), areaMax.Y - margin - height);
        var max = min + new Vector2(width, height);

        // A child window submitted last is drawn, and hit-tested, above its siblings. It is sized to
        // hold the shadow, and stays inside the area so the host window never grows to fit it.
        ImGui.SetCursorScreenPos(min - new Vector2(shadow, shadow));
        using var child = ImRaii.Child("##m3_snackbar", new Vector2(width, height) + new Vector2(shadow * 2f, shadow * 2f), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav);
        if (!child)
        {
            return;
        }

        Hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        // ImGui folds the style alpha into every colour it hands out, M3.U32 included.
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, alpha);
        var drawList = ImGui.GetWindowDrawList();

        M3Draw.Elevation(drawList, min, max, M3.ShapeExtraSmall, 3);
        drawList.AddRectFilled(min, max, M3.U32(s.InverseSurface), M3.ShapeExtraSmall);
        _ = M3Draw.WrappedText(entry.Message, new Vector2(min.X + padding, min.Y + ((height - textSize.Y) * 0.5f)), textWrap, s.InverseOnSurface);

        var dismiss = false;
        var cursorX = max.X - (4f * scale) - closeWidth;
        if (hasAction)
        {
            cursorX -= actionWidth;
            ImGui.SetCursorScreenPos(new Vector2(cursorX, min.Y + ((height - M3Widgets.ButtonHeight) * 0.5f)));
            if (ActionButton(entry.ActionLabel!, actionWidth))
            {
                entry.OnAction?.Invoke();
                dismiss = true;
            }

            cursorX += actionWidth;
        }

        ImGui.SetCursorScreenPos(new Vector2(cursorX, min.Y + ((height - closeWidth) * 0.5f)));
        if (M3Widgets.IconButton("##m3_snackbar_close", FontAwesomeIcon.Times, null, tint: s.InverseOnSurface))
        {
            dismiss = true;
        }

        if (dismiss)
        {
            // Straight into the fade-out rather than gone mid-frame.
            Remaining = MathF.Min(Remaining, 0f);
            Hovered = false;
        }
    }

    private static bool ActionButton(string label, float width)
    {
        var s = M3.Scheme;
        var height = M3Widgets.ButtonHeight;

        var pressed = ImGui.InvisibleButton("##m3_snackbar_action", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        if (hovered || held)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.InversePrimary, held ? M3.StatePressed : M3.StateHover), height * 0.5f);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(min + ((max - min - textSize) * 0.5f), M3.U32(s.InversePrimary), label);
        return pressed;
    }
}
