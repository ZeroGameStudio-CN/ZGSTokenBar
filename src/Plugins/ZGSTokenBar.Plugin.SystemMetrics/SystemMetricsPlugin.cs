using System.Diagnostics;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.SystemMetrics;

public sealed class SystemMetricsPlugin : BuiltinPluginBase, IDataSource
{
    public override PluginManifest Manifest => new(
        1, "zgstokenbar.metrics.system", "1.0.0", 1, 0, PluginRuntime.Builtin,
        false, "system", ["system-metrics", "details"],
        true, 0, [])
    {
        DisplayName = "System Metrics",
    };

    public ValueTask<PluginDataSnapshot> RefreshAsync(
        PluginRefreshContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64;
        var summary = new ContributionSummaryItem[]
        {
            new("system.host.memory", new("integer", Integer: workingSet)),
            new("system.host.threads", new("integer", Integer: process.Threads.Count)),
        };
        return ValueTask.FromResult(new PluginDataSnapshot(
            Manifest.Id,
            context.Now,
            new(
                PluginHealthCode.Current,
                true,
                false,
                context.Now,
                "system.metrics.current"),
            [
                new(
                    "card.system.metrics",
                    Manifest.Id,
                    "system",
                    ContributionKind.Metric,
                    0,
                    "system.metrics.title",
                    "system.metrics.icon",
                    "accent.system",
                    summary),
            ],
            [
                new(
                    "detail.system.metrics",
                    Manifest.Id,
                    [
                        new(
                            "section.system.metrics",
                            "system.metrics.details",
                            0,
                            summary.Select(item => new DetailRowContribution(
                                item.LabelKey,
                                item.Value,
                                ObservedAt: context.Now)).ToArray()),
                    ]),
            ],
            []));
    }
}
