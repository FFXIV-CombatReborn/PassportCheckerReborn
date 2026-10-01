using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PassportCheckerReborn.Services;

public sealed record BlacklistCacheEntry(string Name, string World, DateTime LastSeen);

// The player's in-game blacklist as last read from the game, kept on disk so it is known before the
// game has loaded it. Keys are "Name@World", or "Name" when the world is unknown.
public sealed class BlacklistCache : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string filePath;
    private readonly Dictionary<string, BlacklistCacheEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private bool dirty;

    public BlacklistCache()
    {
        filePath = Path.Combine(PassportCheckerReborn.PluginInterface.GetPluginConfigDirectory(), "blacklist_cache.json");
        Load();
    }

    public int Count => entries.Count;

    public bool Contains(string key)
    {
        return entries.ContainsKey(key);
    }

    public void ReplaceAll(IReadOnlyCollection<string> keys)
    {
        if (IsSameSet(keys))
        {
            return;
        }

        entries.Clear();
        foreach (var key in keys)
        {
            var atIndex = key.IndexOf('@');
            var name = atIndex >= 0 ? key[..atIndex] : key;
            var world = atIndex >= 0 ? key[(atIndex + 1)..] : string.Empty;
            entries[key] = new BlacklistCacheEntry(name, world, DateTime.UtcNow);
        }

        dirty = true;
        Save();
    }

    public void Clear()
    {
        ReplaceAll([]);
    }

    public void Dispose()
    {
        Save();
    }

    private bool IsSameSet(IReadOnlyCollection<string> keys)
    {
        if (keys.Count != entries.Count)
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (!entries.ContainsKey(key))
            {
                return false;
            }
        }

        return true;
    }

    private void Save()
    {
        if (!dirty)
        {
            return;
        }

        try
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(entries, JsonOptions));
            dirty = false;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[BlacklistCache] Failed to save.");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<Dictionary<string, BlacklistCacheEntry>>(File.ReadAllText(filePath), JsonOptions);
            if (saved == null)
            {
                return;
            }

            foreach (var (key, entry) in saved)
            {
                entries[key] = entry;
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[BlacklistCache] Failed to load.");
        }
    }
}
