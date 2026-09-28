using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using System;
using System.Collections.Generic;

namespace PassportCheckerReborn.UI;

/// <summary>
/// Supplies the fonts behind the Material type scale (<see cref="M3.TitleMedium"/> and friends).
/// Each size gets its own Axis game-font handle on the plugin's atlas, created on first use.
/// </summary>
internal static class FontManager
{
    private static readonly Dictionary<int, IFontHandle> Handles = [];

    public static ImFontPtr GetFont(float size)
    {
        // Round to a whole pixel so near-identical sizes share one handle.
        var key = Math.Max(1, (int)MathF.Round(size));

        if (!Handles.TryGetValue(key, out var handle))
        {
            // The pixel-size constructor matters: GetRecommendedFamilyAndSize takes points, and feeding
            // it pixels snaps each size up to the next game font (a 22px headline comes out at 48px).
            var style = new GameFontStyle(GameFontFamily.Axis, key);
            handle = PassportCheckerReborn.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(style);
            Handles[key] = handle;
        }

        // The atlas builds asynchronously; until it is ready, keep drawing in the current font.
        if (!handle.Available)
        {
            return ImGui.GetFont();
        }

        try
        {
            using var locked = handle.Lock();
            var font = locked.ImFont;
            return font.IsLoaded() ? font : ImGui.GetFont();
        }
        catch (InvalidOperationException)
        {
            return ImGui.GetFont();
        }
    }

    public static void DisposeAll()
    {
        foreach (var handle in Handles.Values)
        {
            handle.Dispose();
        }

        Handles.Clear();
    }
}
