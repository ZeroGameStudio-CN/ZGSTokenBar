using ZGSTokenBar.Core;
using ZGSTokenBar.App;

internal static class LiveMetricTests
{
    internal static void Run()
    {
        var at = DateTimeOffset.UtcNow;
        var tracker = new ObservedOutputRate();
        CodexResponseSample Response(string id, int start, int end, long count) => new(id, "model", at.AddSeconds(start), at.AddSeconds(end), count);
        CodexLiveRateFeed Feed(CodexResponseSample[] rows, CodexLiveSession[]? sessions = null) => new(at, rows, sessions ?? [], 0);
        var summed = tracker.Observe(Feed([Response("a", -4, 0, 400), Response("b", -1, 0, 30)]), at);
        Check(Math.Abs(summed.Measured - 110) < .001, "Concurrent response intervals sum by three-second overlap.");
        Check(tracker.Observe(Feed([Response("old", -15, -10, 90000)]), at).Total == 0,
            "Late historical discovery never becomes a polling-time burst.");
        var active = new CodexLiveSession("a", "model", "generating", at.AddSeconds(-2), at);
        var estimated = tracker.Observe(Feed([Response("a", -7, -5, 200)], [active]), at);
        Check(Math.Abs(estimated.Estimated - 200d / 3) < .001 && estimated.HasEstimate,
            "Generating uses recent response-weighted speed and exposes the estimated share.");
        Check(tracker.Observe(Feed([Response("a", -7, -5, 200)], [active with { Phase = "tools" }]), at).Total == 0,
            "Tool time does not inherit generation estimates.");
        var overlap = tracker.Observe(Feed([Response("a", -3, -1, 200)], [active with { InFlightStart = at.AddSeconds(-4) }]), at);
        Check(Math.Abs(overlap.Total!.Value - 100) < .001, "Estimates begin after the last recorded response and cannot overlap it.");
        Check(tracker.Observe(Feed([Response("fast", 0, 0, 30)]), at).Measured == 10,
            "Sub-500ms responses use a bounded interval rather than dividing by zero.");
        Check(tracker.Observe(Feed([]) with { CapturedAt = at.AddMinutes(-1) }, at).Total is null,
            "Expired feeds are unavailable.");
        Check(tracker.Observe(Feed([]), at).Total is null, "No readable session is unavailable, not a measured zero.");
        Check(tracker.Observe(Feed([], [active with { Phase = "idle" }]), at).Total == 0, "An observed idle session has zero current output.");
        ReaderIntervals(at);
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
        var context = SynchronizationContext.Current;
        HoverSurfaces(snapshot with
        {
            TopProcesses = [new("worker", 1, 5, 1234, 0)],
            LiveRate = new LiveOutputRate(at, 12, 10, 2, 1, 0, 1, 0),
            ObservedOutputTokensPerSecond = 12,
            OutputRateCapturedAt = at,
        });
        Check(ReferenceEquals(context, SynchronizationContext.Current),
            "Native hover checks cannot leak a UI synchronization context into later async checks.");
    }

    private static void HoverSurfaces(SystemUsageSnapshot snapshot)
    {
        var context = SynchronizationContext.Current;
        try { CheckHoverSurfaces(snapshot); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static void CheckHoverSurfaces(SystemUsageSnapshot snapshot)
    {
        using var popover = new SystemUsagePopoverForm();
        var text = NativeText.For("zh-CN");
        popover.UpdateContent(new SystemUsagePopoverContent(snapshot, false), text);
        Check(!popover.LiveRatesContentForAcceptance, "CPU hover keeps the system surface mode.");
        Check(popover.MetricLabelsForAcceptance.Contains("CPU")
            && !popover.MetricLabelsForAcceptance.Contains("合计输出")
            && popover.ProcessRowCountForAcceptance == 1,
            "CPU hover contains CPU metrics and process rows only.");

        popover.UpdateContent(new SystemUsagePopoverContent(snapshot, false, true), text);
        Check(popover.LiveRatesContentForAcceptance, "Rate hover switches to the live surface mode.");
        Check(popover.CurrentLogicalBodyHeight < SystemUsagePopoverForm.LogicalBodyHeight,
            "Rate hover uses the compact live layout height.");
        Check(popover.MetricLabelsForAcceptance.Contains("合计输出")
            && popover.MetricLabelsForAcceptance.Contains("网络接收")
            && !popover.MetricLabelsForAcceptance.Contains("CPU")
            && popover.ProcessRowCountForAcceptance == 0,
            "Rate hover contains output/network metrics without CPU or process rows.");

        popover.UpdateContent(new SystemUsagePopoverContent(snapshot, true), text);
        Check(!popover.LiveRatesContentForAcceptance && popover.ProcessRowCountForAcceptance == 1,
            "Switching back to a pinned CPU hover restores only CPU details.");
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
            form.SetLiveOutputRate(new(at, 53.2, 33.2, 20, 2, 1, 4, 0));
            Check(form.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.SystemMetrics) == original,
                "Live readouts cannot change the CPU area's saved layout.");
            Check(form.GetMiniAreaStates().Count(area => area.AreaId == MiniAreaIds.LiveMetrics) == 1,
                "Rates use their own existing-style Mini area.");
            form.CreateControl();
            foreach (var collapsed in new[] { true, false })
            {
                form.SetMiniAreaFromCommand(MiniAreaIds.LiveMetrics, collapsed, null);
                using var bitmap = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(directory, $"live-rates-{(collapsed ? "collapsed" : "expanded")}-{dpi}dpi.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            foreach (var locale in new[] { "zh-CN", "en" })
            {
                foreach (var isLive in new[] { true, false })
                {
                    using var popover = new SystemUsagePopoverForm();
                    using var bitmap = popover.RenderForTest(new SystemUsagePopoverContent(form.LiveMetrics, false, isLive),
                        NativeText.For(locale), QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), dpi);
                    bitmap.Save(Path.Combine(directory, $"hover-{(isLive ? "rates" : "cpu")}-{locale}-{dpi}dpi.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                var samples = new[]
                {
                    ("estimated", form.LiveMetrics with { LiveRate = new(at, 44.2, 0, 44.2, 2, 3, 12, 0), ObservedOutputTokensPerSecond = 44.2 }),
                    ("unavailable", form.LiveMetrics with { LiveRate = null, ObservedOutputTokensPerSecond = null, NetworkReceiveBytesPerSecond = null, NetworkSendBytesPerSecond = null }),
                    ("large", form.LiveMetrics with { LiveRate = new(at, 12345.6, 12000, 345.6, 12, 9, 12, 3), ObservedOutputTokensPerSecond = 12345.6 })
                };
                foreach (var (name, sample) in samples)
                {
                    using var popover = new SystemUsagePopoverForm();
                    using var bitmap = popover.RenderForTest(new SystemUsagePopoverContent(sample, true, true),
                        NativeText.For(locale), QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), dpi);
                    bitmap.Save(Path.Combine(directory, $"hover-rates-{name}-{locale}-{dpi}dpi.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            Check(form.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.SystemMetrics) == original,
                "Collapsing rates preserves CPU state.");
        }
    }
    private static void ReaderIntervals(DateTimeOffset at)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tokenbar-response-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "session-test.jsonl");
            string Line(int seconds, string type, object payload) => System.Text.Json.JsonSerializer.Serialize(new
            { timestamp = at.AddSeconds(seconds).ToString("O"), type, payload });
            object Usage(long output, long total) => new { type = "token_count", info = new {
                total_token_usage = new { output_tokens = output, total_tokens = total, input_tokens = total-output, cached_input_tokens = 0 },
                last_token_usage = new { output_tokens = 90, total_tokens = 100, input_tokens = 10, cached_input_tokens = 0 } } };
            File.WriteAllLines(path, [Line(-11, "event_msg", Usage(100, 200)),
                Line(-10, "event_msg", new { type = "task_started", turn_id = "turn" }),
                Line(-9, "response_item", new { type = "reasoning" }),
                Line(-1, "event_msg", Usage(190, 300))]);
            var reader = new CodexConversationUsageReader();
            reader.Refresh([path], at);
            var sample = reader.LiveRateFeed!.Responses.Single();
            Check(sample.Output == 90 && sample.Start == at.AddSeconds(-10) && sample.End == at.AddSeconds(-1),
                "The reader emits per-response counter deltas and log timestamps.");
            Check(reader.LiveRateFeed.Sessions.Single().Phase == "tools", "Completed response enters the tools phase.");
            File.AppendAllText(path, Line(0, "response_item", new { type = "function_call_output" }) + Environment.NewLine);
            reader.Refresh([path], at);
            Check(reader.LiveRateFeed!.Sessions.Single().Phase == "generating", "Tool output begins the next generation interval.");
            File.AppendAllText(path, Line(0, "event_msg", Usage(190, 300)) + Environment.NewLine);
            reader.Refresh([path], at.AddSeconds(1));
            Check(reader.LiveRateFeed!.Sessions.Single().Phase == "generating", "Duplicate counters cannot end active generation.");
            Check(reader.LiveRateFeed!.Responses.Count == 1, "Unchanged polling never duplicates responses.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
