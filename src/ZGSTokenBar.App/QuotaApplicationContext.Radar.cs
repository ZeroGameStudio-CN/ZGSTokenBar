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

/// <summary>Radar: refresh, previews, alerts and model-group persistence.</summary>
internal sealed partial class QuotaApplicationContext
{
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
            if (_radarRestorePending)
            {
                if (!_store.TryLoadRadarState(out var restored))
                    throw new IOException("Radar state is temporarily unavailable.");
                _radarState = restored;
                stateBeforeFetch = restored;
                _radarService.RestoreRecommendationCache(restored.LastSnapshot);
                _radarViewState = WithRadarUnreadState(_radarViewState with
                {
                    Snapshot = restored.LastSnapshot,
                    LastSuccessfulFetchAt = restored.LastSuccessfulFetchAt,
                }, restored);
                _radarRestorePending = false;
            }
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

    private void SaveRadarModelGroups()
    {
        if (_quitting) return;
        var previous = _settings.RadarModelGroups;
        _settings.RadarModelGroups = AppSettings.CopyRadarModelGroups(_bar.RadarModelGroups);
        if (!TrySaveSettings(_settings, showError: true))
        {
            _settings.RadarModelGroups = previous;
            _bar.SetRadarModelGroups(previous);
        }
        else
        {
            _uiRevision++;
        }
    }

    private void ApplyRadarInterval()
    {
        _radarTimer.Enabled = _settings.EnableRadar && _settings.EnableRadarAlerts;
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
}
