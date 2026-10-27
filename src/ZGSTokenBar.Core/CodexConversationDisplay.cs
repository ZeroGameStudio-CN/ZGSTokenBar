namespace ZGSTokenBar.Core;

public static class CodexConversationDisplay
{
    public static IReadOnlyList<CodexConversationTurn> Select(CodexTokenUsageSummary? usage) =>
        usage?.ConversationTurns.Where(HasMetrics).ToArray() ?? [];

    private static bool HasMetrics(CodexConversationTurn turn) => turn.NewInputTokens is not null
        || turn.UsageComplete || turn.OutputTokens > 0 || turn.CachedInputTokens is not null
        || turn.ApiEquivalentUsd is not null || turn.OutputTokensPerSecond is not null || turn.FirstTokenSeconds is not null;

    public static int ColumnMask(IReadOnlyList<CodexConversationTurn> rows)
    {
        var mask = 7; // Name, state and start time are always meaningful for retained rows.
        if (rows.Any(row => row.NewInputTokens is not null)) mask |= 1 << 3;
        if (rows.Any(row => row.UsageComplete || row.OutputTokens > 0)) mask |= 1 << 4;
        if (rows.Any(row => row.CachedInputTokens is not null)) mask |= 1 << 5;
        if (rows.Any(row => row.CacheHitPercent is not null)) mask |= 1 << 6;
        if (rows.Any(row => row.ApiEquivalentUsd is not null)) mask |= 1 << 7;
        if (rows.Any(row => row.OutputTokensPerSecond is { } rate && double.IsFinite(rate))) mask |= 1 << 8;
        if (rows.Any(row => row.FirstTokenSeconds is { } first && double.IsFinite(first))) mask |= 1 << 9;
        return mask;
    }
}
