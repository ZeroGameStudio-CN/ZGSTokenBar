using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal enum CodexStrategyState { Ready, Waiting, Refresh, Expired }
internal enum CodexStrategyDeadline { Reset, Subscription, ResetCredit }

internal sealed record CodexStrategyAccount(
    CodexAccountInfo Account,
    IReadOnlyList<QuotaWindow> Windows,
    CodexStrategyState State,
    CodexStrategyDeadline? DeadlineKind,
    DateTimeOffset? Deadline,
    DateTimeOffset? Recovery,
    DateTimeOffset? CardExpiry,
    DateTimeOffset? RedeemBy,
    DateTimeOffset? ReviewBy,
    int? Cards,
    bool CanRedeem,
    bool ResetBeforeCard)
{
    public bool HasLittleQuota => Windows.Any(window => window.UsedPercent >= 95);
}

internal sealed record CodexUsageStrategy(
    IReadOnlyList<CodexStrategyAccount> Accounts,
    IReadOnlyList<(CodexStrategyAccount Account, CodexStrategyDeadline Kind, DateTimeOffset At)> Urgent)
{
    private static readonly TimeSpan UrgencyWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumQuotaAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RedeemLead = TimeSpan.FromMinutes(30);

    public static CodexUsageStrategy Create(
        IReadOnlyList<CodexAccountInfo> accounts,
        IReadOnlyList<CodexAccountQuota> quotas,
        DateTimeOffset now)
    {
        var byAccount = quotas.ToLookup(quota => quota.AccountId, StringComparer.Ordinal);
        var rows = accounts.Where(account => CodexAccountFormatting.PlanLabel(account.Plan) != "API key")
            .Select(account => (Account: account, Quota: byAccount[account.AccountId]
                .OrderByDescending(quota => quota.CapturedAt).FirstOrDefault()))
            .GroupBy(item => item.Quota?.QuotaIdentity ?? item.Account.AccountId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Quota?.CapturedAt)
                .ThenByDescending(item => item.Account.Active).First())
            .Select(item => Project(item.Account, item.Quota, now))
            .OrderBy(row => row.State)
            .ThenBy(row => row.State == CodexStrategyState.Ready && row.HasLittleQuota)
            .ThenBy(row => row.Deadline ?? row.Recovery ?? DateTimeOffset.MaxValue)
            .ThenByDescending(row => row.Account.Active)
            .ToArray();
        var urgent = rows.SelectMany(row => new[]
            {
                (Account: row, Kind: row.DeadlineKind, At: row.Deadline),
                (Account: row, Kind: (CodexStrategyDeadline?)CodexStrategyDeadline.ResetCredit, At: row.RedeemBy),
            })
            .Where(item => item.Kind is not null && item.At > now && item.At - now <= UrgencyWindow)
            .OrderBy(item => item.At)
            .Select(item => (item.Account, item.Kind!.Value, item.At!.Value))
            .ToArray();
        return new(rows, urgent);
    }

    private static CodexStrategyAccount Project(
        CodexAccountInfo account,
        CodexAccountQuota? quota,
        DateTimeOffset now)
    {
        var subscription = CodexAccountFormatting.PlanLabel(account.Plan) == "free"
            ? null
            : Earlier(account.SubscriptionActiveUntil, quota?.SubscriptionActiveUntil);
        var windows = quota?.Windows ?? [];
        var expired = subscription <= now;
        var confirmed = windows.Count > 0
            && quota?.CapturedAt is { } captured && captured <= now && now - captured <= MaximumQuotaAge
            && windows.All(window => window.UsedPercent is >= 0 and <= 100 && !(window.ResetsAt <= now));
        var blocked = confirmed
            ? windows.Where(window => window.UsedPercent == 100).ToArray()
            : [];
        var recovery = blocked.Length > 0 && blocked.All(window => window.ResetsAt is not null)
            ? blocked.Max(window => window.ResetsAt)
            : null;
        if (recovery >= subscription) recovery = null;
        var state = expired ? CodexStrategyState.Expired
            : !confirmed ? CodexStrategyState.Refresh
            : blocked.Length > 0 ? CodexStrategyState.Waiting
            : CodexStrategyState.Ready;
        var nextReset = confirmed ? windows.Min(window => window.ResetsAt) : null;
        var deadline = state == CodexStrategyState.Ready ? Earlier(nextReset, subscription) : null;
        var deadlineKind = deadline is null ? (CodexStrategyDeadline?)null
            : deadline == subscription ? CodexStrategyDeadline.Subscription : CodexStrategyDeadline.Reset;

        var cardExpiry = !expired && quota?.ResetCreditsAvailable != 0 && quota?.NextResetCreditExpiry > now
            ? quota.NextResetCreditExpiry
            : null;
        var redeemBy = cardExpiry is null ? null : Earlier(cardExpiry, subscription);
        var lastReview = redeemBy is { } cutoff ? cutoff - RedeemLead : (DateTimeOffset?)null;
        var canRedeem = redeemBy is not null && confirmed && quota!.NextResetCreditCoversMainQuota
            && windows.Any(window => window.UsedPercent > 20);
        return new(account, windows, state, deadlineKind, deadline, recovery, cardExpiry, redeemBy,
            lastReview > now ? lastReview : null,
            quota?.ResetCreditsAvailable, canRedeem, nextReset < lastReview);
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right) =>
        right is null || left < right ? left : right;
}
