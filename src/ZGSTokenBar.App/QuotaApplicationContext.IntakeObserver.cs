using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.Plugin.Intake;

namespace ZGSTokenBar.App;

internal sealed partial class QuotaApplicationContext
{
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
        panel.ShowFor(_bar, request.Anchor, request.Pinned, request.Scale, request.Theme, request.Animate, request.Immediate);
        request.Popup = panel;
    }

    private MonitorPanelForm EnsureIntakeObserver()
    {
        if (!_intakeShowWorkItems && File.Exists(IntakeServiceMonitorClient.ProfilePath(_store.DataDirectory)))
        {
            if (_intakeServiceObserver is null || _intakeServiceObserver.IsDisposed)
            {
                _intakeServiceObserver = new(_text.IsChinese, new IntakeServiceMonitorClient(_store.DataDirectory));
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

    private void CloseIntakeObservers() { _intakeObserver?.Close(); _intakeServiceObserver?.Close(); }
}
