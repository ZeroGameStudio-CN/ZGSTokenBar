using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>Antigravity (agy CLI): JSON envelope groups/buckets, legacy buckets and the text table.</summary>
public static partial class UsageParsers
{
    public static UsageParseResult? ParseAntigravity(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var trimmed = output.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return ParseAntigravityJson(trimmed);
        }

        var windows = trimmed
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 3 && AntigravityWindow(parts[1]) is not null)
            .Select(parts =>
            {
                var remaining = ParseLoosePercent(parts[2]);
                var (suffix, duration) = AntigravityWindow(parts[1])!.Value;
                var group = AntigravityGroup(parts[0]);
                return remaining is null
                    ? null
                    : new UsageWindow(
                        parts[0] + suffix,
                        group + suffix,
                        JsonSupport.ClampPercent(100 - remaining.Value),
                        Time(parts.Length > 3 ? parts[3] : null),
                        duration);
            })
            .OfType<UsageWindow>()
            .ToArray();
        var ordered = OrderAntigravity(windows).Take(8).ToArray();
        return ordered.Length == 0 ? null : new UsageParseResult(ordered, null, [], null, null);
    }

    /// <summary>
    /// agy reports one pool per model group (Gemini, Claude/GPT, preview models), each with its own
    /// weekly and 5-hour limit. The groups are independent, so the headline (first window, which the
    /// ring shows) is the group the user can still use the most: groups are ordered by their binding
    /// (lowest) remaining, highest first, and inside a group the binding window leads (weekly on a tie).
    /// Every window stays in the list so the hover shows all of them with their own reset.
    /// </summary>
    internal static IEnumerable<UsageWindow> OrderAntigravity(IReadOnlyList<UsageWindow> windows)
    {
        static double Remaining(UsageWindow window) => 100 - (window.UsedPercent ?? 100);
        static string Group(UsageWindow window)
        {
            var dot = window.Label.LastIndexOf(" · ", StringComparison.Ordinal);
            return dot > 0 ? window.Label[..dot] : window.Label;
        }
        return windows
            .Select((window, index) => (window, index))
            .GroupBy(item => Group(item.window), StringComparer.Ordinal)
            .Select(group => (
                Binding: group.Min(item => Remaining(item.window)),
                First: group.Min(item => item.index),
                Windows: group
                    .OrderBy(item => Remaining(item.window))
                    .ThenByDescending(item => item.window.Duration)
                    .ThenBy(item => item.index)
                    .Select(item => item.window)))
            .OrderByDescending(group => group.Binding)
            .ThenBy(group => group.First)
            .SelectMany(group => group.Windows);
    }

    /// <summary>Short display names for agy's model groups ("Claude and GPT models" → "Claude/GPT").</summary>
    internal static string AntigravityGroup(string group)
    {
        var name = group.Trim();
        if (name.EndsWith(" models", StringComparison.OrdinalIgnoreCase)) name = name[..^" models".Length];
        name = name.Replace(" and ", "/", StringComparison.OrdinalIgnoreCase);
        return name.Length == 0 ? group : name;
    }

    /// <summary>agy rows: "&lt;group&gt;\t&lt;Weekly|Five Hour&gt; Limit Remaining\t&lt;n&gt;%\t&lt;reset&gt;".</summary>
    private static (string Suffix, TimeSpan Duration)? AntigravityWindow(string label) =>
        label.Contains("Weekly Limit", StringComparison.Ordinal) ? (" · 7d", TimeSpan.FromDays(7))
        : label.Contains("Five Hour Limit", StringComparison.Ordinal) ? (" · 5h", TimeSpan.FromHours(5))
        : null;

    public static string[] AntigravityRows(string? output) =>
        (ParseAntigravity(output)?.Windows ?? [])
            .Take(2)
            .Select(window => $"{window.Label} {UsageFormatting.Percent(UsageFormatting.Remaining(window))}")
            .ToArray();

    private static UsageParseResult? ParseAntigravityJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && AntigravityEnvelope(root) is { } envelope) return envelope;
            var array = root.ValueKind == JsonValueKind.Array
                ? root
                : root.ArrayProperty("buckets")
                    ?? root.ArrayProperty("quotaSummaries")
                    ?? root.ArrayProperty("models")
                    ?? root.ArrayProperty("quotas");
            if (array is not { } items) return null;
            var windows = new List<UsageWindow>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                var label = String(item, "model", "displayName", "label", "name");
                if (label is null) return null;
                double? used = null;
                if (Number(item, "remainingPercent", "remaining_percent") is { } remainingPercent)
                {
                    used = JsonSupport.ClampPercent(100 - remainingPercent);
                }
                else if (Number(item, "remainingFraction", "remaining_fraction") is { } fraction)
                {
                    used = JsonSupport.ClampPercent(100 - NormalizePercent(fraction));
                }
                else if (Number(item, "usedPercent", "used_percent") is { } usedPercent)
                {
                    used = NormalizePercent(usedPercent);
                }

                windows.Add(new UsageWindow(
                    label,
                    label,
                    used,
                    Time(item, "resetTime", "reset_time", "resetsAt"),
                    TimeSpan.FromDays(7)));
            }

            return windows.Count == 0 ? null : new UsageParseResult(OrderAntigravity(windows).ToArray(), null, [], null, null);
        }
    }

    /// <summary>
    /// agy's JSON output (<c>agy -p /usage --output-format json</c>) wraps the same table: exact
    /// <c>remaining_fraction</c> buckets under <c>command.data.groups</c> plus the rounded text in
    /// <c>response</c>. The buckets win (agy rounds 3.6% to "4%"); the text is the fallback.
    /// </summary>
    private static UsageParseResult? AntigravityEnvelope(JsonElement root)
    {
        var groups = root.ObjectProperty("command") is { } command && command.ObjectProperty("data") is { } data
            ? data.ArrayProperty("groups")
            : root.ArrayProperty("groups");
        if (groups is { } list)
        {
            var windows = new List<UsageWindow>();
            foreach (var group in list.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object || group.StringProperty("name") is not { Length: > 0 } name) continue;
                if (group.ArrayProperty("buckets") is not { } buckets) continue;
                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object) continue;
                    var kind = bucket.StringProperty("window") switch
                    {
                        "weekly" => (" · 7d", TimeSpan.FromDays(7)),
                        "5h" => (" · 5h", TimeSpan.FromHours(5)),
                        _ => AntigravityWindow(bucket.StringProperty("name") ?? string.Empty),
                    };
                    if (kind is not var (suffix, duration)) continue;
                    if (Number(bucket, "remaining_fraction", "remainingFraction") is not { } fraction || !double.IsFinite(fraction)) continue;
                    var remaining = fraction <= 1 ? fraction * 100 : fraction;
                    windows.Add(new UsageWindow(
                        name + suffix,
                        AntigravityGroup(name) + suffix,
                        JsonSupport.ClampPercent(100 - remaining),
                        Time(bucket, "reset_time", "resetTime"),
                        duration));
                }
            }

            if (windows.Count > 0) return new UsageParseResult(OrderAntigravity(windows).Take(8).ToArray(), null, [], null, null);
        }

        return root.StringProperty("response") is { Length: > 0 } text && !text.TrimStart().StartsWith('{')
            ? ParseAntigravity(text)
            : null;
    }
}
