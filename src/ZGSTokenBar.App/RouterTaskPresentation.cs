using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

internal sealed record RouterTaskHeading(string Title, string Context, string Original);

internal sealed record RouterDailyThroughput(
    int ActiveDays,
    int TotalProcessed,
    int TodayTotal,
    double? AveragePerActiveDay,
    string SummaryText,
    string TooltipSection);

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

    /// <summary>Formats one date filter choice with its day total when Router includes daily counts.</summary>
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
        return string.Create(CultureInfo.InvariantCulture, $"{date} ({total:N0})");
    }

    /// <summary>Summarizes today's count and the active-day average across Router's daily breakdown.</summary>
    internal static RouterDailyThroughput SummarizeDailyThroughput(IEnumerable<JsonElement> days, DateOnly today)
    {
        var byDate = new Dictionary<string, (int Total, int Completed, int Failed, bool HasCounts)>(StringComparer.Ordinal);
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

            var counts = RouterJson.Field(dayRow, "counts");
            var rowHasCounts = counts.ValueKind == JsonValueKind.Object;
            var completed = rowHasCounts ? Math.Max(0, RouterJson.Integer(counts, "completed")) : 0;
            var failed = rowHasCounts ? Math.Max(0, RouterJson.Integer(counts, "failed")) : 0;
            if (byDate.TryGetValue(date, out var existing))
            {
                byDate[date] = (
                    existing.Total + total,
                    existing.Completed + completed,
                    existing.Failed + failed,
                    existing.HasCounts || rowHasCounts);
            }
            else
            {
                byDate[date] = (total, completed, failed, rowHasCounts);
            }
        }

        var activeRows = byDate
            .Where(entry => entry.Value.Total > 0)
            .OrderByDescending(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        if (activeRows.Length == 0)
            return new(0, 0, 0, null, "", "");

        var activeDays = activeRows.Length;
        var totalProcessed = activeRows.Sum(entry => entry.Value.Total);
        var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var todayTotal = byDate.TryGetValue(todayKey, out var todayEntry) ? Math.Max(0, todayEntry.Total) : 0;
        var average = (double)totalProcessed / activeDays;
        var avgText = average.ToString("0.#", CultureInfo.InvariantCulture);
        var summaryText = string.Create(CultureInfo.InvariantCulture, $"今日 {todayTotal:N0}  ·  日均 {avgText}/天");

        var lines = new List<string>(1 + Math.Min(14, activeRows.Length))
        {
            string.Create(CultureInfo.InvariantCulture, $"日均 {avgText}/天（按 {activeDays:N0} 个活跃日，共 {totalProcessed:N0} 条）")
        };
        foreach (var entry in activeRows.Take(14))
        {
            var detail = entry.Value.HasCounts && (entry.Value.Completed > 0 || entry.Value.Failed > 0)
                ? string.Create(CultureInfo.InvariantCulture, $"（成功 {entry.Value.Completed:N0} · 失败 {entry.Value.Failed:N0}）")
                : "";
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"  {entry.Key}  {entry.Value.Total:N0} 条{detail}"));
        }

        return new(activeDays, totalProcessed, todayTotal, average, summaryText, string.Join(Environment.NewLine, lines));
    }
}
