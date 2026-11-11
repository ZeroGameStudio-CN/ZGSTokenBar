namespace ZGSTokenBar.Core;

public enum UsageFreshness
{
    Current,
    Cached,
    Loading,
    MissingCredentials,
    Unavailable,
    Stale,
}

public enum UsageSurface
{
    /// <summary>Host draws a taskbar card and includes the provider in the usage board.</summary>
    Mini,

    /// <summary>Existing quota pipeline owns the live read. The board only projects its snapshot.</summary>
    Projected,
}

public sealed record UsageWindow(
    string Id,
    string Label,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    TimeSpan? Duration,
    bool Unlimited = false);

public sealed record UsageCredits(
    string Currency,
    decimal? PrepaidBalance,
    decimal? OnDemandUsed,
    decimal? OnDemandCap);

public sealed record UsageLedgerPeriod(long Tokens, decimal? ApiEquivalentCost);

public sealed record UsageLedger(
    UsageLedgerPeriod Today,
    UsageLedgerPeriod SevenDays,
    UsageLedgerPeriod ThirtyDays);

public sealed record UsageProductShare(string Product, double Percent);

/// <summary>
/// Subscription/billing dates read from the same usage response (never from credentials).
/// <see cref="Kind"/>: "billing" = paid period that renews at <see cref="End"/>; "quota-reset" = the
/// plan's monthly allowance reset; "active-until" = paid access ends at <see cref="End"/>.
/// <see cref="Source"/> names the response field the dates came from.
/// </summary>
public sealed record UsageSubscriptionPeriod(DateTimeOffset? Start, DateTimeOffset? End, string Kind, string Source);

public sealed record UsageAccountSnapshot(
    string AccountId,
    string Label,
    string? Plan,
    string SourceKind,
    IReadOnlyList<UsageWindow> Windows,
    UsageCredits? Credits,
    UsageLedger? Ledger,
    IReadOnlyList<UsageProductShare> Products,
    UsageFreshness Freshness,
    DateTimeOffset? SampledAt,
    string? ErrorKey)
{
    public UsageSubscriptionPeriod? Period { get; init; }
}

public sealed record UsageProviderSnapshot(
    string ProviderId,
    string DisplayName,
    string Accent,
    int Order,
    IReadOnlyList<UsageAccountSnapshot> Accounts);

public sealed record UsageBoard(IReadOnlyList<UsageProviderSnapshot> Providers)
{
    public static UsageBoard Empty { get; } = new([]);

    public int AccountCount => Providers.Sum(provider => provider.Accounts.Count);
}

public sealed record ProviderDefinition(
    string Id,
    string DisplayName,
    string Accent,
    int Order,
    UsageSurface Surface,
    string Source);

public static class UsageProviderIds
{
    public const string Claude = "zgstokenbar.provider.claude";
    public const string Codex = "zgstokenbar.provider.codex";
    public const string Grok = "zgstokenbar.provider.grok-local";
    public const string Antigravity = "zgstokenbar.provider.antigravity-local";
    public const string Cursor = "zgstokenbar.provider.cursor";
    public const string Copilot = "zgstokenbar.provider.copilot";
    public const string DeepSeek = "zgstokenbar.provider.deepseek";
    public const string OpenCode = "zgstokenbar.provider.opencode";
    public const string Xai = "zgstokenbar.provider.xai";
    public const string OpenRouter = "zgstokenbar.provider.openrouter";
}

public static class ProviderCatalog
{
    /// <summary>Provider metadata in board order; the providers themselves live in <see cref="UsageProviderRegistry"/>.</summary>
    public static IReadOnlyList<ProviderDefinition> All { get; } =
        UsageProviderRegistry.All.Select(provider => provider.Definition).ToArray();

    private static readonly HashSet<string> MiniIds = All
        .Where(provider => provider.Surface == UsageSurface.Mini)
        .Select(provider => provider.Id)
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> PersistentMiniIds => MiniIds;

    public static bool StartsCollapsed(string? areaId) =>
        areaId is not null && MiniIds.Contains(areaId);

    public static bool IsUsageProvider(string? id) =>
        id is not null && All.Any(provider => string.Equals(provider.Id, id, StringComparison.Ordinal));

    public static ProviderDefinition? Find(string id) => UsageProviderRegistry.Find(id)?.Definition;
}

public static class UsageAccounts
{
    public const int MaximumAccounts = 16;

    public static IReadOnlyList<UsageAccountSnapshot> Normalize(IEnumerable<UsageAccountSnapshot> accounts) =>
        accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.AccountId))
            .GroupBy(account => account.AccountId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(account => account.Freshness == UsageFreshness.Current)
                .ThenByDescending(account => account.SampledAt ?? DateTimeOffset.MinValue)
                .First())
            .OrderBy(account => account.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(account => account.AccountId, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumAccounts)
            .ToArray();
}
