using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public static class Sub2ApiAccountAvailabilityPolicy
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(1);

    public static Sub2ApiQuotaStatus EffectiveStatus(
        Sub2ApiAccountAvailabilitySummary availability,
        DateTimeOffset now)
    {
        if (availability.Status is Sub2ApiQuotaStatus.Unavailable or Sub2ApiQuotaStatus.Unknown)
        {
            return availability.Status;
        }

        return IsWithinFreshnessWindow(availability, now)
            ? availability.Status
            : Sub2ApiQuotaStatus.Unavailable;
    }

    public static bool HasFreshObservation(
        Sub2ApiAccountAvailabilitySummary availability,
        DateTimeOffset now) =>
        Sub2ApiAccountAvailabilityFormatting.IsSchemaValid(availability)
        && availability.Coverage is (Sub2ApiAccountAvailabilityCoverage.Complete
            or Sub2ApiAccountAvailabilityCoverage.Partial)
        && IsWithinFreshnessWindow(availability, now);

    public static Sub2ApiAccountAvailabilitySummary EffectiveAvailability(
        Sub2ApiAccountAvailabilitySummary availability,
        DateTimeOffset now) =>
        availability.Status == Sub2ApiQuotaStatus.Unavailable
            || IsWithinFreshnessWindow(availability, now)
                ? availability
                : Unavailable();

    public static Sub2ApiAccountAvailabilitySummary Unavailable() =>
        new(
            Sub2ApiQuotaStatus.Unavailable,
            Sub2ApiAccountAvailabilityCoverage.None,
            null,
            null,
            null,
            null,
            null,
            null);

    private static bool IsWithinFreshnessWindow(
        Sub2ApiAccountAvailabilitySummary availability,
        DateTimeOffset now) =>
        availability.Status is (Sub2ApiQuotaStatus.Available or Sub2ApiQuotaStatus.Stale)
        && availability.ObservedAt is { } observedAt
        && observedAt <= now.Add(FutureTolerance)
        && now - observedAt <= StaleAfter;
}

public static class Sub2ApiAccountAvailabilityFormatting
{
    private const double RelationTolerance = 0.0001;

    public static bool IsRenderable(Sub2ApiAccountAvailabilitySummary? availability) =>
        availability is not null
        && availability.Status is (Sub2ApiQuotaStatus.Available or Sub2ApiQuotaStatus.Stale)
        && availability.Accounts is not null
        && IsSchemaValid(availability);

    public static bool IsSchemaValid(Sub2ApiAccountAvailabilitySummary? availability)
    {
        if (availability is null) return false;

        if (availability.Status == Sub2ApiQuotaStatus.Unavailable)
        {
            return availability.Coverage == Sub2ApiAccountAvailabilityCoverage.None
                && availability.EligibleAccountCount is null
                && availability.ReadableAccountCount is null
                && availability.AggregateRemainingPercent is null
                && availability.RemainingAccountEquivalents is null
                && availability.Accounts is null
                && availability.ObservedAt is null;
        }

        if (availability.Status is not (Sub2ApiQuotaStatus.Available or Sub2ApiQuotaStatus.Stale)
            || availability.ObservedAt is null
            || availability.EligibleAccountCount is not { } eligible
            || availability.ReadableAccountCount is not { } readable
            || eligible < 0
            || eligible > 64
            || readable < 0
            || readable > eligible
            || availability.Accounts is not { } accounts
            || accounts.Count > 64
            || accounts.Count != eligible
            || !ContiguousAccounts(accounts))
        {
            return false;
        }

        var actualReadable = accounts.Count(account =>
            account.State == Sub2ApiAccountAvailabilityState.Available);
        if (actualReadable != readable)
        {
            return false;
        }

        if (availability.Status == Sub2ApiQuotaStatus.Stale
            && availability.Coverage is not (Sub2ApiAccountAvailabilityCoverage.Complete
                or Sub2ApiAccountAvailabilityCoverage.Partial))
        {
            return false;
        }

        return availability.Coverage switch
        {
            Sub2ApiAccountAvailabilityCoverage.Complete =>
                eligible > 0
                && readable == eligible
                && accounts.All(account =>
                    account.State == Sub2ApiAccountAvailabilityState.Available
                    && IsPercent(account.RemainingPercent))
                && IsCompleteAggregate(availability, accounts, eligible),
            Sub2ApiAccountAvailabilityCoverage.Partial =>
                eligible > 0
                && readable > 0
                && readable < eligible
                && availability.AggregateRemainingPercent is null
                && availability.RemainingAccountEquivalents is null
                && accounts.All(AccountValueIsConsistent),
            Sub2ApiAccountAvailabilityCoverage.None =>
                readable == 0
                && availability.AggregateRemainingPercent is null
                && availability.RemainingAccountEquivalents is null
                && accounts.All(account =>
                    account.State == Sub2ApiAccountAvailabilityState.Unavailable
                    && account.RemainingPercent is null),
            _ => false,
        };
    }

    public static double? MeanRemainingPercent(Sub2ApiAccountAvailabilitySummary? availability) =>
        IsRenderable(availability)
        && availability!.Coverage == Sub2ApiAccountAvailabilityCoverage.Complete
            ? availability.AggregateRemainingPercent
            : null;

    public static bool IsComplete(Sub2ApiAccountAvailabilitySummary? availability) =>
        IsRenderable(availability)
        && availability!.Coverage == Sub2ApiAccountAvailabilityCoverage.Complete;

    private static bool ContiguousAccounts(
        IReadOnlyList<Sub2ApiAccountAvailabilityEntry> accounts)
    {
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            if (account is null || account.Slot != index + 1 || !AccountValueIsConsistent(account))
            {
                return false;
            }
        }
        return true;
    }

    private static bool AccountValueIsConsistent(Sub2ApiAccountAvailabilityEntry account) =>
        account.State switch
        {
            Sub2ApiAccountAvailabilityState.Available => IsPercent(account.RemainingPercent),
            Sub2ApiAccountAvailabilityState.Unavailable => account.RemainingPercent is null,
            _ => false,
        };

    private static bool IsCompleteAggregate(
        Sub2ApiAccountAvailabilitySummary availability,
        IReadOnlyList<Sub2ApiAccountAvailabilityEntry> accounts,
        int eligible)
    {
        if (!IsPercent(availability.AggregateRemainingPercent)
            || availability.RemainingAccountEquivalents is not { } equivalents
            || !double.IsFinite(equivalents)
            || equivalents is < 0 or > 64)
        {
            return false;
        }

        var expectedPercent = accounts.Average(account => account.RemainingPercent!.Value);
        var expectedEquivalents = accounts.Sum(account => account.RemainingPercent!.Value) / 100;
        return Math.Abs(availability.AggregateRemainingPercent!.Value - expectedPercent) <= RelationTolerance
            && Math.Abs(equivalents - expectedEquivalents) <= RelationTolerance
            && Math.Abs(equivalents - expectedPercent / 100 * eligible) <= RelationTolerance;
    }

    private static bool IsPercent(double? value) =>
        value is { } percent
        && double.IsFinite(percent)
        && percent is >= 0 and <= 100;
}

public enum Sub2ApiServicePresentationKind
{
    CompleteAvailability,
    PartialAvailability,
    KnownNoneAvailability,
    LegacyAggregateQuota,
    Usage,
    Pool,
    Unavailable,
}

public sealed record Sub2ApiServicePresentationState(
    Sub2ApiServicePresentationKind Kind,
    Sub2ApiAccountAvailabilitySummary? Availability = null,
    Sub2ApiQuotaWindow? LegacyQuota = null,
    Sub2ApiUsageSummary? Usage = null,
    Sub2ApiPoolAvailability? Pool = null);

public static class Sub2ApiServicePresentation
{
    private const double RelationTolerance = 0.0001;

    public static bool IsSub2ApiService(QuotaCard card) =>
        card.Provider == ProviderKind.Codex
        && card.IsService
        && string.Equals(card.ServiceDisplayName?.Trim(), "sub2api", StringComparison.OrdinalIgnoreCase);

    public static Sub2ApiServicePresentationState Resolve(QuotaCard card) =>
        Resolve(card, DateTimeOffset.UtcNow);

    public static Sub2ApiServicePresentationState Resolve(
        QuotaCard card,
        DateTimeOffset now)
    {
        if (!IsSub2ApiService(card))
        {
            return new(Sub2ApiServicePresentationKind.Unavailable);
        }

        if (card.Sub2ApiAccountAvailability is { } availability
            && Sub2ApiAccountAvailabilityFormatting.IsRenderable(availability)
            && Sub2ApiAccountAvailabilityPolicy.EffectiveStatus(availability, now)
                is not (Sub2ApiQuotaStatus.Unavailable or Sub2ApiQuotaStatus.Unknown))
        {
            var kind = availability.Coverage switch
            {
                Sub2ApiAccountAvailabilityCoverage.Complete =>
                    Sub2ApiServicePresentationKind.CompleteAvailability,
                Sub2ApiAccountAvailabilityCoverage.Partial =>
                    Sub2ApiServicePresentationKind.PartialAvailability,
                Sub2ApiAccountAvailabilityCoverage.None =>
                    Sub2ApiServicePresentationKind.KnownNoneAvailability,
                _ => Sub2ApiServicePresentationKind.Unavailable,
            };
            if (kind != Sub2ApiServicePresentationKind.Unavailable)
            {
                return new(kind, Availability: availability);
            }
        }

        if (card.Sub2ApiQuota is { } quota
            && Sub2ApiQuotaFormatting.PreferredWindow(quota) is { } legacy
            && IsValidLegacyAggregate(quota, legacy, now))
        {
            return new(
                Sub2ApiServicePresentationKind.LegacyAggregateQuota,
                LegacyQuota: legacy);
        }

        if (card.Sub2ApiUsage is { } usage && IsRenderableUsage(usage, now))
        {
            return new(Sub2ApiServicePresentationKind.Usage, Usage: usage);
        }

        if (card.Sub2ApiPool is { } pool && IsRenderablePool(pool, now))
        {
            return new(Sub2ApiServicePresentationKind.Pool, Pool: pool);
        }

        return new(Sub2ApiServicePresentationKind.Unavailable);
    }

    private static bool IsValidLegacyAggregate(
        Sub2ApiQuotaSummary quota,
        Sub2ApiQuotaWindow window,
        DateTimeOffset now) =>
        quota.Status is Sub2ApiQuotaStatus.Available or Sub2ApiQuotaStatus.Stale
        && Sub2ApiQuotaPolicy.HasFreshObservation(quota, now)
        && quota.AccountCount is { } total
        && total > 0
        && window.AccountCount > 0
        && window.AccountCount == total
        && double.IsFinite(window.RemainingPercent)
        && window.RemainingPercent is >= 0 and <= 100
        && double.IsFinite(window.RemainingAccountEquivalents)
        && window.RemainingAccountEquivalents is >= 0
            and <= 64
        && Math.Abs(
            window.RemainingAccountEquivalents / window.AccountCount * 100
            - window.RemainingPercent) <= RelationTolerance;

    private static bool IsRenderableUsage(Sub2ApiUsageSummary usage, DateTimeOffset now) =>
        usage.Status is (Sub2ApiUsageStatus.Available or Sub2ApiUsageStatus.Stale)
        && Sub2ApiUsagePolicy.HasFreshObservation(usage, now)
        && usage.TodayRequests is not null
        && usage.TodayInputTokens is not null
        && usage.TodayOutputTokens is not null
        && usage.TodayCacheCreationTokens is not null
        && usage.TodayCacheReadTokens is not null
        && usage.TodayTokens is not null
        && usage.TotalRequests is not null
        && usage.TotalInputTokens is not null
        && usage.TotalOutputTokens is not null
        && usage.TotalCacheCreationTokens is not null
        && usage.TotalCacheReadTokens is not null
        && usage.TotalTokens is not null;

    private static bool IsRenderablePool(Sub2ApiPoolAvailability pool, DateTimeOffset now) =>
        pool.Status is (Sub2ApiPoolStatus.Available or Sub2ApiPoolStatus.Stale)
        && Sub2ApiPoolPolicy.HasFreshObservation(pool, now)
        && pool.AvailableAccounts is { } available
        && pool.TotalAccounts is { } total
        && pool.RateLimitedAccounts is { } rateLimited
        && pool.ErrorAccounts is { } errors
        && pool.FreeConcurrency is { } free
        && pool.MaxConcurrency is { } maximum
        && available >= 0
        && available <= total
        && rateLimited >= 0
        && rateLimited <= total
        && errors >= 0
        && errors <= total
        && free >= 0
        && free <= maximum;
}

public sealed record Sub2ApiAccountAvailabilityFetchResult(
    Sub2ApiAccountAvailabilitySummary? Availability,
    ProviderHealthCode Code);

public sealed class Sub2ApiAccountAvailabilityService
{
    private const int MaxAccountCount = 64;
    private static readonly string[] ExpectedFields =
    [
        "schema_version",
        "source",
        "status",
        "coverage",
        "eligible_account_count",
        "readable_account_count",
        "aggregate_remaining_percent",
        "remaining_account_equivalents",
        "accounts",
        "observed_at",
    ];
    private static readonly string[] ExpectedAccountFields =
    [
        "slot",
        "state",
        "remaining_percent",
    ];
    private readonly HttpClient _httpClient;
    private readonly ISub2ApiPoolConnectionStore _connectionStore;

    public Sub2ApiAccountAvailabilityService(
        HttpClient httpClient,
        ISub2ApiPoolConnectionStore? connectionStore = null)
    {
        _httpClient = httpClient;
        _connectionStore = connectionStore ?? new Sub2ApiPoolConnectionStore();
    }

    public Task<Sub2ApiAccountAvailabilityFetchResult> FetchAsync(
        CancellationToken cancellationToken = default) =>
        FetchAsync(DateTimeOffset.UtcNow, cancellationToken);

    internal async Task<Sub2ApiAccountAvailabilityFetchResult> FetchAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Sub2ApiPoolConnection? connection;
        try
        {
            connection = _connectionStore.Read();
        }
        catch
        {
            return Failure(ProviderHealthCode.MissingCredentials);
        }

        if (connection is null) return Failure(ProviderHealthCode.MissingCredentials);
        if (!Sub2ApiPoolEndpoint.TryNormalize(connection.Endpoint, out var endpoint))
        {
            return Failure(ProviderHealthCode.EndpointBlocked);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{endpoint}/internal/v1/sub2api-account-availability");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(response.StatusCode == HttpStatusCode.RequestTimeout
                    ? ProviderHealthCode.Timeout
                    : ProviderHealthCode.HttpError);
            }

            var availability = Parse(
                await BoundedHttpBodyReader.ReadAsync(response, cancellationToken),
                now);
            var code = availability.Status switch
            {
                Sub2ApiQuotaStatus.Available => ProviderHealthCode.Current,
                Sub2ApiQuotaStatus.Stale => ProviderHealthCode.Cached,
                _ => ProviderHealthCode.Unavailable,
            };
            return new Sub2ApiAccountAvailabilityFetchResult(availability, code);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(ProviderHealthCode.Timeout);
        }
        catch (HttpRequestException)
        {
            return Failure(ProviderHealthCode.Unavailable);
        }
        catch (InvalidDataException)
        {
            return Failure(ProviderHealthCode.HttpError);
        }
        catch (JsonException)
        {
            return Failure(ProviderHealthCode.HttpError);
        }
    }

    private static Sub2ApiAccountAvailabilitySummary Parse(byte[] body, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!HasExactFields(root, ExpectedFields))
        {
            throw new JsonException("Unexpected Sub2API account availability schema.");
        }
        if (RequiredInt(root, "schema_version") != 1
            || RequiredString(root, "source") != "zgs-sub2api")
        {
            throw new JsonException("Unexpected Sub2API account availability identity.");
        }

        var status = RequiredString(root, "status") switch
        {
            "available" => Sub2ApiQuotaStatus.Available,
            "stale" => Sub2ApiQuotaStatus.Stale,
            "unavailable" => Sub2ApiQuotaStatus.Unavailable,
            _ => throw new JsonException("Unknown Sub2API account availability status."),
        };
        var coverage = RequiredString(root, "coverage") switch
        {
            "complete" => Sub2ApiAccountAvailabilityCoverage.Complete,
            "partial" => Sub2ApiAccountAvailabilityCoverage.Partial,
            "none" => Sub2ApiAccountAvailabilityCoverage.None,
            _ => throw new JsonException("Unknown Sub2API account availability coverage."),
        };
        var availability = new Sub2ApiAccountAvailabilitySummary(
            status,
            coverage,
            OptionalNonNegativeInt(root, "eligible_account_count"),
            OptionalNonNegativeInt(root, "readable_account_count"),
            OptionalPercent(root, "aggregate_remaining_percent"),
            OptionalNonNegativeDouble(root, "remaining_account_equivalents"),
            OptionalAccounts(root),
            OptionalUtc(root, "observed_at"));
        if (!Sub2ApiAccountAvailabilityFormatting.IsSchemaValid(availability))
        {
            throw new JsonException("Sub2API account availability is invalid.");
        }
        if (availability.ObservedAt is { } observedAt
            && observedAt > now.Add(Sub2ApiAccountAvailabilityPolicy.FutureTolerance))
        {
            throw new JsonException("Sub2API account availability timestamp is in the future.");
        }

        return Sub2ApiAccountAvailabilityPolicy.EffectiveAvailability(availability, now);
    }

    private static IReadOnlyList<Sub2ApiAccountAvailabilityEntry>? OptionalAccounts(JsonElement root)
    {
        if (!root.TryGetProperty("accounts", out var value))
        {
            throw new JsonException("Missing accounts.");
        }
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() > MaxAccountCount)
        {
            throw new JsonException("Invalid accounts.");
        }

        var accounts = new List<Sub2ApiAccountAvailabilityEntry>(value.GetArrayLength());
        foreach (var account in value.EnumerateArray())
        {
            if (!HasExactFields(account, ExpectedAccountFields))
            {
                throw new JsonException("Unexpected account availability entry schema.");
            }

            var state = RequiredString(account, "state") switch
            {
                "available" => Sub2ApiAccountAvailabilityState.Available,
                "unavailable" => Sub2ApiAccountAvailabilityState.Unavailable,
                _ => throw new JsonException("Unknown account availability state."),
            };
            var remainingPercent = OptionalPercent(account, "remaining_percent");
            if ((state == Sub2ApiAccountAvailabilityState.Available) == (remainingPercent is null))
            {
                throw new JsonException("Account availability state and percentage disagree.");
            }

            accounts.Add(new Sub2ApiAccountAvailabilityEntry(
                RequiredPositiveInt(account, "slot"),
                state,
                remainingPercent));
        }
        return accounts;
    }

    private static bool HasExactFields(JsonElement value, IReadOnlyList<string> expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var properties = value.EnumerateObject().ToArray();
        return properties.Length == expected.Count
            && properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == expected.Count
            && properties.All(property => expected.Contains(property.Name, StringComparer.Ordinal));
    }

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : throw new JsonException($"Missing integer {name}.");

    private static int RequiredPositiveInt(JsonElement root, string name)
    {
        var value = RequiredInt(root, name);
        return value > 0
            ? value
            : throw new JsonException($"Invalid integer {name}.");
    }

    private static int? OptionalNonNegativeInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) throw new JsonException($"Missing integer {name}.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed >= 0
                ? parsed
                : throw new JsonException($"Invalid integer {name}.");
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new JsonException($"Missing string {name}.");

    private static double? OptionalPercent(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) throw new JsonException($"Missing percentage {name}.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
            && double.IsFinite(parsed)
            && parsed is >= 0 and <= 100
                ? parsed
                : throw new JsonException($"Invalid percentage {name}.");
    }

    private static double? OptionalNonNegativeDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) throw new JsonException($"Missing number {name}.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
            && double.IsFinite(parsed)
            && parsed >= 0
                ? parsed
                : throw new JsonException($"Invalid number {name}.");
    }

    private static DateTimeOffset? OptionalUtc(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)) throw new JsonException($"Missing timestamp {name}.");
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new JsonException($"Invalid timestamp {name}.");
        }
        var value = element.GetString()!;
        if ((!value.EndsWith('Z') && !value.EndsWith("+00:00", StringComparison.OrdinalIgnoreCase))
            || !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            throw new JsonException($"Invalid UTC timestamp {name}.");
        }
        return parsed.ToUniversalTime();
    }

    private static Sub2ApiAccountAvailabilityFetchResult Failure(ProviderHealthCode code) => new(null, code);
}
