using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.Plugin.Intake;

namespace ZGSTokenBar.App;

internal sealed partial class QuotaApplicationContext
{
    private static readonly TimeSpan IntakeSummaryInterval = TimeSpan.FromSeconds(30);
    private IntakeServiceMonitorClient? _intakeServiceClient;
    private CancellationTokenSource? _intakeSummaryRead;
    private bool? _intakeSummaryHealthy;
    private long _intakeSummaryObservedAt;
    private IntakeServiceMonitorForm? _intakeServiceObserver;
    private bool _intakeShowWorkItems;
    private MonitorPanelForm? ActiveIntakePanel => _intakeObserver is { Visible: true, IsDisposed: false } ? _intakeObserver
        : _intakeServiceObserver is { Visible: true, IsDisposed: false } ? _intakeServiceObserver : null;
    private bool IntakeEnabled => _settings.IsPluginEnabled(MiniAreaIds.Intake, IntakeObserverPlugin.DefaultEnabled)
        && _pluginHost.DescribePlugin(MiniAreaIds.Intake) is { Enabled: true };

    private void ShowIntakeObserver()
    {
        if (_quitting) return;
        if (!IntakeEnabled) throw new HostCommandException("plugin_disabled", "Work intake module is disabled.");
        _bar.OpenPluginPopover(MiniAreaIds.Intake, pinned: true);
    }

    private void IntakePopoverRequested(object? sender, PluginPopoverRequest request)
    {
        if (request.PluginId != MiniAreaIds.Intake || !IntakeEnabled || _quitting) return;
        var panel = EnsureIntakeObserver();
        if (panel is IntakeServiceMonitorForm && _intakeSummaryRead is { } reading) _ = reading.CancelAsync();
        panel.ShowFor(_bar, request.Anchor, request.Pinned, request.Scale, request.Theme, request.Animate, request.Immediate);
        request.Popup = panel;
    }

    private MonitorPanelForm EnsureIntakeObserver()
    {
        if (!_intakeShowWorkItems && ReadIntakeProfile() is { } client)
        {
            if (_intakeServiceObserver is null || _intakeServiceObserver.IsDisposed)
            {
                _intakeServiceObserver = new(_text.IsChinese, client);
                _intakeServiceObserver.ProcessingObserved += PublishIntakeCount;
                _intakeServiceObserver.PinChanged += (_, pinned) => _bar.PinPluginPopover(pinned);
                _intakeServiceObserver.CloseRequested += (_, _) => _bar.HidePluginPopover(MiniAreaIds.Intake);
                _intakeServiceObserver.WorkItemsRequested += (_, _) =>
                {
                    _bar.HidePluginPopover(MiniAreaIds.Intake);
                    _intakeShowWorkItems = true;
                    _bar.OpenPluginPopover(MiniAreaIds.Intake, pinned: true);
                };
            }
            return _intakeServiceObserver;
        }
        if (_intakeObserver is null || _intakeObserver.IsDisposed)
        {
            _intakeObserver = new(_text.IsChinese);
            _intakeObserver.PinChanged += (_, pinned) => _bar.PinPluginPopover(pinned);
            _intakeObserver.CloseRequested += (_, _) => _bar.HidePluginPopover(MiniAreaIds.Intake);
            _intakeObserver.VisibleChanged += (_, _) => { if (!_intakeObserver.Visible) _intakeShowWorkItems = false; };
        }
        return _intakeObserver;
    }

    private IntakeServiceMonitorClient? ReadIntakeProfile()
    {
        var configured = File.Exists(IntakeServiceMonitorClient.ProfilePath(_store.DataDirectory));
        _bar.SetIntakeServiceConfigured(configured);
        return configured ? _intakeServiceClient ??= new(_store.DataDirectory, recordReadFailures: true) : null;
    }

    private void RegisterIntakeSummary() => _refreshScheduler.Add("intake-summary", () => IntakeSummaryInterval,
        async () => { await RefreshIntakeSummaryAsync(); return true; }, firstDelay: TimeSpan.FromSeconds(3));

    private async Task RefreshIntakeSummaryAsync()
    {
        if (_quitting || _bar.IsDisposed || _intakeSummaryRead is not null || _intakeServiceObserver is { Visible: true }) return;
        if (!IntakeEnabled || ReadIntakeProfile() is not { } client)
        {
            ResetIntakeSummary();
            return;
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _intakeSummaryRead = operation;
        try
        {
            var results = await client.ReadAsync(operation.Token, summaryOnly: true);
            if (!operation.IsCancellationRequested && !_quitting && IntakeEnabled)
                PublishIntakeCount(client.ProcessingCount(results));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        finally { _intakeSummaryRead = null; }
    }

    private void PublishIntakeCount(int? count)
    {
        if (_quitting || _bar.IsDisposed) return;
        _intakeSummaryHealthy = count.HasValue;
        _intakeSummaryObservedAt = Environment.TickCount64;
        _bar.SetIntakeProcessingCount(count);
    }

    private void ResetIntakeSummary()
    {
        if (_intakeSummaryRead is { } reading) _ = reading.CancelAsync();
        _intakeSummaryHealthy = null;
        _bar.SetIntakeProcessingCount(null);
    }

    private void CloseIntakeObservers()
    {
        ResetIntakeSummary();
        _bar.SetIntakeServiceConfigured(false);
        _intakeObserver?.Close();
        _intakeServiceObserver?.Close();
    }
}
