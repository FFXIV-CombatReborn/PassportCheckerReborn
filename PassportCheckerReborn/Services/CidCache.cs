using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace PassportCheckerReborn.Services;

/// <summary>
/// A single cached entry mapping a Content ID to a player's last-known name and world.
/// </summary>
/// <param name="Name">The player's last-known character name.</param>
/// <param name="WorldId">The player's last-known home-world ID.</param>
/// <param name="WorldName">The player's last-known home-world name (denormalised for display).</param>
/// <param name="LastSeen">UTC timestamp of the most recent time fresh data was written for this entry.</param>
public sealed record CidCacheEntry(
    string Name,
    ushort WorldId,
    string WorldName,
    DateTime LastSeen);

/// <summary>
/// Persistent cache that maps player Content IDs to their last-known name and world.
/// Stored as a JSON file in the plugin configuration directory so resolved names
/// survive plugin reloads and game restarts.
///
/// <para>
/// A player's Content ID is stable even when they change their character name or
/// home world, so cached entries may become stale. The cache is therefore treated as
/// a <em>fallback</em>: any fresh data obtained from PF listing packets or a
/// CharaCard (adventure plate) response always overwrites an existing entry.
/// </para>
/// </summary>
public sealed class CidCache : IDisposable
{
    private readonly string filePath;
    private readonly Dictionary<ulong, CidCacheEntry> entries = [];
    private bool dirty;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CidCache()
    {
        filePath = Path.Combine(
            PassportCheckerReborn.PluginInterface.GetPluginConfigDirectory(),
            "cid_cache.json");

        Load();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the number of entries currently held in the cache.
    /// </summary>
    public int Count => entries.Count;

    /// <summary>
    /// Attempts to retrieve the cached entry for the given Content ID.
    /// </summary>
    public bool TryGet(ulong contentId, out CidCacheEntry? entry)
        => entries.TryGetValue(contentId, out entry);

    /// <summary>
    /// Writes (or overwrites) the cached entry for the given Content ID with
    /// freshly-obtained data and marks the cache as dirty.
    /// </summary>
    public void Set(ulong contentId, string name, ushort worldId, string worldName)
    {
        if (contentId == 0 || string.IsNullOrEmpty(name))
            return;

        entries[contentId] = new CidCacheEntry(name, worldId, worldName, DateTime.UtcNow);
        dirty = true;
    }

    /// <summary>
    /// Flushes any pending changes to disk. No-op if the cache is not dirty.
    /// </summary>
    public void Save()
    {
        if (!dirty)
            return;

        try
        {
            // JSON doesn't allow integer keys, so we serialise the dictionary
            // with the Content ID rendered as a decimal string.
            var serialisable = new Dictionary<string, CidCacheEntry>(entries.Count);
            foreach (var (id, entry) in entries)
                serialisable[id.ToString()] = entry;

            var json = System.Text.Json.JsonSerializer.Serialize(serialisable, JsonOptions);
            File.WriteAllText(filePath, json);
            dirty = false;

            PassportCheckerReborn.Log.Debug($"[CidCache] Saved {entries.Count} entries to disk.");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[CidCache] Failed to save CID cache.");
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
            var deserialised = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CidCacheEntry>>(json, JsonOptions);
            if (deserialised == null)
                return;

            foreach (var (key, entry) in deserialised)
            {
                if (ulong.TryParse(key, out var contentId) && contentId != 0)
                    entries[contentId] = entry;
            }

            PassportCheckerReborn.Log.Debug($"[CidCache] Loaded {entries.Count} entries from disk.");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[CidCache] Failed to load CID cache.");
        }
    }
}
