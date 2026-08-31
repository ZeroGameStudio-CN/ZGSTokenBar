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

internal sealed class QuotaApplicationContext : ApplicationContext, IDesktopControl
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private static readonly HashSet<string> LegacyRenderedPluginIds =
    [
        "zgstokenbar.metrics.system",
        "zgstokenbar.provider.claude",
        "zgstokenbar.provider.codex",
        "zgstokenbar.usage.codex-local",
        "zgstokenbar.intelligence.radar",
    ];
    private readonly AppSettingsStore _store;
    private readonly PluginCredentialStore _pluginCredentialStore = new();
    private readonly QuotaCoordinator _coordinator = new();
    private readonly RadarService _radarService = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _radarGate = new(1, 1);
    private readonly SemaphoreSlim _codexTokenUsageGate = new(1, 1);
    private readonly SemaphoreSlim _systemUsageGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly QuotaSnapshotStabilizer _snapshotStabilizer = new();
    private readonly QuotaPaceTracker _quotaPaceTracker;
    private readonly CodexQuotaTokenTracker _codexQuotaTokenTracker;
    private readonly CodexTokenUsageReader _codexTokenUsageReader;
    private readonly SystemUsageSampler _systemUsageSampler = new();
    private readonly CodexEconomyRouter _codexEconomyRouter = new();
    private readonly ReleaseUpdateChecker _updateChecker = new();
    private readonly BarForm _bar;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _refreshMenuItem;
    private readonly ToolStripMenuItem _radarMenuItem;
    private readonly ToolStripMenuItem _updateMenuItem;
    private readonly ToolStripMenuItem _settingsMenuItem;
    private readonly ToolStripMenuItem _quitMenuItem;
    private readonly QuotaMilestoneTracker _milestoneTracker;
    private readonly System.Windows.Forms.Timer _clockTimer;
    private readonly bool _allowGlobalStartupRegistration;
    private readonly System.Windows.Forms.Timer _confirmationTimer;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _providerActivityTimer;
    private readonly System.Windows.Forms.Timer _radarTimer;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly RegisteredWaitHandle _activationWait;
    private readonly ZgsTokenBarHost _pluginHost;
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
    private RadarAlertState _radarState;
    private RadarViewState _radarViewState;
    private SettingsForm? _settingsDialog;
    private bool _radarBalloonActive;
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
        var now = DateTimeOffset.UtcNow;
        _settings = _store.Load();
        _text = NativeText.For(_settings.Locale);
        _activeProviders = ActiveProviders(_settings);
        _codexAccounts = CockpitCodexAccountDirectory.Read();
        _quotaPaceTracker = new QuotaPaceTracker(_store.LoadQuotaRateHistory(now));
        _codexQuotaTokenTracker = new CodexQuotaTokenTracker(_store.LoadCodexQuotaTokenHistory());
        _codexTokenUsageReader = new CodexTokenUsageReader(_store.LoadCodexTokenUsageIndex());
        _cachedCodexTokenUsage = CodexTokenUsageSummary.ApplyCumulativeFloor(
            _codexTokenUsageReader.Snapshot(now),
            _codexQuotaTokenTracker.GetProfileLifetimeTotal(),
            now);
        _radarState = _store.LoadRadarState();
        _radarService.RestoreRecommendationCache(_radarState.LastSnapshot);
        _radarViewState = WithRadarUnreadState(
            new(
                _radarState.LastSnapshot,
                _radarState.LastSuccessfulFetchAt,
                false,
                null),
            _radarState);
        if (_allowGlobalStartupRegistration)
        {
            StartupManager.Apply(_settings.OpenAtLogin);
        }

        _snapshot = WithoutLegacyAiGateway(_store.LoadCache(now) ?? LoadingSnapshot(_settings, _text));
        _milestoneTracker = new QuotaMilestoneTracker(_snapshot);
        _bar = new BarForm(
            _settings,
            _snapshot,
            _settings.EnableRadar ? [_radarService.Provider] : [],
            codexAccounts: _codexAccounts,
            activeProviders: _activeProviders);
        _bar.SetQuotaPaceEstimates(QuotaPaceEstimates(_snapshot, now));
        _bar.SetCodexQuotaTokenSummaries(CodexQuotaTokenSummaries(now));
        _bar.SetCodexTokenUsage(_activeProviders.Contains(ProviderKind.Codex)
            ? _cachedCodexTokenUsage
            : null);
        RefreshBarCodexEconomyStatus();
        _bar.RefreshRequested += (_, _) => _ = RefreshAsync(userInitiated: true);
        _bar.SettingsRequested += (_, _) => OpenSettings();
        _bar.PlacementCommitted += (_, commit) => SavePlacement(commit);
        _bar.RadarPreviewRequested += (_, request) =>
            RequestRadarPreview(request.Provider, request.SurfaceId);
        _bar.SystemUsageDetailsRequested += (_, _) => _ = RefreshSystemUsageDetailsAsync();
        _bar.CodexEconomyStatusRefreshRequested += (_, _) => RefreshBarCodexEconomyStatus();
        _bar.CodexEconomyModeRequested += (_, request) => ApplyRecommendedCodexEconomyMode(request.Mode);
        _bar.MiniAreaLayoutChanged += (_, _) => SaveMiniAreaLayout();
        _bar.MiniAreaOrderChanged += (_, _) => SaveMiniAreaOrder();
        _bar.FormClosed += (_, _) => Quit();
        _bar.SetRadarState(_radarViewState);

        var pluginEnabled = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["zgstokenbar.metrics.system"] = _settings.IsPluginEnabled("zgstokenbar.metrics.system", true),
            ["zgstokenbar.provider.claude"] = _settings.IsEnabled(ProviderKind.Claude),
            ["zgstokenbar.provider.codex"] = _settings.IsEnabled(ProviderKind.Codex),
            ["zgstokenbar.usage.codex-local"] = _settings.IsEnabled(ProviderKind.Codex),
            ["zgstokenbar.intelligence.radar"] = _settings.EnableRadar,
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
        if (HasPlugin("zgstokenbar.intelligence.radar") && _radarViewState.Snapshot is { } restoredRadar)
        {
            _pluginHost.Publish(CorePluginProjection.Radar("zgstokenbar.intelligence.radar", restoredRadar));
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
        _radarMenuItem = new ToolStripMenuItem(_text.OpenRadarWebsite, null, (_, _) => OpenRadarWebsite())
        {
            Visible = _settings.EnableRadar,
        };
        menu.Items.Add(_radarMenuItem);
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
            else if (_radarBalloonActive) OpenRadarWebsite();
        };

        _clockTimer = new System.Windows.Forms.Timer { Enabled = true };
        _clockTimer.Tick += (_, _) =>
        {
            if (_bar.IsTaskbarMode)
            {
                if (_bar.WantsSystemUsageDetails) _ = RefreshSystemUsageDetailsAsync();
                else _ = RefreshSystemUsageOverviewAsync();
                _bar.SyncTaskbarPlacement();
            }
            else
            {
                _bar.Invalidate();
            }
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
        _providerActivityTimer.Tick += (_, _) => UpdateProviderActivity(requestRefresh: true);
        _radarTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _radarTimer.Tick += (_, _) => _ = RefreshRadarAsync();
        _updateTimer = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync();
        _updateTimer.Start();
        ApplyRefreshInterval();
        _providerActivityTimer.Start();
        ApplyRadarInterval();
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
        _apiServer.Start();
        _pluginEventTask = WatchPluginEventsAsync();

        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            activationEvent,
            (_, timedOut) =>
            {
                if (timedOut || _bar.IsDisposed) return;
                try
                {
                    _bar.BeginInvoke(OpenSettings);
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
        if (_settings.EnableRadarAlerts) _bar.BeginInvoke(() => _ = RefreshRadarAsync());
    }

    private async Task RefreshAsync(bool userInitiated = false, bool forceProviderRefresh = false)
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        UpdateProviderActivity(requestRefresh: false);
        _bar.SetRefreshing(true);
        try
        {
            var activeCandidate = await _coordinator.RefreshAsync(
                _settings,
                _snapshot,
                _shutdown.Token,
                allowClaudeOAuthRefresh: userInitiated,
                forceProviderRefresh: userInitiated || forceProviderRefresh,
                activeProviders: _activeProviders);
            if (_shutdown.IsCancellationRequested || _bar.IsDisposed) return;
            var observedAt = DateTimeOffset.UtcNow;
            var candidate = MergeActiveProviderResults(
                _snapshot,
                activeCandidate,
                _activeProviders,
                observedAt);
            var stabilization = _snapshotStabilizer.Apply(_snapshot, candidate, observedAt);
            var next = stabilization.Snapshot;
            if (stabilization.ConfirmationRequired) _confirmationTimer.Start();
            else _confirmationTimer.Stop();
            var alerts = _milestoneTracker.Observe(next);
            _snapshot = next;
            var paceChanged = !stabilization.ConfirmationRequired
                && _quotaPaceTracker.Observe(next, observedAt);
            var codexQuotaTokenCapacityChanged = !stabilization.ConfirmationRequired
                && _activeProviders.Contains(ProviderKind.Codex)
                && _codexQuotaTokenTracker.Merge(
                    CodexQuotaTokenObservations(next, observedAt),
                    observedAt);
            _bar.SetSnapshot(next, QuotaPaceEstimates(next, observedAt));
            if (codexQuotaTokenCapacityChanged)
            {
                try
                {
                    _store.SaveCodexQuotaTokenHistory(_codexQuotaTokenTracker.Export());
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Account-scoped token estimates remain useful in memory when history cannot be saved.
                }
            }
            if (!stabilization.ConfirmationRequired
                && _activeProviders.Contains(ProviderKind.Codex))
            {
                // Refresh the non-persistent Profile activity reference even when
                // the lifetime counter itself did not advance.
                _bar.SetCodexQuotaTokenSummaries(CodexQuotaTokenSummaries(observedAt));
            }
            PublishQuotaPlugins(next);
            _ = RefreshGenericPluginsAsync();
            if (_activeProviders.Contains(ProviderKind.Codex))
            {
                _ = RefreshCodexTokenUsageAsync(observedAt);
            }
            if (!stabilization.ConfirmationRequired
                && _activeProviders.Contains(ProviderKind.Codex))
            {
                ScheduleCodexRolloutImport(next, observedAt);
            }
            if (_settings.EnableAlerts) ShowMilestoneAlerts(alerts);
            try
            {
                _store.SaveCache(next);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A cache failure must not hide fresh provider data from the running bar.
            }
            if (paceChanged)
            {
                try
                {
                    _store.SaveQuotaRateHistory(_quotaPaceTracker.Export(observedAt));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Pace history is best-effort and never blocks live quota.
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch
        {
            // Keep the last good snapshot visible if an unexpected refresh failure occurs.
        }
        finally
        {
            if (!_bar.IsDisposed) _bar.SetRefreshing(false);
            _refreshGate.Release();
        }
    }

    private void OpenSettings()
    {
        if (_quitting) return;
        if (_settingsDialog is { IsDisposed: false } existing)
        {
            RestoreSettingsWindow(existing, Screen.FromControl(_bar).WorkingArea);
            return;
        }

        var economyProfiles = DiscoverCodexEconomyProfiles();
        var pluginStatuses = _pluginHost.ListPlugins();
        var dialog = new SettingsForm(
            _settings,
            _bar.DeviceDpi,
            plugins: pluginStatuses,
            codexEconomyStatus: InspectRecommendedCodexEconomyProfile(economyProfiles),
            codexEconomyProfiles: economyProfiles,
            inspectCodexEconomy: _codexEconomyRouter.Inspect,
            setCodexEconomyMode: _codexEconomyRouter.SetMode);
        _settingsDialog = dialog;
        dialog.RadarTestNotificationRequested += (_, _) => ShowRadarTestNotification();
        dialog.CodexEconomyStatusChanged += (_, _) => RefreshBarCodexEconomyStatus();
        dialog.FormClosed += SettingsDialogClosed;
        var area = Screen.FromControl(_bar).WorkingArea;
        dialog.Location = new Point(
            area.Left + Math.Max(0, (area.Width - dialog.Width) / 2),
            area.Top + Math.Max(0, (area.Height - dialog.Height) / 2));
        try
        {
            RestoreSettingsWindow(dialog, area);
        }
        catch
        {
            _settingsDialog = null;
            dialog.Dispose();
            throw;
        }
    }

    internal static void RestoreSettingsWindow(Form dialog, Rectangle? fallbackWorkingArea = null)
    {
        if (dialog.WindowState == FormWindowState.Minimized)
        {
            dialog.WindowState = FormWindowState.Normal;
        }
        var workingAreas = Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
        var fallback = fallbackWorkingArea ?? Screen.FromControl(dialog).WorkingArea;
        dialog.Location = RestoredSettingsLocation(dialog.Bounds, workingAreas, fallback);
        if (!dialog.Visible) dialog.Show();
        if (dialog.IsHandleCreated)
        {
            SetWindowPos(
                dialog.Handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
        dialog.BringToFront();
        dialog.Activate();
    }

    internal static Point RestoredSettingsLocation(
        Rectangle bounds,
        IReadOnlyList<Rectangle> workingAreas,
        Rectangle fallbackWorkingArea)
    {
        if (workingAreas.Any(area => Rectangle.Intersect(area, bounds) is { Width: > 0, Height: > 0 }))
        {
            return bounds.Location;
        }
        return new Point(
            fallbackWorkingArea.Left + Math.Max(0, (fallbackWorkingArea.Width - bounds.Width) / 2),
            fallbackWorkingArea.Top + Math.Max(0, (fallbackWorkingArea.Height - bounds.Height) / 2));
    }

    private CodexEconomyStatus? InspectRecommendedCodexEconomyProfile() =>
        InspectRecommendedCodexEconomyProfile(DiscoverCodexEconomyProfiles());

    private CodexEconomyStatus? InspectRecommendedCodexEconomyProfile(
        IReadOnlyList<CodexEconomyProfile> profiles)
    {
        try
        {
            var profile = profiles.FirstOrDefault(candidate => candidate.Recommended)
                ?? profiles.FirstOrDefault();
            return profile is null ? null : _codexEconomyRouter.Inspect(profile);
        }
        catch
        {
            return null;
        }
    }

    private void RefreshBarCodexEconomyStatus()
    {
        if (_quitting || _bar.IsDisposed) return;
        try { _bar.SetCodexEconomyStatus(_codexEconomyRouter.Inspect(DefaultCodexEconomyProfile())); }
        catch { _bar.SetCodexEconomyStatus(null); }
    }

    private void ApplyRecommendedCodexEconomyMode(CodexEconomyMode mode)
    {
        if (_quitting || _bar.IsDisposed) return;
        try
        {
            var profile = DefaultCodexEconomyProfile();
            var applied = _codexEconomyRouter.SetMode(profile, mode);
            _bar.SetCodexEconomyStatus(applied);
            _settingsDialog?.RefreshCodexEconomyStatus();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                _bar,
                _text.CodexEconomyApplyFailed(exception.Message),
                _text.CodexEconomyApplyFailedTitle,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            RefreshBarCodexEconomyStatus();
        }
    }

    private static IReadOnlyList<CodexEconomyProfile> DiscoverCodexEconomyProfiles()
    {
        try { return CodexEconomyRouter.DiscoverProfiles(); }
        catch { return []; }
    }

    private static CodexEconomyProfile DefaultCodexEconomyProfile()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new CodexEconomyException("The Windows user profile directory is unavailable.");
        }
        return CodexEconomyRouter.ResolveProfile(Path.Combine(userProfile, ".codex"));
    }

    private void SettingsDialogClosed(object? sender, FormClosedEventArgs eventArgs)
    {
        if (sender is not SettingsForm dialog) return;
        _settingsDialog = null;
        try
        {
            if (_quitting || dialog.DialogResult != DialogResult.OK || dialog.ResultSettings is null) return;
            var nextSettings = dialog.ResultSettings;
            nextSettings.CopyPlacementStateFrom(_settings);
            nextSettings.CopyMiniAreaLayoutsFrom(_settings);
            if (!TrySaveSettings(nextSettings, true)) return;
            var localeOnlyChange = IsLocaleOnlyChange(_settings, nextSettings);
            ApplySettingsSnapshot(nextSettings);
            _ = SyncPluginEnablementAsync();
            if (!localeOnlyChange)
            {
                _ = RefreshAsync();
                if (_settings.EnableRadarAlerts) _ = RefreshRadarAsync();
            }
        }
        finally
        {
            dialog.Dispose();
        }
    }

    private void SavePlacement(WindowPlacementCommit commit)
    {
        if (_quitting) return;
        var previous = new AppSettings();
        previous.CopyPlacementStateFrom(_settings);
        _settings.PlacementSchemaVersion = AppSettings.CurrentPlacementSchemaVersion;
        _settings.PlacementProfiles[commit.TopologyKey] = commit.Profile.Copy();
        _settings.TaskbarDocked = commit.Profile.IsDocked;
        if (commit.DockedMonitorName is not null && commit.DockedPosition is { } position)
        {
            _settings.TaskbarMonitor = commit.DockedMonitorName;
            _settings.TaskbarPosition = Math.Clamp(position, 0, 1);
            _settings.TaskbarPositions = new Dictionary<string, double>(
                commit.LegacyTaskbarPositions,
                StringComparer.OrdinalIgnoreCase);
        }
        if (commit.FloatingLocation is { } floating)
        {
            _settings.WindowX = floating.X;
            _settings.WindowY = floating.Y;
        }
        if (!TrySaveSettings(_settings))
        {
            _settings.CopyPlacementStateFrom(previous);
        }
    }

    private Task SyncPluginEnablementAsync() =>
        SyncPluginEnablementAsync(suppressErrors: true, _shutdown.Token);

    private async Task SyncPluginEnablementAsync(
        bool suppressErrors,
        CancellationToken cancellationToken)
    {
        try
        {
            var statuses = _pluginHost.ListPlugins();
            var changes = statuses
                .Where(plugin => !plugin.Manifest.Required)
                .Where(plugin => plugin.Enabled != _settings.IsPluginEnabled(
                    plugin.Manifest.Id,
                    plugin.Manifest.DefaultEnabled))
                .ToArray();
            foreach (var plugin in changes
                         .Where(plugin => !Desired(plugin))
                         .OrderByDescending(plugin => plugin.Manifest.Order))
            {
                await SetPluginEnabledFromSettingsAsync(
                    plugin.Manifest.Id,
                    false,
                    cancellationToken);
            }
            foreach (var plugin in changes
                         .Where(Desired)
                         .OrderBy(plugin => plugin.Manifest.Order))
            {
                await SetPluginEnabledFromSettingsAsync(
                    plugin.Manifest.Id,
                    true,
                    cancellationToken);
            }
        }
        catch when (suppressErrors)
        {
            // The saved setting remains authoritative; Host health exposes isolated failures.
        }

        bool Desired(PluginStatus plugin) => _settings.IsPluginEnabled(
            plugin.Manifest.Id,
            plugin.Manifest.DefaultEnabled);
    }

    private async Task SetPluginEnabledFromSettingsAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var revision = _pluginHost.Describe().Revisions.ConfigRevision;
        await _pluginHost.SetEnabledAsync(pluginId, enabled, revision, cancellationToken);
    }

    private void ApplySettingsSnapshot(AppSettings settings)
    {
        _settings = settings;
        _text = NativeText.For(_settings.Locale);
        if (_allowGlobalStartupRegistration)
        {
            StartupManager.Apply(_settings.OpenAtLogin);
        }
        ApplyRefreshInterval();
        _bar.ApplySettings(_settings);
        UpdateProviderActivity(requestRefresh: false);
        _bar.SetQuotaPaceEstimates(QuotaPaceEstimates(_snapshot, DateTimeOffset.UtcNow));
        _bar.SetRadarProviders(_settings.EnableRadar ? [_radarService.Provider] : []);
        ApplyRadarInterval();
        ApplyClockInterval();
        ApplyText();
        _radarMenuItem.Visible = _settings.EnableRadar;
    }

    private void SaveMiniAreaLayout()
    {
        if (_quitting) return;
        var previous = AppSettings.CopyMiniAreaLayouts(_settings.MiniAreaLayouts);
        _settings.MiniAreaLayouts = AppSettings.CopyMiniAreaLayouts(_bar.MiniAreaLayouts);
        if (!TrySaveSettings(_settings))
        {
            _settings.MiniAreaLayouts = previous;
            _bar.SetMiniAreaLayouts(previous, preserveAnchor: true);
        }
        else if (!MiniAreaLayoutsEqual(previous, _settings.MiniAreaLayouts))
        {
            _uiRevision++;
        }
    }

    private void SaveMiniAreaOrder()
    {
        if (_quitting) return;
        var previous = AppSettings.CopyMiniAreaOrder(_settings.MiniAreaOrder);
        _settings.MiniAreaOrder = AppSettings.CopyMiniAreaOrder(_bar.MiniAreaOrder);
        if (!TrySaveSettings(_settings))
        {
            _settings.MiniAreaOrder = previous;
            _bar.SetMiniAreaOrder(previous, preserveAnchor: true);
        }
        else if (!previous.SequenceEqual(_settings.MiniAreaOrder, StringComparer.Ordinal))
        {
            _uiRevision++;
        }
    }

    private bool TrySaveSettings(AppSettings settings, bool showError = false)
    {
        try
        {
            _store.Save(settings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (showError && !_bar.IsDisposed)
            {
                MessageBox.Show(
                    _bar,
                    _text.SettingsSaveFailed,
                    _text.SettingsNotSaved,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return false;
        }
    }

    private static void OpenRadarWebsite()
    {
        Process.Start(new ProcessStartInfo(RadarService.SiteUrl) { UseShellExecute = true });
    }

    private void RequestRadarPreview(ProviderKind provider, string surfaceId)
    {
        if (!_settings.EnableRadar
            || provider != _radarService.Provider
            || !RadarSurfaceIds.BuiltIn.Contains(surfaceId, StringComparer.Ordinal)) return;
        if (RadarAlertTracker.HasUnread(_radarState, _radarViewState.Snapshot, surfaceId))
        {
            RadarAlertTracker.RecordViewed(_radarState, _radarViewState.Snapshot, surfaceId);
            _radarViewState = WithRadarUnreadState(_radarViewState, _radarState);
            try
            {
                _store.SaveRadarState(_radarState);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Viewing the Radar must still work if unread state cannot be persisted.
            }
        }
        _bar.SetRadarState(_radarViewState);
        if (_radarViewState.IsStale(DateTimeOffset.UtcNow)) _ = RefreshRadarAsync();
    }

    private async Task RefreshRadarAsync()
    {
        if (!_settings.EnableRadar || !await _radarGate.WaitAsync(0)) return;
        var stateBeforeFetch = _radarState;
        _radarViewState = _radarViewState with { Loading = true };
        if (!_bar.IsDisposed) _bar.SetRadarState(_radarViewState);
        try
        {
            var next = await _radarService.FetchAsync(_shutdown.Token);
            if (_shutdown.IsCancellationRequested || _bar.IsDisposed || !_settings.EnableRadar) return;
            var decision = _radarService.Evaluate(_radarState, next);
            _radarState = RadarAlertTracker.RecordFetch(_radarState, next);
            if (_settings.EnableRadarAlerts)
            {
                if (decision.ShouldSeedBaseline)
                {
                    _radarState = RadarAlertTracker.RecordBaseline(_radarState, next, DateTimeOffset.UtcNow);
                }
                else if (decision.ShouldNotify && ShowRadarAlert(next, decision))
                {
                    _radarState = RadarAlertTracker.RecordNotification(
                        _radarState,
                        next,
                        DateTimeOffset.UtcNow);
                }
            }
            if (_bar.VisibleRadarSurfaceId is { } visibleSurfaceId)
            {
                RadarAlertTracker.RecordViewed(_radarState, next, visibleSurfaceId);
            }
            _store.SaveRadarState(_radarState);
            _radarViewState = WithRadarUnreadState(
                new(next, next.CapturedAt, false, null),
                _radarState);
            _bar.SetRadarState(_radarViewState);
            if (HasPlugin("zgstokenbar.intelligence.radar"))
            {
                _pluginHost.Publish(CorePluginProjection.Radar("zgstokenbar.intelligence.radar", next));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or System.Text.Json.JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or TaskCanceledException)
        {
            if (exception is IOException or UnauthorizedAccessException) _radarState = stateBeforeFetch;
            _radarViewState = _radarViewState with
            {
                Loading = false,
                Error = RadarError(exception),
            };
            if (!_bar.IsDisposed) _bar.SetRadarState(_radarViewState);
        }
        finally
        {
            if (!_bar.IsDisposed && _radarViewState.Loading)
            {
                _radarViewState = _radarViewState with { Loading = false };
                _bar.SetRadarState(_radarViewState);
            }
            _radarGate.Release();
        }
    }

    private static RadarViewState WithRadarUnreadState(
        RadarViewState viewState,
        RadarAlertState alertState)
    {
        var unreadSurfaceIds = RadarSurfaceIds.BuiltIn
            .Where(surfaceId => RadarAlertTracker.HasUnread(
                alertState,
                viewState.Snapshot,
                surfaceId))
            .ToHashSet(StringComparer.Ordinal);
        return viewState with
        {
            HasUnread = unreadSurfaceIds.Count > 0,
            UnreadSurfaceIds = unreadSurfaceIds,
        };
    }

    private void ApplyRefreshInterval()
    {
        _refreshTimer.Interval = Math.Clamp(_settings.RefreshMinutes, 1, 60) * 60_000;
        _refreshTimer.Enabled = true;
    }

    private void UpdateProviderActivity(bool requestRefresh)
    {
        if (_quitting || _bar.IsDisposed) return;

        var nextAccounts = CockpitCodexAccountDirectory.Read();
        if (!AccountsEqual(_codexAccounts, nextAccounts))
        {
            _codexAccounts = nextAccounts;
            _bar.SetCodexAccounts(_codexAccounts);
        }

        var nextProviders = ActiveProviders(_settings);
        if (nextProviders.SetEquals(_activeProviders)) return;

        var codexWasActive = _activeProviders.Contains(ProviderKind.Codex);
        _activeProviders = nextProviders;
        _bar.SetActiveProviders(_activeProviders);
        var refreshRequested = ApplyCodexUsageActivityTransition(
            codexWasActive,
            _activeProviders.Contains(ProviderKind.Codex),
            _cachedCodexTokenUsage,
            requestRefresh,
            _bar.SetCodexTokenUsage,
            (summary, cached) => _pluginHost.Publish(CorePluginProjection.CodexUsage(
                "zgstokenbar.usage.codex-local",
                summary,
                DateTimeOffset.UtcNow,
                cached)),
            () => _ = RefreshAsync(forceProviderRefresh: true));
        if (requestRefresh && !refreshRequested)
        {
            _ = RefreshAsync(forceProviderRefresh: true);
        }
    }

    internal static bool ApplyCodexUsageActivityTransition(
        bool wasActive,
        bool isActive,
        CodexTokenUsageSummary? cachedSummary,
        bool requestRefresh,
        Action<CodexTokenUsageSummary?> setUsage,
        Action<CodexTokenUsageSummary?, bool> publishUsage,
        Action requestRefreshAction)
    {
        if (wasActive == isActive) return false;
        if (isActive)
        {
            setUsage(cachedSummary);
            publishUsage(cachedSummary, cachedSummary is not null);
        }
        else
        {
            setUsage(null);
        }
        if (!requestRefresh) return false;
        requestRefreshAction();
        return true;
    }

    private static HashSet<ProviderKind> ActiveProviders(AppSettings settings)
    {
        var active = ProviderProcessActivity.DetectActiveProviders()
            .Where(settings.IsEnabled)
            .ToHashSet();
        return active;
    }

    private static bool AccountsEqual(
        IReadOnlyList<CodexAccountInfo> left,
        IReadOnlyList<CodexAccountInfo> right) =>
        left.SequenceEqual(right);

    private static QuotaSnapshot MergeActiveProviderResults(
        QuotaSnapshot previous,
        QuotaSnapshot activeResult,
        IReadOnlySet<ProviderKind> activeProviders,
        DateTimeOffset capturedAt)
    {
        var cards = previous.Cards
            .Where(card => !activeProviders.Contains(card.Provider))
            .Concat(activeResult.Cards)
            .ToArray();
        var health = previous.Health
            .Where(item => !activeProviders.Contains(item.Provider))
            .Concat(activeResult.Health)
            .ToArray();
        return new QuotaSnapshot(cards, health, capturedAt)
        {
            CodexAccounts = activeProviders.Contains(ProviderKind.Codex)
                ? activeResult.CodexAccounts.Count > 0
                    ? activeResult.CodexAccounts
                    : previous.CodexAccounts
                : previous.CodexAccounts,
            CodexQuotaTokenCounters = activeProviders.Contains(ProviderKind.Codex)
                ? activeResult.CodexQuotaTokenCounters
                : previous.CodexQuotaTokenCounters,
        };
    }

    private IReadOnlyDictionary<string, QuotaPaceEstimate> QuotaPaceEstimates(
        QuotaSnapshot snapshot,
        DateTimeOffset now)
    {
        var estimates = new Dictionary<string, QuotaPaceEstimate>(StringComparer.Ordinal);
        foreach (var card in snapshot.Cards)
        {
            foreach (var window in card.Windows)
            {
                estimates[QuotaPaceTracker.SeriesKey(card, window)] =
                    _quotaPaceTracker.Estimate(card, window, now, _settings.RefreshMinutes);
            }
        }
        return estimates;
    }

    private void ScheduleCodexRolloutImport(QuotaSnapshot snapshot, DateTimeOffset observedAt)
    {
        if (_quitting || _shutdown.IsCancellationRequested) return;
        var signature = FreshCodexCardSignature(snapshot, observedAt);
        if (signature is null) return;
        if (_rolloutImportRunning)
        {
            _pendingRolloutImport = string.Equals(
                signature,
                _rolloutImportSignature,
                StringComparison.Ordinal)
                ? null
                : (snapshot, observedAt, signature);
            return;
        }
        if (string.Equals(signature, _rolloutImportSignature, StringComparison.Ordinal)) return;

        _rolloutImportRunning = true;
        _rolloutImportSignature = signature;
        var task = Task.Run(
            () => CodexRolloutQuotaImporter.Import(snapshot, observedAt, _shutdown.Token),
            _shutdown.Token);
        _ = task.ContinueWith(
            completed =>
            {
                if (_bar.IsDisposed) return;
                try
                {
                    _bar.BeginInvoke(() => CompleteCodexRolloutImport(completed));
                }
                catch (InvalidOperationException)
                {
                    // Shutdown can dispose the window between the guard and BeginInvoke.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteCodexRolloutImport(Task<CodexRolloutImportResult> completed)
    {
        _rolloutImportRunning = false;
        if (completed.IsFaulted) _ = completed.Exception;
        if (!_quitting
            && !_shutdown.IsCancellationRequested
            && completed.Status == TaskStatus.RanToCompletion)
        {
            var now = DateTimeOffset.UtcNow;
            if (_quotaPaceTracker.MergeImported(completed.Result.Samples, now))
            {
                try
                {
                    _store.SaveQuotaRateHistory(_quotaPaceTracker.Export(now));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Imported history is best-effort and never blocks live quota.
                }
                _bar.SetQuotaPaceEstimates(QuotaPaceEstimates(_snapshot, now));
            }

            var rolloutObservations = completed.Result.Observations
                .Where(observation =>
                    !CodexQuotaTokenTracker.IsRolloutFallbackSource(observation.SourceKey))
                .ToArray();
            var eligibleSeries = rolloutObservations
                .Where(observation =>
                    _codexQuotaTokenTracker.IsRolloutFallbackEligible(observation, now))
                .Select(observation => new CodexQuotaTokenSeriesKey(
                    observation.CardKey,
                    observation.WindowLabel,
                    observation.DurationTicks).ToString())
                .ToHashSet(StringComparer.Ordinal);
            var fallbackObservations = rolloutObservations
                .Where(observation => eligibleSeries.Contains(
                    new CodexQuotaTokenSeriesKey(
                        observation.CardKey,
                        observation.WindowLabel,
                        observation.DurationTicks).ToString()))
                .Where(observation =>
                    _codexQuotaTokenTracker.IsRolloutFallbackReplayObservation(observation, now))
                .Select(observation => observation with
                {
                    SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(
                        observation.SourceKey),
                })
                .ToArray();
            if (_codexQuotaTokenTracker.Merge(fallbackObservations, now))
            {
                try
                {
                    _store.SaveCodexQuotaTokenHistory(_codexQuotaTokenTracker.Export());
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Imported capacity is best-effort and never blocks live quota or pace data.
                }
                _bar.SetCodexQuotaTokenSummaries(CodexQuotaTokenSummaries(now));
            }
        }

        var pending = _pendingRolloutImport;
        _pendingRolloutImport = null;
        if (pending is not null)
        {
            ScheduleCodexRolloutImport(pending.Value.Snapshot, pending.Value.ObservedAt);
        }
    }

    private async Task RefreshCodexTokenUsageAsync(DateTimeOffset observedAt)
    {
        if (!await _codexTokenUsageGate.WaitAsync(0)) return;
        try
        {
            var result = await Task.Run(
                () => _codexTokenUsageReader.Refresh(observedAt, _shutdown.Token),
                _shutdown.Token);
            var summary = CodexTokenUsageSummary.ApplyCumulativeFloor(
                result.Summary,
                _codexQuotaTokenTracker.GetProfileLifetimeTotal(),
                observedAt);
            _cachedCodexTokenUsage = summary;
            if (result.Changed)
            {
                try
                {
                    await Task.Run(
                        () => _store.SaveCodexTokenUsageIndex(result.Index),
                        _shutdown.Token);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The in-memory summary remains useful when its incremental index cannot be saved.
                }
            }
            if (!_shutdown.IsCancellationRequested
                && !_bar.IsDisposed
                && _activeProviders.Contains(ProviderKind.Codex))
            {
                _bar.SetCodexTokenUsage(summary);
                _pluginHost.Publish(CorePluginProjection.CodexUsage(
                    "zgstokenbar.usage.codex-local",
                    summary,
                    observedAt));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch
        {
            // Local token aggregation is best-effort and never blocks live quota.
        }
        finally
        {
            _codexTokenUsageGate.Release();
        }
    }

    private static string? FreshCodexCardSignature(
        QuotaSnapshot snapshot,
        DateTimeOffset observedAt)
    {
        var freshHealth = snapshot.Health.Any(health =>
            health.Provider == ProviderKind.Codex
            && health.Connected
            && health.Code is ProviderHealthCode.Current or ProviderHealthCode.Unknown);
        if (!freshHealth) return null;
        var keys = snapshot.Cards
            .Where(card => card.Provider == ProviderKind.Codex)
            .Where(card => card.CapturedAt is { } capturedAt
                && QuotaPaceTracker.IsFreshCapture(capturedAt, snapshot.CapturedAt, observedAt))
            .Select(card => string.Join(
                '\u0001',
                card.Key,
                card.CapturedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "-",
                string.Join(
                    '\u0002',
                    card.Windows
                        .OrderBy(window => window.Duration)
                        .Select(window => string.Join(
                            ':',
                            window.Label,
                            window.Duration.Ticks.ToString(CultureInfo.InvariantCulture),
                            window.UsedPercent?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
                            window.ResetsAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "-")))))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        return keys.Length == 0 ? null : string.Join('\0', keys);
    }

    private IReadOnlyDictionary<string, CodexQuotaTokenSummary> CodexQuotaTokenSummaries(
        DateTimeOffset now)
    {
        var recentWeeklyByCard = _snapshot.CodexQuotaTokenCounters
            .Where(counter => counter.RecentWeeklyAverageTokens is not null)
            .GroupBy(counter => counter.CardKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().RecentWeeklyAverageTokens,
                StringComparer.Ordinal);
        return _codexQuotaTokenTracker.ExportSummaries(now)
            .Select(summary => summary with
            {
                RecentWeeklyAverageTokens = recentWeeklyByCard.TryGetValue(
                    summary.CardKey,
                    out var recentWeeklyAverageTokens)
                        ? recentWeeklyAverageTokens
                        : null,
            })
            .ToDictionary(
                summary => new CodexQuotaTokenSeriesKey(
                    summary.CardKey,
                    summary.WindowLabel,
                    summary.DurationTicks).ToString(),
                summary => summary,
                StringComparer.Ordinal);
    }

    internal static IReadOnlyList<CodexQuotaTokenObservation> CodexQuotaTokenObservations(
        QuotaSnapshot snapshot,
        DateTimeOffset observedAt)
    {
        if (!snapshot.Health.Any(health =>
                health.Provider == ProviderKind.Codex
                && health.Connected
                && health.Code is ProviderHealthCode.Current or ProviderHealthCode.Unknown))
        {
            return [];
        }

        var cards = snapshot.Cards
            .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
            .ToDictionary(card => card.Key, StringComparer.Ordinal);
        var observations = new List<CodexQuotaTokenObservation>();
        foreach (var counter in snapshot.CodexQuotaTokenCounters)
        {
            if (counter.LifetimeTokens < 0
                || !QuotaPaceTracker.IsFreshCapture(counter.CapturedAt, snapshot.CapturedAt, observedAt)
                || !cards.TryGetValue(counter.CardKey, out var card)
                || card.CapturedAt is not { } cardCapturedAt
                || !QuotaPaceTracker.IsFreshCapture(cardCapturedAt, snapshot.CapturedAt, observedAt))
            {
                continue;
            }

            foreach (var window in card.Windows)
            {
                if (window.Duration != TimeSpan.FromHours(5)
                    && window.Duration != TimeSpan.FromDays(7))
                {
                    continue;
                }

                if (window.UsedPercent is not { } used
                    || !double.IsFinite(used)
                    || used is < 0 or > 100
                    || window.ResetsAt is not { } reset
                    || window.Duration <= TimeSpan.Zero)
                {
                    continue;
                }

                observations.Add(new CodexQuotaTokenObservation(
                    counter.CardKey,
                    window.Label,
                    window.Duration.Ticks,
                    counter.CapturedAt,
                    used,
                    reset,
                    CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                    counter.LifetimeTokens));
            }
        }

        return observations;
    }

    private void ApplyRadarInterval()
    {
        _radarTimer.Enabled = _settings.EnableRadar && _settings.EnableRadarAlerts;
    }

    private void ApplyText()
    {
        _refreshMenuItem.Text = _text.RefreshNow;
        _radarMenuItem.Text = _text.OpenRadarWebsite;
        if (_availableUpdate is { } update) _updateMenuItem.Text = _text.UpdateTo(update.Version);
        _settingsMenuItem.Text = _text.Settings;
        _quitMenuItem.Text = _text.Quit;
        _tray.Text = _text.TrayText;
    }

    internal static bool IsLocaleOnlyChange(AppSettings previous, AppSettings next)
    {
        if (string.Equals(previous.Locale, next.Locale, StringComparison.Ordinal)) return false;
        return previous.EnabledProviders.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(next.EnabledProviders)
            && previous.RefreshMinutes == next.RefreshMinutes
            && previous.AutoRefreshClaudeOAuth == next.AutoRefreshClaudeOAuth
            && previous.OpenAtLogin == next.OpenAtLogin
            && previous.EnableAlerts == next.EnableAlerts
            && previous.TaskbarDocked == next.TaskbarDocked
            && previous.EnableAnimations == next.EnableAnimations
            && previous.EnableRadar == next.EnableRadar
            && previous.EnableRadarAlerts == next.EnableRadarAlerts
            && previous.EnableCodexEconomyBar == next.EnableCodexEconomyBar
            && previous.EnableAiGatewayBalance == next.EnableAiGatewayBalance
            && previous.EnableSub2ApiPool == next.EnableSub2ApiPool
            && PluginEnabledEqual(previous.PluginEnabled, next.PluginEnabled)
            && MiniAreaLayoutsEqual(previous.MiniAreaLayouts, next.MiniAreaLayouts)
            && previous.MiniAreaOrder.SequenceEqual(next.MiniAreaOrder, StringComparer.Ordinal)
            && string.Equals(previous.CodexMiniDisplayMode, next.CodexMiniDisplayMode, StringComparison.Ordinal)
            && previous.WindowX == next.WindowX
            && previous.WindowY == next.WindowY
            && previous.TaskbarPosition == next.TaskbarPosition
            && string.Equals(previous.TaskbarMonitor, next.TaskbarMonitor, StringComparison.Ordinal)
            && TaskbarPositionsEqual(previous.TaskbarPositions, next.TaskbarPositions)
            && previous.PlacementSchemaVersion == next.PlacementSchemaVersion
            && PlacementMigrationSeedsEqual(previous.PlacementMigrationSeed, next.PlacementMigrationSeed)
            && PlacementProfilesEqual(previous.PlacementProfiles, next.PlacementProfiles);
    }

    private static bool PluginEnabledEqual(
        IReadOnlyDictionary<string, bool> left,
        IReadOnlyDictionary<string, bool> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

    private static bool TaskbarPositionsEqual(
        IReadOnlyDictionary<string, double>? left,
        IReadOnlyDictionary<string, double>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var value) || value != entry.Value) return false;
        }
        return true;
    }

    private static bool MiniAreaLayoutsEqual(
        IReadOnlyDictionary<string, MiniAreaLayout> left,
        IReadOnlyDictionary<string, MiniAreaLayout> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);

    private static bool PlacementMigrationSeedsEqual(
        PlacementMigrationSeed? left,
        PlacementMigrationSeed? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.TaskbarDocked == right.TaskbarDocked
            && left.WindowX == right.WindowX
            && left.WindowY == right.WindowY
            && left.TaskbarPosition == right.TaskbarPosition
            && string.Equals(left.TaskbarMonitor, right.TaskbarMonitor, StringComparison.Ordinal)
            && TaskbarPositionsEqual(left.TaskbarPositions, right.TaskbarPositions);
    }

    private static bool PlacementProfilesEqual(
        IReadOnlyDictionary<string, WindowPlacementProfile>? left,
        IReadOnlyDictionary<string, WindowPlacementProfile>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var profile)
                || entry.Value.IsDocked != profile.IsDocked
                || !string.Equals(entry.Value.DockedMonitorKey, profile.DockedMonitorKey, StringComparison.Ordinal)
                || !string.Equals(entry.Value.FloatingMonitorKey, profile.FloatingMonitorKey, StringComparison.Ordinal)
                || entry.Value.FloatingX != profile.FloatingX
                || entry.Value.FloatingY != profile.FloatingY
                || !TaskbarPositionsEqual(entry.Value.TaskbarPositions, profile.TaskbarPositions))
            {
                return false;
            }
        }
        return true;
    }

    private void ApplyClockInterval()
    {
        _clockTimer.Interval = _bar.IsTaskbarMode ? 1_000 : 30_000;
    }

    private async Task RefreshSystemUsageOverviewAsync()
    {
        if (_quitting
            || !_bar.IsTaskbarMode
            || !_settings.IsPluginEnabled("zgstokenbar.metrics.system", true)) return;
        var generation = Interlocked.Increment(ref _systemUsageGeneration);
        try
        {
            var snapshot = await SystemUsageSampling.TrySampleAsync(
                _systemUsageGate,
                includeProcesses => _systemUsageSampler.Sample(includeProcesses),
                includeProcesses: false,
                _shutdown.Token);
            if (snapshot is not null
                && generation == Volatile.Read(ref _systemUsageGeneration)
                && !_quitting
                && !_bar.IsDisposed)
            {
                _bar.SetSystemUsage(snapshot);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshSystemUsageDetailsAsync()
    {
        if (_quitting
            || !_bar.IsTaskbarMode
            || !_settings.IsPluginEnabled("zgstokenbar.metrics.system", true)
            || !_bar.WantsSystemUsageDetails) return;
        var generation = Interlocked.Increment(ref _systemUsageGeneration);
        try
        {
            var snapshot = await SystemUsageSampling.TrySampleAsync(
                _systemUsageGate,
                includeProcesses => _systemUsageSampler.Sample(includeProcesses),
                includeProcesses: true,
                _shutdown.Token);
            if (snapshot is not null
                && generation == Volatile.Read(ref _systemUsageGeneration)
                && !_quitting
                && !_bar.IsDisposed
                && _bar.WantsSystemUsageDetails)
            {
                _bar.SetSystemUsage(snapshot);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void EnsureVisible()
    {
        if (_bar.IsDisposed) return;
        if (_bar.IsTaskbarDocked)
        {
            _bar.SyncTaskbarPlacement();
            return;
        }
        _bar.ClampToVisibleScreen();
        if (!_bar.Visible) _bar.Show();
        _bar.BringToFront();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!await _updateGate.WaitAsync(0)) return;
        try
        {
            if (!Version.TryParse(ProductVersion(), out var currentVersion)) return;
            var update = await _updateChecker.CheckAsync(currentVersion, _shutdown.Token);
            if (update is null || _shutdown.IsCancellationRequested || _bar.IsDisposed) return;
            _availableUpdate = update;
            _updateMenuItem.Text = _text.UpdateTo(update.Version);
            _updateMenuItem.Visible = true;
            if (_notifiedUpdateVersion == update.Version || _quitting) return;
            _notifiedUpdateVersion = update.Version;
            _radarBalloonActive = false;
            _updateBalloonActive = true;
            _tray.ShowBalloonTip(
                8_000,
                _text.UpdateAvailableTitle(update.Version),
                _text.UpdateAvailableBody,
                ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or InvalidDataException
            or System.Text.Json.JsonException
            or InvalidOperationException)
        {
            // Update discovery is best-effort and must not disturb the quota bar.
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private void OpenUpdatePage()
    {
        if (_availableUpdate is not { } update) return;
        Process.Start(new ProcessStartInfo(update.PageUri.AbsoluteUri) { UseShellExecute = true });
    }

    private void ShowMilestoneAlerts(IReadOnlyList<QuotaMilestoneAlert> alerts)
    {
        if (alerts.Count == 0 || _quitting) return;
        _radarBalloonActive = false;
        _updateBalloonActive = false;
        var now = DateTimeOffset.UtcNow;
        var warning = alerts.Any(alert => alert.Threshold >= 90);
        var title = alerts.Count == 1
            ? _text.QuotaMilestoneTitle(
                alerts[0].CardLabel,
                alerts[0].WindowLabel,
                alerts[0].Threshold)
            : _text.QuotaMilestonesTitle(alerts.Count);
        var body = alerts.Count == 1
            ? AlertDetail(alerts[0], now, true)
            : string.Join(Environment.NewLine, alerts.Take(3).Select(alert =>
                $"{alert.CardLabel} {_text.WindowLimit(alert.WindowLabel)}: {AlertDetail(alert, now, false)}"));
        if (alerts.Count > 3) body += $"{Environment.NewLine}{_text.MoreAlerts(alerts.Count - 3)}";
        title = NativeText.TruncateTextElements(title, 60);
        body = NativeText.TruncateTextElements(body, 250);

        try
        {
            _tray.ShowBalloonTip(8_000, title, body, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (InvalidOperationException)
        {
            // The notification area can disappear while Windows is shutting down.
        }
    }

    private bool ShowRadarAlert(ProviderRadarSnapshot snapshot, RadarAlertDecision decision)
    {
        if (_quitting) return false;
        var primary = snapshot.Primary;
        var title = $"Codex Radar · {primary.Model} {primary.ReasoningEffort} · {_text.RadarStatusLabel(primary.Status)}";
        var changes = decision.Changes.Count == 0
            ? _text.RadarPrimaryChanged
            : string.Join("; ", decision.Changes.Select(_text.RadarChange));
        var body = $"{changes} · IQ {primary.Score?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a"}";
        if (snapshot.SourceUpdatedAt is { } sourceAt)
        {
            body += $" · {_text.RadarSourceUpdated(sourceAt)}";
        }
        title = NativeText.TruncateTextElements(title, 60);
        body = NativeText.TruncateTextElements(body, 250);
        var previousScore = _radarState.LastNotifiedModelIqSnapshot?.Score;
        var warning = primary.Status is "yellow" or "red"
            || previousScore is { } oldScore && primary.Score is { } newScore && newScore < oldScore;
        try
        {
            _updateBalloonActive = false;
            _radarBalloonActive = true;
            _tray.ShowBalloonTip(
                8_000,
                title,
                body,
                warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            return true;
        }
        catch (InvalidOperationException)
        {
            _radarBalloonActive = false;
            return false;
        }
    }

    private void ShowRadarTestNotification()
    {
        if (_quitting) return;
        _updateBalloonActive = false;
        _radarBalloonActive = true;
        try
        {
            _tray.ShowBalloonTip(
                8_000,
                _text.RadarTestTitle,
                _text.RadarTestBody,
                ToolTipIcon.Info);
        }
        catch (InvalidOperationException)
        {
            _radarBalloonActive = false;
        }
    }

    private static RadarErrorCode RadarError(Exception exception) => exception switch
    {
        TaskCanceledException => RadarErrorCode.Timeout,
        System.Text.Json.JsonException => RadarErrorCode.SchemaChanged,
        IOException or UnauthorizedAccessException => RadarErrorCode.StateSaveFailed,
        _ => RadarErrorCode.Unavailable,
    };

    private string AlertDetail(QuotaMilestoneAlert alert, DateTimeOffset now, bool includeCurrent)
    {
        var used = (int)Math.Round(alert.UsedPercent);
        var remaining = Math.Max(0, 100 - used);
        return _text.QuotaMilestoneDetail(
            alert.Threshold,
            used,
            remaining,
            alert.ResetsAt,
            now,
            includeCurrent);
    }

    private void PublishQuotaPlugins(QuotaSnapshot snapshot)
    {
        PublishProviderPlugin(
            snapshot,
            ProviderKind.Claude,
            "zgstokenbar.provider.claude",
            "claude",
            "provider.claude.icon",
            "accent.claude");
        PublishProviderPlugin(
            snapshot,
            ProviderKind.Codex,
            "zgstokenbar.provider.codex",
            "codex",
            "provider.codex.icon",
            "accent.codex");
    }

    private async Task RefreshGenericPluginsAsync()
    {
        foreach (var plugin in _pluginHost.ListPlugins().Where(plugin =>
                     plugin.Enabled && !LegacyRenderedPluginIds.Contains(plugin.Manifest.Id)))
        {
            try
            {
                await _pluginHost.RefreshPluginAsync(
                    plugin.Manifest.Id,
                    "scheduler",
                    _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Process plugin failures are isolated by the Host.
            }
        }
    }

    private async Task WatchPluginEventsAsync()
    {
        try
        {
            await foreach (var hostEvent in _pluginSubscription.Events.ReadAllAsync(_shutdown.Token))
            {
                if (_bar.IsDisposed) return;
                if (!IsGenericPluginEvent(hostEvent)) continue;
                if (!QueueGenericPluginCardsUpdate()) return;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (HostCommandException exception) when (exception.Code == "event_backpressure")
        {
            if (!_bar.IsDisposed)
            {
                QueueGenericPluginCardsUpdate();
            }
        }
    }

    private bool QueueGenericPluginCardsUpdate()
    {
        if (Interlocked.Exchange(ref _genericCardsUpdateQueued, 1) != 0) return true;
        try
        {
            _bar.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _genericCardsUpdateQueued, 0);
                ApplyGenericPluginCards();
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _genericCardsUpdateQueued, 0);
            return false;
        }
    }

    private static bool IsGenericPluginEvent(HostEvent hostEvent)
    {
        if (hostEvent.Type is not ("plugin.data.changed" or "plugin.config.changed")) return false;
        return hostEvent.Payload.TryGetProperty("value", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
            && value.GetString() is { } pluginId
            && !LegacyRenderedPluginIds.Contains(pluginId);
    }

    private void ApplyGenericPluginCards()
    {
        if (_bar.IsDisposed) return;
        var genericPlugins = _pluginHost.ListPlugins()
            .Where(plugin => plugin.Enabled && !LegacyRenderedPluginIds.Contains(plugin.Manifest.Id))
            .ToDictionary(plugin => plugin.Manifest.Id, StringComparer.Ordinal);
        var assets = genericPlugins.ToDictionary(
            pair => pair.Key,
            pair => PluginAssetsFor(pair.Key),
            StringComparer.Ordinal);
        var cards = _pluginHost.Snapshot(includeValues: true).Plugins
            .Where(plugin => genericPlugins.ContainsKey(plugin.PluginId))
            .SelectMany(plugin =>
                (plugin.Cards ?? []).Select(card => new PluginMiniCardView(
                    plugin.PluginId,
                    assets[plugin.PluginId].Text.TryGetValue(card.TitleKey, out var title)
                        ? title
                        : genericPlugins[plugin.PluginId].Manifest.DisplayName ?? plugin.PluginId,
                    card,
                    assets[plugin.PluginId].Text,
                    assets[plugin.PluginId].Icon)))
            .ToArray();
        _bar.SetPluginMiniCards(cards);
    }

    private PluginAssets PluginAssetsFor(string pluginId)
    {
        var key = (pluginId, _settings.Locale);
        if (_pluginAssets.TryGetValue(key, out var assets)) return assets;
        try
        {
            assets = new PluginAssets(
                _pluginHost.GetPluginIconPng(pluginId),
                _pluginHost.GetPluginLocalization(pluginId, _settings.Locale));
        }
        catch (HostCommandException)
        {
            assets = new PluginAssets(
                null,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }
        _pluginAssets[key] = assets;
        return assets;
    }

    private void PublishProviderPlugin(
        QuotaSnapshot snapshot,
        ProviderKind provider,
        string pluginId,
        string groupId,
        string iconKey,
        string accentToken)
    {
        if (!HasPlugin(pluginId)) return;

        var health = snapshot.Health.FirstOrDefault(item => item.Provider == provider);
        if (health is null) return;
        var result = new ProviderResult(
            provider,
            snapshot.Cards.Where(card => card.Provider == provider).ToArray(),
            health)
        {
            CodexAccounts = provider == ProviderKind.Codex ? snapshot.CodexAccounts : [],
        };
        _pluginHost.Publish(CorePluginProjection.Provider(
            pluginId,
            groupId,
            iconKey,
            accentToken,
            result));
    }

    private bool HasPlugin(string pluginId) => _pluginHost.DescribePlugin(pluginId) is not null;

    public ValueTask PersistPluginEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () =>
            {
                var previous = _settings.IsPluginEnabled(pluginId);
                _settings.SetPluginEnabled(pluginId, enabled);
                if (!TrySaveSettings(_settings))
                {
                    _settings.SetPluginEnabled(pluginId, previous);
                    throw new IOException("Plugin setting could not be saved.");
                }
                _activeProviders = ActiveProviders(_settings);
                _bar.ApplySettings(_settings);
                _bar.SetActiveProviders(_activeProviders);
                _bar.SetRadarProviders(_settings.EnableRadar ? [_radarService.Provider] : []);
                _radarMenuItem.Visible = _settings.EnableRadar;
                ApplyRadarInterval();
                _ = RefreshAsync(userInitiated: true);
                return true;
            },
            cancellationToken));

    public ValueTask<MiniState> GetMiniStateAsync(CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () => new MiniState(
                _bar.AreAllMiniAreasCollapsed,
                _bar.IsTaskbarDocked,
                ToUiBounds(_bar.Bounds),
                $"{_bar.Left},{_bar.Top}",
                _uiRevision,
                _bar.GetMiniAreaStates()),
            cancellationToken));

    public ValueTask<MiniMutationResult> SetMiniCollapsedAsync(
        bool collapsed,
        long expectedUiRevision,
        CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () =>
            {
                if (expectedUiRevision != _uiRevision)
                {
                    throw HostCommandException.Conflict("ui", _uiRevision);
                }
                var before = _bar.Bounds;
                var previous = AppSettings.CopyMiniAreaLayouts(_settings.MiniAreaLayouts);
                if (_bar.GetMiniAreaStates().All(area => area.Collapsed == collapsed))
                {
                    return new MiniMutationResult(
                        _pluginHost.Describe().Revisions.Revision,
                        _uiRevision,
                        collapsed,
                        ToUiBounds(before),
                        ToUiBounds(before),
                        true,
                        true,
                        null,
                        null,
                        _bar.GetMiniAreaStates());
                }
                _bar.SetAllMiniAreasCollapsedFromCommand(collapsed);
                _settings.MiniAreaLayouts = AppSettings.CopyMiniAreaLayouts(_bar.MiniAreaLayouts);
                if (!TrySaveSettings(_settings))
                {
                    _settings.MiniAreaLayouts = previous;
                    _bar.SetMiniAreaLayouts(previous, preserveAnchor: true);
                    throw new IOException("Mini state could not be saved.");
                }
                _uiRevision++;
                var after = _bar.Bounds;
                return new MiniMutationResult(
                    _pluginHost.Describe().Revisions.Revision + 1,
                    _uiRevision,
                    collapsed,
                    ToUiBounds(before),
                    ToUiBounds(after),
                    before.Location == after.Location,
                    true,
                    null,
                    null,
                    _bar.GetMiniAreaStates());
            },
            cancellationToken));

    public ValueTask<MiniMutationResult> SetMiniAreaAsync(
        string areaId,
        bool? collapsed,
        int? width,
        long expectedUiRevision,
        CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () =>
            {
                if (expectedUiRevision != _uiRevision)
                {
                    throw HostCommandException.Conflict("ui", _uiRevision);
                }
                var currentArea = _bar.GetMiniAreaStates().FirstOrDefault(area =>
                    string.Equals(area.AreaId, areaId, StringComparison.Ordinal))
                    ?? throw new HostCommandException("area_not_found", "Mini area was not found.");
                if (width is { } requested
                    && (requested < currentArea.MinimumWidth || requested > currentArea.MaximumWidth))
                {
                    throw new HostCommandException("invalid_request", "width is outside the supported range.");
                }
                var before = _bar.Bounds;
                var previous = AppSettings.CopyMiniAreaLayouts(_settings.MiniAreaLayouts);
                var desiredCollapsed = collapsed ?? currentArea.Collapsed;
                var desiredWidth = width ?? currentArea.Width;
                if (currentArea.Collapsed == desiredCollapsed && currentArea.Width == desiredWidth)
                {
                    return new MiniMutationResult(
                        _pluginHost.Describe().Revisions.Revision,
                        _uiRevision,
                        _bar.AreAllMiniAreasCollapsed,
                        ToUiBounds(before),
                        ToUiBounds(before),
                        true,
                        true,
                        areaId,
                        currentArea.Width,
                        _bar.GetMiniAreaStates());
                }
                if (!_bar.SetMiniAreaFromCommand(areaId, collapsed, width))
                {
                    throw new HostCommandException("area_not_found", "Mini area was not found.");
                }
                _settings.MiniAreaLayouts = AppSettings.CopyMiniAreaLayouts(_bar.MiniAreaLayouts);
                if (!TrySaveSettings(_settings))
                {
                    _settings.MiniAreaLayouts = previous;
                    _bar.SetMiniAreaLayouts(previous, preserveAnchor: true);
                    throw new IOException("Mini area state could not be saved.");
                }
                _uiRevision++;
                var after = _bar.Bounds;
                var area = _bar.GetMiniAreaStates().First(item =>
                    string.Equals(item.AreaId, areaId, StringComparison.Ordinal));
                return new MiniMutationResult(
                    _pluginHost.Describe().Revisions.Revision + 1,
                    _uiRevision,
                    _bar.AreAllMiniAreasCollapsed,
                    ToUiBounds(before),
                    ToUiBounds(after),
                    before.Location == after.Location,
                    true,
                    areaId,
                    area.Width,
                    _bar.GetMiniAreaStates());
            },
            cancellationToken));

    public ValueTask<MiniMutationResult> MoveMiniAreaAsync(
        string areaId,
        string? beforeAreaId,
        long expectedUiRevision,
        CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () =>
            {
                if (expectedUiRevision != _uiRevision)
                {
                    throw HostCommandException.Conflict("ui", _uiRevision);
                }
                var reorderable = _bar.GetReorderableMiniAreaIds();
                if (!reorderable.Contains(areaId, StringComparer.Ordinal)
                    || (beforeAreaId is not null
                        && !string.Equals(beforeAreaId, areaId, StringComparison.Ordinal)
                        && !reorderable.Contains(beforeAreaId, StringComparer.Ordinal)))
                {
                    throw new HostCommandException("area_not_found", "Mini area was not found.");
                }

                var before = _bar.Bounds;
                var previous = AppSettings.CopyMiniAreaOrder(_settings.MiniAreaOrder);
                if (!_bar.MoveMiniAreaFromCommand(areaId, beforeAreaId))
                {
                    var current = _bar.GetMiniAreaStates().First(area =>
                        string.Equals(area.AreaId, areaId, StringComparison.Ordinal));
                    return new MiniMutationResult(
                        _pluginHost.Describe().Revisions.Revision,
                        _uiRevision,
                        _bar.AreAllMiniAreasCollapsed,
                        ToUiBounds(before),
                        ToUiBounds(before),
                        true,
                        true,
                        areaId,
                        current.Width,
                        _bar.GetMiniAreaStates());
                }

                _settings.MiniAreaOrder = AppSettings.CopyMiniAreaOrder(_bar.MiniAreaOrder);
                if (!TrySaveSettings(_settings))
                {
                    _settings.MiniAreaOrder = previous;
                    _bar.SetMiniAreaOrder(previous, preserveAnchor: true);
                    throw new IOException("Mini area order could not be saved.");
                }

                _uiRevision++;
                var after = _bar.Bounds;
                var area = _bar.GetMiniAreaStates().First(item =>
                    string.Equals(item.AreaId, areaId, StringComparison.Ordinal));
                return new MiniMutationResult(
                    _pluginHost.Describe().Revisions.Revision + 1,
                    _uiRevision,
                    _bar.AreAllMiniAreasCollapsed,
                    ToUiBounds(before),
                    ToUiBounds(after),
                    before.Location == after.Location,
                    true,
                    areaId,
                    area.Width,
                    _bar.GetMiniAreaStates());
            },
            cancellationToken));

    public ValueTask<WindowInspection> InspectWindowAsync(CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () => new WindowInspection(
                !_bar.IsDisposed,
                Environment.ProcessId,
                Environment.ProcessPath,
                _bar.IsHandleCreated && !_bar.IsDisposed,
                ToUiBounds(_bar.Bounds),
                _bar.TopMost,
                _bar.DeviceDpi),
            cancellationToken));

    public async ValueTask RequestRefreshAsync(
        bool reloadSettings,
        CancellationToken cancellationToken)
    {
        if (reloadSettings)
        {
            var reloaded = await Task.Run(_store.Load, cancellationToken);
            await InvokeOnUiAsync(
                () =>
                {
                    ApplySettingsSnapshot(reloaded);
                    return true;
                },
                cancellationToken);
            await SyncPluginEnablementAsync(
                suppressErrors: false,
                cancellationToken);
        }

        await InvokeOnUiAsync(
            () =>
            {
                _ = RefreshAsync(userInitiated: true);
                return true;
            },
            cancellationToken);
    }

    public ValueTask OpenSettingsAsync(CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () =>
            {
                OpenSettings();
                return true;
            },
            cancellationToken));

    public ValueTask RequestExitAsync(CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () =>
            {
                _bar.BeginInvoke(Quit);
                return true;
            },
            cancellationToken));

    private Task<T> InvokeOnUiAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_bar.InvokeRequired) return Task.FromResult(action());
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _bar.BeginInvoke(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }
                try { completion.TrySetResult(action()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            });
        }
        catch (InvalidOperationException)
        {
            completion.TrySetException(new HostCommandException(
                "app_not_running",
                "ZGSTokenBar is shutting down.",
                true));
        }
        return completion.Task;
    }

    private static UiBounds ToUiBounds(Rectangle bounds) =>
        new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

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
        _refreshTimer.Stop();
        _radarTimer.Stop();
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
            await _radarGate.WaitAsync();
            _radarGate.Release();
            await _codexTokenUsageGate.WaitAsync();
            _codexTokenUsageGate.Release();
            await _systemUsageGate.WaitAsync();
            _systemUsageGate.Release();
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
            _shutdown.Cancel();
            _refreshTimer.Dispose();
            _providerActivityTimer.Dispose();
            _radarTimer.Dispose();
            _updateTimer.Dispose();
            _clockTimer.Dispose();
            _confirmationTimer.Dispose();
            _activationWait.Unregister(null);
            _settingsDialog?.Dispose();
            _tray.Dispose();
            _bar.Dispose();
            _coordinator.Dispose();
            _radarService.Dispose();
            _refreshGate.Dispose();
            _radarGate.Dispose();
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

    private sealed record PluginAssets(
        byte[]? Icon,
        IReadOnlyDictionary<string, string> Text);

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
