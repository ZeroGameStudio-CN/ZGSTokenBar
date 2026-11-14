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

/// <summary>IDesktopControl: the CLI/named-pipe control surface, marshalled onto the UI thread.</summary>
internal sealed partial class QuotaApplicationContext
{
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

    public async ValueTask<CodexHistoryExportChunk> ReadCodexHistoryAsync(CodexHistoryExportRequest request, CancellationToken cancellationToken)
    {
        var usage = await InvokeOnUiAsync(() => _cachedCodexTokenUsage, cancellationToken)
            ?? throw new HostCommandException("data_unavailable", "Retained usage has not been observed yet.");
        try
        {
            return await Task.Run(() =>
            {
                var period = request.Period switch { "today" => CodexHistoryPeriod.Today, "7d" => CodexHistoryPeriod.Days7,
                    "30d" => CodexHistoryPeriod.Days30, _ => CodexHistoryPeriod.Retained };
                var view = CodexConversationView.Create(usage, new(period, request.Model, request.Project, request.Search,
                    request.SortColumn, request.Descending), DateTimeOffset.UtcNow);
                var chunk = view.ReadCsvChunk(request.Offset, request.Snapshot);
                return new CodexHistoryExportChunk(chunk.Snapshot, chunk.NextOffset, chunk.TotalBytes, chunk.Data,
                    view.Rows.Count, view.ObservedCount, usage.CapturedAt, usage.PendingConversationCount);
            }, cancellationToken);
        }
        catch (CodexHistoryChangedException error) { throw new HostCommandException("data_changed", error.Message); }
        catch (ArgumentException) { throw new HostCommandException("invalid_request", "History export offset is invalid."); }
        catch (InvalidOperationException error) { throw new HostCommandException("export_limit", error.Message); }
    }

    public ValueTask<WindowInspection> InspectWindowAsync(CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () => new WindowInspection(
                !_bar.IsDisposed,
                Environment.ProcessId,
                Environment.ProcessPath,
                _bar.IsHandleCreated && !_bar.IsDisposed,
                ToUiBounds(_bar.ScreenBounds),
                _bar.IsHandleCreated && TaskbarEmbedding.IsTopmost(_bar.Handle),
                _bar.DeviceDpi)
            {
                AppModuleId = typeof(QuotaApplicationContext).Module.ModuleVersionId.ToString(),
                CoreModuleId = typeof(CodexTokenUsageReader).Module.ModuleVersionId.ToString(),
                DataDirectory = _store.DataDirectory,
                TaskbarDocked = _bar.IsTaskbarDocked,
                Visible = _bar.Visible,
                EmbeddedInTaskbar = _bar.EmbeddedInTaskbar,
                TaskbarEmbedStatus = _bar.TaskbarEmbedStatus,
                RemoteSession = SystemInformation.TerminalServerSession,
                ObservedOutputTokensPerSecond = _bar.LiveMetrics.CurrentOutputRate,
                OutputRateCapturedAt = _bar.LiveMetrics.OutputRateCapturedAt,
                MeasuredOutputTokensPerSecond = _bar.LiveMetrics.LiveRate?.Measured,
                EstimatedOutputTokensPerSecond = _bar.LiveMetrics.LiveRate?.Estimated,
                OutputRateWindowSeconds = 3,
                GeneratingSessionCount = _bar.LiveMetrics.LiveRate?.Generating ?? 0,
                ToolsSessionCount = _bar.LiveMetrics.LiveRate?.Tools ?? 0,
                NetworkReceiveBytesPerSecond = _bar.LiveMetrics.CurrentNetworkReceive,
                NetworkSendBytesPerSecond = _bar.LiveMetrics.CurrentNetworkSend,
                NetworkCapturedAt = _bar.LiveMetrics.CapturedAt,
                NetworkInterfaceCount = _bar.LiveMetrics.NetworkInterfaces,
                NetworkReceivedTotalBytes = _bar.LiveMetrics.NetworkHistory?.ReceivedBytes,
                NetworkSentTotalBytes = _bar.LiveMetrics.NetworkHistory?.SentBytes,
                NetworkTotalBytes = _bar.LiveMetrics.NetworkHistory?.TotalBytes,
                NetworkHistorySince = _bar.LiveMetrics.NetworkHistory?.Since,
                NetworkHistorySavedAt = _bar.LiveMetrics.NetworkHistory?.SavedAt,
                NetworkHistorySaveFailed = _bar.LiveMetrics.NetworkHistorySaveFailed,
                DisplayedTokenTotal = _bar.DisplayedTokenTotal,
                SettingsVisible = _settingsDialog is { Visible: true },
                SettingsFontsMatchLayout = _settingsDialog?.FontsMatchLayout,
                RouterVisible = _routerModule.Visible,
                IntakeVisible = _intakeObserver is { Visible: true, IsDisposed: false },
                IntakeMiniBounds = _bar.Visible && _bar.HasIntakeCard && _bar.MiniPluginBounds(MiniAreaIds.Intake) is { } intakeBounds
                    ? new((int)intakeBounds.X, (int)intakeBounds.Y, (int)intakeBounds.Width, (int)intakeBounds.Height) : null,
                IntakeProjectCount = _intakeObserver is { Visible: true, IsDisposed: false } intake
                    ? intake.Observation.Projects : 0,
                IntakeWorkItemCount = _intakeObserver is { Visible: true, IsDisposed: false } items
                    ? items.Observation.Recent : 0,
                DisplayedRouterQueuedCount = _bar.DisplayedRouterQueuedCount,
                DisplayedRouterState = _bar.DisplayedRouterState,
                RouterProjectKey = _routerModule.Snapshot?.ProjectKey,
                RouterRequestId = _routerModule.Snapshot is { } router ? ZGSTokenBar.Plugin.Router.RouterJson.Text(router.View, "requestId") : null,
                RouterObservedAt = _routerModule.Snapshot?.ReceivedAt,
                RecentConversationCount = _cachedCodexTokenUsage?.RecentTurns.Count ?? 0,
                PendingConversationCount = _cachedCodexTokenUsage?.PendingConversationCount ?? 0,
                LatestTurnOutputTokensPerSecond = _cachedCodexTokenUsage?.RecentTurns.FirstOrDefault()?.OutputTokensPerSecond,
                ConversationLiveRateTotal = _bar.LiveMetrics.CurrentOutputRate is null ? null
                    : CodexConversationDisplay.Select(_cachedCodexTokenUsage).Sum(turn => turn.LiveRate?.Total ?? 0),
                ConversationLiveRateCount = CodexConversationDisplay.Select(_cachedCodexTokenUsage).Count(turn => turn.LiveRate is not null),
            },
            cancellationToken));

    public async ValueTask RequestRefreshAsync(
        bool reloadSettings,
        CancellationToken cancellationToken)
    {
        if (reloadSettings)
        {
            var reloaded = await Task.Run(() =>
            {
                if (!_store.TryLoad(out var settings))
                    throw new IOException("Settings are temporarily unavailable.");
                return settings;
            }, cancellationToken);
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

    public ValueTask SetRouterVisibleAsync(bool visible, CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(() => { _routerModule.SetVisible(visible); return true; }, cancellationToken));

    public ValueTask SetIntakeVisibleAsync(bool visible, CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(() =>
        {
            if (visible) ShowIntakeObserver();
            else _intakeObserver?.Close();
            return true;
        }, cancellationToken));

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
}
