using System;
using System.Text.Json.Serialization;

namespace PassportCheckerReborn.Players;

public sealed record CidCacheEntry(
    string Name,
    ushort WorldId,
    string WorldName,
    DateTime LastSeen,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DateTime LastRefreshed = default);
