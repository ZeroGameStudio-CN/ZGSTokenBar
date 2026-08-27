using System.Text.Json;

namespace ZGSTokenBar.Core;

internal static class JsonSupport
{
    public static JsonElement? ObjectProperty(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }

        return null;
    }

    public static JsonElement? ArrayProperty(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                return value;
            }
        }

        return null;
    }

    public static string? StringProperty(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return null;
    }

    public static double? NumberProperty(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        }

        return null;
    }

    public static DateTimeOffset? ResetAt(this JsonElement element)
    {
        foreach (var name in new[] { "reset_at", "resets_at" })
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var timestamp))
            {
                try
                {
                    return timestamp > 10_000_000_000
                        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                        : DateTimeOffset.FromUnixTimeSeconds(timestamp);
                }
                catch
                {
                    return null;
                }
            }
        }

        var afterSeconds = element.NumberProperty("reset_after_seconds");
        if (afterSeconds is not > 0 || !double.IsFinite(afterSeconds.Value)) return null;
        try
        {
            return DateTimeOffset.UtcNow.AddSeconds(afterSeconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static double ClampPercent(double value) => Math.Clamp(value, 0, 100);
}
