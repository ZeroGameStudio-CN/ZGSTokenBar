using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ZGSTokenBar.Cli;

internal static class CliOutput
{
    public static int Result(
        bool asJson,
        string command,
        JsonElement result,
        string? text = null)
    {
        Write(asJson, command, result, null, text);
        return 0;
    }

    public static int Invalid(bool asJson, string command, string message)
    {
        Write(asJson, command, null, new("invalid_arguments", message));
        return 2;
    }

    public static int Unknown(string command, bool asJson)
    {
        Write(asJson, command, null, new("unknown_command", $"Unknown command: {command}."));
        return 2;
    }

    public static void Write(
        bool asJson,
        string command,
        JsonElement? result,
        CliError? error,
        string? text = null)
    {
        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new CliEnvelope(1, command, error is null, result, error),
                CliJsonContext.Default.CliEnvelope));
            return;
        }
        if (error is not null)
        {
            Console.Error.WriteLine(error.Message);
            return;
        }
        Console.WriteLine(text ?? (result is null ? "OK" : Pretty(result.Value)));
    }

    public static void Legacy<T>(
        bool asJson,
        T payload,
        string text,
        JsonTypeInfo<T> typeInfo) =>
        Console.WriteLine(asJson ? JsonSerializer.Serialize(payload, typeInfo) : text);

    public static JsonElement EmptyObject() => ObjectElement();

    public static JsonElement ObjectElement(params (string Key, object? Value)[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values)
            {
                if (value is null) continue;
                writer.WritePropertyName(key);
                switch (value)
                {
                    case string text: writer.WriteStringValue(text); break;
                    case bool boolean: writer.WriteBooleanValue(boolean); break;
                    case int number: writer.WriteNumberValue(number); break;
                    case long number: writer.WriteNumberValue(number); break;
                    case IEnumerable<string> items:
                        writer.WriteStartArray();
                        foreach (var item in items) writer.WriteStringValue(item);
                        writer.WriteEndArray();
                        break;
                    default: throw new InvalidOperationException("Unsupported CLI parameter type.");
                }
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static string Pretty(JsonElement value)
    {
        var options = new JsonWriterOptions { Indented = true };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options)) value.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
