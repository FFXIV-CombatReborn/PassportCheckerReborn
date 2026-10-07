using System;
using System.Text.Json;

namespace PassportCheckerReborn.Services;

// False or null, rather than throwing, when the JSON is not the expected shape.
internal static class JsonExtensions
{
    public static bool TryGetPath(this JsonElement element, out JsonElement result, params ReadOnlySpan<string> path)
    {
        result = element;
        foreach (var name in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out result))
            {
                return false;
            }
        }

        return result.ValueKind != JsonValueKind.Null;
    }

    public static double? GetNumberOrNull(this JsonElement element, string name)
    {
        return element.TryGetPath(out var value, name) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    public static string? GetStringOrNull(this JsonElement element, string name)
    {
        return element.TryGetPath(out var value, name) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
