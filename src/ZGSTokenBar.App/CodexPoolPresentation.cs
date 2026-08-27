using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed record CodexPoolSegment(
    QuotaCard Card,
    QuotaWindow? Window,
    double? RemainingPercent);

internal sealed record CodexPoolRow(
    string Label,
    TimeSpan Duration,
    IReadOnlyList<CodexPoolSegment> Segments,
    double? AggregateRemainingPercent,
    double? RemainingAccountEquivalents,
    int AvailableAccountCount,
    int TotalAccountCount,
    DateTimeOffset? NextResetAt);

internal static class CodexPoolPresentation
{
    private static readonly WindowDefinition[] WindowDefinitions =
    [
        new("7d", TimeSpan.FromDays(7), IsWeeklyLabel),
        new("5h", TimeSpan.FromHours(5), label =>
            string.Equals(label.Trim(), "5h", StringComparison.OrdinalIgnoreCase)),
    ];

    public static IReadOnlyList<CodexPoolRow> Create(
        IReadOnlyList<QuotaCard> cards,
        DateTimeOffset now)
    {
        var codexCards = cards
            .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
            .ToArray();
        var planLabels = codexCards
            .Select(card => CodexAccountFormatting.PlanLabel(card.Badge))
            .ToArray();
        var plansMatch = planLabels.Length > 0
            && planLabels.All(label => label is "pro" or "plus" or "free")
            && planLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;

        var rows = WindowDefinitions
            .Select(definition => CreateRow(codexCards, definition, plansMatch, now))
            .ToArray();
        var visibleRows = rows
            .Where(row => row.Segments.Any(segment => HasWindowData(segment.Window)))
            .ToArray();
        return visibleRows.Length > 0 ? visibleRows : [rows[0]];
    }

    public static string CapacitySummary(CodexPoolRow row) =>
        row.RemainingAccountEquivalents is { } equivalents
            ? FormattableString.Invariant($"{equivalents * 100:0}/{row.AvailableAccountCount * 100}")
            : $"— {row.AvailableAccountCount}/{row.TotalAccountCount}";

    private static CodexPoolRow CreateRow(
        IReadOnlyList<QuotaCard> cards,
        WindowDefinition definition,
        bool plansMatch,
        DateTimeOffset now)
    {
        var segments = cards
            .Select(card => CreateSegment(card, definition, now))
            .ToArray();
        var knownRemaining = segments
            .Where(segment => segment.RemainingPercent is not null)
            .Select(segment => segment.RemainingPercent!.Value)
            .ToArray();
        var availableCount = knownRemaining.Length;
        var hasComparableAggregate = plansMatch && availableCount > 0;
        var remainingTotal = hasComparableAggregate
            ? knownRemaining.Sum()
            : (double?)null;
        var futureResets = segments
            .Where(segment => segment.Window?.ResetsAt is { } reset && reset > now)
            .Select(segment => segment.Window!.ResetsAt!.Value)
            .ToArray();
        var nextResetAt = futureResets.Length > 0
            ? futureResets.Min()
            : (DateTimeOffset?)null;

        return new CodexPoolRow(
            definition.Label,
            definition.Duration,
            segments,
            remainingTotal / availableCount,
            remainingTotal / 100d,
            availableCount,
            cards.Count,
            nextResetAt);
    }

    private static CodexPoolSegment CreateSegment(
        QuotaCard card,
        WindowDefinition definition,
        DateTimeOffset now)
    {
        var window = card.Windows.FirstOrDefault(window => window.Duration == definition.Duration)
            ?? card.Windows.FirstOrDefault(window => definition.LabelMatches(window.Label));
        var remainingPercent = window is not null
            && window.UsedPercent is { } usedPercent
            && (window.ResetsAt is null || window.ResetsAt > now)
                ? Math.Clamp(100d - usedPercent, 0d, 100d)
                : (double?)null;
        return new CodexPoolSegment(card, window, remainingPercent);
    }

    private static bool IsWeeklyLabel(string label)
    {
        var trimmed = label.AsSpan().Trim();
        return trimmed.Equals("1w", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("week", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("7d", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasWindowData(QuotaWindow? window) =>
        window is not null
        && (window.UsedPercent is not null || window.ResetsAt is not null);

    private sealed record WindowDefinition(
        string Label,
        TimeSpan Duration,
        Func<string, bool> LabelMatches);
}
