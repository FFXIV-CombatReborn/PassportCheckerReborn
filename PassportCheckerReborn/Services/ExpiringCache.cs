using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PassportCheckerReborn.Services;

internal sealed class ExpiringCache<TKey, TValue>(TimeSpan lifetime) where TKey : notnull
{
    private const int PruneThreshold = 1024;

    private readonly ConcurrentDictionary<TKey, (TValue Value, DateTime Expiry)> entries = new();

    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (entries.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.Expiry)
        {
            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        var now = DateTime.UtcNow;
        if (entries.Count >= PruneThreshold)
        {
            foreach (var (staleKey, entry) in entries)
            {
                if (entry.Expiry <= now)
                {
                    entries.TryRemove(staleKey, out _);
                }
            }
        }

        entries[key] = (value, now + lifetime);
    }
}
