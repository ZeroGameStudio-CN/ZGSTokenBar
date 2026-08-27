using System.Security.Cryptography;
using System.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal static class CodexPoolCardProjection
{
    public static IReadOnlyList<QuotaCard> Create(
        IReadOnlyList<QuotaCard> cards,
        IReadOnlyList<CodexAccountInfo> accounts,
        IReadOnlyList<CodexAccountQuota> quotas)
    {
        var ordinaryCards = cards
            .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
            .ToArray();
        var distinctAccounts = accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.AccountId))
            .GroupBy(account => account.AccountId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var targetPlan = TargetPlan(ordinaryCards, distinctAccounts);
        if (targetPlan is null) return cards;

        var poolAccounts = distinctAccounts
            .Where(account => string.Equals(
                SubscriptionPlan(account.Plan),
                targetPlan,
                StringComparison.Ordinal))
            .ToArray();
        if (poolAccounts.Length == 0) return cards;

        var quotaByAccount = quotas
            .Where(quota => !string.IsNullOrWhiteSpace(quota.AccountId))
            .GroupBy(quota => quota.AccountId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var unusedCards = ordinaryCards.ToList();
        var accountQuotas = poolAccounts
            .Select(account =>
            {
                quotaByAccount.TryGetValue(account.AccountId, out var quota);
                return (Account: account, Quota: quota);
            })
            .ToArray();
        var sourceCards = accountQuotas
            .Select(item => TakeExactSourceCard(
                unusedCards,
                item.Account,
                item.Quota,
                targetPlan))
            .ToArray();
        for (var index = 0; index < sourceCards.Length; index++)
        {
            sourceCards[index] ??= TakeFallbackSourceCard(
                unusedCards,
                accountQuotas[index].Account,
                targetPlan);
        }

        var projected = accountQuotas
            .Select((item, index) => ProjectAccount(
                item.Account,
                item.Quota,
                sourceCards[index],
                targetPlan))
            .ToArray();
        var labeled = projected
            .Select((card, index) => card with
            {
                Label = CodexDisplayFormatting.AccountLabel(projected.Length, index),
                Badge = targetPlan,
            })
            .ToArray();
        return ReplaceOrdinaryCards(cards, labeled);
    }

    private static string? TargetPlan(
        IReadOnlyList<QuotaCard> cards,
        IReadOnlyList<CodexAccountInfo> accounts)
    {
        var cardPlans = cards
            .Select(card => SubscriptionPlan(card.Badge))
            .Where(plan => plan is not null)
            .ToArray();
        return accounts
            .Select(account => SubscriptionPlan(account.Plan))
            .Where(plan => plan is not null)
            .GroupBy(plan => plan!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => cardPlans.Count(plan =>
                string.Equals(plan, group.Key, StringComparison.Ordinal)))
            .ThenBy(group => SubscriptionPlanRank(group.Key))
            .Select(group => group.Key)
            .FirstOrDefault();
    }

    private static QuotaCard? TakeExactSourceCard(
        List<QuotaCard> cards,
        CodexAccountInfo account,
        CodexAccountQuota? quota,
        string targetPlan)
    {
        var index = -1;
        if (!string.IsNullOrWhiteSpace(quota?.CardKey))
        {
            index = cards.FindIndex(card =>
                string.Equals(card.Key, quota.CardKey, StringComparison.Ordinal));
        }

        var hint = CodexAccountFormatting.MaskEmail(account.Email);
        if (index < 0 && !string.Equals(hint, "Codex account", StringComparison.Ordinal))
        {
            index = cards.FindIndex(card =>
                string.Equals(card.AccountHint, hint, StringComparison.OrdinalIgnoreCase)
                && CardPlanMatches(card, targetPlan));
        }

        if (index < 0) return null;
        var source = cards[index];
        cards.RemoveAt(index);
        return source;
    }

    private static QuotaCard? TakeFallbackSourceCard(
        List<QuotaCard> cards,
        CodexAccountInfo account,
        string targetPlan)
    {
        var index = cards.FindIndex(card =>
            card.Active == account.Active && CardPlanMatches(card, targetPlan));
        if (index < 0)
        {
            index = cards.FindIndex(card => CardPlanMatches(card, targetPlan));
        }
        if (index < 0) return null;

        var source = cards[index];
        cards.RemoveAt(index);
        return source;
    }

    private static QuotaCard ProjectAccount(
        CodexAccountInfo account,
        CodexAccountQuota? quota,
        QuotaCard? source,
        string targetPlan)
    {
        var sourceHasData = HasQuotaData(source?.Windows);
        var quotaHasData = HasQuotaData(quota?.Windows);
        var useQuota = quota is not null && (quotaHasData || !sourceHasData);
        var windows = useQuota
            ? quota!.Windows
            : source?.Windows ?? [];
        if (!HasQuotaData(windows)) windows = [];

        var capturedAt = useQuota
            ? quota?.CapturedAt
            : source?.CapturedAt;
        var accountHint = CodexAccountFormatting.MaskEmail(account.Email);
        var projectedKey = !string.IsNullOrWhiteSpace(quota?.CardKey)
            ? quota.CardKey!
            : SyntheticCardKey(account.AccountId);
        return source is null
            ? new QuotaCard(
                projectedKey,
                ProviderKind.Codex,
                "Codex",
                targetPlan,
                "#10a37f",
                account.Active,
                windows)
            {
                CapturedAt = capturedAt,
                AccountHint = accountHint,
            }
            : source with
            {
                Badge = targetPlan,
                Active = account.Active,
                Windows = windows,
                CapturedAt = capturedAt,
                AccountHint = accountHint,
            };
    }

    private static IReadOnlyList<QuotaCard> ReplaceOrdinaryCards(
        IReadOnlyList<QuotaCard> cards,
        IReadOnlyList<QuotaCard> projected)
    {
        var result = new List<QuotaCard>(cards.Count + projected.Count);
        var poolAdded = false;
        foreach (var card in cards)
        {
            var ordinaryCodex = card.Provider == ProviderKind.Codex && !card.IsService;
            if (ordinaryCodex)
            {
                if (!poolAdded)
                {
                    result.AddRange(projected);
                    poolAdded = true;
                }
                continue;
            }

            if (!poolAdded && card.Provider == ProviderKind.Codex)
            {
                result.AddRange(projected);
                poolAdded = true;
            }
            result.Add(card);
        }

        if (!poolAdded) result.AddRange(projected);
        return result;
    }

    private static bool HasQuotaData(IReadOnlyList<QuotaWindow>? windows) =>
        windows?.Any(window => window.UsedPercent is not null || window.ResetsAt is not null) == true;

    private static string? SubscriptionPlan(string? plan)
    {
        var label = CodexAccountFormatting.PlanLabel(plan);
        return label is "pro" or "plus" or "free" ? label : null;
    }

    private static bool CardPlanMatches(QuotaCard card, string plan)
    {
        var cardPlan = SubscriptionPlan(card.Badge);
        return cardPlan is null || string.Equals(cardPlan, plan, StringComparison.Ordinal);
    }

    private static int SubscriptionPlanRank(string plan) => plan switch
    {
        "pro" => 0,
        "plus" => 1,
        "free" => 2,
        _ => 3,
    };

    private static string SyntheticCardKey(string accountId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(accountId));
        return $"codex.pool.{Convert.ToHexString(digest)[..12].ToLowerInvariant()}";
    }
}
