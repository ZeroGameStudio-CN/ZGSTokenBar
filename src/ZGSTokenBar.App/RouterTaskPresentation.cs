using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

internal enum RouterHistoryRangeKind { All, Today, Day }

internal sealed record RouterTaskHeading(string Title, string Context, string Original);

internal sealed record RouterDailyThroughput(
    int ActiveDays,
    int TotalProcessed,
    int TodayTotal,
    double? AveragePerActiveDay,
    string SummaryText,
    string TooltipSection,
    bool HasDurations = false,
    double TodayOccupiedSeconds = 0,
    double TotalOccupiedSeconds = 0,
    double? AverageDailyOccupiedSeconds = null,
    double? AverageTaskOccupiedSeconds = null,
    double TotalEditorActiveSeconds = 0,
    double TotalRouterOverheadSeconds = 0,
    double TotalNonEditorScriptSeconds = 0,
    double TotalUnmeasuredSeconds = 0,
    int MeasuredCount = 0,
    int ZeroEditorScriptCount = 0,
    int DispatchedCount = 0,
    RouterHistoryRangeKind RangeKind = RouterHistoryRangeKind.All,
    string RangeDate = "",
    int SelectedCompleted = 0,
    int SelectedFailed = 0,
    bool HasAnyHistory = false,
    double TotalCleanupSeconds = 0);

internal static class RouterTaskPresentation
{
    internal static RouterQueuePriority.Display Priority(string? priority, string? reason) =>
        RouterQueuePriority.Describe(priority, reason);

    internal static RouterTaskHeading Heading(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—") return new("暂无正在执行的任务", "", value);
        var original = value;
        var stage = Regex.Match(value, @"^\d+/\d+\s*·\s*");
        var prefix = stage.Success ? stage.Value.TrimEnd() + " " : "";
        if (stage.Success) value = value[stage.Length..];
        if (new[] { ".py", ".ps1", ".sh", ".cs" }.Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            return new(prefix + Path.GetFileName(value), "", original);
        var split = LastScopeSeparator(value);
        if (split < 0) return new(prefix + value, "", original);
        var context = value[..split];
        context = context[(LastScopeSeparator(context) + 1)..];
        return new(prefix + Words(value[(split + 1)..]), Words(context), original);
    }

    internal static string Source(string value)
    {
        var labelEnd = value.IndexOf(" · ", StringComparison.Ordinal);
        var label = labelEnd is > 0 and < 32 ? value[..(labelEnd + 3)] : "";
        var caption = value[label.Length..].Trim();
        while (caption.StartsWith("[$", StringComparison.Ordinal))
        {
            var link = Regex.Match(caption, @"^\[\$[^\]\r\n]+\]\((?:[^()\r\n]|\([^()\r\n]*\))*\)\s*");
            if (!link.Success) return label + "技能引用";
            caption = caption[link.Length..].TrimStart();
        }
        if (caption.StartsWith("## Referenced chats with Codex", StringComparison.OrdinalIgnoreCase))
        {
            const string request = "## My request:";
            var requestAt = caption.IndexOf(request, StringComparison.OrdinalIgnoreCase);
            caption = requestAt >= 0 ? caption[(requestAt + request.Length)..] : "引用对话";
        }
        caption = Regex.Replace(caption.TrimStart(), @"^#{1,6}\s+", "");
        if (caption.Length == 0 && value.AsSpan(label.Length).TrimStart().StartsWith("[$", StringComparison.Ordinal))
            caption = "技能引用";
        if (Guid.TryParse(caption, out _)) caption = "原对话";
        var text = Regex.Replace(label + caption, @"\s+", " ").Trim();
        return text.Length > 72 ? text[..71] + "…" : text;
    }

    private static int LastScopeSeparator(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0])) return -1;
        var depth = 0;
        var result = -1;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character is '(' or '[' or '<') depth++;
            else if (character is ')' or ']' or '>') depth--;
            else if (depth == 0)
            {
                if (character == '.')
                {
                    if (i + 1 >= value.Length || !IsIdentifierStart(value[i + 1])) return -1;
                    result = i;
                }
                else if (!char.IsAsciiLetterOrDigit(character) && character != '_') return -1;
            }
            if (depth < 0) return -1;
        }
        return depth == 0 ? result : -1;
    }
    private static bool IsIdentifierStart(char character) => char.IsAsciiLetter(character) || character == '_';
    private static string Words(string value) => Regex.Replace(value, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Replace("_", " · ");

    /// <summary>Formats a non-negative duration in seconds concisely for summaries and tooltips.</summary>
    internal static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "0s";
        if (seconds < 10) return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#}s");
        var rounded = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (rounded < 60) return string.Create(CultureInfo.InvariantCulture, $"{rounded}s");
        if (rounded < 3600)
        {
            var minutes = rounded / 60;
            var rem = rounded % 60;
            return rem > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{minutes}m{rem}s")
                : string.Create(CultureInfo.InvariantCulture, $"{minutes}m");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600d:0.#}h");
    }

    /// <summary>Formats one share as an integer percentage or "&lt;1%" for tiny non-zero portions.</summary>
    internal static string FormatShare(double part, double whole)
    {
        if (!double.IsFinite(part) || !double.IsFinite(whole) || part <= 0.001 || whole <= 0) return "0%";
        var pct = part * 100d / whole;
        if (pct < 0.5) return "<1%";
        return string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(pct, MidpointRounding.AwayFromZero)}%");
    }

    /// <summary>Formats a nullable execution duration for table cells, keeping sub-minute precision and shortening longer spans.</summary>
    internal static string FormatCellDuration(double? seconds)
    {
        if (seconds is not { } s || !double.IsFinite(s) || s < 0) return "—";
        if (s < 60) return string.Create(CultureInfo.InvariantCulture, $"{s:0.##}s");
        return FormatDuration(s);
    }

    /// <summary>Formats one date filter choice with its day total and occupied duration when available.</summary>
    internal static string DayOptionLabel(JsonElement dayRow)
    {
        var date = RouterJson.Text(dayRow, "date");
        if (date.Length == 0) return "";
        if (dayRow.ValueKind != JsonValueKind.Object
            || !dayRow.TryGetProperty("total", out var totalElement)
            || totalElement.ValueKind != JsonValueKind.Number
            || RouterJson.Number(dayRow, "total") is not { } rawTotal)
        {
            return date;
        }
        var total = Math.Max(0, (int)Math.Round(rawTotal));
        var durations = RouterJson.Field(dayRow, "durations");
        if (durations.ValueKind == JsonValueKind.Object
            && RouterJson.Number(durations, "occupiedSeconds") is { } occupied
            && occupied > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{date} ({total:N0} · {FormatDuration(occupied)})");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{date} ({total:N0})");
    }

    private sealed class DayAccumulator
    {
        public int Total;
        public int Completed;
        public int Failed;
        public bool HasCounts;
        public bool HasDurations;
        public int DispatchedCount;
        public int MeasuredCount;
        public int ZeroEditorScriptCount;
        public double OccupiedSeconds;
        public double CleanupSeconds;
        public double EditorActiveSeconds;
        public double RouterOverheadSeconds;
        public double NonEditorScriptSeconds;
        public double UnmeasuredSeconds;
    }

    /// <summary>Summarizes Router's daily breakdown for either today, a specific day, or the full active window.</summary>
    internal static RouterDailyThroughput SummarizeDailyThroughput(IEnumerable<JsonElement> days, DateOnly today, string? selectedDay = null)
    {
        var byDate = new Dictionary<string, DayAccumulator>(StringComparer.Ordinal);
        foreach (var dayRow in days)
        {
            var date = RouterJson.Text(dayRow, "date");
            if (date.Length == 0
                || dayRow.ValueKind != JsonValueKind.Object
                || !dayRow.TryGetProperty("total", out var totalEl)
                || totalEl.ValueKind != JsonValueKind.Number
                || RouterJson.Number(dayRow, "total") is not { } rawTotal)
            {
                continue;
            }
            var total = (int)Math.Round(rawTotal);
            if (total < 0) continue;

            if (!byDate.TryGetValue(date, out var acc))
            {
                acc = new DayAccumulator();
                byDate[date] = acc;
            }
            acc.Total += total;

            var counts = RouterJson.Field(dayRow, "counts");
            if (counts.ValueKind == JsonValueKind.Object)
            {
                acc.HasCounts = true;
                acc.Completed += Math.Max(0, RouterJson.Integer(counts, "completed"));
                acc.Failed += Math.Max(0, RouterJson.Integer(counts, "failed"));
            }

            var durations = RouterJson.Field(dayRow, "durations");
            if (durations.ValueKind == JsonValueKind.Object)
            {
                acc.HasDurations = true;
                acc.DispatchedCount += Math.Max(0, RouterJson.Integer(durations, "dispatchedCount"));
                acc.MeasuredCount += Math.Max(0, RouterJson.Integer(durations, "measuredCount"));
                acc.ZeroEditorScriptCount += Math.Max(0, RouterJson.Integer(durations, "zeroEditorScriptCount"));
                acc.OccupiedSeconds += Math.Max(0d, RouterJson.Number(durations, "occupiedSeconds") ?? 0d);
                acc.CleanupSeconds += Math.Max(0d, RouterJson.Number(durations, "cleanupSeconds") ?? 0d);
                acc.EditorActiveSeconds += Math.Max(0d, RouterJson.Number(durations, "editorActiveSeconds") ?? 0d);
                acc.RouterOverheadSeconds += Math.Max(0d, RouterJson.Number(durations, "routerOverheadSeconds") ?? 0d);
                acc.NonEditorScriptSeconds += Math.Max(0d, RouterJson.Number(durations, "nonEditorScriptSeconds") ?? 0d);
                acc.UnmeasuredSeconds += Math.Max(0d, RouterJson.Number(durations, "unmeasuredSeconds") ?? 0d);
            }
        }

        var activeRows = byDate
            .Where(entry => entry.Value.Total > 0)
            .OrderByDescending(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var normalizedSelection = string.IsNullOrWhiteSpace(selectedDay) ? "all" : selectedDay.Trim();
        var rangeKind = normalizedSelection == "all"
            ? RouterHistoryRangeKind.All
            : normalizedSelection == todayKey ? RouterHistoryRangeKind.Today : RouterHistoryRangeKind.Day;
        var hasAnyHistory = activeRows.Length > 0;

        if (!hasAnyHistory)
            return new(0, 0, 0, null, "", "", RangeKind: rangeKind, RangeDate: rangeKind == RouterHistoryRangeKind.All ? "" : normalizedSelection);

        if (rangeKind != RouterHistoryRangeKind.All)
        {
            var dayLabel = rangeKind == RouterHistoryRangeKind.Today
                ? "今日"
                : (normalizedSelection.Length == 10 ? normalizedSelection[5..] : normalizedSelection);
            byDate.TryGetValue(normalizedSelection, out var entry);
            if (entry is null || entry.Total == 0)
            {
                var emptyLines = new List<string>(2 + Math.Min(14, activeRows.Length))
                {
                    string.Create(CultureInfo.InvariantCulture, $"{dayLabel}（{normalizedSelection}）暂无已结束任务"),
                };
                AppendRecentDayLines(emptyLines, "最近活跃日参考：", activeRows);
                return new(
                    0,
                    0,
                    0,
                    null,
                    $"{dayLabel} 0 条",
                    string.Join(Environment.NewLine, emptyLines),
                    RangeKind: rangeKind,
                    RangeDate: normalizedSelection,
                    HasAnyHistory: true);
            }

            var isToday = rangeKind == RouterHistoryRangeKind.Today;
            var dayHasDurations = entry.HasDurations && entry.DispatchedCount > 0;
            if (!dayHasDurations)
            {
                var countSuffix = entry.HasCounts && (entry.Completed > 0 || entry.Failed > 0)
                    ? string.Create(CultureInfo.InvariantCulture, $"（成功 {entry.Completed:N0} · 失败 {entry.Failed:N0}）")
                    : "";
                var dayTipLines = new List<string>(2 + Math.Min(14, activeRows.Length))
                {
                    string.Create(CultureInfo.InvariantCulture, $"{dayLabel}（{normalizedSelection}）· 共 {entry.Total:N0} 条{countSuffix}"),
                };
                AppendRecentDayLines(dayTipLines, "最近活跃日参考：", activeRows);
                return new(
                    1,
                    entry.Total,
                    isToday ? entry.Total : 0,
                    entry.Total,
                    string.Create(CultureInfo.InvariantCulture, $"{dayLabel} {entry.Total:N0} 条"),
                    string.Join(Environment.NewLine, dayTipLines),
                    RangeKind: rangeKind,
                    RangeDate: normalizedSelection,
                    SelectedCompleted: entry.Completed,
                    SelectedFailed: entry.Failed,
                    HasAnyHistory: true);
            }

            var avgTask = entry.OccupiedSeconds / entry.DispatchedCount;
            var daySummary = string.Create(CultureInfo.InvariantCulture,
                $"{dayLabel} {entry.Total:N0} 条 · 总占 {FormatDuration(entry.OccupiedSeconds)} · 均占 {FormatDuration(avgTask)}");
            var tip = new List<string>(10 + Math.Min(14, activeRows.Length))
            {
                string.Create(CultureInfo.InvariantCulture,
                    $"{dayLabel}（{normalizedSelection}）· 共 {entry.Total:N0} 条 · 总占用 {FormatDuration(entry.OccupiedSeconds)} · 单任务均占 {FormatDuration(avgTask)}")
            };
            AppendDurationBreakdownLines(
                tip,
                entry.OccupiedSeconds,
                entry.EditorActiveSeconds,
                entry.RouterOverheadSeconds,
                entry.NonEditorScriptSeconds,
                entry.UnmeasuredSeconds,
                entry.MeasuredCount,
                entry.DispatchedCount,
                entry.ZeroEditorScriptCount,
                entry.CleanupSeconds);
            AppendRecentDayLines(tip, "最近活跃日参考：", activeRows);
            return new(
                1,
                entry.Total,
                isToday ? entry.Total : 0,
                entry.Total,
                daySummary,
                string.Join(Environment.NewLine, tip),
                true,
                isToday ? entry.OccupiedSeconds : 0,
                entry.OccupiedSeconds,
                entry.OccupiedSeconds,
                avgTask,
                entry.EditorActiveSeconds,
                entry.RouterOverheadSeconds,
                entry.NonEditorScriptSeconds,
                entry.UnmeasuredSeconds,
                entry.MeasuredCount,
                entry.ZeroEditorScriptCount,
                entry.DispatchedCount,
                rangeKind,
                normalizedSelection,
                entry.Completed,
                entry.Failed,
                true,
                entry.CleanupSeconds);
        }

        var activeDays = activeRows.Length;
        var totalProcessed = activeRows.Sum(entry => entry.Value.Total);
        byDate.TryGetValue(todayKey, out var todayEntry);
        var todayTotal = todayEntry is not null ? Math.Max(0, todayEntry.Total) : 0;
        var todayOccupied = todayEntry is not null ? Math.Max(0d, todayEntry.OccupiedSeconds) : 0d;
        var average = (double)totalProcessed / activeDays;
        var avgText = average.ToString("0.#", CultureInfo.InvariantCulture);

        var totalDispatched = activeRows.Sum(entry => entry.Value.DispatchedCount);
        var hasDurations = activeRows.Any(entry => entry.Value.HasDurations) && totalDispatched > 0;
        if (!hasDurations)
        {
            var summaryText = string.Create(CultureInfo.InvariantCulture, $"日均 {avgText} 条/天 · 活跃 {activeDays:N0} 天 · 共 {totalProcessed:N0} 条");
            var lines = new List<string>(2 + Math.Min(14, activeRows.Length))
            {
                string.Create(CultureInfo.InvariantCulture, $"日均 {avgText} 条/天（按 {activeDays:N0} 个活跃日，共 {totalProcessed:N0} 条）"),
            };
            AppendRecentDayLines(lines, "最近活跃日明细：", activeRows);
            return new(
                activeDays,
                totalProcessed,
                todayTotal,
                average,
                summaryText,
                string.Join(Environment.NewLine, lines),
                RangeKind: RouterHistoryRangeKind.All,
                HasAnyHistory: true);
        }

        var totalOccupied = activeRows.Sum(entry => entry.Value.OccupiedSeconds);
        var totalCleanup = activeRows.Sum(entry => entry.Value.CleanupSeconds);
        var totalEditor = activeRows.Sum(entry => entry.Value.EditorActiveSeconds);
        var totalOverhead = activeRows.Sum(entry => entry.Value.RouterOverheadSeconds);
        var totalNonEditor = activeRows.Sum(entry => entry.Value.NonEditorScriptSeconds);
        var totalUnmeasured = activeRows.Sum(entry => entry.Value.UnmeasuredSeconds);
        var measuredCount = activeRows.Sum(entry => entry.Value.MeasuredCount);
        var zeroEditorScripts = activeRows.Sum(entry => entry.Value.ZeroEditorScriptCount);
        var avgDailyOccupied = totalOccupied / activeDays;
        var avgTaskOccupied = totalOccupied / totalDispatched;

        var summaryWithDurations = string.Create(CultureInfo.InvariantCulture,
            $"日均 {avgText} 条/天（{FormatDuration(avgDailyOccupied)}/天）· 均占 {FormatDuration(avgTaskOccupied)} · 累计 {FormatDuration(totalOccupied)}");

        var tipLines = new List<string>(10 + Math.Min(14, activeRows.Length))
        {
            string.Create(CultureInfo.InvariantCulture,
                $"日均 {avgText} 条/天 · 日均占 {FormatDuration(avgDailyOccupied)} · 单任务均占 {FormatDuration(avgTaskOccupied)}（按 {activeDays:N0} 个活跃日，共 {totalProcessed:N0} 条）")
        };
        AppendDurationBreakdownLines(
            tipLines,
            totalOccupied,
            totalEditor,
            totalOverhead,
            totalNonEditor,
            totalUnmeasured,
            measuredCount,
            totalDispatched,
            zeroEditorScripts,
            totalCleanup);
        AppendRecentDayLines(tipLines, "最近活跃日明细：", activeRows);

        return new(
            activeDays,
            totalProcessed,
            todayTotal,
            average,
            summaryWithDurations,
            string.Join(Environment.NewLine, tipLines),
            true,
            todayOccupied,
            totalOccupied,
            avgDailyOccupied,
            avgTaskOccupied,
            totalEditor,
            totalOverhead,
            totalNonEditor,
            totalUnmeasured,
            measuredCount,
            zeroEditorScripts,
            totalDispatched,
            RouterHistoryRangeKind.All,
            "",
            0,
            0,
            true,
            totalCleanup);
    }

    private static void AppendDurationBreakdownLines(
        List<string> tipLines,
        double totalOccupied,
        double totalEditor,
        double totalOverhead,
        double totalNonEditor,
        double totalUnmeasured,
        int measuredCount,
        int totalDispatched,
        int zeroEditorScripts,
        double totalCleanup = 0)
    {
        if (totalOccupied > 0)
        {
            tipLines.Add("");
            tipLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"槽位时间拆解（仅统计任务占用期间，不含空闲；总计 {FormatDuration(totalOccupied)}，已拆解 {measuredCount:N0}/{totalDispatched:N0} 条）："));
            tipLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  真实 Editor    {FormatDuration(totalEditor)} ({FormatShare(totalEditor, totalOccupied)})"));
            var overheadSuffix = totalOverhead > 0.05 && totalCleanup > 0.05
                ? (Math.Max(0d, totalOverhead - totalCleanup) is var prep && prep > 0.05
                    ? $"（清理 {FormatDuration(totalCleanup)} · 准备/恢复 {FormatDuration(prep)}）"
                    : $"（含清理 {FormatDuration(totalCleanup)}）")
                : "";
            tipLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  Router 开销    {FormatDuration(totalOverhead)} ({FormatShare(totalOverhead, totalOccupied)}){overheadSuffix}"));
            tipLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  非 Editor 脚本  {FormatDuration(totalNonEditor)} ({FormatShare(totalNonEditor, totalOccupied)})"));
            if (totalUnmeasured > 0.05)
            {
                tipLines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  旧未拆分历史    {FormatDuration(totalUnmeasured)} ({FormatShare(totalUnmeasured, totalOccupied)})"));
            }
        }
        if (zeroEditorScripts > 0)
        {
            tipLines.Add(string.Create(CultureInfo.InvariantCulture,
                $"  提示：未调用 Unity CLI 的脚本 {zeroEditorScripts:N0} 条（可优先评估移出 Editor 队列）"));
        }
    }

    private static void AppendRecentDayLines(List<string> tipLines, string heading, IEnumerable<KeyValuePair<string, DayAccumulator>> activeRows)
    {
        tipLines.Add("");
        tipLines.Add(heading);
        foreach (var entry in activeRows.Take(14))
        {
            var d = entry.Value;
            if (d.HasDurations && d.OccupiedSeconds > 0)
            {
                var unSuffix = d.UnmeasuredSeconds > 0.05
                    ? string.Create(CultureInfo.InvariantCulture, $" / 未拆分 {FormatDuration(d.UnmeasuredSeconds)}")
                    : "";
                tipLines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  {entry.Key}  {d.Total:N0} 条 · 占 {FormatDuration(d.OccupiedSeconds)}（Editor {FormatDuration(d.EditorActiveSeconds)} / 开销 {FormatDuration(d.RouterOverheadSeconds)} / 非Editor {FormatDuration(d.NonEditorScriptSeconds)}{unSuffix}）"));
            }
            else
            {
                var detail = d.HasCounts && (d.Completed > 0 || d.Failed > 0)
                    ? string.Create(CultureInfo.InvariantCulture, $"（成功 {d.Completed:N0} · 失败 {d.Failed:N0}）")
                    : "";
                tipLines.Add(string.Create(CultureInfo.InvariantCulture, $"  {entry.Key}  {d.Total:N0} 条{detail}"));
            }
        }
    }
}
