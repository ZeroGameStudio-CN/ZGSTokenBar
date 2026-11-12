using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record UsageLedgerEvent(DateTimeOffset At, long Tokens, decimal? CostUsd);

public static class UsageLedgerReader
{
    public static UsageLedger Summarize(IEnumerable<UsageLedgerEvent> events, DateTimeOffset now)
    {
        var origin = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var today = Empty();
        var week = Empty();
        var month = Empty();
        foreach (var usage in events)
        {
            if (usage.Tokens < 0 || usage.At > now.AddMinutes(5)) continue;
            if (usage.At >= origin) today = Add(today, usage);
            if (usage.At >= now.AddDays(-7)) week = Add(week, usage);
            if (usage.At >= now.AddDays(-30)) month = Add(month, usage);
        }

        return new UsageLedger(today, week, month);
    }

    public static IReadOnlyList<UsageLedgerEvent> Parse(string? contents)
    {
        if (string.IsNullOrWhiteSpace(contents)) return [];
        var trimmed = contents.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(contents);
                return ReadElement(document.RootElement);
            }
            catch (JsonException)
            {
                return [];
            }
        }

        var events = new List<UsageLedgerEvent>();
        foreach (var line in contents.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                events.AddRange(ReadElement(document.RootElement));
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return events;
    }

    public static UsageLedger? ReadGrok(string grokHome, DateTimeOffset now)
    {
        try
        {
            var events = new List<UsageLedgerEvent>();
            var log = Path.Combine(grokHome, "logs", "unified.jsonl");
            if (File.Exists(log)) events.AddRange(Parse(ReadBounded(log, 1_048_576)));
            var sessions = Path.Combine(grokHome, "sessions");
            if (Directory.Exists(sessions))
            {
                var files = 0;
                foreach (var project in Directory.EnumerateDirectories(sessions))
                {
                    foreach (var session in Directory.EnumerateDirectories(project))
                    {
                        if (files++ >= 64) break;
                        var usage = Path.Combine(session, "usage.json");
                        if (!File.Exists(usage)) continue;
                        events.AddRange(Parse(ReadBounded(usage, 262_144)));
                    }
                }
            }

            if (events.Count == 0) return null;
            var summary = Summarize(events, now);
            return summary.ThirtyDays.Tokens == 0 && summary.ThirtyDays.ApiEquivalentCost is null or 0
                ? null
                : summary;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<UsageLedgerEvent> ReadElement(JsonElement element)
    {
        var events = new List<UsageLedgerEvent>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) events.AddRange(ReadElement(item));
            return events;
        }

        if (element.ValueKind != JsonValueKind.Object) return events;
        if (element.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in turns.EnumerateArray()) events.AddRange(ReadElement(turn));
        }

        var when = ReadTime(element, "endedAt", "timestamp", "at", "time");
        var tokens = ReadLong(element, "totalTokens", "tokens")
            ?? (element.TryGetProperty("usage", out var usage) ? ReadLong(usage, "totalTokens", "tokens") : null);
        var cost = ReadDecimal(element, "costUsd", "cost", "apiEquivalentUsd");
        if (when is { } at && tokens is { } count) events.Add(new UsageLedgerEvent(at, count, cost));
        return events;
    }

    private static UsageLedgerPeriod Empty() => new(0, null);

    private static UsageLedgerPeriod Add(UsageLedgerPeriod period, UsageLedgerEvent usage)
    {
        decimal? cost = period.ApiEquivalentCost;
        if (usage.CostUsd is { } next)
        {
            cost = (cost ?? 0) + next;
        }

        return new UsageLedgerPeriod(period.Tokens + usage.Tokens, cost);
    }

    private static string ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(stream.Length, maximum);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }

    private static DateTimeOffset? ReadTime(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static long? ReadLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0) return number;
        }

        return null;
    }

    private static decimal? ReadDecimal(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        }

        return null;
    }
}
