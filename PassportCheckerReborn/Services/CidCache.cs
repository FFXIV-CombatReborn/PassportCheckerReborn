using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Services;

public sealed record CidCacheEntry(string Name, ushort WorldId, string WorldName, DateTime LastSeen);

// Content ID to last-known name and world, kept on disk between sessions. A content ID outlives name
// and world changes, so an entry may be stale; fresh data always overwrites it.
// Not thread-safe: read and written on the framework thread only.
public sealed class CidCache : IDisposable
{
    private readonly string filePath;
    private readonly Dictionary<ulong, CidCacheEntry> entries = [];
    private bool dirty;

    // Writes are chained, so an older snapshot can never land after a newer one.
    private Task writeTask = Task.CompletedTask;

    public CidCache()
    {
        filePath = Path.Combine(PassportCheckerReborn.PluginInterface.GetPluginConfigDirectory(), "cid_cache.json");
        Load();
    }

    public int Count => entries.Count;

    public bool TryGet(ulong contentId, out CidCacheEntry? entry)
    {
        return entries.TryGetValue(contentId, out entry);
    }

    public void Set(ulong contentId, string name, ushort worldId, string worldName)
    {
        if (contentId == 0 || string.IsNullOrEmpty(name))
        {
            return;
        }

        entries[contentId] = new CidCacheEntry(name, worldId, worldName, DateTime.UtcNow);
        dirty = true;
    }

    // The file runs to megabytes, so it is written off the calling thread.
    public void Save()
    {
        if (!dirty)
        {
            return;
        }

        dirty = false;

        // JSON object keys must be strings.
        var snapshot = new Dictionary<string, CidCacheEntry>(entries.Count);
        foreach (var (id, entry) in entries)
        {
            snapshot[id.ToString()] = entry;
        }

        writeTask = writeTask.ContinueWith(_ => Write(snapshot), TaskScheduler.Default);
    }

    public void Dispose()
    {
        Save();
        writeTask.Wait();
    }

    private void Write(Dictionary<string, CidCacheEntry> snapshot)
    {
        try
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[CidCache] Failed to save.");
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

            var saved = JsonSerializer.Deserialize<Dictionary<string, CidCacheEntry>>(File.ReadAllText(filePath));
            if (saved == null)
            {
                return;
            }

            foreach (var (key, entry) in saved)
            {
                if (ulong.TryParse(key, out var contentId) && contentId != 0)
                {
                    entries[contentId] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[CidCache] Failed to load.");
        }
    }
}
