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

/// <summary>Refresh scheduling: the quota refresh loop, its interval, provider activity and pace estimates.</summary>
internal sealed partial class QuotaApplicationContext
{
    private async Task RefreshAsync(bool userInitiated = false, bool forceProviderRefresh = false, bool codexOnly = false)
    {
        if (userInitiated) _otherToolsModule.RequestRefresh();
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            if (_store.SettingsReadPending)
            {
                if (!_store.TryLoad(out var restored)) return;
                ApplySettingsSnapshot(restored);
                await SyncPluginEnablementAsync(suppressErrors: false, _shutdown.Token);
            }
            await UpdateProviderActivityAsync(requestRefresh: false);
            if (_shutdown.IsCancellationRequested || _bar.IsDisposed) return;
            _bar.SetRefreshing(true);
            // The coordinator's synchronous prefix (process snapshots, account files, credential
            // stats) must never run on the UI thread: when the bar is embedded in the taskbar a
            // busy UI thread also stalls Explorer's input for the shared queue.
            var settings = _settings;
            var previousSnapshot = _snapshot;
            var activeProviders = codexOnly
                ? _activeProviders.Where(provider => provider == ProviderKind.Codex).ToHashSet()
                : _activeProviders;
            if (codexOnly && activeProviders.Count == 0) return;
            var activeCandidate = await Task.Run(
                () => _coordinator.RefreshAsync(
                    settings,
                    previousSnapshot,
                    _shutdown.Token,
                    allowClaudeOAuthRefresh: userInitiated,
                    forceProviderRefresh: userInitiated || forceProviderRefresh || codexOnly,
                    activeProviders: activeProviders),
                _shutdown.Token);
            if (_shutdown.IsCancellationRequested || _bar.IsDisposed) return;
            if (activeProviders.Contains(ProviderKind.Codex)) ObserveCodexQuotaHealth(activeCandidate);
            var observedAt = DateTimeOffset.UtcNow;
            var candidate = MergeActiveProviderResults(
                _snapshot,
                activeCandidate,
                activeProviders,
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
            _otherToolsModule.SetExternal(UsageProjection.FromQuotaSnapshot(next));
            if (codexQuotaTokenCapacityChanged)
            {
                var exportedTokens = _codexQuotaTokenTracker.Export();
                EnqueueStateSave("codex-quota-token-history", () => _store.SaveCodexQuotaTokenHistory(exportedTokens));
            }
            if (!stabilization.ConfirmationRequired
                && _activeProviders.Contains(ProviderKind.Codex)
                && _planChanges.Observe(next.CodexPlanObservations, out var planStructuralChange))
            {
                var tick = Environment.TickCount64;
                if (planStructuralChange || tick - _planHistorySavedAt >= PlanHistoryHeartbeatSaveMs)
                {
                    _planHistorySavedAt = tick;
                    _planHistoryDirty = false;
                    var exportedPlans = _planChanges.Export();
                    EnqueueStateSave("codex-plan-change-history", () => _store.SaveCodexPlanChangeHistory(exportedPlans));
                }
                else
                {
                    _planHistoryDirty = true;
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
            if (forceProviderRefresh || userInitiated)
            {
                _ = RefreshExternalProviderTokensAsync();
            }
            if (!stabilization.ConfirmationRequired
                && _activeProviders.Contains(ProviderKind.Codex))
            {
                ScheduleCodexRolloutImport(next, observedAt);
            }
            if (_settings.EnableAlerts) ShowMilestoneAlerts(alerts);
            EnqueueStateSave("quota-cache", () => _store.SaveCache(next));
            if (paceChanged)
            {
                var exportedPace = _quotaPaceTracker.Export(observedAt);
                EnqueueStateSave("quota-rate-history", () => _store.SaveQuotaRateHistory(exportedPace));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception error)
        {
            // Keep the last good snapshot visible if an unexpected refresh failure occurs.
            AppDiagnostics.Record("refresh", error);
        }
        finally
        {
            if (!_bar.IsDisposed) _bar.SetRefreshing(false);
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Registers the periodic quota refresh on first call; afterwards restarts its countdown only when
    /// the interval setting changed (as the old timer's Interval setter did).
    /// </summary>
    private void ApplyRefreshInterval()
    {
        var minutes = Math.Clamp(_settings.RefreshMinutes, 1, 60);
        if (_appliedRefreshMinutes == 0)
        {
            _refreshScheduler.Add(
                QuotaRefreshSource,
                () => TimeSpan.FromMinutes(Math.Clamp(_settings.RefreshMinutes, 1, 60)),
                async () => { await RefreshAsync().ConfigureAwait(true); return true; });
            _refreshScheduler.Add("codex-interactive-quota",
                () => TimeSpan.FromMinutes(1),
                async () =>
                {
                    var latest = _snapshot.Cards.Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
                        .Select(card => card.CapturedAt).Where(at => at is not null).Max();
                    if (_activeProviders.Contains(ProviderKind.Codex)
                        && CodexQuotaService.HasRegisteredInteractiveHomes()
                        && (latest is null || DateTimeOffset.UtcNow - latest >= TimeSpan.FromSeconds(45)))
                        await RefreshAsync(codexOnly: true).ConfigureAwait(true);
                    return true;
                });
            _refreshScheduler.Add("external-provider-tokens",
                () => TimeSpan.FromMinutes(1),
                async () => { await RefreshExternalProviderTokensAsync().ConfigureAwait(true); return true; });
        }
        else if (minutes != _appliedRefreshMinutes)
        {
            _refreshScheduler.Reschedule(QuotaRefreshSource);
        }
        _appliedRefreshMinutes = minutes;
    }

    private static (long, long, long, long) CodexAccountFilesStamp()
    {
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".antigravity_cockpit");
        static (long, long) Stamp(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : (-1, 0);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return (-1, 0); }
        }
        var accounts = Stamp(Path.Combine(home, "codex_accounts.json"));
        var instances = Stamp(Path.Combine(home, "codex_instances.json"));
        return (accounts.Item1, accounts.Item2, instances.Item1, instances.Item2);
    }

    private Task? _providerActivityTask;

    /// <summary>
    /// Detects running providers and Codex accounts off the UI thread (Toolhelp snapshots and
    /// account-file reads cost 30-150 ms), then applies the result on the UI thread. Overlapping
    /// callers share the probe already in flight instead of starting another one.
    /// </summary>
    private Task UpdateProviderActivityAsync(bool requestRefresh)
    {
        if (_quitting || _bar.IsDisposed) return Task.CompletedTask;
        if (_store.SettingsReadPending)
        {
            if (requestRefresh) _ = RefreshAsync();
            return Task.CompletedTask;
        }

        if (_providerActivityTask is { IsCompleted: false } running) return running;
        return _providerActivityTask = UpdateProviderActivityCoreAsync(requestRefresh);
    }

    private async Task UpdateProviderActivityCoreAsync(bool requestRefresh)
    {
        var settings = _settings;
        var previousStamp = _codexAccountStamp;
        var readAt = _codexAccountsReadAt;
        var tick = Environment.TickCount64;
        (ValueTuple<long, long, long, long> Stamp, IReadOnlyList<CodexAccountInfo>? Accounts, HashSet<ProviderKind> Providers,
            IReadOnlyDictionary<string, bool?> Activity) probe;
        try
        {
            probe = await Task.Run(() =>
            {
                var stamp = CodexAccountFilesStamp();
                var accounts = !requestRefresh || stamp != previousStamp || tick - readAt >= CodexAccountPollMs
                    ? CockpitCodexAccountDirectory.Read()
                    : null;
                var activity = ProviderProcessActivity.DetectUsageProviderActivity();
                return (stamp, accounts, ActiveProviders(settings, activity), activity);
            }, _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_quitting || _bar.IsDisposed || _shutdown.IsCancellationRequested) return;
        _bar.SetUsageProviderActivities(probe.Activity);
        if (probe.Accounts is { } nextAccounts)
        {
            _codexAccountStamp = probe.Stamp;
            _codexAccountsReadAt = tick;
            if (!AccountsEqual(_codexAccounts, nextAccounts))
            {
                _codexAccounts = nextAccounts;
                _bar.SetCodexAccounts(_codexAccounts);
            }
        }

        // Settings may have changed while the probe ran; keep only providers still enabled.
        var nextProviders = ReferenceEquals(settings, _settings)
            ? probe.Providers
            : probe.Providers.Where(_settings.IsEnabled).ToHashSet();
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

    private static HashSet<ProviderKind> ActiveProviders(AppSettings settings, IReadOnlyDictionary<string, bool?>? activity = null)
    {
        activity ??= ProviderProcessActivity.DetectUsageProviderActivity();
        var active = new[] { ProviderKind.Claude, ProviderKind.Codex }
            .Where(provider => activity[provider == ProviderKind.Codex ? UsageProviderIds.Codex : UsageProviderIds.Claude] is not false)
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
            CodexPlanObservations = activeProviders.Contains(ProviderKind.Codex)
                ? activeResult.CodexPlanObservations
                : previous.CodexPlanObservations,
        };
    }

    private IReadOnlyDictionary<string, QuotaPaceEstimate> QuotaPaceEstimates(
        QuotaSnapshot snapshot,
        DateTimeOffset now)
    {
        var estimates = new Dictionary<string, QuotaPaceEstimate>(StringComparer.Ordinal);
        foreach (var card in snapshot.Cards.Concat(_usagePaceCards))
        {
            foreach (var window in card.Windows)
            {
                estimates[QuotaPaceTracker.SeriesKey(card, window)] =
                    _quotaPaceTracker.Estimate(card, window, now, _settings.RefreshMinutes);
            }
        }
        return estimates;
    }

    private IReadOnlyList<QuotaCard> _usagePaceCards = [];

    /// <summary>
    /// Samples every usage-board window (Grok, Antigravity, Cursor, …) into the same persisted pace
    /// tracker Codex and Claude use, so their shared hover shows the same "at reset" / "runs out"
    /// projection rows. Claude and Codex are skipped: their native snapshot already feeds the tracker.
    /// </summary>
    private void ObserveUsagePace(UsageBoard board)
    {
        var now = DateTimeOffset.UtcNow;
        var allCards = new List<QuotaCard>();
        var liveCards = new List<QuotaCard>();
        foreach (var provider in board.Providers)
        {
            if (provider.ProviderId is UsageProviderIds.Claude or UsageProviderIds.Codex) continue;
            foreach (var account in provider.Accounts)
            {
                if (BarForm.UsageQuotaCard(provider, account, provider.ProviderId) is not { } projected) continue;
                allCards.Add(projected.Card);
                if (account.Freshness == UsageFreshness.Current) liveCards.Add(projected.Card);
            }
        }

        _usagePaceCards = allCards;
        if (allCards.Count == 0) return;
        if (liveCards.Count > 0)
        {
            var observed = new QuotaSnapshot(liveCards, [new ProviderHealth(ProviderKind.Claude, true, "usage board", ProviderHealthCode.Current)], now);
            if (_quotaPaceTracker.Observe(observed, now))
            {
                var exportedPace = _quotaPaceTracker.Export(now);
                EnqueueStateSave("quota-rate-history", () => _store.SaveQuotaRateHistory(exportedPace));
            }
        }

        _bar.SetQuotaPaceEstimates(QuotaPaceEstimates(_snapshot, now));
    }

    private void ApplyClockInterval()
    {
        // WinForms only re-arms the timer when the value actually changes.
        _clockTimer.Interval = ClockIntervalMs(_bar.IsTaskbarMode, _bar.HasOpenHoverOrPopover);
    }

    /// <summary>
    /// Live system counters and tok/s sample every 2 s while the bar is idle, every 1 s while a
    /// hover or popover is showing them, and every 30 s outside the taskbar.
    /// </summary>
    internal static int ClockIntervalMs(bool taskbarMode, bool hoverOpen) =>
        !taskbarMode ? 30_000 : hoverOpen ? 1_000 : 2_000;
}
