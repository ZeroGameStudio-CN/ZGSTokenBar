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
    string? ErrorKey);

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
    string FetcherKey,
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
    public static IReadOnlyList<ProviderDefinition> All { get; } =
    [
        new(UsageProviderIds.Grok, "Grok", "#38bdf8", 900, "grok", UsageSurface.Mini,
            "GET https://cli-chat-proxy.grok.com/v1/billing?format=credits with ~/.grok/auth.json, else grok usage --json"),
        new(UsageProviderIds.Claude, "Claude", "#d97757", 100, "claude-extra", UsageSurface.Projected,
            "Existing OAuth usage read; extra accounts from ~/.claude/accounts"),
        new(UsageProviderIds.Codex, "Codex", "#10a37f", 110, "none", UsageSurface.Projected,
            "Existing ChatGPT wham/usage read, one snapshot per local account"),
        new(UsageProviderIds.Cursor, "Cursor", "#8b5cf6", 920, "cursor", UsageSurface.Mini,
            "GET https://cursor.com/api/usage-summary with the local Cursor session"),
        new(UsageProviderIds.Antigravity, "Antigravity", "#4285f4", 910, "antigravity", UsageSurface.Mini,
            "agy /usage text or Cloud Code retrieveUserQuotaSummary"),
        new(UsageProviderIds.Copilot, "Copilot", "#6e40c9", 930, "copilot", UsageSurface.Mini,
            "GET https://api.github.com/copilot_internal/user with the GitHub CLI token"),
        new(UsageProviderIds.DeepSeek, "DeepSeek", "#4d6bfe", 940, "deepseek", UsageSurface.Mini,
            "GET https://api.deepseek.com/user/balance; Harness balance still projects in"),
        new(UsageProviderIds.OpenCode, "OpenCode", "#f97316", 950, "opencode", UsageSurface.Mini,
            "GET https://opencode.ai/zen/go/v1/usage with the local OpenCode key"),
        new(UsageProviderIds.Xai, "xAI", "#a3e635", 960, "xai", UsageSurface.Mini,
            "GET https://management-api.x.ai/v1/billing/teams/{team}/prepaid/balance"),
        new(UsageProviderIds.OpenRouter, "OpenRouter", "#6467f2", 970, "openrouter", UsageSurface.Mini,
            "GET https://openrouter.ai/api/v1/credits"),
    ];

    private static readonly HashSet<string> MiniIds = All
        .Where(provider => provider.Surface == UsageSurface.Mini)
        .Select(provider => provider.Id)
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> PersistentMiniIds => MiniIds;

    public static bool StartsCollapsed(string? areaId) =>
        areaId is not null && MiniIds.Contains(areaId);

    public static bool IsUsageProvider(string? id) =>
        id is not null && All.Any(provider => string.Equals(provider.Id, id, StringComparison.Ordinal));

    public static ProviderDefinition? Find(string id) =>
        All.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.Ordinal));
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
