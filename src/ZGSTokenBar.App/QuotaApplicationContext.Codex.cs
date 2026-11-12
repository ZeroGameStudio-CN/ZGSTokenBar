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

/// <summary>Codex local data: rollout imports, token usage, conversations and quota-token observations.</summary>
internal sealed partial class QuotaApplicationContext
{
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
            var summary = await Task.Run(
                () => BackgroundWork.BelowNormal(() => _codexTokenUsageLedger.Refresh(observedAt, _shutdown.Token)),
                _shutdown.Token);
            _cachedCodexTokenUsage = summary;
            if (!_shutdown.IsCancellationRequested
                && !_bar.IsDisposed
                && _activeProviders.Contains(ProviderKind.Codex))
            {
                if (summary is not null)
                {
                    var rate = _observedOutput.Observe(summary.LiveRateFeed, DateTimeOffset.UtcNow);
                    summary = summary with { LiveOutputRate = rate };
                    _cachedCodexTokenUsage = summary;
                    _bar.SetLiveOutputRate(rate);
                }
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

    private async Task RefreshCodexConversationsAsync()
    {
        if (!await _codexTokenUsageGate.WaitAsync(0)) return;
        try
        {
            var summary = await Task.Run(
                () => BackgroundWork.BelowNormal(() => _codexTokenUsageLedger.RefreshConversations(DateTimeOffset.UtcNow, _shutdown.Token)),
                _shutdown.Token);
            if (_shutdown.IsCancellationRequested || _bar.IsDisposed || summary is null
                || !_activeProviders.Contains(ProviderKind.Codex)) return;
            var rate = _observedOutput.Observe(summary.LiveRateFeed, DateTimeOffset.UtcNow);
            summary = summary with { LiveOutputRate = rate };
            _cachedCodexTokenUsage = summary;
            _bar.SetLiveOutputRate(rate);
            _bar.SetCodexTokenUsage(summary);
            _pluginHost.Publish(CorePluginProjection.CodexUsage(
                "zgstokenbar.usage.codex-local", summary, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally { _codexTokenUsageGate.Release(); }
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
}
