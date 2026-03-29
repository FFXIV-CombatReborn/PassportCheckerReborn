using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace PassportCheckerReborn.Services;

/// <summary>
/// A single persisted blacklist entry holding the player's name and world.
/// </summary>
/// <param name="Name">The player's character name.</param>
/// <param name="World">The player's home-world name (empty string if unknown).</param>
/// <param name="LastSeen">UTC timestamp of the most recent time this entry was written.</param>
public sealed record BlacklistCacheEntry(
    string Name,
    string World,
    DateTime LastSeen);

/// <summary>
/// Persistent cache of the local player's blacklist entries.
/// Stored as a JSON file in the plugin configuration directory so the blacklist
/// is available immediately on plugin load without requiring the BlackList addon
/// to have been opened in the current session.
///
/// <para>
/// Treated as a snapshot: whenever a live read from <c>BlackListStringArray</c>
/// succeeds, the entire cache is replaced with the current game state.
/// </para>
/// </summary>
public sealed class BlacklistCache : IDisposable
{
    private readonly string filePath;

    // Key: "Name@World" or "Name" (no world) – same format as blacklistedPlayers in PartyFinderManager.
    private readonly Dictionary<string, BlacklistCacheEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private bool dirty;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public BlacklistCache()
    {
        filePath = Path.Combine(
            PassportCheckerReborn.PluginInterface.GetPluginConfigDirectory(),
            "blacklist_cache.json");

        Load();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Number of entries currently held in the cache.</summary>
    public int Count => entries.Count;

    /// <summary>Returns <c>true</c> if the given key is present in the cache.</summary>
    public bool Contains(string key) => entries.ContainsKey(key);

    /// <summary>
    /// Enumerates all cached keys (each in "Name@World" or "Name" format)
    /// for seeding the in-memory blacklist dict on startup.
    /// </summary>
    public IEnumerable<string> GetAllKeys() => entries.Keys;

    /// <summary>
    /// Replaces the entire cache with the supplied live snapshot and immediately
    /// saves to disk. Pass the keys from the <c>newEntries</c> dict built inside
    /// <c>ReadBlacklistFromAddon</c>.
    /// </summary>
    public void ReplaceAll(IEnumerable<string> keys)
    {
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

    /// <summary>Removes all entries and saves an empty cache to disk.</summary>
    public void Clear()
    {
        entries.Clear();
        dirty = true;
        Save();
    }

    /// <summary>Flushes any pending changes to disk. No-op if the cache is not dirty.</summary>
    public void Save()
    {
        if (!dirty)
            return;

        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(entries, JsonOptions);
            File.WriteAllText(filePath, json);
            dirty = false;

            PassportCheckerReborn.Log.Debug($"[BlacklistCache] Saved {entries.Count} entries to disk.");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[BlacklistCache] Failed to save.");
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => Save();

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath))
                return;

            var json = File.ReadAllText(filePath);
            var deserialised = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, BlacklistCacheEntry>>(json, JsonOptions);
            if (deserialised == null)
                return;

            foreach (var (key, entry) in deserialised)
                entries[key] = entry;

            PassportCheckerReborn.Log.Debug($"[BlacklistCache] Loaded {entries.Count} entries from disk.");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[BlacklistCache] Failed to load.");
        }
    }
}
