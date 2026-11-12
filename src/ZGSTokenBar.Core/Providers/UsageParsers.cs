using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record UsageParseResult(
    IReadOnlyList<UsageWindow> Windows,
    UsageCredits? Credits,
    IReadOnlyList<UsageProductShare> Products,
    string? Plan,
    string? ErrorKey)
{
    public UsageSubscriptionPeriod? Period { get; init; }
}

/// <summary>
/// Usage parsers: one file per provider under Providers/Parsers (fetch-independent, fixture-tested);
/// this file keeps the shared JSON/percent/time helpers they compose.
/// </summary>
public static partial class UsageParsers
{
    private static void AddIfPresent(
        List<UsageWindow> windows,
        string id,
        string label,
        double? used,
        DateTimeOffset? resetsAt,
        TimeSpan duration)
    {
        if (used is null && resetsAt is null) return;
        windows.Add(new UsageWindow(id, label, used, resetsAt, duration));
    }

    private static void AddPercent(
        List<UsageWindow> windows,
        string id,
        string label,
        double? percent,
        DateTimeOffset? reset,
        TimeSpan? duration)
    {
        if (percent is not { } value || value is < 0 or > 100) return;
        windows.Add(new UsageWindow(id, label, value, reset, duration));
    }

    private static void AddRatio(
        List<UsageWindow> windows,
        string id,
        string label,
        JsonElement node,
        DateTimeOffset? reset,
        TimeSpan? duration)
    {
        var used = Number(node, "used");
        var limit = Number(node, "limit");
        if (used is not >= 0 || limit is not > 0) return;
        windows.Add(new UsageWindow(id, label, JsonSupport.ClampPercent(used.Value / limit.Value * 100), reset, duration));
    }

    private static string WindowLabel(TimeSpan? duration)
    {
        var days = duration?.TotalDays ?? 0;
        if (days is >= 6 and <= 8) return "Weekly";
        if (days is >= 27 and <= 32) return "Monthly";
        return "Credits";
    }

    private static double NormalizePercent(double value) =>
        JsonSupport.ClampPercent(value is > 0 and <= 1 ? value * 100 : value);

    private static double? ParseLoosePercent(string text)
    {
        var cleaned = text.Trim().TrimEnd('%');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static JsonElement? ReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? Number(JsonElement? element, params string[] names)
    {
        if (element is not { } node) return null;
        foreach (var name in names)
        {
            if (!node.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static decimal? Decimal(JsonElement? element, params string[] names)
    {
        if (element is not { } node) return null;
        foreach (var name in names)
        {
            if (!node.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? String(JsonElement? element, params string[] names) =>
        element is { } node ? node.StringProperty(names) : null;

    private static DateTimeOffset? Time(JsonElement? element, params string[] names)
    {
        if (element is not { } node) return null;
        foreach (var name in names)
        {
            if (!node.TryGetProperty(name, out var value)) continue;
            var parsed = Time(value);
            if (parsed is not null) return parsed;
        }

        return null;
    }

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? Time(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return Time(value.GetString());
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var timestamp))
        {
            try
            {
                return timestamp > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                    : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static decimal? RawCent(JsonElement? element, string name)
    {
        if (element is not { } node || !node.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var direct)) return direct;
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (!value.TryGetProperty("val", out var inner) || inner.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return 0;
        }

        return inner.ValueKind == JsonValueKind.Number && inner.TryGetDecimal(out var cents) ? cents : null;
    }

    private static decimal? DollarCent(JsonElement? element, params string[] names)
    {
        foreach (var name in names)
        {
            var raw = RawCent(element, name);
            if (raw is { } cents)
            {
                return element is { } node
                    && node.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.Object
                    ? cents / 100m
                    : cents;
            }
        }

        return null;
    }

    private static decimal? Cents(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number >= 0)
        {
            return decimal.Truncate(number) == number ? number / 100m : number;
        }

        return null;
    }
}
