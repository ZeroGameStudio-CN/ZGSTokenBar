using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Core;

public static class UsageProjection
{
    public static IReadOnlyList<UsageProviderSnapshot> FromQuotaSnapshot(QuotaSnapshot snapshot)
    {
        var providers = new List<UsageProviderSnapshot>();
        Add(providers, UsageProviderIds.Claude, snapshot.Cards.Where(card => card.Provider == ProviderKind.Claude).Select(FromCard));
        var codex = snapshot.CodexAccounts.Count > 0
            ? snapshot.CodexAccounts.Select(FromCodexAccount)
            : snapshot.Cards.Where(card => card.Provider == ProviderKind.Codex).Select(FromCard);
        Add(providers, UsageProviderIds.Codex, codex);
        Add(providers, UsageProviderIds.DeepSeek, snapshot.Cards.Where(card => card.Provider == ProviderKind.AiGateway).Select(FromGateway));
        return providers;
    }

    public static MiniCardContribution MiniCard(UsageProviderSnapshot provider, bool chinese)
    {
        double? tightest = null;
        foreach (var account in provider.Accounts)
        {
            var remaining = UsageFormatting.Remaining(UsageFormatting.Primary(account));
            if (remaining is { } value && (tightest is null || value < tightest)) tightest = value;
        }

        var text = UsageFormatting.Compact(provider, chinese);
        return new MiniCardContribution(
            "card." + provider.ProviderId,
            provider.ProviderId,
            provider.ProviderId,
            provider.Accounts.Any(account => account.Credits is not null) && tightest is null
                ? ContributionKind.Balance
                : ContributionKind.Quota,
            provider.Order,
            provider.DisplayName,
            provider.ProviderId + ".icon",
            provider.Accent,
            [
                new ContributionSummaryItem(
                    "usage.primary",
                    new ContributionValue("text", Text: text, Number: tightest),
                    tightest is null ? "usage-text" : "usage-remaining"),
            ]);
    }

    private static void Add(
        List<UsageProviderSnapshot> providers,
        string id,
        IEnumerable<UsageAccountSnapshot> accounts)
    {
        var definition = ProviderCatalog.Find(id);
        var normalized = UsageAccounts.Normalize(accounts);
        if (definition is null || normalized.Count == 0) return;
        providers.Add(new UsageProviderSnapshot(definition.Id, definition.DisplayName, definition.Accent, definition.Order, normalized));
    }

    private static UsageAccountSnapshot FromCard(QuotaCard card) => new(
        card.Key,
        string.IsNullOrWhiteSpace(card.AccountHint) ? card.DisplayLabel : card.AccountHint,
        card.Badge,
        "quota-card",
        card.Windows.Select(FromWindow).ToArray(),
        card.Balance is { } balance
            ? new UsageCredits(balance.Currency, balance.TotalBalance, null, null)
            : null,
        null,
        [],
        card.CapturedAt is null ? UsageFreshness.Unavailable : UsageFreshness.Current,
        card.CapturedAt,
        null);

    private static UsageAccountSnapshot FromCodexAccount(CodexAccountQuota account) => new(
        account.AccountId,
        account.AccountId,
        null,
        "codex-account",
        account.Windows.Select(FromWindow).ToArray(),
        null,
        null,
        [],
        account.Error is null ? UsageFreshness.Current : UsageFreshness.Unavailable,
        account.CapturedAt,
        account.Error is null ? null : "unavailable");

    private static UsageAccountSnapshot FromGateway(QuotaCard card)
    {
        var account = FromCard(card);
        if (card.Balance is not { } balance) return account;
        var freshness = balance.Status switch
        {
            AiGatewayBalanceStatus.Available => UsageFreshness.Current,
            AiGatewayBalanceStatus.Stale => UsageFreshness.Stale,
            _ => UsageFreshness.Unavailable,
        };
        return account with
        {
            Freshness = freshness,
            ErrorKey = freshness == UsageFreshness.Current ? null : "balance_unavailable",
        };
    }

    private static UsageWindow FromWindow(QuotaWindow window) => new(
        window.Label,
        window.Label,
        window.UsedPercent,
        window.ResetsAt,
        window.Duration > TimeSpan.Zero ? window.Duration : null);
}
