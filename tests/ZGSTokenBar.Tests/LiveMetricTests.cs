using ZGSTokenBar.Core;
using ZGSTokenBar.App;

internal static class LiveMetricTests
{
    internal static void Run()
    {
        var at = DateTimeOffset.UtcNow;
        CodexConversationTurn Turn(string id, long output) => new("s", id, "model", "high", at.AddMinutes(-1), at,
            output, 60, false, true);
        var tracker = new ObservedOutputRate();
        Check(tracker.Observe([Turn("a", 100), Turn("b", 20)], at) is null, "First observation is a baseline, not a burst.");
        Check(tracker.Observe([Turn("a", 150), Turn("b", 70)], at.AddSeconds(5)) == 20, "Observed deltas sum concurrent turns over sample time.");
        Check(tracker.Observe([Turn("a", 150), Turn("b", 70)], at.AddSeconds(10)) == 0, "Unchanged counters give a measured zero.");
        Check(tracker.Observe([Turn("a", 10)], at.AddSeconds(15)) is null, "Counter resets are not negative output.");
        Check(tracker.Observe([Turn("a", 30)], at.AddSeconds(60)) is null, "Suspended sampling does not smear an old burst over a gap.");
        Check(tracker.Observe([Turn("a", 40) with { Stale = true }], at.AddSeconds(65)) is null, "Read failures are unavailable, not idle.");
        var mixed = new ObservedOutputRate();
        mixed.Observe([Turn("active", 100), Turn("orphan", 20) with { Stale = true }], at);
        Check(mixed.Observe([Turn("active", 150), Turn("orphan", 20) with { Stale = true }], at.AddSeconds(5)) == 10,
            "An old unavailable session cannot hide readable active output.");
        Check(mixed.Observe([Turn("late", 90000)], at.AddSeconds(10)) == 0,
            "Late historical discovery is a baseline, not a current burst.");
        var network = new NetworkUsageSampler();
        Check(network.Observe([new("a", 1000, 2000)], 1).Received is null, "Network needs a baseline.");
        var rate = network.Observe([new("a", 3000, 3000)], 3);
        Check(rate.Received == 1000 && rate.Sent == 500, "Network uses monotonic elapsed time and byte counter deltas.");
        Check(network.Observe([new("b", 900000, 900000)], 4).Received is null, "A new interface does not produce a phantom spike.");
        Check(network.Observe([new("b", 1, 1)], 5).Sent is null, "Network reset is unavailable.");
        Check(network.Observe(null, 6).Received is null, "Failed network reads clear the baseline.");
        Check(NetworkUsageSampler.Compact(1024) == "1K" && NetworkUsageSampler.Compact(null) == "—", "Compact units retain unavailable state.");
        var snapshot = new SystemUsageSnapshot(1, 2, 3, 5, 1, null, 0, 4, at.AddMinutes(-1))
        { NetworkReceiveBytesPerSecond = 100, ObservedOutputTokensPerSecond = 30, OutputRateCapturedAt = at.AddMinutes(-1) };
        Check(snapshot.CurrentOutputRate is null && snapshot.CurrentNetworkReceive is null, "Stale display values expire.");
    }
    internal static void Render(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            var at = DateTimeOffset.UtcNow;
            using var form = new BarForm(new AppSettings { Locale = "zh-CN", EnableAnimations = false, EnableRadar = false },
                new QuotaSnapshot([], [], at), renderOnly: true, renderDpi: dpi);
            var original = form.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.SystemMetrics);
            form.SetSystemUsage(new SystemUsageSnapshot(34, 32UL << 30, 32UL << 30, 64UL << 30, 12, "3D", 2, 16, at)
            { DiskActivePercent = 22, NetworkReceiveBytesPerSecond = 12.5 * 1024 * 1024, NetworkSendBytesPerSecond = 128 * 1024, NetworkInterfaces = 1 });
            form.SetObservedOutputRate(53.2, at);
            Check(form.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.SystemMetrics) == original,
                "Live readouts cannot change the CPU area's saved layout.");
            Check(form.GetMiniAreaStates().Count(area => area.AreaId == MiniAreaIds.LiveMetrics) == 1,
                "Rates use their own existing-style Mini area.");
            form.CreateControl();
            using var bitmap = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(directory, $"live-rates-{dpi}dpi.png"), System.Drawing.Imaging.ImageFormat.Png);
            Check(form.SetMiniAreaFromCommand(MiniAreaIds.LiveMetrics, true, null), "The independent rates area can collapse.");
            Check(form.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.SystemMetrics) == original,
                "Collapsing rates preserves CPU state.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
