using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>Claude and Codex: project the existing quota services onto usage windows.</summary>
public static partial class UsageParsers
{
    public static UsageParseResult FromClaude(ClaudeUsageData usage)
    {
        var windows = new List<UsageWindow>();
        AddIfPresent(windows, "5h", "5h", usage.FiveHourUsedPercent, usage.FiveHourResetsAt, TimeSpan.FromHours(5));
        AddIfPresent(windows, "7d", "7d", usage.WeekUsedPercent, usage.WeekResetsAt, TimeSpan.FromDays(7));
        AddIfPresent(windows, "fable", "Fable", usage.FableWeekUsedPercent, usage.FableWeekResetsAt, TimeSpan.FromDays(7));
        return new UsageParseResult(windows, null, [], null, windows.Count == 0 ? "usage_unknown" : null);
    }

    public static UsageParseResult FromCodex(CodexUsageData usage) => new(
        usage.Windows
            .Select(window => new UsageWindow(
                window.Label,
                window.Label,
                window.UsedPercent,
                window.ResetsAt,
                window.Duration > TimeSpan.Zero ? window.Duration : null))
            .ToArray(),
        null,
        [],
        usage.Plan,
        usage.Windows.Count == 0 ? "usage_unknown" : null);
}
