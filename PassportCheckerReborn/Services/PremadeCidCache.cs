using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PassportCheckerReborn.Services;

/// <summary>
/// Read-only cache of CID → player-name mappings bundled as an embedded resource
/// inside the plugin assembly. This list ships with the plugin and is updated
/// together with plugin updates, providing an instant lookup for well-known
/// players without requiring a CharaCard round-trip or prior PF encounter.
///
/// <para>
/// The embedded resource uses the same JSON schema as <see cref="CidCache"/>:
/// a dictionary keyed by the Content ID rendered as a decimal string, with
/// <see cref="CidCacheEntry"/> values.
/// </para>
/// </summary>
public sealed class PremadeCidCache
{
    private readonly Dictionary<ulong, CidCacheEntry> entries = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The embedded-resource name. Must match the default resource naming:
    /// {RootNamespace}.{FolderPath}.{FileName} with dots replacing path separators.
    /// </summary>
    private const string ResourceName = "PassportCheckerReborn.Data.premade_cid_cache.json";

    private const string FileName = "premade_cid_cache.json";

    private readonly string filePath = Path.Combine(
            PassportCheckerReborn.PluginInterface.GetPluginConfigDirectory(),
            FileName);

    public PremadeCidCache()
    {
        Load();
        EnsureFileOnDisk();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the number of entries in the premade list.
    /// </summary>
    public int Count => entries.Count;

    /// <summary>
    /// Attempts to retrieve the premade entry for the given Content ID.
    /// </summary>
    public bool TryGet(ulong contentId, out CidCacheEntry? entry)
        => entries.TryGetValue(contentId, out entry);

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName);

            if (stream == null)
            {
                //PassportCheckerReborn.Log.Warning($"[PremadeCidCache] Embedded resource '{ResourceName}' not found.");
                return;
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            var deserialised = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CidCacheEntry>>(json, JsonOptions);
            if (deserialised == null)
                return;

            foreach (var (key, entry) in deserialised)
            {
                if (ulong.TryParse(key, out var contentId) && contentId != 0)
                    entries[contentId] = entry;
            }

            //PassportCheckerReborn.Log.Debug($"[PremadeCidCache] Loaded {entries.Count} entries from embedded resource.");
        }
        catch (Exception)
        {
            //PassportCheckerReborn.Log.Warning(ex, "[PremadeCidCache] Failed to load premade CID cache from embedded resource.");
        }
    }

    private void EnsureFileOnDisk()
    {
        try
        {
            if (File.Exists(filePath))
            {
                var existingJson = File.ReadAllText(filePath);
                var existingEntries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CidCacheEntry>>(existingJson, JsonOptions);
                var existingCount = existingEntries?.Count ?? 0;

                if (existingCount >= entries.Count)
                    return;

                File.Delete(filePath);
            }

            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName);

            if (stream == null)
                return;

            var dir = Path.GetDirectoryName(filePath);
            if (dir != null)
                Directory.CreateDirectory(dir);

            using var fileStream = File.Create(filePath);
            stream.CopyTo(fileStream);
        }
        catch (Exception)
        {
        }
    }
}
