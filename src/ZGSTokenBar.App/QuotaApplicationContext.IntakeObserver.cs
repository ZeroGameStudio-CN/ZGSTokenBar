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
        if (_intakeObserver is { IsDisposed: false } opened)
        {
            if (opened.WindowState == FormWindowState.Minimized) opened.WindowState = FormWindowState.Normal;
            opened.Activate();
            return;
        }
        _intakeObserver = new(_text.IsChinese);
        _intakeObserver.Show();
        _intakeObserver.Activate();
    }
}
