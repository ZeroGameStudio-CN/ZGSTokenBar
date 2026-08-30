using System.Diagnostics.CodeAnalysis;

namespace ZGSTokenBar.Core;

public sealed record CodexModelPricing(
    string CanonicalModel,
    decimal InputUsdPerMillionTokens,
    decimal CachedInputUsdPerMillionTokens,
    decimal OutputUsdPerMillionTokens,
    decimal? CacheWriteInputUsdPerMillionTokens = null,
    bool AppliesLongContextSurcharge = false);

public sealed record CodexModelTokenUsage(
    string? Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens = 0,
    long UnattributedTokens = 0,
    bool IsLongContext = false,
    long CacheWriteInputTokens = 0)
{
    public long AttributedTokens => checked(InputTokens + OutputTokens);
    public long TotalTokens => checked(AttributedTokens + UnattributedTokens);
}

public sealed record CodexCostEstimate(
    string? Model,
    string? CanonicalModel,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long UnattributedTokens,
    bool IsLongContext,
    decimal? ApiEquivalentUsd)
{
    public bool IsPriced => ApiEquivalentUsd.HasValue;
    public long UncachedInputTokens => InputTokens - CachedInputTokens - CacheWriteInputTokens;
    public long AttributedTokens => checked(InputTokens + OutputTokens);
    public long TotalTokens => checked(AttributedTokens + UnattributedTokens);
}

public sealed record CodexSpendPeriod(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long UnattributedTokens,
    long PricedTokens,
    long UnpricedTokens,
    decimal PricedApiEquivalentUsd,
    long CacheWriteInputTokens = 0)
{
    public static CodexSpendPeriod Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0m, 0);

    public long AttributedTokens => checked(InputTokens + OutputTokens);
    public long TotalTokens => checked(AttributedTokens + UnattributedTokens);
    public bool HasUsage => TotalTokens > 0;
    public bool HasPricedUsage => PricedTokens > 0;
    public bool HasUnpricedUsage => UnpricedTokens > 0;
    public bool IsFullyPriced => HasUsage && !HasUnpricedUsage;
    public bool IsPartiallyPriced => HasPricedUsage && HasUnpricedUsage;
    public decimal? ApiEquivalentUsd => HasPricedUsage ? PricedApiEquivalentUsd : null;
}

public static class CodexPricingCatalog
{
    public const long LongContextInputTokenThreshold = 272_000;

    private const decimal TokensPerMillion = 1_000_000m;
    private const decimal LongContextInputMultiplier = 2m;
    private const decimal LongContextOutputMultiplier = 1.5m;

    private static readonly CodexModelPricing Gpt56Sol = new(
        "gpt-5.6-sol",
        4m,
        0.4m,
        20m,
        5m,
        true);

    private static readonly CodexModelPricing Gpt56Terra = new(
        "gpt-5.6-terra",
        2m,
        0.2m,
        12m,
        2.5m,
        true);

    private static readonly CodexModelPricing Gpt56Luna = new(
        "gpt-5.6-luna",
        0.2m,
        0.02m,
        1.2m,
        0.25m,
        true);

    private static readonly IReadOnlyDictionary<string, CodexModelPricing> PricingByExactModel =
        new Dictionary<string, CodexModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = Gpt56Sol,
            ["gpt-5.6"] = Gpt56Sol,
            ["gpt-5.6-terra"] = Gpt56Terra,
            ["gpt-5.6-luna"] = Gpt56Luna,
            ["gpt-5.5"] = new(
                "gpt-5.5",
                5m,
                0.5m,
                30m,
                AppliesLongContextSurcharge: true),
            ["gpt-5.4"] = new(
                "gpt-5.4",
                2.5m,
                0.25m,
                15m,
                AppliesLongContextSurcharge: true),
            ["gpt-5.4-mini"] = new("gpt-5.4-mini", 0.75m, 0.075m, 4.5m),
            ["gpt-5.3-codex"] = new("gpt-5.3-codex", 1.75m, 0.175m, 14m),
            ["gpt-5.2"] = new("gpt-5.2", 1.75m, 0.175m, 14m),
        };

    private static readonly IReadOnlySet<string> ExplicitlyUnpricedModels =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gpt-5.3-codex-spark",
        };

    public static DateOnly PriceSnapshotDate { get; } = new(2026, 8, 21);

    public static IReadOnlyCollection<CodexModelPricing> PricedModels { get; } =
        PricingByExactModel.Values
            .Distinct()
            .ToArray();

    public static bool TryGetPricing(
        string? model,
        [NotNullWhen(true)] out CodexModelPricing? pricing)
    {
        pricing = null;
        return !string.IsNullOrWhiteSpace(model)
            && PricingByExactModel.TryGetValue(model.Trim(), out pricing);
    }

    public static bool IsExplicitlyUnpriced(string? model) =>
        !string.IsNullOrWhiteSpace(model)
        && ExplicitlyUnpricedModels.Contains(model.Trim());

    public static CodexCostEstimate Estimate(CodexModelTokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ValidateUsage(usage);

        if (!TryGetPricing(usage.Model, out var pricing))
        {
            return new CodexCostEstimate(
                usage.Model,
                null,
                usage.InputTokens,
                usage.CachedInputTokens,
                usage.CacheWriteInputTokens,
                usage.OutputTokens,
                usage.ReasoningOutputTokens,
                usage.UnattributedTokens,
                usage.IsLongContext,
                null);
        }

        var inputMultiplier = usage.IsLongContext && pricing.AppliesLongContextSurcharge
            ? LongContextInputMultiplier
            : 1m;
        var outputMultiplier = usage.IsLongContext && pricing.AppliesLongContextSurcharge
            ? LongContextOutputMultiplier
            : 1m;
        if (usage.CacheWriteInputTokens > 0
            && pricing.CacheWriteInputUsdPerMillionTokens is null)
        {
            return new CodexCostEstimate(
                usage.Model,
                pricing.CanonicalModel,
                usage.InputTokens,
                usage.CachedInputTokens,
                usage.CacheWriteInputTokens,
                usage.OutputTokens,
                usage.ReasoningOutputTokens,
                usage.UnattributedTokens,
                usage.IsLongContext,
                null);
        }
        var uncachedInputTokens = usage.InputTokens
            - usage.CachedInputTokens
            - usage.CacheWriteInputTokens;
        var cost = (
            uncachedInputTokens * pricing.InputUsdPerMillionTokens * inputMultiplier
            + usage.CachedInputTokens * pricing.CachedInputUsdPerMillionTokens * inputMultiplier
            + usage.CacheWriteInputTokens
                * (pricing.CacheWriteInputUsdPerMillionTokens ?? 0m)
                * inputMultiplier
            + usage.OutputTokens * pricing.OutputUsdPerMillionTokens * outputMultiplier)
            / TokensPerMillion;

        return new CodexCostEstimate(
            usage.Model,
            pricing.CanonicalModel,
            usage.InputTokens,
            usage.CachedInputTokens,
            usage.CacheWriteInputTokens,
            usage.OutputTokens,
            usage.ReasoningOutputTokens,
            usage.UnattributedTokens,
            usage.IsLongContext,
            cost);
    }

    public static CodexSpendPeriod SummarizePeriod(IEnumerable<CodexModelTokenUsage> usages)
    {
        ArgumentNullException.ThrowIfNull(usages);

        long inputTokens = 0;
        long cachedInputTokens = 0;
        long outputTokens = 0;
        long cacheWriteInputTokens = 0;
        long reasoningOutputTokens = 0;
        long unattributedTokens = 0;
        long pricedTokens = 0;
        long unpricedTokens = 0;
        decimal pricedApiEquivalentUsd = 0m;

        foreach (var usage in usages)
        {
            var estimate = Estimate(usage);
            var attributedTokens = estimate.AttributedTokens;
            checked
            {
                inputTokens += estimate.InputTokens;
                cachedInputTokens += estimate.CachedInputTokens;
                cacheWriteInputTokens += estimate.CacheWriteInputTokens;
                outputTokens += estimate.OutputTokens;
                reasoningOutputTokens += estimate.ReasoningOutputTokens;
                unattributedTokens += estimate.UnattributedTokens;
                unpricedTokens += estimate.UnattributedTokens;
                if (estimate.ApiEquivalentUsd is { } cost)
                {
                    pricedTokens += attributedTokens;
                    pricedApiEquivalentUsd += cost;
                }
                else
                {
                    unpricedTokens += attributedTokens;
                }
            }
        }

        return new CodexSpendPeriod(
            inputTokens,
            cachedInputTokens,
            outputTokens,
            reasoningOutputTokens,
            unattributedTokens,
            pricedTokens,
            unpricedTokens,
            pricedApiEquivalentUsd,
            cacheWriteInputTokens);
    }

    private static void ValidateUsage(CodexModelTokenUsage usage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usage.InputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.CachedInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.CacheWriteInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.OutputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.ReasoningOutputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(usage.UnattributedTokens);
        if (usage.CacheWriteInputTokens > usage.InputTokens
            || usage.CachedInputTokens > usage.InputTokens - usage.CacheWriteInputTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usage),
                "Cached and cache-write input tokens cannot exceed input tokens.");
        }
    }
}
