using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed record TaskbarMiniCardGroup(
    IReadOnlyList<QuotaCard> Cards,
    string AreaId)
{
    public bool IsCodexPool { get; init; }

    public bool IsStackedCodex =>
        !IsCodexPool
        && Cards.Count >= 2
        && Cards.All(card => card.Provider == ProviderKind.Codex);
}

internal static class TaskbarMiniGrouping
{
    internal const string CodexServiceAreaId = "zgstokenbar.provider.codex-service";

    public static IReadOnlyList<TaskbarMiniCardGroup> Create(
        IReadOnlyList<QuotaCard> cards,
        string codexMiniDisplayMode = CodexMiniDisplayModes.Accounts)
    {
        if (CodexMiniDisplayModes.Normalize(codexMiniDisplayMode) == CodexMiniDisplayModes.Pool)
        {
            return CreatePoolGroups(cards);
        }

        var groups = new List<List<QuotaCard>>();
        foreach (var card in cards)
        {
            if (card.Provider == ProviderKind.Codex)
            {
                var available = groups.FirstOrDefault(group =>
                    group.All(item => item.Provider == ProviderKind.Codex));
                if (available is not null)
                {
                    available.Add(card);
                    continue;
                }
            }

            groups.Add([card]);
        }

        var codexGroupIndex = 0;
        return groups
            .Select(group =>
            {
                var provider = group[0].Provider;
                var orderedCards = provider == ProviderKind.Codex
                    ? OrderCodexCards(group)
                    : group.ToArray();
                var areaId = provider switch
                {
                    ProviderKind.Claude => MiniAreaIds.Claude,
                    ProviderKind.Codex => CodexAreaId(++codexGroupIndex),
                    ProviderKind.AiGateway => MiniAreaIds.AiGateway,
                    _ => provider.ToString().ToLowerInvariant(),
                };
                return new TaskbarMiniCardGroup(orderedCards, areaId);
            })
            .ToArray();
    }

    private static QuotaCard[] OrderCodexCards(IReadOnlyList<QuotaCard> cards) => cards
        .Select((card, index) => new
        {
            Card = card,
            Index = index,
            Remaining = DisplayedRemainingPercent(card),
        })
        .OrderBy(item => PlanSortRank(item.Card))
        .ThenByDescending(item => item.Remaining ?? double.NegativeInfinity)
        .ThenBy(item => item.Index)
        .Select(item => item.Card)
        .ToArray();

    private static int PlanSortRank(QuotaCard card)
    {
        var plan = CodexAccountFormatting.PlanLabel(card.Badge);
        if (string.Equals(plan, "pro", StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(plan, "plus", StringComparison.OrdinalIgnoreCase)) return 1;
        return card.IsService ? 3 : 2;
    }

    internal static double? DisplayedRemainingPercent(QuotaCard card)
    {
        var window = CodexRowWindows(card).LastOrDefault();
        return window?.UsedPercent is { } used
            ? Math.Clamp(100 - used, 0, 100)
            : null;
    }

    private static IReadOnlyList<TaskbarMiniCardGroup> CreatePoolGroups(IReadOnlyList<QuotaCard> cards)
    {
        var poolCards = cards
            .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
            .ToArray();
        var groups = new List<TaskbarMiniCardGroup>();
        var poolAdded = false;
        foreach (var card in cards)
        {
            if (card.Provider == ProviderKind.Codex && !card.IsService)
            {
                if (!poolAdded)
                {
                    groups.Add(new TaskbarMiniCardGroup(poolCards, MiniAreaIds.Codex)
                    {
                        IsCodexPool = true,
                    });
                    poolAdded = true;
                }
                continue;
            }

            var areaId = card.Provider switch
            {
                ProviderKind.Claude => MiniAreaIds.Claude,
                ProviderKind.Codex => CodexServiceAreaId,
                ProviderKind.AiGateway => MiniAreaIds.AiGateway,
                _ => card.Provider.ToString().ToLowerInvariant(),
            };
            groups.Add(new TaskbarMiniCardGroup([card], areaId));
        }

        return groups;
    }

    private static string CodexAreaId(int groupIndex) => groupIndex == 1
        ? MiniAreaIds.Codex
        : $"{MiniAreaIds.Codex}.{groupIndex}";

    public static IReadOnlyList<QuotaWindow> CodexRowWindows(QuotaCard card)
    {
        if (card.IsService)
        {
            return card.Windows.Count > 0
                ? card.Windows.Take(1).ToArray()
                : [new QuotaWindow("7d", null, null, TimeSpan.FromDays(7))];
        }

        var fiveHour = card.Windows.FirstOrDefault(window =>
            window.Duration == TimeSpan.FromHours(5)
            || string.Equals(window.Label, "5h", StringComparison.OrdinalIgnoreCase));
        var weekly = card.Windows.FirstOrDefault(window =>
            window.Duration == TimeSpan.FromDays(7)
            || IsWeeklyLabel(window.Label));
        var fiveHourAvailable = HasQuotaData(fiveHour);
        var weeklyAvailable = HasQuotaData(weekly);
        if (fiveHourAvailable && weeklyAvailable) return [fiveHour!, weekly!];
        if (fiveHourAvailable) return [fiveHour!];
        if (weeklyAvailable) return [weekly!];
        if (weekly is not null) return [weekly];
        if (fiveHour is not null) return [fiveHour];
        return [new QuotaWindow("7d", null, null, TimeSpan.FromDays(7))];
    }

    private static bool HasQuotaData(QuotaWindow? window) =>
        window is not null && (window.UsedPercent is not null || window.ResetsAt is not null);

    private static bool IsWeeklyLabel(string label)
    {
        var trimmed = label.AsSpan().Trim();
        return trimmed.Equals("1w", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("week", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("7d", StringComparison.OrdinalIgnoreCase);
    }
}
