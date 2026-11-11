using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed partial class BarForm
{
    private System.Windows.Forms.Timer? _perfProbeTimer;

    /// <summary>
    /// Developer latency probe (ZGSTOKENBAR_PERF_PROBE=1 with ZGSTOKENBAR_PERF_TRACE): 20 s after
    /// start it drives the overflow expand/collapse and the Router hover/open paths five times each,
    /// exactly as the mouse handlers call them (without moving the cursor), and traces each step.
    /// </summary>
    private void StartPerfProbeIfRequested()
    {
        if (_renderOnly || !PerfTrace.Enabled || _perfProbeTimer is not null
            || Environment.GetEnvironmentVariable("ZGSTOKENBAR_PERF_PROBE") != "1") return;
        var step = 0;
        _perfProbeTimer = new System.Windows.Forms.Timer { Interval = 20_000 };
        _perfProbeTimer.Tick += (_, _) =>
        {
            _perfProbeTimer!.Interval = 700;
            var round = step / 6;
            if (round >= 5)
            {
                _perfProbeTimer.Stop();
                PerfTrace.Note("probe.done");
                return;
            }
            switch (step % 6)
            {
                case 0:
                    using (PerfTrace.Measure("probe.expand")) SetGlassExpanded(true);
                    break;
                case 1:
                    using (PerfTrace.Measure("probe.collapse")) SetGlassExpanded(false);
                    break;
                case 2:
                    using (PerfTrace.Measure("probe.router-hover"))
                        RequestPluginPopover(MiniAreaIds.Router, RouterProbeAnchor(), pinned: false);
                    break;
                case 3:
                    using (PerfTrace.Measure("probe.router-hide")) HidePluginPopover();
                    break;
                case 4:
                    using (PerfTrace.Measure("probe.router-open")) RequestPluginPopover(MiniAreaIds.Router, RouterProbeAnchor(), pinned: true);
                    break;
                default:
                    using (PerfTrace.Measure("probe.router-close")) HidePluginPopover();
                    break;
            }
            step++;
        };
        _perfProbeTimer.Start();
    }

    private Rectangle RouterProbeAnchor()
    {
        var target = _taskbarPluginBounds.FirstOrDefault(item => item.Card.PluginId == MiniAreaIds.Router);
        return target.Card is null ? Bounds : QuotaTargetScreenBounds(target.Bounds);
    }
}
