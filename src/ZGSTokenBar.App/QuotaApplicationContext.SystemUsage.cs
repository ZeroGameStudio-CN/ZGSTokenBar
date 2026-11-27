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

/// <summary>System usage sampling for the Mini and its details popover.</summary>
internal sealed partial class QuotaApplicationContext
{
    private SystemUsageSnapshot SampleSystemUsage(bool includeProcesses)
    {
        var snapshot = _systemUsageSampler.Sample(includeProcesses);
        var totals = _networkTraffic.Observe(snapshot.NetworkReceiveDeltaBytes, snapshot.NetworkSendDeltaBytes, snapshot.CapturedAt);
        return snapshot with { NetworkHistory = totals, NetworkHistorySaveFailed = _networkTraffic.SaveFailed };
    }

    private async Task RefreshSystemUsageAsync(bool includeProcesses)
    {
        if (_quitting
            || !_bar.IsTaskbarMode
            || !_settings.IsPluginEnabled("zgstokenbar.metrics.system", true)
            || includeProcesses && !_bar.WantsSystemUsageDetails) return;
        try
        {
            await SystemUsageSampling.TrySampleAsync(
                _systemUsageGate,
                SampleSystemUsage,
                snapshot =>
                {
                    if (!_quitting
                        && !_bar.IsDisposed
                        && _bar.IsTaskbarMode
                        && _settings.IsPluginEnabled("zgstokenbar.metrics.system", true)
                        && (!includeProcesses || _bar.WantsSystemUsageDetails))
                    {
                        _bar.SetSystemUsage(snapshot);
                    }
                },
                includeProcesses,
                _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }
}
