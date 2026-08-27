using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public static class Sub2ApiQuotaPolicy
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(1);

    public static Sub2ApiQuotaStatus EffectiveStatus(Sub2ApiQuotaSummary quota, DateTimeOffset now)
    {
        if (quota.Status is Sub2ApiQuotaStatus.Unavailable or Sub2ApiQuotaStatus.Unknown)
        {
            return quota.Status;
        }
        return HasFreshObservation(quota, now) ? quota.Status : Sub2ApiQuotaStatus.Stale;
    }

    public static bool HasFreshObservation(Sub2ApiQuotaSummary quota, DateTimeOffset now) =>
        quota.ObservedAt is { } observedAt
        && observedAt <= now.Add(FutureTolerance)
        && now - observedAt <= StaleAfter;
}

public static class Sub2ApiQuotaFormatting
{
    public static Sub2ApiQuotaWindow? PreferredWindow(Sub2ApiQuotaSummary? quota)
    {
        if (quota is null || quota.Status is Sub2ApiQuotaStatus.Unavailable or Sub2ApiQuotaStatus.Unknown)
        {
            return null;
        }
        return Window(
                   "7d",
                   quota.SevenDayAccountCount,
                   quota.SevenDayRemainingPercent,
                   quota.SevenDayRemainingAccountEquivalents)
               ?? Window(
                   "5h",
                   quota.FiveHourAccountCount,
                   quota.FiveHourRemainingPercent,
                   quota.FiveHourRemainingAccountEquivalents);
    }

    public static Sub2ApiQuotaWindow? OtherWindow(Sub2ApiQuotaSummary? quota)
    {
        var preferred = PreferredWindow(quota);
        if (quota is null || preferred is null) return null;
        return string.Equals(preferred.Label, "7d", StringComparison.Ordinal)
            ? Window(
                "5h",
                quota.FiveHourAccountCount,
                quota.FiveHourRemainingPercent,
                quota.FiveHourRemainingAccountEquivalents)
            : Window(
                "7d",
                quota.SevenDayAccountCount,
                quota.SevenDayRemainingPercent,
                quota.SevenDayRemainingAccountEquivalents);
    }

    private static Sub2ApiQuotaWindow? Window(
        string label,
        int? accountCount,
        double? remainingPercent,
        double? remainingAccountEquivalents) =>
        accountCount is { } count
        && remainingPercent is { } percent
        && remainingAccountEquivalents is { } equivalents
            ? new Sub2ApiQuotaWindow(label, count, percent, equivalents)
            : null;
}

public sealed record Sub2ApiQuotaFetchResult(
    Sub2ApiQuotaSummary? Quota,
    ProviderHealthCode Code);

public sealed class Sub2ApiQuotaService
{
    private const double RelationTolerance = 0.0001;
    private static readonly string[] ExpectedFields =
    [
        "schema_version",
        "source",
        "status",
        "account_count",
        "five_hour_account_count",
        "five_hour_remaining_percent",
        "five_hour_remaining_account_equivalents",
        "seven_day_account_count",
        "seven_day_remaining_percent",
        "seven_day_remaining_account_equivalents",
        "observed_at",
    ];
    private readonly HttpClient _httpClient;
    private readonly ISub2ApiPoolConnectionStore _connectionStore;

    public Sub2ApiQuotaService(
        HttpClient httpClient,
        ISub2ApiPoolConnectionStore? connectionStore = null)
    {
        _httpClient = httpClient;
        _connectionStore = connectionStore ?? new Sub2ApiPoolConnectionStore();
    }

    public Task<Sub2ApiQuotaFetchResult> FetchAsync(CancellationToken cancellationToken = default) =>
        FetchAsync(DateTimeOffset.UtcNow, cancellationToken);

    internal async Task<Sub2ApiQuotaFetchResult> FetchAsync(
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
            $"{endpoint}/internal/v1/sub2api-quota");
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

            var quota = Parse(
                await BoundedHttpBodyReader.ReadAsync(response, cancellationToken),
                now);
            var code = quota.Status switch
            {
                Sub2ApiQuotaStatus.Available => ProviderHealthCode.Current,
                Sub2ApiQuotaStatus.Stale => ProviderHealthCode.Cached,
                _ => ProviderHealthCode.Unavailable,
            };
            return new Sub2ApiQuotaFetchResult(quota, code);
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

    private static Sub2ApiQuotaSummary Parse(byte[] body, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != ExpectedFields.Length
            || root.EnumerateObject().Any(property => !ExpectedFields.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw new JsonException("Unexpected Sub2API quota schema.");
        }

        if (RequiredInt(root, "schema_version") != 1
            || RequiredString(root, "source") != "zgs-sub2api")
        {
            throw new JsonException("Unexpected Sub2API quota identity.");
        }

        var status = RequiredString(root, "status") switch
        {
            "available" => Sub2ApiQuotaStatus.Available,
            "unavailable" => Sub2ApiQuotaStatus.Unavailable,
            "stale" => Sub2ApiQuotaStatus.Stale,
            "unknown" => Sub2ApiQuotaStatus.Unknown,
            _ => throw new JsonException("Unknown Sub2API quota status."),
        };
        var quota = new Sub2ApiQuotaSummary(
            status,
            OptionalNonNegativeInt(root, "account_count"),
            OptionalNonNegativeInt(root, "five_hour_account_count"),
            OptionalPercent(root, "five_hour_remaining_percent"),
            OptionalNonNegativeDouble(root, "five_hour_remaining_account_equivalents"),
            OptionalNonNegativeInt(root, "seven_day_account_count"),
            OptionalPercent(root, "seven_day_remaining_percent"),
            OptionalNonNegativeDouble(root, "seven_day_remaining_account_equivalents"),
            OptionalUtc(root, "observed_at"));
        Validate(quota);
        return quota with { Status = Sub2ApiQuotaPolicy.EffectiveStatus(quota, now) };
    }

    private static void Validate(Sub2ApiQuotaSummary quota)
    {
        if (quota.Status is Sub2ApiQuotaStatus.Available or Sub2ApiQuotaStatus.Stale)
        {
            if (quota.AccountCount is not { } accountCount || accountCount <= 0 || quota.ObservedAt is null)
            {
                throw new JsonException("Sub2API quota summary is invalid.");
            }
            var hasFiveHour = ValidateWindow(
                accountCount,
                quota.FiveHourAccountCount,
                quota.FiveHourRemainingPercent,
                quota.FiveHourRemainingAccountEquivalents,
                "five-hour");
            var hasSevenDay = ValidateWindow(
                accountCount,
                quota.SevenDayAccountCount,
                quota.SevenDayRemainingPercent,
                quota.SevenDayRemainingAccountEquivalents,
                "seven-day");
            if (!hasFiveHour && !hasSevenDay)
            {
                throw new JsonException("Sub2API quota contains no window.");
            }
            return;
        }

        if (quota.AccountCount is not null
            || quota.FiveHourAccountCount is not null
            || quota.FiveHourRemainingPercent is not null
            || quota.FiveHourRemainingAccountEquivalents is not null
            || quota.SevenDayAccountCount is not null
            || quota.SevenDayRemainingPercent is not null
            || quota.SevenDayRemainingAccountEquivalents is not null
            || quota.ObservedAt is not null)
        {
            throw new JsonException("Unavailable Sub2API quota contains data.");
        }
    }

    private static bool ValidateWindow(
        int totalAccounts,
        int? accountCount,
        double? remainingPercent,
        double? remainingAccountEquivalents,
        string label)
    {
        var hasAny = accountCount is not null
            || remainingPercent is not null
            || remainingAccountEquivalents is not null;
        if (!hasAny) return false;
        if (accountCount is not { } count
            || remainingPercent is not { } percent
            || remainingAccountEquivalents is not { } equivalents
            || count <= 0
            || count > totalAccounts
            || percent is < 0 or > 100
            || equivalents < 0
            || equivalents > count
            || Math.Abs(equivalents / count * 100 - percent) > RelationTolerance)
        {
            throw new JsonException($"Sub2API {label} quota window is invalid.");
        }
        return true;
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

    private static double? OptionalPercent(JsonElement root, string name)
    {
        var value = OptionalNonNegativeDouble(root, name);
        return value is null || value <= 100
            ? value
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

    private static Sub2ApiQuotaFetchResult Failure(ProviderHealthCode code) => new(null, code);
}
