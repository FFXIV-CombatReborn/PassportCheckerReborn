using System;

namespace PassportCheckerReborn.Players;

public sealed record BlacklistCacheEntry(string Name, string World, DateTime LastSeen);
