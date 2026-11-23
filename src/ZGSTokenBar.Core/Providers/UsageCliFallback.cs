using System.Diagnostics;
using System.Net;

namespace ZGSTokenBar.Core;

/// <summary>Runs a provider CLI and returns its stdout, or null on failure/timeout/oversized output.</summary>
public delegate Task<string?> UsageCliRunner(string executable, IReadOnlyList<string> arguments);

/// <summary>
/// Fills in providers whose HTTP read produced no usable reading from their local CLI
/// (<see cref="IUsageProvider.Cli"/>), reusing cached readings for CLIs that are slow to start.
/// </summary>
public sealed class UsageCliFallback(UsageCliRunner? runner = null)
{
    private readonly UsageCliRunner _run = runner ?? RunProcessAsync;
    private readonly Dictionary<string, (DateTimeOffset At, UsageParseResult? Parsed)> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public async Task<UsageBoard> ApplyAsync(UsageBoard board, UsageDetectContext context, Func<string, bool> isEnabled, DateTimeOffset now)
    {
        var existingById = board.Providers.ToDictionary(provider => provider.ProviderId, StringComparer.Ordinal);
        var tasks = new List<Task<UsageProviderSnapshot?>>();
        var replacedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in UsageProviderRegistry.All)
        {
            var id = provider.Definition.Id;
            existingById.TryGetValue(id, out var current);
            if (provider.Cli is null || !isEnabled(id) || HasReading(current)) continue;
            if (provider.Cli.Executable(context) is null) continue;
            replacedIds.Add(id);
            tasks.Add(ApplyProviderAsync(provider, current, context, now));
        }

        if (tasks.Count == 0) return board;
        var updated = await Task.WhenAll(tasks).ConfigureAwait(false);
        var preserved = board.Providers.Where(provider => !replacedIds.Contains(provider.ProviderId)).ToArray();
        return UsageProviderEngine.Merge(new UsageBoard(preserved), updated.OfType<UsageProviderSnapshot>().ToArray());
    }

    public async Task<UsageProviderSnapshot?> ApplyProviderAsync(
        IUsageProvider provider,
        UsageProviderSnapshot? current,
        UsageDetectContext context,
        DateTimeOffset now)
    {
        if (provider.Cli is not { } cli || HasReading(current)) return current;
        if (cli.Executable(context) is not { } executable) return current;
        UsageParseResult? parsed;
        (DateTimeOffset At, UsageParseResult? Parsed) cached;
        bool hit;
        lock (_gate) hit = _cache.TryGetValue(provider.Definition.Id, out cached);
        if (cli.CacheFor > TimeSpan.Zero && hit && now - cached.At <= cli.CacheFor)
        {
            parsed = cached.Parsed;
        }
        else
        {
            parsed = null;
            foreach (var attempt in cli.Attempts)
            {
                parsed = cli.Parse(await _run(executable, attempt).ConfigureAwait(false));
                if (parsed is not null) break;
            }

            if (cli.CacheFor > TimeSpan.Zero)
            {
                lock (_gate) _cache[provider.Definition.Id] = (now, parsed);
            }
        }

        if (parsed is null && current is not null) return current;
        var account = CreateCliAccount(provider.Definition, parsed, cli.SourceKind, now, cli.Account?.Invoke(context));
        return UsageProviderEngine.Snapshot(provider.Definition, [account]);
    }

    internal static UsageBoard Replace(UsageBoard board, ProviderDefinition definition, UsageParseResult? parsed, string source, DateTimeOffset now,
        string? accountLabel = null)
    {
        if (parsed is null && board.Providers.Any(provider => provider.ProviderId == definition.Id)) return board;
        var account = CreateCliAccount(definition, parsed, source, now, accountLabel);
        var others = board.Providers.Where(provider => !string.Equals(provider.ProviderId, definition.Id, StringComparison.Ordinal)).ToArray();
        return UsageProviderEngine.Merge(new UsageBoard(others), [UsageProviderEngine.Snapshot(definition, [account])]);
    }

    private static UsageAccountSnapshot CreateCliAccount(
        ProviderDefinition definition,
        UsageParseResult? parsed,
        string source,
        DateTimeOffset now,
        string? accountLabel)
    {
        // The account id stays the CLI source, so pace history keys do not change with the sign-in.
        return new UsageAccountSnapshot(
            source,
            string.IsNullOrWhiteSpace(accountLabel) ? definition.DisplayName : accountLabel,
            parsed?.Plan,
            source,
            parsed?.Windows ?? [],
            parsed?.Credits,
            null,
            parsed?.Products ?? [],
            parsed is null ? UsageFreshness.Unavailable : UsageFreshness.Current,
            now,
            parsed is null ? "unavailable" : parsed.ErrorKey)
        {
            Period = parsed?.Period,
        };
    }

    public static bool HasReading(UsageProviderSnapshot? provider) =>
        provider is not null
        && provider.Accounts.Any(account => account.Freshness == UsageFreshness.Current
            && (account.Windows.Any(window => window.UsedPercent is not null) || account.Credits is not null));

    public static bool HasReading(UsageBoard board, string providerId) =>
        HasReading(board.Providers.FirstOrDefault(provider => provider.ProviderId == providerId));

    public static Task<string?> RunProcessAsync(string executable, IReadOnlyList<string> arguments) =>
        RunProcessAsync(executable, arguments, CancellationToken.None);

    internal static async Task<string?> RunProcessAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, Action<ProcessStartInfo>? configure = null)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            configure?.Invoke(process.StartInfo);
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Allow slow self-contained CLIs (e.g. agy.exe) cold-start headroom on top of --print-timeout 20s.
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                process.StandardInput.Close();
                var output = ReadStandardOutputAsync(process.StandardOutput, timeout.Token);
                var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
                // On Windows a blocking pipe read may not observe cancellation until the
                // writer exits. Bound the wait itself so cleanup can terminate that writer.
                await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token))
                    .WaitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static void ApplySystemProxy(ProcessStartInfo start, Uri probeUri, Func<Uri, Uri?>? resolveProxy = null)
    {
        if (HasProxyEnvironment(start)) return;
        var resolved = (resolveProxy ?? ResolveWindowsSystemProxy)(probeUri);
        if (resolved is null || resolved.Equals(probeUri) || string.IsNullOrWhiteSpace(resolved.Host)) return;
        var proxyValue = resolved.GetComponents(UriComponents.SchemeAndServer | UriComponents.UserInfo, UriFormat.UriEscaped);
        if (string.IsNullOrWhiteSpace(proxyValue)) return;
        start.Environment["HTTP_PROXY"] = proxyValue;
        start.Environment["http_proxy"] = proxyValue;
        start.Environment["HTTPS_PROXY"] = proxyValue;
        start.Environment["https_proxy"] = proxyValue;
    }

    private static bool HasProxyEnvironment(ProcessStartInfo start)
    {
        foreach (var key in new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" })
        {
            if (start.Environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return true;
        }
        return false;
    }

    private static Uri? ResolveWindowsSystemProxy(Uri probeUri)
    {
        try
        {
            var resolved = WebRequest.GetSystemWebProxy().GetProxy(probeUri);
            return resolved is null || resolved.Equals(probeUri) ? null : resolved;
        }
        catch (Exception error) when (error is NotSupportedException or PlatformNotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadStandardOutputAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        const int limit = 65_536;
        var output = new System.Text.StringBuilder();
        var buffer = new char[4096];
        var oversized = false;
        while (await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) is > 0 and var count)
        {
            if (output.Length + count > limit) oversized = true;
            if (!oversized) output.Append(buffer, 0, count);
        }
        return oversized ? null : output.ToString();
    }
}
