using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record ClaudeUsageData(
    double? FiveHourUsedPercent,
    DateTimeOffset? FiveHourResetsAt,
    double? WeekUsedPercent,
    DateTimeOffset? WeekResetsAt,
    double? FableWeekUsedPercent,
    DateTimeOffset? FableWeekResetsAt);

public static class ClaudeUsageParser
{
    public static ClaudeUsageData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var fiveHour = root.ObjectProperty("five_hour");
        var week = root.ObjectProperty("seven_day");
        var fableWeek = FableWeekLimit(root.ArrayProperty("limits"));
        return new ClaudeUsageData(
            Utilization(fiveHour),
            fiveHour?.ResetAt(),
            Utilization(week),
            week?.ResetAt(),
            fableWeek?.NumberProperty("percent") is { } fablePercent
                ? JsonSupport.ClampPercent(fablePercent)
                : null,
            fableWeek?.ResetAt());
    }

    private static JsonElement? FableWeekLimit(JsonElement? limits)
    {
        if (limits is not { ValueKind: JsonValueKind.Array }) return null;
        foreach (var limit in limits.Value.EnumerateArray())
        {
            if (!string.Equals(
                limit.StringProperty("kind"),
                "weekly_scoped",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var displayName = limit
                .ObjectProperty("scope")?
                .ObjectProperty("model")?
                .StringProperty("display_name");
            if (string.Equals(displayName, "Fable", StringComparison.OrdinalIgnoreCase))
            {
                return limit;
            }
        }

        return null;
    }

    private static double? Utilization(JsonElement? window)
    {
        var value = window?.NumberProperty("utilization");
        return value is null ? null : JsonSupport.ClampPercent(value.Value);
    }
}

public sealed record CodexUsageData(
    IReadOnlyList<QuotaWindow> Windows,
    string? Plan);

public static class CodexUsageParser
{
    public static CodexUsageData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var source = root.ObjectProperty("rate_limit_status") ?? root;
        var rateLimit = source.ObjectProperty("rate_limit") ?? source;
        var primary = rateLimit.ObjectProperty("primary_window", "primary");
        var secondary = rateLimit.ObjectProperty("secondary_window", "secondary");

        var windows = new[] { primary, secondary }
            .Where(window => window is not null)
            .Select(window => ParseWindow(window!.Value))
            .OfType<QuotaWindow>()
            .GroupBy(window => window.Label, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(WindowOrder)
            .ToArray();
        return new CodexUsageData(
            windows,
            source.StringProperty("plan_type") ?? root.StringProperty("plan_type"));
    }

    private static QuotaWindow? ParseWindow(JsonElement window)
    {
        var seconds = window.NumberProperty("limit_window_seconds");
        var minutes = window.NumberProperty("window_minutes");
        var duration = WindowDuration(seconds, minutes);
        var used = UsedPercent(window);
        var resetsAt = window.ResetAt();
        if (duration <= TimeSpan.Zero && used is null && resetsAt is null) return null;
        var label = WindowLabel(duration);
        return new QuotaWindow(label, used, resetsAt, duration);
    }

    private static TimeSpan WindowDuration(double? seconds, double? minutes)
    {
        if (seconds is { } secondValue)
        {
            return double.IsFinite(secondValue)
                   && secondValue > 0
                   && secondValue < TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.FromSeconds(secondValue)
                : TimeSpan.Zero;
        }

        return minutes is { } minuteValue
               && double.IsFinite(minuteValue)
               && minuteValue > 0
               && minuteValue < TimeSpan.MaxValue.TotalMinutes
            ? TimeSpan.FromMinutes(minuteValue)
            : TimeSpan.Zero;
    }

    private static string WindowLabel(TimeSpan duration)
    {
        var seconds = (long)Math.Round(duration.TotalSeconds);
        return seconds switch
        {
            18_000 => "5h",
            604_800 => "7d",
            2_592_000 => "30d",
            _ when seconds > 0 && seconds % 86_400 == 0 => $"{seconds / 86_400}d",
            _ when seconds > 0 && seconds % 3_600 == 0 => $"{seconds / 3_600}h",
            _ when seconds > 0 && seconds % 60 == 0 => $"{seconds / 60}m",
            _ => "other",
        };
    }

    private static int WindowOrder(QuotaWindow window) => window.Label switch
    {
        "5h" => 0,
        "7d" => 1,
        "30d" => 2,
        _ => 3,
    };

    private static double? UsedPercent(JsonElement window)
    {
        var remaining = window.NumberProperty("remaining_percent", "remaining_percentage");
        if (remaining is not null) return JsonSupport.ClampPercent(100 - remaining.Value);
        var used = window.NumberProperty("used_percent", "used_percentage");
        if (used is not null) return JsonSupport.ClampPercent(used.Value);
        var utilization = window.NumberProperty("utilization");
        if (utilization is null) return null;
        return JsonSupport.ClampPercent(utilization.Value <= 1 ? utilization.Value * 100 : utilization.Value);
    }
}
