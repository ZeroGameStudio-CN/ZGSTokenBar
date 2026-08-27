using System.Buffers.Binary;
using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Cli;

internal static class ProcessPluginFixture
{
    private const string PluginId = "test.process-fixture";

    public static async Task<int> RunAsync()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        try
        {
            while (true)
            {
                var request = await ReadAsync(
                    input,
                    ApiJsonContext.Default.ApiRequestEnvelope,
                    CancellationToken.None);
                if (request is null) return 0;
                if (request.Method == "plugin.refresh"
                    && request.Params?.GetProperty("reason").GetString() == "credential")
                {
                    var credentialRequest = new ApiRequestEnvelope(
                        1,
                        Guid.NewGuid().ToString("N"),
                        "host.credential.resolve",
                        Object(("slot", "fixture")));
                    await WriteAsync(
                        output,
                        credentialRequest,
                        ApiJsonContext.Default.ApiRequestEnvelope,
                        CancellationToken.None);
                    var credentialResponse = await ReadAsync(
                        input,
                        ApiJsonContext.Default.ApiResponseEnvelope,
                        CancellationToken.None);
                    if (credentialResponse?.Ok != true
                        || credentialResponse.Result?.GetProperty("value").GetString()
                            != "fixture-secret")
                    {
                        await WriteAsync(
                            output,
                            new ApiResponseEnvelope(
                                1,
                                request.RequestId,
                                false,
                                null,
                                new("credential_required", "Fixture credential was unavailable.")),
                            ApiJsonContext.Default.ApiResponseEnvelope,
                            CancellationToken.None);
                        continue;
                    }
                }
                var response = await HandleAsync(request);
                await WriteAsync(
                    output,
                    response,
                    ApiJsonContext.Default.ApiResponseEnvelope,
                    CancellationToken.None);
                if (request.Method == "plugin.dispose") return 0;
            }
        }
        catch
        {
            return 1;
        }
    }

    private static async ValueTask<ApiResponseEnvelope> HandleAsync(ApiRequestEnvelope request)
    {
        if (request.Method == "plugin.handshake")
        {
            var parameters = request.Params
                ?? throw new JsonException();
            var result = new ProcessHandshakeResult(
                1,
                0,
                PluginId,
                "1.0.0",
                parameters.GetProperty("filesDigest").GetString() ?? string.Empty);
            return Success(
                request,
                JsonSerializer.SerializeToElement(
                    result,
                    PluginSdkJsonContext.Default.ProcessHandshakeResult));
        }
        if (request.Method == "plugin.describe")
        {
            return Success(
                request,
                JsonSerializer.SerializeToElement(
                    new ProcessPluginDescription([], []),
                    PluginSdkJsonContext.Default.ProcessPluginDescription));
        }
        if (request.Method == "plugin.refresh")
        {
            var reason = request.Params?.GetProperty("reason").GetString();
            if (reason == "cancel")
            {
                var dataRoot = Environment.GetEnvironmentVariable("ZGSTOKENBAR_PLUGIN_DATA")
                    ?? throw new InvalidOperationException();
                Directory.CreateDirectory(dataRoot);
                File.WriteAllText(Path.Combine(dataRoot, "cancel-received"), "received");
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            if (reason == "timeout")
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            if (reason == "error")
            {
                return new(
                    1,
                    request.RequestId,
                    false,
                    null,
                    new("fixture_error", "Fixture requested an error."));
            }
            var now = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
            var snapshot = new PluginDataSnapshot(
                PluginId,
                now,
                new(
                    PluginHealthCode.Current,
                    true,
                    false,
                    now,
                    "fixture.current"),
                [
                    new(
                        "card.process-fixture",
                        PluginId,
                        "fixture",
                        ContributionKind.Metric,
                        0,
                        "fixture.title",
                        "fixture.icon",
                        "accent.fixture",
                        [new("fixture.value", new("integer", Integer: 7))]),
                ],
                [],
                []);
            return Success(
                request,
                JsonSerializer.SerializeToElement(
                    snapshot,
                    PluginSdkJsonContext.Default.PluginDataSnapshot));
        }
        if (request.Method is "plugin.command" or "plugin.dispose")
        {
            return Success(request, EmptyObject());
        }
        return new(
            1,
            request.RequestId,
            false,
            null,
            new("unknown_method", "Fixture method is unknown."));
    }

    private static ApiResponseEnvelope Success(
        ApiRequestEnvelope request,
        JsonElement result) =>
        new(1, request.RequestId, true, result, null);

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement Object(params (string Key, string Value)[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values) writer.WriteString(key, value);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static async ValueTask<T?> ReadAsync<T>(
        Stream stream,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerRead = await ReadExactOrEofAsync(stream, header, cancellationToken);
        if (!headerRead) return default;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > ZgsHostApi.MaximumFrameBytes) throw new InvalidDataException();
        var payload = new byte[length];
        if (!await ReadExactOrEofAsync(stream, payload, cancellationToken))
        {
            throw new EndOfStreamException();
        }
        return JsonSerializer.Deserialize(payload, typeInfo);
    }

    private static async ValueTask WriteAsync<T>(
        Stream stream,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<bool> ReadExactOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) return offset == 0;
            offset += read;
        }
        return true;
    }
}
