using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public static class Sub2ApiPoolPolicy
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(1);

    public static Sub2ApiPoolStatus EffectiveStatus(Sub2ApiPoolAvailability pool, DateTimeOffset now)
    {
        if (pool.Status is Sub2ApiPoolStatus.Unavailable or Sub2ApiPoolStatus.Unknown)
        {
            return pool.Status;
        }
        return HasFreshObservation(pool, now) ? pool.Status : Sub2ApiPoolStatus.Stale;
    }

    public static bool HasFreshObservation(Sub2ApiPoolAvailability pool, DateTimeOffset now) =>
        pool.ObservedAt is { } observedAt
        && observedAt <= now.Add(FutureTolerance)
        && now - observedAt <= StaleAfter;
}

public static class Sub2ApiPoolFormatting
{
    public static string AccountPair(Sub2ApiPoolAvailability? pool) => Pair(
        pool?.AvailableAccounts,
        pool?.TotalAccounts);

    public static string ConcurrencyPair(Sub2ApiPoolAvailability? pool) => Pair(
        pool?.FreeConcurrency,
        pool?.MaxConcurrency);

    private static string Pair(int? available, int? total) => available is { } left
        && total is { } right
        && left >= 0
        && right >= left
            ? $"{left}/{right}"
            : "-";
}

public sealed record Sub2ApiPoolFetchResult(
    Sub2ApiPoolAvailability? Pool,
    ProviderHealthCode Code);

public sealed class Sub2ApiPoolService
{
    private static readonly string[] ExpectedFields =
    [
        "schema_version",
        "source",
        "status",
        "available_accounts",
        "total_accounts",
        "rate_limited_accounts",
        "error_accounts",
        "free_concurrency",
        "max_concurrency",
        "observed_at",
    ];
    private readonly HttpClient _httpClient;
    private readonly ISub2ApiPoolConnectionStore _connectionStore;

    public Sub2ApiPoolService(
        HttpClient httpClient,
        ISub2ApiPoolConnectionStore? connectionStore = null)
    {
        _httpClient = httpClient;
        _connectionStore = connectionStore ?? new Sub2ApiPoolConnectionStore();
    }

    public Task<Sub2ApiPoolFetchResult> FetchAsync(CancellationToken cancellationToken = default) =>
        FetchAsync(DateTimeOffset.UtcNow, cancellationToken);

    internal async Task<Sub2ApiPoolFetchResult> FetchAsync(
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
            $"{endpoint}/internal/v1/sub2api-pool");
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

            var pool = Parse(
                await BoundedHttpBodyReader.ReadAsync(response, cancellationToken),
                now);
            var code = pool.Status switch
            {
                Sub2ApiPoolStatus.Available => ProviderHealthCode.Current,
                Sub2ApiPoolStatus.Stale => ProviderHealthCode.Cached,
                _ => ProviderHealthCode.Unavailable,
            };
            return new Sub2ApiPoolFetchResult(pool, code);
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

    private static Sub2ApiPoolAvailability Parse(byte[] body, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != ExpectedFields.Length
            || root.EnumerateObject().Any(property => !ExpectedFields.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw new JsonException("Unexpected Sub2API pool schema.");
        }

        if (RequiredInt(root, "schema_version") != 1
            || RequiredString(root, "source") != "zgs-sub2api")
        {
            throw new JsonException("Unexpected Sub2API pool identity.");
        }

        var status = RequiredString(root, "status") switch
        {
            "available" => Sub2ApiPoolStatus.Available,
            "unavailable" => Sub2ApiPoolStatus.Unavailable,
            "stale" => Sub2ApiPoolStatus.Stale,
            "unknown" => Sub2ApiPoolStatus.Unknown,
            _ => throw new JsonException("Unknown Sub2API pool status."),
        };
        var pool = new Sub2ApiPoolAvailability(
            status,
            OptionalNonNegativeInt(root, "available_accounts"),
            OptionalNonNegativeInt(root, "total_accounts"),
            OptionalNonNegativeInt(root, "rate_limited_accounts"),
            OptionalNonNegativeInt(root, "error_accounts"),
            OptionalNonNegativeInt(root, "free_concurrency"),
            OptionalNonNegativeInt(root, "max_concurrency"),
            OptionalUtc(root, "observed_at"));
        Validate(pool);
        return pool with { Status = Sub2ApiPoolPolicy.EffectiveStatus(pool, now) };
    }

    private static void Validate(Sub2ApiPoolAvailability pool)
    {
        if (pool.Status is Sub2ApiPoolStatus.Available or Sub2ApiPoolStatus.Stale)
        {
            if (pool.AvailableAccounts is not { } available
                || pool.TotalAccounts is not { } total
                || pool.RateLimitedAccounts is not { } rateLimited
                || pool.ErrorAccounts is not { } errors
                || pool.FreeConcurrency is not { } free
                || pool.MaxConcurrency is not { } maximum
                || pool.ObservedAt is null
                || available > total
                || rateLimited > total
                || errors > total
                || free > maximum)
            {
                throw new JsonException("Sub2API pool counts are invalid.");
            }
            return;
        }

        if (pool.AvailableAccounts is not null
            || pool.TotalAccounts is not null
            || pool.RateLimitedAccounts is not null
            || pool.ErrorAccounts is not null
            || pool.FreeConcurrency is not null
            || pool.MaxConcurrency is not null
            || pool.ObservedAt is not null)
        {
            throw new JsonException("Unavailable Sub2API pool contains data.");
        }
    }

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : throw new JsonException($"Missing integer {name}.");

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new JsonException($"Missing string {name}.");

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

    private static Sub2ApiPoolFetchResult Failure(ProviderHealthCode code) => new(null, code);
}
