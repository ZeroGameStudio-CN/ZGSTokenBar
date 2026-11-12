using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using ZGSTokenBar.Builtins;
using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;
using ZGSTokenBar.Transport.NamedPipe;

namespace ZGSTokenBar.App;

internal sealed partial class QuotaApplicationContext : ApplicationContext, IDesktopControl
{
    private static readonly HashSet<string> LegacyRenderedPluginIds =
    [
        "zgstokenbar.metrics.system",
        "zgstokenbar.provider.claude",
        "zgstokenbar.provider.codex",
        "zgstokenbar.usage.codex-local",
    ];
    private readonly AppSettingsStore _store;
    private readonly PluginCredentialStore _pluginCredentialStore = new();
    private readonly QuotaCoordinator _coordinator = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _codexTokenUsageGate = new(1, 1);
    private readonly SemaphoreSlim _systemUsageGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly QuotaSnapshotStabilizer _snapshotStabilizer = new();
    private readonly QuotaPaceTracker _quotaPaceTracker;
    private readonly CodexQuotaTokenTracker _codexQuotaTokenTracker;
    private readonly CodexTokenUsageLedger _codexTokenUsageLedger;
    private readonly SystemUsageSampler _systemUsageSampler = new();
    private readonly NetworkTrafficLedger _networkTraffic;
    private readonly ObservedOutputRate _observedOutput = new();
    private readonly ReleaseUpdateChecker _updateChecker = new();
    private readonly BarForm _bar;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _refreshMenuItem;
    private readonly ToolStripMenuItem _updateMenuItem;
    private readonly ToolStripMenuItem _settingsMenuItem;
    private readonly ToolStripMenuItem _quitMenuItem;
    private readonly QuotaMilestoneTracker _milestoneTracker;
    private readonly System.Windows.Forms.Timer _clockTimer;
    private readonly bool _allowGlobalStartupRegistration;
    private readonly System.Windows.Forms.Timer _confirmationTimer;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _providerActivityTimer;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly RegisteredWaitHandle _activationWait;
    private readonly ZgsTokenBarHost _pluginHost;
    private readonly RouterModule _routerModule;
    private readonly OtherToolsModule _otherToolsModule;
    private readonly ZgsNamedPipeServer _apiServer;
    private readonly ZgsTokenBarHost.Subscription _pluginSubscription;
    private readonly Task _pluginEventTask;
    private readonly Dictionary<(string PluginId, string Locale), PluginAssets> _pluginAssets = [];
    private AppSettings _settings;
    private NativeText _text;
    private QuotaSnapshot _snapshot;
    private HashSet<ProviderKind> _activeProviders;
    private CodexTokenUsageSummary? _cachedCodexTokenUsage;
    private IReadOnlyList<CodexAccountInfo> _codexAccounts = [];
    private (long, long, long, long) _codexAccountStamp;
    private long _codexAccountsReadAt;
    // Account activity needs a process snapshot; poll it slowly unless Cockpit's files change.
    private const long CodexAccountPollMs = 30_000;
    private SettingsForm? _settingsDialog;
    private bool _updateBalloonActive;
    private ReleaseUpdateInfo? _availableUpdate;
    private Version? _notifiedUpdateVersion;
    private bool _quitting;
    private bool _rolloutImportRunning;
    private string? _rolloutImportSignature;
    private (QuotaSnapshot Snapshot, DateTimeOffset ObservedAt, string Signature)? _pendingRolloutImport;
    private long _uiRevision;
    private int _genericCardsUpdateQueued;
    private bool _pluginRuntimeDisposed;
    private long _systemUsageGeneration;

    public QuotaApplicationContext(
        EventWaitHandle activationEvent,
        bool openSettingsOnStart = false,
        AppSettingsStore? store = null,
        bool allowGlobalStartupRegistration = true,
        bool bundledPluginInstallFailed = false)
    {
        _allowGlobalStartupRegistration = allowGlobalStartupRegistration;
        _store = store ?? new AppSettingsStore();
        _networkTraffic = new(_store.DataDirectory);
        var now = DateTimeOffset.UtcNow;
        _settings = _store.Load();
        _text = NativeText.For(_settings.Locale);
        _activeProviders = ActiveProviders(_settings);
        _codexAccounts = CockpitCodexAccountDirectory.Read();
        _quotaPaceTracker = new QuotaPaceTracker(_store.LoadQuotaRateHistory(now));
        _codexQuotaTokenTracker = new CodexQuotaTokenTracker(_store.LoadCodexQuotaTokenHistory());
        _codexTokenUsageLedger = new CodexTokenUsageLedger(_store);
        _cachedCodexTokenUsage = _codexTokenUsageLedger.Snapshot(now);
        if (_allowGlobalStartupRegistration && !_store.SettingsReadPending)
        {
            StartupManager.Apply(_settings.OpenAtLogin);
        }

        _snapshot = WithoutLegacyAiGateway(_store.LoadCache(now) ?? LoadingSnapshot(_settings, _text));
        _milestoneTracker = new QuotaMilestoneTracker(_snapshot);
        _bar = new BarForm(
            _settings,
            _snapshot,
            codexAccounts: _codexAccounts,
            activeProviders: _activeProviders);
        _bar.UsageBoardChanged += (_, board) => ObserveUsagePace(board);
        _otherToolsModule = new OtherToolsModule(_bar, ApplyGenericPluginCards);
        _otherToolsModule.SetAntigravityPrimaryGroup(_settings.AntigravityPrimaryGroup);
        _bar.SetQuotaPaceEstimates(QuotaPaceEstimates(_snapshot, now));
        _bar.SetCodexQuotaTokenSummaries(CodexQuotaTokenSummaries(now));
        _bar.SetCodexTokenUsage(_activeProviders.Contains(ProviderKind.Codex)
            ? _cachedCodexTokenUsage
            : null);
        _bar.RefreshRequested += (_, _) => _ = RefreshAsync(userInitiated: true);
        _bar.SettingsRequested += (_, _) => OpenSettings();
        _bar.ProbeSettingsSaveRequested += (_, layoutChange) => ProbeSettingsSave(layoutChange);
        _bar.OverviewRequested += (_, _) => _otherToolsModule.ShowOverview();
        _bar.PlacementCommitted += (_, commit) => SavePlacement(commit);
        _bar.SystemUsageDetailsRequested += (_, _) => _ = RefreshSystemUsageDetailsAsync();
        _bar.GlassExpandedChanged += (_, expanded) => SaveGlassExpanded(expanded);
        _bar.FormClosing += BarFormClosing;
        _bar.FormClosed += (_, _) => Quit();

        var pluginEnabled = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["zgstokenbar.metrics.system"] = _settings.IsPluginEnabled("zgstokenbar.metrics.system", true),
            ["zgstokenbar.provider.claude"] = _settings.IsEnabled(ProviderKind.Claude),
            ["zgstokenbar.provider.codex"] = _settings.IsEnabled(ProviderKind.Codex),
            ["zgstokenbar.usage.codex-local"] = _settings.IsEnabled(ProviderKind.Codex),
            ["zgstokenbar.provider.ai-gateway"] = _settings.IsEnabled(ProviderKind.AiGateway),
        };
        foreach (var entry in _settings.PluginEnabled)
        {
            pluginEnabled[entry.Key] = entry.Value;
        }
        var plugins = GeneratedBuiltinPluginRegistry.Create().ToList();
        var processPlugins = new PluginPackageManager(_store.DataDirectory).LoadProcessPlugins(
            _pluginCredentialStore);
        var selection = PluginCatalogComposer.SelectOptional(plugins, processPlugins);
        plugins.AddRange(selection.Accepted);
        foreach (var plugin in selection.Rejected)
        {
            try { plugin.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { }
        }
        var profile = ProfileComposition.IncludePlugins(
            BuiltinProfiles.Desktop(pluginEnabled),
            plugins.Select(plugin => plugin.Manifest),
            pluginEnabled);
        _pluginHost = new(
            plugins,
            profile,
            ProductVersion(),
            _store.DataDirectory,
            this);
        // Process plugins perform asynchronous pipe handshakes before the WinForms
        // message loop starts, so they must not capture the UI synchronization context.
        Task.Run(() => _pluginHost.StartAsync(_shutdown.Token).AsTask())
            .GetAwaiter()
            .GetResult();
        PublishQuotaPlugins(_snapshot);
        if (_activeProviders.Contains(ProviderKind.Codex))
        {
            _pluginHost.Publish(CorePluginProjection.CodexUsage(
                "zgstokenbar.usage.codex-local",
                _cachedCodexTokenUsage,
                now,
                cached: _cachedCodexTokenUsage is not null));
        }
        _apiServer = new ZgsNamedPipeServer(_pluginHost);
        _pluginSubscription = _pluginHost.Subscribe(includeValues: true);
        ApplyGenericPluginCards();

        var menu = new ContextMenuStrip();
        _refreshMenuItem = new ToolStripMenuItem(
            _text.RefreshNow,
            null,
            (_, _) => _ = RefreshAsync(userInitiated: true));
        menu.Items.Add(_refreshMenuItem);
        _updateMenuItem = new ToolStripMenuItem(string.Empty, null, (_, _) => OpenUpdatePage())
        {
            Visible = false,
        };
        menu.Items.Add(_updateMenuItem);
        _settingsMenuItem = new ToolStripMenuItem(_text.Settings, null, (_, _) => OpenSettings());
        menu.Items.Add(_settingsMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        _quitMenuItem = new ToolStripMenuItem(_text.Quit, null, (_, _) => Quit());
        menu.Items.Add(_quitMenuItem);
        _bar.ContextMenuStrip = menu;

        _tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = _text.TrayText,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) EnsureVisible();
        };
        _tray.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) OpenSettings();
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_updateBalloonActive) OpenUpdatePage();
        };

        _clockTimer = new System.Windows.Forms.Timer { Enabled = true };
        _clockTimer.Tick += (_, _) =>
        {
            if (_bar.IsTaskbarMode)
            {
                if (_bar.WantsSystemUsageDetails) _ = RefreshSystemUsageDetailsAsync();
                else _ = RefreshSystemUsageOverviewAsync();
                _bar.SyncTaskbarPlacement();
                if (_settings.IsPluginEnabled("zgstokenbar.metrics.system", true) && _activeProviders.Contains(ProviderKind.Codex))
                    _ = RefreshCodexConversationsAsync();
            }
            _bar.Invalidate();
        };
        _confirmationTimer = new System.Windows.Forms.Timer { Interval = 3_000 };
        _confirmationTimer.Tick += (_, _) =>
        {
            _confirmationTimer.Stop();
            _ = RefreshAsync(forceProviderRefresh: true);
        };
        _refreshTimer = new System.Windows.Forms.Timer();
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _providerActivityTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _providerActivityTimer.Tick += (_, _) =>
        {
            _ = UpdateProviderActivityAsync(requestRefresh: true);
            if (_cachedCodexTokenUsage?.PendingConversationCount > 0
                && _activeProviders.Contains(ProviderKind.Codex))
                _ = RefreshCodexConversationsAsync();
        };
        _updateTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync();
        _updateTimer.Start();
        ApplyRefreshInterval();
        _providerActivityTimer.Start();
        SubscribePowerEvents();
        ApplyClockInterval();

        _bar.Show();
        if (bundledPluginInstallFailed)
        {
            _tray.ShowBalloonTip(
                8_000,
                _text.PluginBundleTrustFailedTitle,
                _text.PluginBundleTrustFailedBody,
                ToolTipIcon.Warning);
        }
        _bar.SyncTaskbarPlacement();
        _routerModule = new(_bar, _pluginHost,
            () => _settings.IsPluginEnabled(ZGSTokenBar.Plugin.Router.RouterPlugin.Id, false), _shutdown.Token);
        _apiServer.Start();
        _pluginEventTask = WatchPluginEventsAsync();

        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            (_, timedOut) =>
            {
                if (timedOut || _bar.IsDisposed) return;
                try
                {
                    _bar.BeginInvoke(() =>
                    {
                        EnsureVisible();
                        OpenSettings();
                    });
                }
                catch (InvalidOperationException)
                {
                    // The bar can be disposed between the guard and BeginInvoke during shutdown.
                }
            },
            null,
            Timeout.Infinite,
            false);

        _bar.BeginInvoke(() => _ = RefreshSystemUsageOverviewAsync());
        _bar.BeginInvoke(() => _ = RefreshAsync());
        _bar.BeginInvoke(() => _ = CheckForUpdatesAsync());
        if (openSettingsOnStart) _bar.BeginInvoke(OpenSettings);
    }

    private void EnsureVisible()
    {
        if (_bar.IsDisposed) return;
        _bar.RestoreFromTray();
        if (_bar.IsTaskbarDocked)
        {
            _bar.SyncTaskbarPlacement();
            return;
        }
        _bar.ClampToVisibleScreen();
        _bar.BringToFront();
    }

    private void BarFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_quitting || eventArgs.CloseReason != CloseReason.UserClosing) return;
        eventArgs.Cancel = true;
        _bar.HideToTray();
    }

    private static string ProductVersion() =>
        (Assembly.GetExecutingAssembly()
             .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
             ?.InformationalVersion
         ?? "unknown").Split('+', 2)[0];

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _shutdown.Cancel();
        UnsubscribePowerEvents();
        _refreshTimer.Stop();
        _updateTimer.Stop();
        _clockTimer.Stop();
        _confirmationTimer.Stop();
        _settingsDialog?.Dispose();
        _settingsDialog = null;
        if (!_bar.IsDisposed) _bar.Hide();
        try
        {
            await _refreshGate.WaitAsync();
            _refreshGate.Release();
            await _codexTokenUsageGate.WaitAsync();
            _codexTokenUsageGate.Release();
            await _systemUsageGate.WaitAsync();
            _systemUsageGate.Release();
            _networkTraffic.Flush(DateTimeOffset.UtcNow);
            await _updateGate.WaitAsync();
            _updateGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposal already owns the remaining shutdown work.
        }
        _activationWait.Unregister(null);
        _tray.Visible = false;
        await DisposePluginRuntimeAsync();
        ExitThread();
    }

    private async ValueTask DisposePluginRuntimeAsync()
    {
        if (_pluginRuntimeDisposed) return;
        _pluginRuntimeDisposed = true;
        _pluginHost.Unsubscribe(_pluginSubscription);
        try { await _pluginEventTask; }
        catch { }
        await _apiServer.DisposeAsync();
        await _pluginHost.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _routerModule.Dispose();
            _otherToolsModule.Dispose();
            _shutdown.Cancel();
            UnsubscribePowerEvents();
            _refreshTimer.Dispose();
            _providerActivityTimer.Dispose();
            _updateTimer.Dispose();
            _clockTimer.Dispose();
            _confirmationTimer.Dispose();
            _activationWait.Unregister(null);
            _settingsDialog?.Dispose();
            _tray.Dispose();
            _bar.Dispose();
            _coordinator.Dispose();
            _refreshGate.Dispose();
            _codexTokenUsageGate.Dispose();
            _systemUsageGate.Dispose();
            _updateGate.Dispose();
            _systemUsageSampler.Dispose();
            _updateChecker.Dispose();
            DisposePluginRuntimeAsync().AsTask().GetAwaiter().GetResult();
            _shutdown.Dispose();
        }
        base.Dispose(disposing);
    }

    private static QuotaSnapshot LoadingSnapshot(AppSettings settings, NativeText text)
    {
        var cards = new List<QuotaCard>();
        var health = new List<ProviderHealth>();
        if (settings.IsEnabled(ProviderKind.Claude))
        {
            cards.Add(new QuotaCard(
                "claude.account",
                ProviderKind.Claude,
                "Claude",
                null,
                "#d97757",
                true,
                [new("5h", null, null, TimeSpan.FromHours(5)), new("1w", null, null, TimeSpan.FromDays(7))]));
            health.Add(new ProviderHealth(
                ProviderKind.Claude,
                false,
                text.ProviderLoading(ProviderKind.Claude),
                ProviderHealthCode.Loading));
        }
        if (settings.IsEnabled(ProviderKind.Codex))
        {
            cards.Add(new QuotaCard(
                "codex.account",
                ProviderKind.Codex,
                "Codex",
                null,
                "#10a37f",
                true,
                [new("5h", null, null, TimeSpan.FromHours(5)), new("1w", null, null, TimeSpan.FromDays(7))]));
            health.Add(new ProviderHealth(
                ProviderKind.Codex,
                false,
                text.ProviderLoading(ProviderKind.Codex),
                ProviderHealthCode.Loading));
        }
        return new QuotaSnapshot(cards, health, DateTimeOffset.UtcNow);
    }

    private static QuotaSnapshot WithoutLegacyAiGateway(QuotaSnapshot snapshot) => snapshot with
    {
        Cards = snapshot.Cards
            .Where(card => card.Provider != ProviderKind.AiGateway)
            .ToArray(),
        Health = snapshot.Health
            .Where(health => health.Provider != ProviderKind.AiGateway)
            .ToArray(),
    };
}
