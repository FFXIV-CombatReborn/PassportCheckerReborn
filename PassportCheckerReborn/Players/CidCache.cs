using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Players;

public sealed class CidCache : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);

    private readonly string filePath;
    private readonly Dictionary<ulong, CidCacheEntry> entries = [];
    private bool dirty;
    private Task writeTask = Task.CompletedTask;

    public CidCache()
    {
        filePath = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "cid_cache.json");
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

    public bool NeedsRefresh(ulong contentId)
    {
        var cutoff = DateTime.UtcNow - RefreshInterval;
        return entries.TryGetValue(contentId, out var entry) && entry.LastSeen < cutoff && entry.LastRefreshed < cutoff;
    }

    public void MarkRefreshed(ulong contentId)
    {
        if (entries.TryGetValue(contentId, out var entry))
        {
            entries[contentId] = entry with { LastRefreshed = DateTime.UtcNow };
            dirty = true;
        }
    }

    public void Save()
    {
        if (!dirty)
        {
            return;
        }

        dirty = false;

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
            Plugin.Log.Warning(ex, "[CidCache] Failed to save.");
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
            Plugin.Log.Warning(ex, "[CidCache] Failed to load.");
        }
    }
}
