using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public static class AiGatewayBalancePolicy
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(1);

    public static AiGatewayBalanceStatus EffectiveStatus(AiGatewayBalance balance, DateTimeOffset now)
    {
        if (balance.Status == AiGatewayBalanceStatus.Unavailable
            || (balance.Status == AiGatewayBalanceStatus.Unknown && balance.ObservedAt is null))
        {
            return balance.Status;
        }
        if (balance.ObservedAt is not { } observedAt
            || observedAt > now.Add(FutureTolerance)
            || now - observedAt > StaleAfter)
        {
            return AiGatewayBalanceStatus.Stale;
        }
        return balance.Status;
    }
}

public static class AiGatewayBalanceFormatting
{
    public static string Amount(decimal? amount) => amount is { } value
        ? $"¥{value.ToString("0.00", CultureInfo.InvariantCulture)}"
        : "—";

    public static string CompactAmount(decimal? amount)
    {
        if (amount is not { } value) return "—";
        if (value < 10m) return $"¥{value.ToString("0.#", CultureInfo.InvariantCulture)}";
        if (value < 1_000m) return $"¥{value.ToString("0", CultureInfo.InvariantCulture)}";
        if (value < 1_000_000m)
        {
            return $"¥{(value / 1_000m).ToString("0.#", CultureInfo.InvariantCulture)}K";
        }
        return $"¥{(value / 1_000_000m).ToString("0.#", CultureInfo.InvariantCulture)}M";
    }

    public static string Status(AiGatewayBalanceStatus status) => status switch
    {
        AiGatewayBalanceStatus.Available => "available",
        AiGatewayBalanceStatus.Unavailable => "unavailable",
        AiGatewayBalanceStatus.Stale => "stale",
        _ => "unknown",
    };
}

public sealed class AiGatewayBalanceService
{
    private static readonly string[] ExpectedFields =
    [
        "schema_version",
        "source",
        "provider",
        "currency",
        "status",
        "total_balance",
        "topped_up_balance",
        "granted_balance",
        "observed_at",
    ];
    private readonly HttpClient _httpClient;
    private readonly IAiGatewayConnectionStore _connectionStore;

    public AiGatewayBalanceService(
        HttpClient httpClient,
        IAiGatewayConnectionStore? connectionStore = null)
    {
        _httpClient = httpClient;
        _connectionStore = connectionStore ?? new AiGatewayConnectionStore();
    }

    public static QuotaCard UnavailableCard(DateTimeOffset? capturedAt = null) => new(
        "ai-gateway.balance",
        ProviderKind.AiGateway,
        "AI 网关",
        null,
        "#8b5cf6",
        true,
        [new QuotaWindow("AI", null, null, TimeSpan.Zero)])
    {
        CapturedAt = capturedAt,
        IsService = true,
        Balance = new AiGatewayBalance(
            AiGatewayBalanceStatus.Unavailable,
            "CNY",
            null,
            null,
            null,
            null),
    };

    public Task<ProviderResult> FetchAsync(CancellationToken cancellationToken = default) =>
        FetchAsync(DateTimeOffset.UtcNow, cancellationToken);

    internal async Task<ProviderResult> FetchAsync(
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
            return Failure("AI Gateway observer credential is unavailable.", ProviderHealthCode.MissingCredentials);
        }

        if (connection is null)
        {
            return Failure("AI Gateway observer credential is not configured.", ProviderHealthCode.MissingCredentials);
        }

        if (!AiGatewayEndpoint.TryNormalize(connection.Endpoint, out var endpoint))
        {
            return Failure("AI Gateway endpoint is not allowed.", ProviderHealthCode.EndpointBlocked);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{endpoint}/internal/v1/balance");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Failure(
                    "AI Gateway observer credential was rejected.",
                    ProviderHealthCode.HttpError,
                    (int)response.StatusCode);
            }
            if (!response.IsSuccessStatusCode)
            {
                return Failure(
                    "AI Gateway balance endpoint returned an error.",
                    ProviderHealthCode.HttpError,
                    (int)response.StatusCode);
            }

            var body = await BoundedHttpBodyReader.ReadAsync(response, cancellationToken);
            var balance = Parse(body, now);
            return Success(balance, now);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("AI Gateway balance request timed out.", ProviderHealthCode.Timeout);
        }
        catch (HttpRequestException)
        {
            return Failure("AI Gateway balance endpoint is unavailable.", ProviderHealthCode.Unavailable);
        }
        catch (InvalidDataException)
        {
            return Failure("AI Gateway balance response was invalid.", ProviderHealthCode.HttpError);
        }
        catch (JsonException)
        {
            return Failure("AI Gateway balance response was invalid.", ProviderHealthCode.HttpError);
        }
    }

    private static AiGatewayBalance Parse(byte[] body, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != ExpectedFields.Length
            || root.EnumerateObject().Any(property => !ExpectedFields.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw new JsonException("Unexpected AI Gateway balance schema.");
        }

        var schema = RequiredInt(root, "schema_version");
        var source = RequiredString(root, "source");
        var provider = RequiredString(root, "provider");
        var currency = RequiredString(root, "currency");
        var status = RequiredString(root, "status");
        if (schema != 1 || source != "zgs-ai-gateway" || provider != "deepseek" || currency != "CNY")
        {
            throw new JsonException("Unexpected AI Gateway balance identity.");
        }

        var parsedStatus = status switch
        {
            "available" => AiGatewayBalanceStatus.Available,
            "unavailable" => AiGatewayBalanceStatus.Unavailable,
            "stale" => AiGatewayBalanceStatus.Stale,
            "unknown" => AiGatewayBalanceStatus.Unknown,
            _ => throw new JsonException("Unknown AI Gateway balance status."),
        };
        var observedAt = OptionalUtc(root, "observed_at");
        if (parsedStatus is AiGatewayBalanceStatus.Available or AiGatewayBalanceStatus.Stale
            && observedAt is null)
        {
            throw new JsonException("A successful balance requires an observation timestamp.");
        }
        var balance = new AiGatewayBalance(
            parsedStatus,
            currency,
            OptionalDecimal(root, "total_balance"),
            OptionalDecimal(root, "topped_up_balance"),
            OptionalDecimal(root, "granted_balance"),
            observedAt);
        return balance with { Status = AiGatewayBalancePolicy.EffectiveStatus(balance, now) };
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

    private static decimal? OptionalDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) throw new JsonException($"Missing decimal {name}.");
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

    private static ProviderResult Success(AiGatewayBalance balance, DateTimeOffset now)
    {
        var card = new QuotaCard(
            "ai-gateway.balance",
            ProviderKind.AiGateway,
            "AI 网关",
            null,
            "#8b5cf6",
            true,
            [new QuotaWindow("AI", null, null, TimeSpan.Zero)])
        {
            CapturedAt = now,
            IsService = true,
            Balance = balance,
        };
        var code = balance.Status switch
        {
            AiGatewayBalanceStatus.Available => ProviderHealthCode.Current,
            AiGatewayBalanceStatus.Stale => ProviderHealthCode.Cached,
            _ => ProviderHealthCode.Unavailable,
        };
        return new ProviderResult(
            ProviderKind.AiGateway,
            [card],
            new ProviderHealth(
                ProviderKind.AiGateway,
                balance.Status is AiGatewayBalanceStatus.Available or AiGatewayBalanceStatus.Stale,
                "AI Gateway balance was received.",
                code));
    }

    private static ProviderResult Failure(string detail, ProviderHealthCode code, int? status = null) =>
        new(
            ProviderKind.AiGateway,
            [],
            new ProviderHealth(ProviderKind.AiGateway, false, detail, code, status));
}
