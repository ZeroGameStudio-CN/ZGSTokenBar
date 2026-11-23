namespace ZGSTokenBar.Core;

/// <summary>Reads the signed-in CLI's quota through its documented headless, read-only command.</summary>
internal sealed class AntigravityLocalQuota(
    Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>>? run = null)
{
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
            var output = await _run(path, ["--version"], token).ConfigureAwait(false);
            if (!Version.TryParse(output?.Trim(), out var version) || ReadStamp(path) != stamp) return null;
            _qualified = qualified = new QualifiedExecutable(stamp, version);
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

    private sealed record ExecutableStamp(string Path, long Length, DateTime LastWrite);
    private sealed record QualifiedExecutable(ExecutableStamp File, Version Version);
}
