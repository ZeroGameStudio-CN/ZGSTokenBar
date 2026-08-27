using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal enum RadarResetTimingKind
{
    Unknown,
    Exact,
    EstimatedDate,
}

internal sealed record RadarResetTiming(
    RadarResetTimingKind Kind,
    DateTimeOffset? ExactTargetAt = null,
    DateOnly? EstimatedDate = null,
    bool EstimatedFromWeekday = false)
{
    internal const int ExactRefreshIntervalMilliseconds = 1_000;
    internal const int EstimatedDateRefreshIntervalMilliseconds = 60_000;
    private static readonly TimeSpan BeijingUtcOffset = TimeSpan.FromHours(8);

    public static RadarResetTiming Resolve(RadarResetWindow? window)
    {
        if (window?.Open != true) return new(RadarResetTimingKind.Unknown);
        if (window.TargetAt is { } exactTarget)
        {
            return new(RadarResetTimingKind.Exact, ExactTargetAt: exactTarget);
        }
        if (window.OpenedAt is not { } openedAt)
        {
            return new(RadarResetTimingKind.Unknown);
        }

        if (BeijingDate(openedAt) is not { } openedDate)
        {
            return new(RadarResetTimingKind.Unknown);
        }
        if (TargetWeekday(window.Scope) is { } targetWeekday)
        {
            var offset = ((int)targetWeekday - (int)openedDate.DayOfWeek + 7) % 7;
            if (AddDays(openedDate, offset) is not { } estimatedDate)
            {
                return new(RadarResetTimingKind.Unknown);
            }
            return new(
                RadarResetTimingKind.EstimatedDate,
                EstimatedDate: estimatedDate,
                EstimatedFromWeekday: true);
        }

        if (AddDays(openedDate, 1) is not { } fallbackDate)
        {
            return new(RadarResetTimingKind.Unknown);
        }
        return new(
            RadarResetTimingKind.EstimatedDate,
            EstimatedDate: fallbackDate);
    }

    public int CalendarDaysUntil(DateTimeOffset now) =>
        EstimatedDate is { } estimatedDate && BeijingDate(now) is { } currentDate
        ? estimatedDate.DayNumber - currentDate.DayNumber
        : int.MinValue;

    public static int? RefreshIntervalMilliseconds(RadarResetWindow? window, DateTimeOffset now)
    {
        var timing = Resolve(window);
        if (timing.Kind == RadarResetTimingKind.Exact)
        {
            return timing.ExactTargetAt > now
                ? ExactRefreshIntervalMilliseconds
                : null;
        }
        if (timing.Kind == RadarResetTimingKind.EstimatedDate)
        {
            return timing.CalendarDaysUntil(now) >= 0
                ? EstimatedDateRefreshIntervalMilliseconds
                : null;
        }
        return null;
    }

    internal static DateOnly? BeijingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(value.ToOffset(BeijingUtcOffset).DateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateOnly? AddDays(DateOnly value, int days)
    {
        try
        {
            return value.AddDays(days);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static DayOfWeek? TargetWeekday(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        foreach (var target in new (string Needle, DayOfWeek Day)[]
        {
            ("周一", DayOfWeek.Monday),
            ("星期一", DayOfWeek.Monday),
            ("周二", DayOfWeek.Tuesday),
            ("星期二", DayOfWeek.Tuesday),
            ("周三", DayOfWeek.Wednesday),
            ("星期三", DayOfWeek.Wednesday),
            ("周四", DayOfWeek.Thursday),
            ("星期四", DayOfWeek.Thursday),
            ("周五", DayOfWeek.Friday),
            ("星期五", DayOfWeek.Friday),
            ("周六", DayOfWeek.Saturday),
            ("星期六", DayOfWeek.Saturday),
            ("周日", DayOfWeek.Sunday),
            ("周天", DayOfWeek.Sunday),
            ("星期日", DayOfWeek.Sunday),
            ("星期天", DayOfWeek.Sunday),
            ("monday", DayOfWeek.Monday),
            ("tuesday", DayOfWeek.Tuesday),
            ("wednesday", DayOfWeek.Wednesday),
            ("thursday", DayOfWeek.Thursday),
            ("friday", DayOfWeek.Friday),
            ("saturday", DayOfWeek.Saturday),
            ("sunday", DayOfWeek.Sunday),
        })
        {
            if (scope.Contains(target.Needle, StringComparison.OrdinalIgnoreCase))
            {
                return target.Day;
            }
        }
        return null;
    }
}
