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
                includeProcesses => SampleSystemUsage(includeProcesses),
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
                includeProcesses => SampleSystemUsage(includeProcesses),
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
}
