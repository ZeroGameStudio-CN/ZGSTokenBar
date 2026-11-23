using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>Reads the signed-in CLI's quota through its documented headless, read-only command.</summary>
internal sealed class AntigravityLocalQuota(
    Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>>? run = null)
{
    private const string QualificationCacheFileName = "agy-cli-qualification.json";
    private static readonly Version MinimumVersion = new(1, 5, 3);
    private static readonly string[] QuotaArguments = ["-p", "/usage", "--output-format", "json", "--print-timeout", "20s"];
    private static readonly Uri CloudCodeProbeUri = new("https://cloudcode-pa.googleapis.com/");
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>> _run = run ?? RunDetachedAsync;
    private QualifiedExecutable? _qualified;

    internal async Task<UsageAccountSnapshot?> ReadAsync(UsagePaths paths, DateTimeOffset now, CancellationToken token)
    {
        if (UsageProbes.AntigravityExecutable(paths) is not { } path || ReadStamp(path) is not { } stamp) return null;
        var qualified = _qualified;
        if (qualified?.File != stamp)
        {
            var cached = TryLoadCachedVersion(paths.CacheDirectory, stamp);
            if (cached is not null)
            {
                _qualified = qualified = new QualifiedExecutable(stamp, cached);
            }
            else
            {
                var output = await _run(path, ["--version"], token).ConfigureAwait(false);
                if (!Version.TryParse(output?.Trim(), out var version) || ReadStamp(path) != stamp) return null;
                _qualified = qualified = new QualifiedExecutable(stamp, version);
                TrySaveCachedVersion(paths.CacheDirectory, stamp, version);
            }
        }
        if (qualified.Version < MinimumVersion) return null;

        var accountBefore = UsageAccountDirectory.AntigravitySignedInAccount(paths);
        var response = await _run(path, QuotaArguments, token).ConfigureAwait(false);
        var reading = ReadStamp(path) == stamp && string.Equals(accountBefore,
            UsageAccountDirectory.AntigravitySignedInAccount(paths), StringComparison.OrdinalIgnoreCase)
            ? UsageParsers.ParseAntigravityHeadless(response)
            : null;
        // The CLI quota response does not identify the account. In particular, do not label it
        // with an email remembered by a different client or use the legacy token on failure.
        return new UsageAccountSnapshot("antigravity", "Antigravity", null, "agy-cli", reading?.Windows ?? [],
            null, null, [], reading is null ? UsageFreshness.Unavailable : UsageFreshness.Current,
            reading is null ? null : now, reading is null ? "unavailable" : null);
    }

    private static Task<string?> RunDetachedAsync(string path, IReadOnlyList<string> arguments, CancellationToken token) =>
        UsageCliFallback.RunProcessAsync(path, arguments, token, start =>
        {
            start.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            start.StandardOutputEncoding = System.Text.Encoding.UTF8;
            start.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
            UsageCliFallback.ApplySystemProxy(start, CloudCodeProbeUri);
        });

    private static ExecutableStamp? ReadStamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? new ExecutableStamp(file.FullName, file.Length, file.LastWriteTimeUtc) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Version? TryLoadCachedVersion(string? cacheDirectory, ExecutableStamp stamp)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory)) return null;
        var cachePath = Path.Combine(cacheDirectory, QualificationCacheFileName);
        try
        {
            if (!File.Exists(cachePath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(cachePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var cachedPath = root.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == JsonValueKind.String
                ? pathProp.GetString() : null;
            var cachedLength = root.TryGetProperty("length", out var lenProp) && lenProp.TryGetInt64(out var length)
                ? length : -1L;
            var cachedTicks = root.TryGetProperty("lastWriteUtcTicks", out var ticksProp) && ticksProp.TryGetInt64(out var ticks)
                ? ticks : -1L;
            var cachedVersionText = root.TryGetProperty("version", out var verProp) && verProp.ValueKind == JsonValueKind.String
                ? verProp.GetString() : null;
            if (!string.Equals(cachedPath, stamp.Path, StringComparison.OrdinalIgnoreCase)
                || cachedLength != stamp.Length
                || cachedTicks != stamp.LastWrite.Ticks
                || !Version.TryParse(cachedVersionText, out var version))
            {
                return null;
            }
            return version;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TrySaveCachedVersion(string? cacheDirectory, ExecutableStamp stamp, Version version)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory)) return;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var cachePath = Path.Combine(cacheDirectory, QualificationCacheFileName);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("path", stamp.Path);
                writer.WriteNumber("length", stamp.Length);
                writer.WriteNumber("lastWriteUtcTicks", stamp.LastWrite.Ticks);
                writer.WriteString("version", version.ToString());
                writer.WriteEndObject();
            }
            CredentialSupport.AtomicWrite(cachePath, System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ExecutableStamp(string Path, long Length, DateTime LastWrite);
    private sealed record QualifiedExecutable(ExecutableStamp File, Version Version);
}
