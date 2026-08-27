namespace ZGSTokenBar.Core;

public sealed class QuotaCoordinator : IDisposable
{
    public static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(7);
    internal static readonly TimeSpan ExhaustedProviderRefreshInterval = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient = QuotaHttp.Create();
    private readonly HttpClient _aiGatewayHttpClient = QuotaHttp.CreateAiGateway();
    private readonly ClaudeQuotaService _claude;
    private readonly CodexQuotaService _codex;
    private readonly AiGatewayUsageService _aiGatewayUsage;
    private readonly Dictionary<ProviderKind, DateTimeOffset> _lastProviderAttemptAt = [];

    public QuotaCoordinator()
    {
        _claude = new ClaudeQuotaService(_httpClient);
        _codex = new CodexQuotaService(_httpClient);
        _aiGatewayUsage = new AiGatewayUsageService(_aiGatewayHttpClient);
    }

    public Task<AiGatewayUsageFetchResult> FetchAiGatewayUsageAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        _aiGatewayUsage.FetchAsync(now, cancellationToken);

    public async Task<QuotaSnapshot> RefreshAsync(
        AppSettings settings,
        QuotaSnapshot? previous,
        CancellationToken cancellationToken = default,
        bool allowClaudeOAuthRefresh = false,
        bool forceProviderRefresh = false,
        IReadOnlySet<ProviderKind>? activeProviders = null)
    {
        var tasks = new List<Task<ProviderResult>>();
        var now = DateTimeOffset.UtcNow;

        void AddProviderTask(ProviderKind provider, Func<Task<ProviderResult>> fetch)
        {
            var deferred = !forceProviderRefresh
                && ShouldDeferProviderRefresh(
                    previous,
                    provider,
                    now,
                    _lastProviderAttemptAt.GetValueOrDefault(provider))
                ? DeferredProviderResult(previous, provider)
                : null;
            if (deferred is not null)
            {
                tasks.Add(Task.FromResult(deferred));
            }
            else
            {
                _lastProviderAttemptAt[provider] = now;
                tasks.Add(fetch());
            }
        }

        if (settings.IsEnabled(ProviderKind.Claude)
            && (activeProviders is null || activeProviders.Contains(ProviderKind.Claude)))
        {
            AddProviderTask(
                ProviderKind.Claude,
                () => _claude.FetchAsync(settings, cancellationToken, allowClaudeOAuthRefresh));
        }
        if (settings.IsEnabled(ProviderKind.Codex)
            && (activeProviders is null || activeProviders.Contains(ProviderKind.Codex)))
        {
            AddProviderTask(ProviderKind.Codex, () => _codex.FetchAsync(cancellationToken));
        }
        var results = await Task.WhenAll(tasks);
        return MergeResults(results, previous, now);
    }

    internal static QuotaSnapshot AttachSub2ApiPool(
        QuotaSnapshot snapshot,
        QuotaSnapshot? previous,
        Sub2ApiPoolFetchResult result)
    {
        var pool = result.Pool ?? CachedSub2ApiPool(previous, result.Code, snapshot.CapturedAt);
        if (pool is null) return snapshot;
        var cards = snapshot.Cards
            .Select(card => IsSub2ApiService(card) ? card with { Sub2ApiPool = pool } : card)
            .ToArray();
        return cards.Any(card => card.Sub2ApiPool == pool)
            ? snapshot with { Cards = cards }
            : snapshot;
    }

    private static Sub2ApiPoolAvailability? CachedSub2ApiPool(
        QuotaSnapshot? previous,
        ProviderHealthCode code,
        DateTimeOffset now)
    {
        if (code is ProviderHealthCode.MissingCredentials or ProviderHealthCode.EndpointBlocked) return null;
        var pool = previous?.Cards.FirstOrDefault(IsSub2ApiService)?.Sub2ApiPool;
        return pool is not null && Sub2ApiPoolPolicy.HasFreshObservation(pool, now)
            ? pool with { Status = Sub2ApiPoolStatus.Stale }
            : null;
    }

    internal static QuotaSnapshot AttachSub2ApiUsage(
        QuotaSnapshot snapshot,
        QuotaSnapshot? previous,
        Sub2ApiUsageFetchResult result)
    {
        var usage = result.Usage ?? CachedSub2ApiUsage(previous, result.Code, snapshot.CapturedAt);
        if (usage is null) return snapshot;
        var cards = snapshot.Cards
            .Select(card => IsSub2ApiService(card) ? card with { Sub2ApiUsage = usage } : card)
            .ToArray();
        return cards.Any(card => card.Sub2ApiUsage == usage)
            ? snapshot with { Cards = cards }
            : snapshot;
    }

    private static Sub2ApiUsageSummary? CachedSub2ApiUsage(
        QuotaSnapshot? previous,
        ProviderHealthCode code,
        DateTimeOffset now)
    {
        if (code is ProviderHealthCode.MissingCredentials or ProviderHealthCode.EndpointBlocked) return null;
        var usage = previous?.Cards.FirstOrDefault(IsSub2ApiService)?.Sub2ApiUsage;
        return usage is not null && Sub2ApiUsagePolicy.HasFreshObservation(usage, now)
            ? usage with { Status = Sub2ApiUsageStatus.Stale }
            : null;
    }

    internal static QuotaSnapshot AttachSub2ApiQuota(
        QuotaSnapshot snapshot,
        QuotaSnapshot? previous,
        Sub2ApiQuotaFetchResult result)
    {
        var quota = result.Quota ?? CachedSub2ApiQuota(previous, result.Code, snapshot.CapturedAt);
        if (quota is null) return snapshot;
        var cards = snapshot.Cards
            .Select(card => IsSub2ApiService(card) ? card with { Sub2ApiQuota = quota } : card)
            .ToArray();
        return cards.Any(card => card.Sub2ApiQuota == quota)
            ? snapshot with { Cards = cards }
            : snapshot;
    }

    private static Sub2ApiQuotaSummary? CachedSub2ApiQuota(
        QuotaSnapshot? previous,
        ProviderHealthCode code,
        DateTimeOffset now)
    {
        if (code is ProviderHealthCode.MissingCredentials or ProviderHealthCode.EndpointBlocked) return null;
        var quota = previous?.Cards.FirstOrDefault(IsSub2ApiService)?.Sub2ApiQuota;
        return quota is not null && Sub2ApiQuotaPolicy.HasFreshObservation(quota, now)
            ? quota with { Status = Sub2ApiQuotaStatus.Stale }
            : null;
    }

    internal static QuotaSnapshot AttachSub2ApiAccountAvailability(
        QuotaSnapshot snapshot,
        QuotaSnapshot? previous,
        Sub2ApiAccountAvailabilityFetchResult result)
    {
        var availability = result.Availability
            ?? CachedSub2ApiAccountAvailability(previous, result.Code, snapshot.CapturedAt);
        if (!snapshot.Cards.Any(IsSub2ApiService)) return snapshot;
        var cards = snapshot.Cards
            .Select(card => IsSub2ApiService(card) ? card with { Sub2ApiAccountAvailability = availability } : card)
            .ToArray();
        return snapshot with { Cards = cards };
    }

    private static Sub2ApiAccountAvailabilitySummary? CachedSub2ApiAccountAvailability(
        QuotaSnapshot? previous,
        ProviderHealthCode code,
        DateTimeOffset now)
    {
        if (code is ProviderHealthCode.MissingCredentials or ProviderHealthCode.EndpointBlocked) return null;
        var availability = previous?.Cards.FirstOrDefault(IsSub2ApiService)?.Sub2ApiAccountAvailability;
        return availability is not null && Sub2ApiAccountAvailabilityPolicy.HasFreshObservation(availability, now)
            ? availability with { Status = Sub2ApiQuotaStatus.Stale }
            : null;
    }

    private static bool IsSub2ApiService(QuotaCard card) =>
        Sub2ApiServicePresentation.IsSub2ApiService(card);

    public static QuotaSnapshot MergeResults(
        IReadOnlyList<ProviderResult> results,
        QuotaSnapshot? previous,
        DateTimeOffset now)
    {

        var cards = new List<QuotaCard>();
        foreach (var result in results)
        {
            var providerCards = result.Cards.Where(card => !card.IsService).ToArray();
            var liveCards = result.Cards.Where(HasUsage).ToArray();
            var serviceCards = result.Cards.Where(card => card.IsService).ToArray();
            if (result.Provider == ProviderKind.AiGateway)
            {
                if (serviceCards.Length > 0)
                {
                    cards.AddRange(serviceCards.Select(card => card.CapturedAt is null
                        ? card with { CapturedAt = now }
                        : card));
                }
                else if (result.Health.Code != ProviderHealthCode.MissingCredentials
                    && previous?.Cards.FirstOrDefault(card =>
                        card.Provider == ProviderKind.AiGateway && card.Balance is not null) is { } previousCard)
                {
                    var balance = previousCard.Balance!;
                    cards.Add(previousCard with
                    {
                        CapturedAt = now,
                        Balance = balance with { Status = AiGatewayBalanceStatus.Stale },
                    });
                }
                else
                {
                    cards.Add(AiGatewayBalanceService.UnavailableCard(now));
                }
                continue;
            }
            if (result.ReplaceCachedCards)
            {
                cards.AddRange(result.Cards.Select(card => card.CapturedAt is null
                    ? card with { CapturedAt = now }
                    : card));
                continue;
            }
            if (liveCards.Length > 0)
            {
                cards.AddRange(providerCards.Select(card => card.CapturedAt is null
                    ? card with { CapturedAt = now }
                    : card));
                cards.AddRange(serviceCards.Select(card => card.CapturedAt is null
                    ? card with { CapturedAt = now }
                    : card));
                continue;
            }

            var cachedCards = result.Provider == ProviderKind.Claude
                ? previous?.Cards
                    .Where(card => card.Provider == ProviderKind.Claude && IsFresh(card.CapturedAt, now))
                    .Select(card => KeepActiveExhaustedWindows(card, now))
                    .Where(HasUsage)
                    .ToArray() ?? []
                : previous?.Cards
                    .Where(card => card.Provider == result.Provider && HasUsage(card))
                    .Select(card => card.CapturedAt is null
                        ? card with { CapturedAt = previous.CapturedAt }
                        : card)
                    .Where(card => IsFresh(card.CapturedAt, now))
                    .Select(card => RemoveResetWindows(card, now))
                    .Where(HasUsage)
                    .ToArray() ?? [];
            cards.AddRange(cachedCards.Length > 0
                ? cachedCards
                : result.Cards.Select(card => card with { CapturedAt = now }));
            if (cachedCards.Length > 0)
            {
                cards.AddRange(serviceCards.Select(card => card with { CapturedAt = now }));
            }
        }

        cards = cards
            .OrderBy(card => card.Provider)
            .ThenByDescending(card => card.Active)
            .ToList();
        var snapshot = new QuotaSnapshot(cards, results.Select(result => result.Health).ToArray(), now)
        {
            CodexAccounts = results
                .Where(result => result.Provider == ProviderKind.Codex)
                .SelectMany(result => result.CodexAccounts)
                .ToArray(),
            CodexQuotaTokenCounters = results
                .Where(result => result.Provider == ProviderKind.Codex)
                .SelectMany(result => result.CodexQuotaTokenCounters)
                .ToArray(),
        };
        return snapshot;
    }

    private static bool HasUsage(QuotaCard card) => card.Windows.Any(window => window.UsedPercent is not null);

    internal static bool ShouldDeferProviderRefresh(
        QuotaSnapshot? snapshot,
        ProviderKind provider,
        DateTimeOffset now,
        DateTimeOffset? lastAttemptAt = null)
    {
        var cards = snapshot?.Cards
            .Where(card => card.Provider == provider && HasUsage(card))
            .ToArray() ?? [];
        if (cards.Length == 0
            || cards.Any(card => !card.Windows.Any(window => IsActivelyExhausted(window, now))))
        {
            return false;
        }

        var sampledAt = cards
            .Select(card => card.CapturedAt ?? snapshot!.CapturedAt)
            .Min();
        var intervalStart = lastAttemptAt is { } attempt && attempt > sampledAt ? attempt : sampledAt;
        var nextIntervalRefresh = intervalStart + ExhaustedProviderRefreshInterval;
        var nextReset = cards
            .SelectMany(card => card.Windows)
            .Where(window => IsActivelyExhausted(window, now))
            .Select(window => window.ResetsAt!.Value)
            .Min();
        return now < (nextReset < nextIntervalRefresh ? nextReset : nextIntervalRefresh);
    }

    private static ProviderResult? DeferredProviderResult(QuotaSnapshot? previous, ProviderKind provider)
    {
        if (previous is null) return null;
        var cards = previous.Cards
            .Where(card => card.Provider == provider && HasUsage(card))
            .ToArray();
        if (cards.Length == 0) return null;
        var health = previous.Health.FirstOrDefault(item => item.Provider == provider)
            ?? new ProviderHealth(
                provider,
                true,
                $"{provider} quota is cached.",
                ProviderHealthCode.Cached);
        if (health.Code == ProviderHealthCode.Unknown)
        {
            health = health with
            {
                Code = health.Connected ? ProviderHealthCode.Cached : ProviderHealthCode.Unavailable,
            };
        }
        return new ProviderResult(provider, cards, health)
        {
            CodexAccounts = provider == ProviderKind.Codex
                ? previous.CodexAccounts
                : [],
        };
    }

    private static bool IsActivelyExhausted(QuotaWindow window, DateTimeOffset now) =>
        window.UsedPercent is >= 100
        && window.ResetsAt is { } reset
        && reset > now;

    private static QuotaCard KeepActiveExhaustedWindows(QuotaCard card, DateTimeOffset now) => card with
    {
        Windows = card.Windows
            .Select(window => IsActivelyExhausted(window, now)
                ? window
                : window with { UsedPercent = null, ResetsAt = null })
            .ToArray(),
    };

    private static QuotaCard RemoveResetWindows(QuotaCard card, DateTimeOffset now) => card with
    {
        Windows = card.Windows
            .Select(window => window.ResetsAt is { } reset && reset <= now
                ? window with { UsedPercent = null, ResetsAt = null }
                : window)
            .ToArray(),
    };

    public static bool IsFresh(DateTimeOffset? capturedAt, DateTimeOffset now) =>
        capturedAt is { } timestamp
        && timestamp <= now.AddMinutes(1)
        && now - timestamp <= CacheMaxAge;

    public void Dispose()
    {
        _httpClient.Dispose();
        _aiGatewayHttpClient.Dispose();
    }
}
