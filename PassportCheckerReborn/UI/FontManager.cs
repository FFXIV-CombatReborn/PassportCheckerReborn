using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using System;
using System.Collections.Generic;
using System.Threading;

namespace PassportCheckerReborn.UI;

internal static class FontManager
{
    private const int GameFontCapacity = 12;
    private const int DefaultFontCapacity = 4;

    private readonly record struct CachedFont(IFontHandle Handle, int LastUsedFrame);

    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<int, CachedFont> Handles = [];
    private static readonly Dictionary<int, CachedFont> DefaultHandles = [];

    /// <summary>The Axis game font at <paramref name="size"/> pixels.</summary>
    public static ImFontPtr GetFont(float size)
    {
        // Round to a whole pixel so near-identical sizes share one handle.
        var key = Math.Max(1, (int)MathF.Round(size));

        // The pixel-size constructor matters: GetRecommendedFamilyAndSize takes points, and feeding
        // it pixels snaps each size up to the next game font (a 22px headline comes out at 48px).
        return Resolve(Handles, GameFontCapacity, key, static px =>
            PassportCheckerReborn.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, px)));
    }

    /// <summary>Dalamud's default font at <paramref name="scale"/> times its configured size.</summary>
    public static ImFontPtr GetDefaultFont(float scale)
    {
        var key = Math.Max(1, (int)MathF.Round(PassportCheckerReborn.PluginInterface.UiBuilder.FontDefaultSizePx * scale));

        return Resolve(DefaultHandles, DefaultFontCapacity, key, static px =>
            PassportCheckerReborn.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
                e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(px))));
    }

    private static ImFontPtr Resolve(Dictionary<int, CachedFont> cache, int capacity, int key, Func<int, IFontHandle> create)
    {
        var frame = ImGui.GetFrameCount();
        lock (CacheLock)
        {
            if (!cache.TryGetValue(key, out var cached))
            {
                Evict(cache, capacity - 1, frame);
                cached = new CachedFont(create(key), frame);
            }

            cache[key] = cached with { LastUsedFrame = frame };
            if (Loaded(cached.Handle) is { } font)
            {
                return font;
            }

            // The atlas builds asynchronously; until this size is ready, draw in the nearest size
            // that is, rather than dropping back to the body font and jumping when it arrives.
            var nearest = -1;
            ImFontPtr? fallback = null;
            foreach (var (other, candidate) in cache)
            {
                if (other == key || (fallback != null && Math.Abs(other - key) >= Math.Abs(nearest - key)))
                {
                    continue;
                }

                if (Loaded(candidate.Handle) is { } ready)
                {
                    nearest = other;
                    fallback = ready;
                }
            }

            if (fallback is not { } nearestFont)
            {
                return ImGui.GetFont();
            }

            // Still in use, so it must not be the one evicted to make room.
            cache[nearest] = cache[nearest] with { LastUsedFrame = frame };
            return nearestFont;
        }
    }

    private static ImFontPtr? Loaded(IFontHandle handle)
    {
        if (!handle.Available)
        {
            return null;
        }

        try
        {
            using var locked = handle.Lock();
            var font = locked.ImFont;
            return font.IsLoaded() ? font : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Evict(Dictionary<int, CachedFont> cache, int keep, int frame)
    {
        while (cache.Count > keep)
        {
            var oldestKey = -1;
            var oldestFrame = frame;
            foreach (var (key, cached) in cache)
            {
                if (cached.LastUsedFrame < oldestFrame)
                {
                    oldestKey = key;
                    oldestFrame = cached.LastUsedFrame;
                }
            }

            if (oldestKey < 0)
            {
                return;
            }

            cache[oldestKey].Handle.Dispose();
            _ = cache.Remove(oldestKey);
        }
    }

    public static void DisposeAll()
    {
        lock (CacheLock)
        {
            foreach (var cached in Handles.Values)
            {
                cached.Handle.Dispose();
            }

            Handles.Clear();

            foreach (var cached in DefaultHandles.Values)
            {
                cached.Handle.Dispose();
            }

            DefaultHandles.Clear();
        }
    }
}
