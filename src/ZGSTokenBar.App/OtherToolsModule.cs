using System.Diagnostics;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal sealed class OtherToolsModule : IDisposable
{
    public const string GrokId = UsageProviderIds.Grok;
    public const string AntigravityId = UsageProviderIds.Antigravity;
    private readonly BarForm _bar;
    private readonly Action _changed;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1_000 };
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    private readonly UsagePaths _paths = UsagePaths.Current();
    private readonly object _gate = new();
    private UsageBoard _board = UsageBoard.Empty;
    private UsageBoard _fetched = UsageBoard.Empty;
    private IReadOnlyList<UsageProviderSnapshot> _external = [];
    private UsageBoardForm? _popup;
    private Func<string, bool> _enabled = static _ => true;
    private bool _chinese;
    private bool _light;
    private bool _refreshing;
    private bool _disposed;

    public OtherToolsModule(BarForm bar, Action changed)
    {
        _bar = bar;
        _changed = changed;
        _bar.PluginPopoverRequested += OnPopoverRequested;
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = 120_000;
            _ = RefreshAsync();
        };
        _timer.Start();
    }

    public IReadOnlyList<PluginMiniCardView> Cards(bool chinese, Func<string, bool> enabled)
    {
        _chinese = chinese;
        _enabled = enabled;
        UsageBoard board;
        lock (_gate) board = _board;
        var cards = new List<PluginMiniCardView>();
        foreach (var definition in ProviderCatalog.All)
        {
            if (definition.Surface != UsageSurface.Mini || !enabled(definition.Id)) continue;
            var provider = board.Providers.FirstOrDefault(item => item.ProviderId == definition.Id)
                ?? Placeholder(definition);
            if (provider is null) continue;
            cards.Add(new PluginMiniCardView(
                definition.Id,
                definition.DisplayName,
                UsageProjection.MiniCard(provider, chinese),
                new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        return cards;
    }

    public void SetExternal(IReadOnlyList<UsageProviderSnapshot> snapshots)
    {
        _external = snapshots;
        Publish();
    }

    internal static string? ParseGrok(string? json) => UsageParsers.GrokRemainingText(json);

    internal static string[] ParseAntigravity(string? output) => UsageParsers.AntigravityRows(output);

    private UsageProviderSnapshot? Placeholder(ProviderDefinition definition)
    {
        var present = definition.Id switch
        {
            GrokId => UsageProbes.GrokPresent(_paths, ProcessUsageEnvironment.Instance),
            AntigravityId => UsageProbes.AntigravityPresent(_paths),
            _ => false,
        };
        if (!present) return null;
        return new UsageProviderSnapshot(definition.Id, definition.DisplayName, definition.Accent, definition.Order,
        [
            new UsageAccountSnapshot(definition.Id, definition.DisplayName, null, "probe", [], null, null, [],
                UsageFreshness.Loading, null, null),
        ]);
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            var request = new UsageLoadRequest(
                DateTimeOffset.UtcNow,
                _paths,
                _enabled,
                new HttpUsageTransport(_http),
                ProcessUsageEnvironment.Instance,
                _enabled(UsageProviderIds.Claude));
            var fetched = await UsageProviderEngine.LoadAsync(request, CancellationToken.None).ConfigureAwait(true);
            fetched = await WithCliAsync(fetched).ConfigureAwait(true);
            if (_disposed) return;
            _fetched = fetched;
            Publish();
        }
        catch (Exception error) when (error is IOException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task<UsageBoard> WithCliAsync(UsageBoard board)
    {
        if (_enabled(GrokId) && !HasReading(board, GrokId) && UsageProbes.GrokExecutable(_paths, ProcessUsageEnvironment.Instance) is { } grok)
        {
            board = Replace(board, GrokId, UsageParsers.ParseGrok(await RunAsync(grok, "usage", "--json")), "grok-cli");
        }

        if (_enabled(AntigravityId)
            && !HasReading(board, AntigravityId)
            && UsageProbes.AntigravityExecutable(_paths) is { } antigravity)
        {
            var output = await RunAsync(antigravity, "-p", "/usage", "--print-timeout", "20s");
            var parsed = UsageParsers.ParseAntigravity(output)
                ?? UsageParsers.ParseAntigravity(await RunAsync(antigravity, "-p", "/quota", "--output-format", "json"));
            board = Replace(board, AntigravityId, parsed, "agy-cli");
        }

        return board;
    }

    private static UsageBoard Replace(UsageBoard board, string providerId, UsageParseResult? parsed, string source)
    {
        var definition = ProviderCatalog.Find(providerId);
        if (definition is null || (parsed is null && board.Providers.Any(provider => provider.ProviderId == providerId)))
        {
            return board;
        }
        var account = new UsageAccountSnapshot(
            source,
            definition.DisplayName,
            parsed?.Plan,
            source,
            parsed?.Windows ?? [],
            parsed?.Credits,
            null,
            parsed?.Products ?? [],
            parsed is null ? UsageFreshness.Unavailable : UsageFreshness.Current,
            DateTimeOffset.UtcNow,
            parsed is null ? "unavailable" : parsed.ErrorKey);
        var snapshot = new UsageProviderSnapshot(definition.Id, definition.DisplayName, definition.Accent, definition.Order, [account]);
        var others = board.Providers.Where(provider => !string.Equals(provider.ProviderId, providerId, StringComparison.Ordinal)).ToArray();
        return UsageProviderEngine.Merge(new UsageBoard(others), [snapshot]);
    }

    private static bool HasReading(UsageBoard board, string providerId) =>
        board.Providers.Any(provider => provider.ProviderId == providerId
            && provider.Accounts.Any(account => account.Freshness == UsageFreshness.Current
                && (account.Windows.Any(window => window.UsedPercent is not null) || account.Credits is not null)));

    private static async Task<string?> RunAsync(string executable, params string[] args)
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
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0) return null;
            var output = await process.StandardOutput.ReadToEndAsync();
            return output.Length <= 65_536 ? output : null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private void RequestRefresh()
    {
        if (_disposed) return;
        _ = RefreshAsync();
    }

    private void Publish()
    {
        var merged = UsageProviderEngine.Merge(_fetched, _external);
        lock (_gate) _board = merged;
        if (_bar.IsDisposed) return;
        if (_bar.InvokeRequired)
        {
            _bar.BeginInvoke(() =>
            {
                _changed();
                _bar.SetUsageBoard(merged);
                _popup?.ShowBoard(merged);
            });
            return;
        }

        _changed();
        _bar.SetUsageBoard(merged);
        _popup?.ShowBoard(merged);
    }

    public void ShowOverview()
    {
        if (_disposed || _bar.IsDisposed) return;
        _popup?.Dispose();
        _popup = new UsageBoardForm(
            Math.Max(1, _bar.DeviceDpi / 96f),
            _chinese,
            _light,
            RequestRefresh,
            () => _bar.RequestUsageSettings());
        UsageBoard board;
        lock (_gate) board = _board;
        _popup.ShowBoard(board);
        var anchor = _bar.RectangleToScreen(_bar.ClientRectangle);
        var screen = Screen.FromRectangle(anchor).WorkingArea;
        var x = Math.Clamp(anchor.Left, screen.Left, Math.Max(screen.Left, screen.Right - _popup.Width));
        var y = anchor.Top - _popup.Height >= screen.Top
            ? anchor.Top - _popup.Height
            : anchor.Bottom;
        _popup.Location = new Point(x, y);
        _popup.FormClosed += (_, _) => _light = _popup?.Light == true;
        _popup.Show(BarForm.PopoverOwnerFor(_bar));
    }

    private void OnPopoverRequested(object? sender, PluginPopoverRequest request)
    {
        if (!ProviderCatalog.StartsCollapsed(request.PluginId)) return;
        _popup?.Dispose();
        _popup = new UsageBoardForm(request.Scale, _chinese, _light, RequestRefresh, () => _bar.RequestUsageSettings());
        UsageBoard board;
        lock (_gate) board = _board;
        _popup.ShowBoard(board);
        var screen = Screen.FromRectangle(request.Anchor).WorkingArea;
        var x = Math.Clamp(request.Anchor.Left, screen.Left, Math.Max(screen.Left, screen.Right - _popup.Width));
        var y = request.Anchor.Top - _popup.Height >= screen.Top
            ? request.Anchor.Top - _popup.Height
            : request.Anchor.Bottom;
        _popup.Location = new Point(x, y);
        _popup.FormClosed += (_, _) => _light = _popup?.Light == true;
        _popup.Show(BarForm.PopoverOwnerFor(_bar));
        request.Popup = _popup;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _http.Dispose();
        _bar.PluginPopoverRequested -= OnPopoverRequested;
        _popup?.Dispose();
    }
}
