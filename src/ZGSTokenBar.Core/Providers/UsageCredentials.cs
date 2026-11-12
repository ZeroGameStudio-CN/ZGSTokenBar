using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed class UsageCredential
{
    public UsageCredential(
        string accountId,
        string label,
        string sourceKind,
        string? secret,
        IReadOnlyDictionary<string, string>? fields = null,
        string? unavailableReason = null)
    {
        AccountId = Trim(accountId, 80) ?? "account";
        Label = Trim(label, 80) ?? AccountId;
        SourceKind = sourceKind;
        Secret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        Fields = fields ?? new Dictionary<string, string>(StringComparer.Ordinal);
        UnavailableReason = unavailableReason;
    }

    public string AccountId { get; }
    public string Label { get; }
    public string SourceKind { get; }
    public string? Secret { get; }
    public IReadOnlyDictionary<string, string> Fields { get; }
    public string? UnavailableReason { get; }

    public override string ToString() => SourceKind + ":" + AccountId;

    private static string? Trim(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var filtered = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (filtered.Length == 0) return null;
        return filtered.Length <= maximum ? filtered : filtered[..maximum];
    }
}

public interface IUsageEnvironment
{
    string? Get(string name);
}

public sealed class ProcessUsageEnvironment : IUsageEnvironment
{
    public static ProcessUsageEnvironment Instance { get; } = new();

    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

public sealed class MapUsageEnvironment(IReadOnlyDictionary<string, string> values) : IUsageEnvironment
{
    public string? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
}

public sealed record UsagePaths(string Home, string ApplicationData, string LocalApplicationData)
{
    public static UsagePaths Current() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}

/// <summary>
/// Local account discovery: one file per provider under Providers/Credentials; this file keeps
/// Discover and the shared bounded/JWT/file helpers.
/// </summary>
public static partial class UsageAccountDirectory
{
    public static IReadOnlyList<UsageCredential> Discover(
        ProviderDefinition provider,
        UsagePaths paths,
        IUsageEnvironment environment,
        bool skipPrimaryClaude)
    {
        var discovered = UsageProviderRegistry.Find(provider.Id)?.Detect(new UsageDetectContext(paths, environment, skipPrimaryClaude)) ?? [];
        return discovered
            .Concat(Declared(provider.Id, paths))
            .GroupBy(account => account.AccountId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(UsageAccounts.MaximumAccounts)
            .ToArray();
    }

    public static string? ProbeSqliteText(string path, string key)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return null;
            var length = new FileInfo(path).Length;
            if (length <= 0 || length > 33_554_432) return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var keyBytes = Encoding.UTF8.GetBytes(key);
        var origin = IndexOf(bytes, keyBytes);
        if (origin < 0) return null;
        var window = bytes.AsSpan(origin, Math.Min(4096, bytes.Length - origin));
        var marker = "eyJ"u8;
        var tokenAt = IndexOf(window, marker);
        if (tokenAt < 0) return null;
        var end = tokenAt;
        while (end < window.Length && IsJwtChar(window[end])) end++;
        var token = Encoding.ASCII.GetString(window[tokenAt..end]);
        return token.Count(character => character == '.') >= 2 ? token : null;
    }

    public static string? JwtSubject(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload,
            };
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var subject = document.RootElement.StringProperty("sub");
            if (string.IsNullOrWhiteSpace(subject)) return null;
            var separator = subject.LastIndexOf('|');
            return separator >= 0 && separator < subject.Length - 1 ? subject[(separator + 1)..] : subject;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadTokenFile(string path, params string[] names)
    {
        var document = Read(path);
        if (document is null) return null;
        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? StringOf(document.RootElement, names)
                : null;
        }
    }

    private static bool IsExpired(JsonElement node)
    {
        if (!node.TryGetProperty("expires_at", out var value) && !node.TryGetProperty("expiresAt", out value))
        {
            return false;
        }

        try
        {
            DateTimeOffset? expiry = value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix > 10_000_000_000 ? unix / 1000 : unix)
                    : null;
            return expiry is { } at && at <= DateTimeOffset.UtcNow.AddMinutes(1);
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static JsonDocument? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = ReadBounded(path, 262_144);
            return JsonDocument.Parse(json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(stream.Length, maximum);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer);
    }

    private static string? StringOf(JsonElement element, params string[] names) => element.StringProperty(names);

    private static string? FirstString(JsonElement element, params string[] names) => StringOf(element, names);

    private static string? NestedString(JsonElement element, string objectName, string property)
    {
        var nested = element.ObjectProperty(objectName);
        return nested is { } node ? StringOf(node, property) : null;
    }

    private static bool IsRelativeFile(string? file) =>
        !string.IsNullOrWhiteSpace(file)
        && file.IndexOfAny(['/', '\\', ':']) < 0
        && !file.Contains("..", StringComparison.Ordinal);

    private static bool IsToken(string value) =>
        value.Length is > 0 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsJwtChar(byte value) =>
        value is (>= (byte)'A' and <= (byte)'Z')
            or (>= (byte)'a' and <= (byte)'z')
            or (>= (byte)'0' and <= (byte)'9')
            or (byte)'-' or (byte)'_' or (byte)'.';

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle)) return index;
        }

        return -1;
    }
}
