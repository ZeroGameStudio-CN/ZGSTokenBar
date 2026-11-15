using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.Plugin.Intake;

namespace ZGSTokenBar.App;

internal sealed partial class QuotaApplicationContext
{
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
        if (_intakeObserver is null || _intakeObserver.IsDisposed)
        {
            _intakeObserver = new(_text.IsChinese);
            _intakeObserver.PinChanged += (_, pinned) => _bar.PinPluginPopover(pinned);
            _intakeObserver.CloseRequested += (_, _) => _bar.HidePluginPopover(MiniAreaIds.Intake);
        }
        _intakeObserver.ShowFor(_bar, request.Anchor, request.Pinned, request.Scale, request.Theme);
        request.Popup = _intakeObserver;
    }
}
