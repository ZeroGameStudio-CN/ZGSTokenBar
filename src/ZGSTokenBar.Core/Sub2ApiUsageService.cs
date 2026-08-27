using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public static class Sub2ApiUsagePolicy
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(1);

    public static Sub2ApiUsageStatus EffectiveStatus(Sub2ApiUsageSummary usage, DateTimeOffset now)
    {
        if (usage.Status is Sub2ApiUsageStatus.Unavailable or Sub2ApiUsageStatus.Unknown)
        {
            return usage.Status;
        }
        return HasFreshObservation(usage, now) ? usage.Status : Sub2ApiUsageStatus.Stale;
    }

    public static bool HasFreshObservation(Sub2ApiUsageSummary usage, DateTimeOffset now) =>
        usage.ObservedAt is { } observedAt
        && observedAt <= now.Add(FutureTolerance)
        && now - observedAt <= StaleAfter;
}

public sealed record Sub2ApiUsageFetchResult(
    Sub2ApiUsageSummary? Usage,
    ProviderHealthCode Code);

public sealed class Sub2ApiUsageService
{
    private static readonly string[] ExpectedFields =
    [
        "schema_version",
        "source",
        "status",
        "today_requests",
        "today_input_tokens",
        "today_output_tokens",
        "today_cache_creation_tokens",
        "today_cache_read_tokens",
        "today_tokens",
        "total_requests",
        "total_input_tokens",
        "total_output_tokens",
        "total_cache_creation_tokens",
        "total_cache_read_tokens",
        "total_tokens",
        "observed_at",
    ];
    private readonly HttpClient _httpClient;
    private readonly ISub2ApiPoolConnectionStore _connectionStore;

    public Sub2ApiUsageService(
        HttpClient httpClient,
        ISub2ApiPoolConnectionStore? connectionStore = null)
    {
        _httpClient = httpClient;
        _connectionStore = connectionStore ?? new Sub2ApiPoolConnectionStore();
    }

    public Task<Sub2ApiUsageFetchResult> FetchAsync(CancellationToken cancellationToken = default) =>
        FetchAsync(DateTimeOffset.UtcNow, cancellationToken);

    internal async Task<Sub2ApiUsageFetchResult> FetchAsync(
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
            $"{endpoint}/internal/v1/sub2api-usage");
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

            var usage = Parse(
                await BoundedHttpBodyReader.ReadAsync(response, cancellationToken),
                now);
            var code = usage.Status switch
            {
                Sub2ApiUsageStatus.Available => ProviderHealthCode.Current,
                Sub2ApiUsageStatus.Stale => ProviderHealthCode.Cached,
                _ => ProviderHealthCode.Unavailable,
            };
            return new Sub2ApiUsageFetchResult(usage, code);
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

    private static Sub2ApiUsageSummary Parse(byte[] body, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != ExpectedFields.Length
            || root.EnumerateObject().Any(property => !ExpectedFields.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw new JsonException("Unexpected Sub2API usage schema.");
        }

        if (RequiredInt(root, "schema_version") != 1
            || RequiredString(root, "source") != "zgs-sub2api")
        {
            throw new JsonException("Unexpected Sub2API usage identity.");
        }

        var status = RequiredString(root, "status") switch
        {
            "available" => Sub2ApiUsageStatus.Available,
            "unavailable" => Sub2ApiUsageStatus.Unavailable,
            "stale" => Sub2ApiUsageStatus.Stale,
            "unknown" => Sub2ApiUsageStatus.Unknown,
            _ => throw new JsonException("Unknown Sub2API usage status."),
        };
        var usage = new Sub2ApiUsageSummary(
            status,
            OptionalNonNegativeLong(root, "today_requests"),
            OptionalNonNegativeLong(root, "today_input_tokens"),
            OptionalNonNegativeLong(root, "today_output_tokens"),
            OptionalNonNegativeLong(root, "today_cache_creation_tokens"),
            OptionalNonNegativeLong(root, "today_cache_read_tokens"),
            OptionalNonNegativeLong(root, "today_tokens"),
            OptionalNonNegativeLong(root, "total_requests"),
            OptionalNonNegativeLong(root, "total_input_tokens"),
            OptionalNonNegativeLong(root, "total_output_tokens"),
            OptionalNonNegativeLong(root, "total_cache_creation_tokens"),
            OptionalNonNegativeLong(root, "total_cache_read_tokens"),
            OptionalNonNegativeLong(root, "total_tokens"),
            OptionalUtc(root, "observed_at"));
        Validate(usage);
        return usage with { Status = Sub2ApiUsagePolicy.EffectiveStatus(usage, now) };
    }

    private static void Validate(Sub2ApiUsageSummary usage)
    {
        if (usage.Status is Sub2ApiUsageStatus.Available or Sub2ApiUsageStatus.Stale)
        {
            if (usage.TodayRequests is null
                || usage.TodayInputTokens is null
                || usage.TodayOutputTokens is null
                || usage.TodayCacheCreationTokens is null
                || usage.TodayCacheReadTokens is null
                || usage.TodayTokens is null
                || usage.TotalRequests is null
                || usage.TotalInputTokens is null
                || usage.TotalOutputTokens is null
                || usage.TotalCacheCreationTokens is null
                || usage.TotalCacheReadTokens is null
                || usage.TotalTokens is null
                || usage.ObservedAt is null)
            {
                throw new JsonException("Sub2API usage counters are invalid.");
            }
            return;
        }

        if (usage.TodayRequests is not null
            || usage.TodayInputTokens is not null
            || usage.TodayOutputTokens is not null
            || usage.TodayCacheCreationTokens is not null
            || usage.TodayCacheReadTokens is not null
            || usage.TodayTokens is not null
            || usage.TotalRequests is not null
            || usage.TotalInputTokens is not null
            || usage.TotalOutputTokens is not null
            || usage.TotalCacheCreationTokens is not null
            || usage.TotalCacheReadTokens is not null
            || usage.TotalTokens is not null
            || usage.ObservedAt is not null)
        {
            throw new JsonException("Unavailable Sub2API usage contains data.");
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

    private static long? OptionalNonNegativeLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) throw new JsonException($"Missing count {name}.");
        if (value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
            && parsed >= 0
                ? parsed
                : throw new JsonException($"Invalid count {name}.");
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

    private static Sub2ApiUsageFetchResult Failure(ProviderHealthCode code) => new(null, code);
}
