using System.Diagnostics;

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
        foreach (var provider in UsageProviderRegistry.All)
        {
            if (provider.Cli is not { } cli || !isEnabled(provider.Definition.Id) || HasReading(board, provider.Definition.Id)) continue;
            if (cli.Executable(context) is not { } executable) continue;
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

            board = Replace(board, provider.Definition, parsed, cli.SourceKind, now, cli.Account?.Invoke(context));
        }

        return board;
    }

    internal static UsageBoard Replace(UsageBoard board, ProviderDefinition definition, UsageParseResult? parsed, string source, DateTimeOffset now,
        string? accountLabel = null)
    {
        if (parsed is null && board.Providers.Any(provider => provider.ProviderId == definition.Id)) return board;
        // The account id stays the CLI source, so pace history keys do not change with the sign-in.
        var account = new UsageAccountSnapshot(
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
        var others = board.Providers.Where(provider => !string.Equals(provider.ProviderId, definition.Id, StringComparison.Ordinal)).ToArray();
        return UsageProviderEngine.Merge(new UsageBoard(others), [UsageProviderEngine.Snapshot(definition, [account])]);
    }

    public static bool HasReading(UsageBoard board, string providerId) =>
        board.Providers.Any(provider => provider.ProviderId == providerId
            && provider.Accounts.Any(account => account.Freshness == UsageFreshness.Current
                && (account.Windows.Any(window => window.UsedPercent is not null) || account.Credits is not null)));

    public static async Task<string?> RunProcessAsync(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0) return null;
            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            return output.Length <= 65_536 ? output : null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
