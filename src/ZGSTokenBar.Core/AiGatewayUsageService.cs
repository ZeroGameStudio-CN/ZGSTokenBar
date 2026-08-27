using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record AiGatewayUsageFetchResult(
    AiGatewayUsageSummary? Summary,
    ProviderHealthCode Code);

public sealed class AiGatewayUsageService
{
    private static readonly string[] ExpectedFields =
    [
        "schema_version",
        "source",
        "provider",
        "currency",
        "status",
        "day_boundary",
        "observed_at",
        "today",
        "total",
    ];
    private static readonly string[] ExpectedPeriodFields =
    [
        "request_count",
        "prompt_tokens",
        "completion_tokens",
        "total_tokens",
        "cache_hit_tokens",
        "cache_miss_tokens",
        "cache_unknown_tokens",
        "cache_hit_rate_percent",
        "estimated_cost_cny",
    ];
    private readonly HttpClient _httpClient;
    private readonly IAiGatewayConnectionStore _connectionStore;

    public AiGatewayUsageService(
        HttpClient httpClient,
        IAiGatewayConnectionStore? connectionStore = null)
    {
        _httpClient = httpClient;
        _connectionStore = connectionStore ?? new AiGatewayConnectionStore();
    }

    public Task<AiGatewayUsageFetchResult> FetchAsync(
        CancellationToken cancellationToken = default) =>
        FetchAsync(DateTimeOffset.UtcNow, cancellationToken);

    internal async Task<AiGatewayUsageFetchResult> FetchAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        AiGatewayConnection? connection;
        try
        {
            connection = _connectionStore.Read();
        }
        catch
        {
            return Failure(ProviderHealthCode.MissingCredentials);
        }

        if (connection is null) return Failure(ProviderHealthCode.MissingCredentials);
        if (!AiGatewayEndpoint.TryNormalize(connection.Endpoint, out var endpoint))
        {
            return Failure(ProviderHealthCode.EndpointBlocked);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{endpoint}/internal/v1/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Failure(ProviderHealthCode.HttpError);
            }
            if (!response.IsSuccessStatusCode)
            {
                return Failure(ProviderHealthCode.HttpError);
            }

            var body = await BoundedHttpBodyReader.ReadAsync(response, cancellationToken);
            var summary = Parse(body, now);
            return new AiGatewayUsageFetchResult(summary, ProviderHealthCode.Current);
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

    private static AiGatewayUsageSummary Parse(byte[] body, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != ExpectedFields.Length
            || root.EnumerateObject().Any(property =>
                !ExpectedFields.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw new JsonException("Unexpected AI Gateway usage schema.");
        }

        if (RequiredInt(root, "schema_version") != 1
            || RequiredString(root, "source") != "zgs-ai-gateway"
            || RequiredString(root, "provider") != "deepseek"
            || RequiredString(root, "currency") != "CNY"
            || RequiredString(root, "day_boundary") != "UTC")
        {
            throw new JsonException("Unexpected AI Gateway usage identity.");
        }

        var status = RequiredString(root, "status") switch
        {
            "available" => AiGatewayBalanceStatus.Available,
            "unavailable" => AiGatewayBalanceStatus.Unavailable,
            "stale" => AiGatewayBalanceStatus.Stale,
            "unknown" => AiGatewayBalanceStatus.Unknown,
            _ => throw new JsonException("Unknown AI Gateway usage status."),
        };
        var observedAt = RequiredUtc(root, "observed_at");
        if (observedAt > now.AddMinutes(1))
        {
            throw new JsonException("AI Gateway usage timestamp is in the future.");
        }

        var summary = new AiGatewayUsageSummary(
            "CNY",
            status,
            ParsePeriod(root, "today"),
            ParsePeriod(root, "total"),
            observedAt,
            "UTC");
        return summary with
        {
            Status = observedAt < now.AddMinutes(-15)
                ? AiGatewayBalanceStatus.Stale
                : summary.Status,
        };
    }

    private static AiGatewayUsagePeriod ParsePeriod(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Count() != ExpectedPeriodFields.Length
            || value.EnumerateObject().Any(property =>
                !ExpectedPeriodFields.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw new JsonException($"Invalid AI Gateway usage period {name}.");
        }

        var requestCount = RequiredLong(value, "request_count");
        var promptTokens = RequiredLong(value, "prompt_tokens");
        var completionTokens = RequiredLong(value, "completion_tokens");
        var totalTokens = RequiredLong(value, "total_tokens");
        var cacheHitTokens = RequiredLong(value, "cache_hit_tokens");
        var cacheMissTokens = RequiredLong(value, "cache_miss_tokens");
        var cacheUnknownTokens = RequiredLong(value, "cache_unknown_tokens");
        if (cacheHitTokens > promptTokens
            || cacheMissTokens > promptTokens - cacheHitTokens
            || cacheUnknownTokens != promptTokens - cacheHitTokens - cacheMissTokens
            || promptTokens > totalTokens
            || completionTokens != totalTokens - promptTokens)
        {
            throw new JsonException($"Inconsistent AI Gateway usage period {name}.");
        }

        var rate = OptionalDecimal(value, "cache_hit_rate_percent");
        var denominator = cacheHitTokens + cacheMissTokens;
        if ((denominator == 0) != (rate is null)
            || rate is < 0 or > 100)
        {
            throw new JsonException($"Invalid AI Gateway cache rate {name}.");
        }
        return new AiGatewayUsagePeriod(
            requestCount,
            promptTokens,
            completionTokens,
            totalTokens,
            cacheHitTokens,
            cacheMissTokens,
            cacheUnknownTokens,
            rate,
            RequiredDecimal(value, "estimated_cost_cny"));
    }

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : throw new JsonException($"Missing integer {name}.");

    private static long RequiredLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
        && parsed >= 0
            ? parsed
            : throw new JsonException($"Missing count {name}.");

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new JsonException($"Missing string {name}.");

    private static decimal RequiredDecimal(JsonElement root, string name) =>
        OptionalDecimal(root, name) is { } parsed
            ? parsed
            : throw new JsonException($"Missing decimal {name}.");

    private static decimal? OptionalDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            throw new JsonException($"Missing decimal {name}.");
        }
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String
            || !decimal.TryParse(
                value.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 0)
        {
            throw new JsonException($"Invalid decimal {name}.");
        }
        return parsed;
    }

    private static DateTimeOffset RequiredUtc(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            throw new JsonException($"Invalid timestamp {name}.");
        }
        return parsed.ToUniversalTime();
    }

    private static AiGatewayUsageFetchResult Failure(ProviderHealthCode code) =>
        new(null, code);
}
