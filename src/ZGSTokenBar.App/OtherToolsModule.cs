using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal sealed class OtherToolsModule : IDisposable
{
    public const string GrokId = UsageProviderIds.Grok;
    public const string AntigravityId = UsageProviderIds.Antigravity;
    private readonly BarForm _bar;
    private readonly Action _changed;
    private readonly RefreshScheduler _scheduler;
    private readonly AppSettingsStore _store;
    internal const string RefreshSource = "usage-providers";
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    private readonly UsagePaths _paths;
    private readonly object _gate = new();
    private UsageBoard _board = UsageBoard.Empty;
    private UsageBoard _fetched = UsageBoard.Empty;
    private IReadOnlyList<UsageProviderSnapshot> _external = [];
    private UsageBoardForm? _popup;
    private Func<string, bool> _enabled;
    private string? _antigravityAccountHash;
    private bool _chinese;
    private bool _light;
    private bool _refreshing;
    private bool _disposed;

    public OtherToolsModule(
        BarForm bar,
        Action changed,
        RefreshScheduler scheduler,
        AppSettingsStore store,
        Func<string, bool> enabled)
    {
        _bar = bar;
        _changed = changed;
        _scheduler = scheduler;
        _store = store;
        _enabled = enabled;
        _paths = UsagePaths.Current() with { CacheDirectory = store.DataDirectory };
        _bar.PluginPopoverRequested += OnPopoverRequested;

        var agyHash = AppSettingsStore.HashUsageIdentity(UsageAccountDirectory.AntigravitySignedInAccount(_paths));
        if (_store.LoadUsageBoardCache(DateTimeOffset.UtcNow, agyHash) is { } cachedBoard)
        {
            var detect = DetectContext();
            var eligible = cachedBoard.Providers
                .Where(provider => _enabled(provider.ProviderId)
                    && UsageProviderRegistry.Find(provider.ProviderId)?.IsInstalled(detect) == true)
                .ToArray();
            if (eligible.Length > 0)
            {
                _fetched = new UsageBoard(eligible);
                if (eligible.Any(provider => provider.ProviderId == AntigravityId))
                    _antigravityAccountHash = agyHash;
                _board = AntigravityGroups.Pin(UsageProviderEngine.Merge(_fetched, _external), _antigravityPrimaryGroup);
            }
        }

        // Start fetching immediately after the message loop enters, while showing any fresh
        // cached readings from the previous session right away.
        _scheduler.Add(
            RefreshSource,
            () => UsageProviderRegistry.RefreshInterval,
            async () => { await RefreshAsync().ConfigureAwait(true); return true; },
            firstDelay: TimeSpan.Zero);
    }

    public void PublishInitial()
    {
        lock (_gate)
        {
            if (_fetched.Providers.Count == 0 && _external.Count == 0) return;
        }
        Publish();
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
        lock (_gate) _external = snapshots;
        Publish();
    }

    internal static string? ParseGrok(string? json) => UsageParsers.GrokRemainingText(json);

    internal static string[] ParseAntigravity(string? output) => UsageParsers.AntigravityRows(output);

    private UsageProviderSnapshot? Placeholder(ProviderDefinition definition)
    {
        var provider = UsageProviderRegistry.Find(definition.Id);
        if (provider is null || !provider.IsInstalled(DetectContext())) return null;
        return UsageProviderEngine.Snapshot(definition,
        [
            new UsageAccountSnapshot(definition.Id, definition.DisplayName, null, "probe", [], null, null, [],
                UsageFreshness.Loading, null, null),
        ]);
    }

    private UsageDetectContext DetectContext() => new(_paths, ProcessUsageEnvironment.Instance, _enabled(UsageProviderIds.Claude));

    /// <summary>Fetch now (e.g. after resume); a fetch already in flight is not duplicated.</summary>
    public void RequestRefresh()
    {
        if (_disposed) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        try
        {
            var enabled = _enabled;
            var request = new UsageLoadRequest(
                DateTimeOffset.UtcNow,
                _paths,
                enabled,
                new HttpUsageTransport(_http),
                ProcessUsageEnvironment.Instance,
                enabled(UsageProviderIds.Claude));
            // Account discovery (SQLite probes, credential stats) runs synchronously before the
            // first await; run every enabled provider concurrently in the background and publish
            // each provider as soon as it finishes so slow CLIs never block fast HTTPS reads.
            await Task.Run(async () =>
            {
                var detect = new UsageDetectContext(_paths, ProcessUsageEnvironment.Instance, enabled(UsageProviderIds.Claude));
                var registry = UsageProviderRegistry.All;
                var results = new UsageProviderSnapshot?[registry.Count];
                var tasks = new Task[registry.Count];
                for (var index = 0; index < registry.Count; index++)
                {
                    var slot = index;
                    var provider = registry[slot];
                    tasks[slot] = RefreshProviderAsync(slot, provider, detect);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                if (_disposed) return;

                var completed = new UsageBoard(results.OfType<UsageProviderSnapshot>().ToArray());
                string? savedAgyHash;
                lock (_gate)
                {
                    _fetched = completed;
                    savedAgyHash = _antigravityAccountHash;
                }
                Publish();

                try
                {
                    _store.SaveUsageBoardCache(completed, DateTimeOffset.UtcNow, savedAgyHash);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

                async Task RefreshProviderAsync(int slot, IUsageProvider provider, UsageDetectContext detectContext)
                {
                    var id = provider.Definition.Id;
                    UsageProviderSnapshot? previous;
                    string? previousAgyHash;
                    lock (_gate)
                    {
                        previous = _fetched.Providers.FirstOrDefault(item => item.ProviderId == id);
                        previousAgyHash = _antigravityAccountHash;
                    }

                    string? currentAgyHash = null;
                    var accountChanged = false;
                    if (id == AntigravityId)
                    {
                        currentAgyHash = AppSettingsStore.HashUsageIdentity(
                            UsageAccountDirectory.AntigravitySignedInAccount(_paths));
                        accountChanged = !string.Equals(previousAgyHash, currentAgyHash, StringComparison.Ordinal);
                    }

                    void RefreshAntigravityIdentity()
                    {
                        if (id != AntigravityId) return;
                        var afterHash = AppSettingsStore.HashUsageIdentity(
                            UsageAccountDirectory.AntigravitySignedInAccount(_paths));
                        if (!string.Equals(currentAgyHash, afterHash, StringComparison.Ordinal))
                            accountChanged = true;
                        currentAgyHash = afterHash;
                    }

                    try
                    {
                        if (!enabled(id) || !provider.FetchesUsage)
                        {
                            ReplaceFetchedProvider(id, null, clearAntigravityHash: id == AntigravityId);
                            return;
                        }
                        var snapshot = await UsageProviderEngine.LoadProviderAsync(provider, request, CancellationToken.None).ConfigureAwait(false);
                        if (!UsageCliFallback.HasReading(snapshot) && provider.Cli is not null)
                        {
                            snapshot = await _cli.ApplyProviderAsync(provider, snapshot, detectContext, request.Now).ConfigureAwait(false);
                        }
                        var now = DateTimeOffset.UtcNow;
                        RefreshAntigravityIdentity();
                        var reconciled = UsageProviderEngine.ReconcileProviderRefresh(
                            previous,
                            snapshot,
                            now,
                            accountIdentityChanged: accountChanged);
                        results[slot] = reconciled;
                        if (_disposed) return;
                        CommitProviderResult(id, reconciled, accountChanged, currentAgyHash);
                        Publish();
                    }
                    catch (Exception error) when (error is IOException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
                    {
                        AppDiagnostics.RecordMonitorFailure("usage-board", id, "", AppDiagnostics.MonitorFailureCode(error));
                        RefreshAntigravityIdentity();
                        var fallback = UsageProviderEngine.ReconcileProviderRefresh(
                            previous,
                            UsageProviderEngine.Snapshot(provider.Definition, []),
                            DateTimeOffset.UtcNow,
                            accountIdentityChanged: accountChanged);
                        var retained = fallback is { Accounts.Count: > 0 } ? fallback : null;
                        results[slot] = retained;
                        if (!_disposed)
                        {
                            CommitProviderResult(id, retained, accountChanged, currentAgyHash);
                            Publish();
                        }
                    }
                }
            }).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            AppDiagnostics.RecordMonitorFailure("usage-board", "refresh", "", AppDiagnostics.MonitorFailureCode(error));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void CommitProviderResult(
        string providerId,
        UsageProviderSnapshot? snapshot,
        bool accountChanged,
        string? currentAgyHash)
    {
        lock (_gate)
        {
            ReplaceFetchedProviderLocked(providerId, snapshot);
            if (providerId == AntigravityId && (UsageCliFallback.HasReading(snapshot) || accountChanged || snapshot is null))
                _antigravityAccountHash = currentAgyHash;
        }
    }

    private void ReplaceFetchedProvider(string providerId, UsageProviderSnapshot? snapshot, bool clearAntigravityHash = false)
    {
        lock (_gate)
        {
            ReplaceFetchedProviderLocked(providerId, snapshot);
            if (clearAntigravityHash && providerId == AntigravityId)
                _antigravityAccountHash = null;
        }
    }

    private void ReplaceFetchedProviderLocked(string providerId, UsageProviderSnapshot? snapshot)
    {
        var map = _fetched.Providers.ToDictionary(item => item.ProviderId, StringComparer.Ordinal);
        if (snapshot is null) map.Remove(providerId);
        else map[providerId] = snapshot;
        _fetched = new UsageBoard(
            UsageProviderRegistry.All
                .Select(provider => map.TryGetValue(provider.Definition.Id, out var existing) ? existing : null)
                .OfType<UsageProviderSnapshot>()
                .ToArray());
    }

    private readonly UsageCliFallback _cli = new();

    private string _antigravityPrimaryGroup = AntigravityGroups.DefaultPrimary;

    /// <summary>Which Antigravity group the ring follows; re-publishes so the bar updates at once.</summary>
    public void SetAntigravityPrimaryGroup(string? group)
    {
        var next = string.IsNullOrWhiteSpace(group) ? AntigravityGroups.DefaultPrimary : group.Trim();
        lock (_gate)
        {
            if (string.Equals(next, _antigravityPrimaryGroup, StringComparison.Ordinal)) return;
            _antigravityPrimaryGroup = next;
        }
        Publish();
    }

    /// <summary>Antigravity group names from the latest reading, for the Settings dropdown.</summary>
    public IReadOnlyList<string> AntigravityGroupNames()
    {
        lock (_gate) return AntigravityGroups.Groups(_board);
    }

    private void Publish()
    {
        UsageBoard merged;
        lock (_gate)
        {
            merged = AntigravityGroups.Pin(UsageProviderEngine.Merge(_fetched, _external), _antigravityPrimaryGroup);
            _board = merged;
        }
        if (_disposed || _bar.IsDisposed) return;
        if (_bar.InvokeRequired)
        {
            _bar.BeginInvoke(() =>
            {
                if (_disposed || _bar.IsDisposed) return;
                UsageBoard latest;
                lock (_gate) latest = _board;
                _changed();
                _bar.SetUsageBoard(latest);
                _popup?.ShowBoard(latest);
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
        if (_popup is { Visible: true })
        {
            _popup.Close();
            _popup.Dispose();
            _popup = null;
            return;
        }
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
        _popup.Deactivate += (_, _) =>
        {
            if (!(_popup?.IsDisposed ?? true)) _popup.Close();
        };
        _popup.ShowPopover(BarForm.PopoverOwnerFor(_bar));
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
        _popup.ShowPopover(BarForm.PopoverOwnerFor(_bar));
        request.Popup = _popup;
    }

    public void Dispose()
    {
        _disposed = true;
        _scheduler.Remove(RefreshSource);
        _http.Dispose();
        _bar.PluginPopoverRequested -= OnPopoverRequested;
        _popup?.Dispose();
    }
}
