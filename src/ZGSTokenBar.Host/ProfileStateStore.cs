using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Host;

public static class ProfileStateStore
{
    private const string FileName = "profile.last-known-good.json";

    public static void SaveLastKnownGood(string dataRoot, EffectiveProfile profile)
    {
        var safe = profile with
        {
            Plugins = profile.Plugins.Select(plugin => plugin with
            {
                Configuration = RedactConfiguration(plugin.Configuration),
            }).ToArray(),
        };
        var root = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(
                    safe,
                    ApiJsonContext.Default.EffectiveProfile));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static EffectiveProfile? LoadLastKnownGood(string dataRoot)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(dataRoot), FileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize(
                    File.ReadAllBytes(path),
                    ApiJsonContext.Default.EffectiveProfile)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> RedactConfiguration(
        IReadOnlyDictionary<string, JsonElement> configuration) =>
        configuration
            .Where(entry => !SecretLike(entry.Key))
            .ToDictionary(
                entry => entry.Key,
                entry => RedactValue(entry.Value),
                StringComparer.Ordinal);

    private static JsonElement RedactValue(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteRedacted(writer, value);
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                if (SecretLike(property.Name)) continue;
                writer.WritePropertyName(property.Name);
                WriteRedacted(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteRedacted(writer, item);
            writer.WriteEndArray();
            return;
        }
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }
        value.WriteTo(writer);
    }

    private static bool SecretLike(string key)
    {
        var normalized = new string(key
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return new[]
        {
            "secret",
            "token",
            "password",
            "passphrase",
            "credential",
            "apikey",
            "authorization",
            "bearer",
            "cookie",
            "privatekey",
            "accesskey",
            "sessionkey",
            "sharedkey",
            "signingkey",
            "encryptionkey",
            "connectionstring",
        }.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }
}
