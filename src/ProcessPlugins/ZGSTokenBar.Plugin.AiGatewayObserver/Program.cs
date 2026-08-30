using System.Buffers.Binary;
using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.AiGatewayObserver;

internal static class PluginIdentity
{
    public const string Id = "zgstokenbar.provider.ai-gateway";
    public const string Version = "1.2.3";
}

internal sealed class AiGatewayObserverPlugin : IDisposable
{
    private AiGatewayObserverClient? _client;

    public AiGatewayObserverPlugin(AiGatewayObserverClient? client = null)
    {
        _client = client;
    }

    public ProcessPluginDescription Describe() => new([], []);

    public bool HasLocalCredentials()
    {
        _client ??= new AiGatewayObserverClient();
        return _client.HasConfiguredLocalCredentials();
    }

    public async ValueTask<PluginDataSnapshot> RefreshAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _client ??= new AiGatewayObserverClient();
        var result = await _client.FetchAsync(cancellationToken).ConfigureAwait(false);
        if (result.Snapshot is not { } snapshot)
        {
            return result.Failure switch
            {
                ObserverFailureKind.MissingCredentials => Empty(
                    now,
                    PluginHealthCode.MissingCredentials,
                    false,
                    false,
                    "zgstokenbar.provider.ai-gateway.health.missing-credentials"),
                ObserverFailureKind.Authentication => Empty(
                    now,
                    PluginHealthCode.MissingCredentials,
                    false,
                    false,
                    "zgstokenbar.provider.ai-gateway.health.invalid-key",
                    result.HttpStatus),
                ObserverFailureKind.Timeout => Empty(
                    now,
                    PluginHealthCode.Timeout,
                    false,
                    true,
                    "zgstokenbar.provider.ai-gateway.health.timeout"),
                ObserverFailureKind.Http => Empty(
                    now,
                    PluginHealthCode.HttpError,
                    false,
                    true,
                    "zgstokenbar.provider.ai-gateway.health.http-error",
                    result.HttpStatus),
                ObserverFailureKind.BalanceUnavailable => Empty(
                    now,
                    PluginHealthCode.Unavailable,
                    true,
                    true,
                    "zgstokenbar.provider.ai-gateway.health.balance-unavailable"),
                _ => Empty(
                    now,
                    PluginHealthCode.Unavailable,
                    false,
                    true,
                    "zgstokenbar.provider.ai-gateway.health.unavailable"),
            };
        }

        var health = new PluginHealth(
            result.IsCached ? PluginHealthCode.Cached : PluginHealthCode.Current,
            !result.IsCached,
            result.IsCached,
            now,
            result.IsCached
                ? "zgstokenbar.provider.ai-gateway.health.cached"
                : "zgstokenbar.provider.ai-gateway.health.current",
            result.HttpStatus);
        var valueStatus = result.IsCached
            ? "cached"
            : snapshot.IsAvailable ? "available" : "unavailable";

        var card = new MiniCardContribution(
            "ai-gateway-balance",
            PluginIdentity.Id,
            "services",
            ContributionKind.Balance,
            400,
            "zgstokenbar.provider.ai-gateway.title",
            "zgstokenbar.provider.ai-gateway.icon",
            "teal",
            [
                new ContributionSummaryItem(
                    "zgstokenbar.provider.ai-gateway.balance.total",
                    new ContributionValue(
                        "currency",
                        Text: snapshot.Currency,
                        Decimal: snapshot.TotalBalance),
                    valueStatus),
            ]);

        return new(
            PluginIdentity.Id,
            now,
            health,
            [card],
            [BuildDetails(snapshot)],
            [],
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = JsonValue(1),
                ["source"] = JsonValue("deepseek-harness"),
                ["provider"] = JsonValue("deepseek-official"),
                ["currency"] = JsonValue(snapshot.Currency),
                ["cached"] = JsonValue(result.IsCached),
                ["isAvailable"] = JsonValue(snapshot.IsAvailable),
            });
    }

    public void Dispose() => _client?.Dispose();

    private static DetailContribution BuildDetails(DeepSeekBalanceSnapshot snapshot) => new(
        "ai-gateway-details",
        PluginIdentity.Id,
        [
            new DetailSectionContribution(
                "balance",
                "zgstokenbar.provider.ai-gateway.section.balance",
                100,
                [
                    CurrencyRow(
                        "zgstokenbar.provider.ai-gateway.balance.total",
                        snapshot.Currency,
                        snapshot.TotalBalance,
                        snapshot.IsAvailable,
                        snapshot.ObservedAt),
                    CurrencyRow(
                        "zgstokenbar.provider.ai-gateway.balance.topped-up",
                        snapshot.Currency,
                        snapshot.ToppedUpBalance,
                        snapshot.IsAvailable,
                        snapshot.ObservedAt),
                    CurrencyRow(
                        "zgstokenbar.provider.ai-gateway.balance.granted",
                        snapshot.Currency,
                        snapshot.GrantedBalance,
                        snapshot.IsAvailable,
                        snapshot.ObservedAt),
                ]),
        ]);

    private static DetailRowContribution CurrencyRow(
        string labelKey,
        string currency,
        decimal value,
        bool isAvailable,
        DateTimeOffset observedAt) => new(
            labelKey,
            new ContributionValue("currency", Text: currency, Decimal: value),
            isAvailable ? "available" : "unavailable",
            observedAt);

    private static PluginDataSnapshot Empty(
        DateTimeOffset now,
        PluginHealthCode code,
        bool connected,
        bool retryable,
        string messageKey,
        int? httpStatus = null) => new(
            PluginIdentity.Id,
            now,
            new PluginHealth(code, connected, retryable, now, messageKey, httpStatus),
            [],
            [],
            []);

    private static JsonElement JsonValue(int value)
    {
        using var document = JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return document.RootElement.Clone();
    }

    private static JsonElement JsonValue(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) writer.WriteStringValue(value);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement JsonValue(bool value)
    {
        using var document = JsonDocument.Parse(value ? "true" : "false");
        return document.RootElement.Clone();
    }
}

public static class Program
{
    public static Task<int> Main() => RunAsync(
        Console.OpenStandardInput(),
        Console.OpenStandardOutput(),
        CancellationToken.None);

    internal static async Task<int> RunAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken)
    {
        using var plugin = new AiGatewayObserverPlugin();
        while (!cancellationToken.IsCancellationRequested)
        {
            ApiRequestEnvelope? request;
            try
            {
                request = await PluginFrameProtocol.ReadAsync(
                    input,
                    ApiJsonContext.Default.ApiRequestEnvelope,
                    cancellationToken);
            }
            catch (EndOfStreamException)
            {
                return 0;
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or JsonException)
            {
                return 2;
            }

            if (request is null
                || request.SchemaVersion != ZgsHostApi.SchemaVersion
                || !PluginValidation.IsRequestId(request.RequestId))
            {
                return 2;
            }

            var shouldExit = false;
            ApiResponseEnvelope response;
            try
            {
                var result = request.Method switch
                {
                    "plugin.handshake" => Handshake(request.Params),
                    "plugin.describe" => JsonSerializer.SerializeToElement(
                        plugin.Describe(),
                        PluginSdkJsonContext.Default.ProcessPluginDescription),
                    "plugin.probe" => BooleanValue(plugin.HasLocalCredentials()),
                    "plugin.refresh" => JsonSerializer.SerializeToElement(
                        await plugin.RefreshAsync(
                            RefreshNow(request.Params),
                            cancellationToken),
                        PluginSdkJsonContext.Default.PluginDataSnapshot),
                    "plugin.dispose" => EmptyObject(),
                    _ => throw new PluginRequestException("method_not_found", "Plugin method is not supported."),
                };
                shouldExit = request.Method == "plugin.dispose";
                response = new(ZgsHostApi.SchemaVersion, request.RequestId, true, result, null);
            }
            catch (PluginRequestException exception)
            {
                response = new(
                    ZgsHostApi.SchemaVersion,
                    request.RequestId,
                    false,
                    null,
                    new PluginError(exception.Code, exception.Message, exception.Retryable));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }
            catch
            {
                response = new(
                    ZgsHostApi.SchemaVersion,
                    request.RequestId,
                    false,
                    null,
                    new PluginError("internal", "Plugin request failed.", true));
            }

            await PluginFrameProtocol.WriteAsync(
                output,
                response,
                ApiJsonContext.Default.ApiResponseEnvelope,
                cancellationToken);
            if (shouldExit) return 0;
        }
        return 0;
    }

    private static JsonElement Handshake(JsonElement? parameters)
    {
        var values = RequireObject(parameters);
        var apiMajor = RequireInt32(values, "apiMajor");
        var apiMinor = RequireInt32(values, "apiMinor");
        var pluginId = RequireString(values, "pluginId");
        var version = RequireString(values, "version");
        var filesDigest = RequireString(values, "filesDigest");
        if (apiMajor != ZgsHostApi.Major
            || apiMinor < 0
            || apiMinor > ZgsHostApi.Minor
            || pluginId != PluginIdentity.Id
            || version != PluginIdentity.Version
            || filesDigest.Length != 64
            || !filesDigest.All(Uri.IsHexDigit))
        {
            throw new PluginRequestException("trust_failed", "Plugin handshake identity did not match.");
        }
        return JsonSerializer.SerializeToElement(
            new ProcessHandshakeResult(apiMajor, apiMinor, pluginId, version, filesDigest),
            PluginSdkJsonContext.Default.ProcessHandshakeResult);
    }

    private static DateTimeOffset RefreshNow(JsonElement? parameters)
    {
        var values = RequireObject(parameters);
        var value = RequireString(values, "now");
        if (!DateTimeOffset.TryParse(value, out var now))
        {
            throw new PluginRequestException("invalid_argument", "Refresh timestamp is invalid.");
        }
        return now.ToUniversalTime();
    }

    private static JsonElement RequireObject(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } result)
        {
            throw new PluginRequestException("invalid_argument", "Plugin parameters are invalid.");
        }
        return result;
    }

    private static int RequireInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is not JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw new PluginRequestException("invalid_argument", "Plugin parameters are invalid.");
        }
        return result;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is not JsonValueKind.String
            || value.GetString() is not { } result)
        {
            throw new PluginRequestException("invalid_argument", "Plugin parameters are invalid.");
        }
        return result;
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement BooleanValue(bool value)
    {
        using var document = JsonDocument.Parse(value ? "true" : "false");
        return document.RootElement.Clone();
    }
}

internal sealed class PluginRequestException : Exception
{
    public PluginRequestException(string code, string message, bool retryable = false)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }
    public bool Retryable { get; }
}

internal static class PluginFrameProtocol
{
    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await ReadExactAsync(stream, lengthBytes, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > ZgsHostApi.MaximumFrameBytes)
        {
            throw new InvalidDataException("Plugin frame is invalid.");
        }
        var payload = new byte[length];
        await ReadExactAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize(payload, typeInfo);
    }

    public static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        if (payload.Length is <= 0 or > ZgsHostApi.MaximumFrameBytes)
        {
            throw new InvalidDataException("Plugin frame exceeds the limit.");
        }
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
