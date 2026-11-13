namespace ZGSTokenBar.Core;

public enum TokenActivityGrain
{
    Daily,
    Weekly,
}

/// <summary>
/// Compact Codex-style activity window: <see cref="WeekCount"/> weeks of local days,
/// Monday-first, ending in the week that contains <see cref="Today"/>.
/// </summary>
public sealed record TokenActivityView(
    DateOnly Today,
    DateOnly StartMonday,
    int WeekCount,
    IReadOnlyList<long> Days,
    long ThisWeekTokens,
    long DayAverage,
    long WeekAverage,
    long AllTimeTotal,
    TokenActivityGrain Grain = TokenActivityGrain.Daily,
    long TodayTokens = 0)
{
    public const int DefaultWeekCount = 12;

    public IReadOnlyList<long> WeekTotals
    {
        get
        {
            var weeks = new long[WeekCount];
            for (var week = 0; week < WeekCount; week++)
            {
                long sum = 0;
                var origin = week * 7;
                for (var day = 0; day < 7 && origin + day < Days.Count; day++)
                    sum = SaturatingAdd(sum, Days[origin + day]);
                weeks[week] = sum;
            }
            return weeks;
        }
    }

    internal static long SaturatingAdd(long left, long right)
    {
        var sum = (ulong)Math.Max(0, left) + (ulong)Math.Max(0, right);
        return sum > long.MaxValue ? long.MaxValue : (long)sum;
    }
}

/// <summary>Builds the compact activity window from local-day totals (Codex session ledger).</summary>
public static class TokenActivityCalendar
{
    public static DateOnly StartMonday(DateOnly today, int weekCount = TokenActivityView.DefaultWeekCount)
    {
        weekCount = Math.Max(1, weekCount);
        var offset = ((int)today.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return today.AddDays(-offset - (weekCount - 1) * 7);
    }

    public static TokenActivityView FromDailyTotals(
        IReadOnlyDictionary<DateOnly, long> byDay,
        DateOnly today,
        long allTimeTotal,
        int weekCount = TokenActivityView.DefaultWeekCount,
        TokenActivityGrain grain = TokenActivityGrain.Daily)
    {
        weekCount = Math.Max(1, weekCount);
        var start = StartMonday(today, weekCount);
        var days = new long[weekCount * 7];
        long window = 0;
        var elapsedDays = 0;
        for (var i = 0; i < days.Length; i++)
        {
            var date = start.AddDays(i);
            if (date > today) continue;
            elapsedDays++;
            if (byDay.TryGetValue(date, out var tokens) && tokens > 0)
            {
                days[i] = tokens;
                window = TokenActivityView.SaturatingAdd(window, tokens);
            }
        }

        var weekStart = StartMonday(today, 1);
        long thisWeek = 0;
        for (var date = weekStart; date <= today; date = date.AddDays(1))
        {
            if (byDay.TryGetValue(date, out var tokens) && tokens > 0)
                thisWeek = TokenActivityView.SaturatingAdd(thisWeek, tokens);
        }

        var dayAverage = elapsedDays > 0 ? window / elapsedDays : 0;
        var weekAverage = window / weekCount;
        long todayTokens = 0;
        if (byDay.TryGetValue(today, out var todayValue) && todayValue > 0)
            todayTokens = todayValue;
        return new TokenActivityView(today, start, weekCount, days, thisWeek, dayAverage, weekAverage, Math.Max(0, allTimeTotal), grain, todayTokens);
    }

    /// <summary>
    /// Sums each local day's spend-accounting totals (input + output + unattributed) from
    /// <see cref="CodexDailyModelUsage"/> rows, which is the ledger's per-day series.
    /// </summary>
    public static Dictionary<DateOnly, long> SumDailyModelUsage(IEnumerable<CodexDailyModelUsage> usage)
    {
        var byDay = new Dictionary<DateOnly, long>();
        foreach (var item in usage)
        {
            if (!DateOnly.TryParse(item.LocalDate, out var date)) continue;
            var total = SaturatingSum(item.InputTokens, item.OutputTokens, item.UnattributedTokens);
            if (total <= 0) continue;
            byDay[date] = byDay.TryGetValue(date, out var existing)
                ? TokenActivityView.SaturatingAdd(existing, total)
                : total;
        }
        return byDay;
    }

    private static long SaturatingSum(long a, long b, long c)
    {
        var sum = TokenActivityView.SaturatingAdd(Math.Max(0, a), Math.Max(0, b));
        return TokenActivityView.SaturatingAdd(sum, Math.Max(0, c));
    }
}
