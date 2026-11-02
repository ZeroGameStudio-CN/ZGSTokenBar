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
        SharedConversationRates(at);
        NetworkHistory(at);
        var network = new NetworkUsageSampler();
        Check(network.Observe([new("a", 1000, 2000)], 1).Received is null, "Network needs a baseline.");
        var rate = network.Observe([new("a", 3000, 3000)], 3);
        Check(rate.Received == 1000 && rate.Sent == 500 && rate.ReceivedDelta == 2000 && rate.SentDelta == 1000, "Network uses monotonic elapsed time and byte counter deltas.");
        Check(network.Observe([new("b", 900000, 900000)], 4).Received is null, "A new interface does not produce a phantom spike.");
        Check(network.Observe([new("b", 1, 1)], 5).Sent is null, "Network reset is unavailable.");
        Check(network.Observe(null, 6).Received is null, "Failed network reads clear the baseline.");
        Check(NetworkUsageSampler.Compact(1024) == "1K" && NetworkUsageSampler.Compact(null) == "—", "Compact units retain unavailable state.");
        Check(BarForm.CompactOutputRate(null) == "—" && BarForm.CompactOutputRate(double.NaN) == "—"
            && BarForm.CompactOutputRate(0) == "0" && BarForm.CompactOutputRate(1200) == "1.2K"
            && BarForm.CompactOutputRate(1200000) == "1.2M", "Narrow output rates distinguish idle, unknown and compact magnitudes.");
        Check(TaskbarMiniLayoutMath.AreaWidth(104, true, MiniAreaIds.LiveMetrics)
                < TaskbarMiniLayoutMath.AreaWidth(104, false, MiniAreaIds.LiveMetrics) * .6,
            "Collapsing rates saves meaningful width even when the expanded module is already compact.");
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
        Check(popover.CurrentLogicalBodyHeight <= 420,
            "Rate and cumulative history fit in a single unscrolled page.");
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
            form.SetCodexTokenUsage(new CodexTokenUsageSummary(1000, 642203161989, 12, at));
            form.SetSystemUsage(new SystemUsageSnapshot(34, 32UL << 30, 32UL << 30, 64UL << 30, 12, "3D", 2, 16, at)
            { DiskActivePercent = 22, NetworkReceiveBytesPerSecond = 12.5 * 1024 * 1024, NetworkSendBytesPerSecond = 128 * 1024, NetworkInterfaces = 1, NetworkHistory = new(at.AddDays(-2), 32_000_000_000, 12_000_000_000, at, false) });
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
            var expandedWidth = form.ClientSize.Width;
            foreach (var value in new double?[] { 0, 999.9, 1200, 1234567, null })
            {
                form.SetLiveOutputRate(new(at, value, 0, value ?? 0, 0, 0, 4, 0));
                form.SetMiniAreaFromCommand(MiniAreaIds.LiveMetrics, true, null);
                using var bitmap = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size));
                Check(form.ClientSize.Width < expandedWidth, "Output magnitude cannot expand the collapsed slot.");
                bitmap.Save(Path.Combine(directory, $"live-rates-narrow-{(value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")}-{dpi}dpi.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            form.SetLiveOutputRate(new(at, 53.2, 33.2, 20, 2, 1, 4, 0));
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
    private static void NetworkHistory(DateTimeOffset at)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tokenbar-network-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, NetworkTrafficLedger.FileName);
            var sentinel = Path.Combine(directory, "settings.json");
            File.WriteAllText(sentinel, "preserve original settings");
            var first = new NetworkTrafficLedger(directory);
            Check(first.Observe(null, null, at) is null && !File.Exists(path),
                "Missing observations cannot invent historical traffic.");
            var initial = first.Observe(1000, 2000, at)!;
            Check(initial.TotalBytes == 3000 && initial.Since == at, "Traffic starts at the first observed delta.");
            Check(first.Observe(100, 200, at.AddSeconds(1))!.TotalBytes == 3300, "Unsaved observed bytes remain in the live total.");
            first.Flush(at.AddSeconds(2));
            first.Flush(at.AddSeconds(2));
            var resumed = new NetworkTrafficLedger(directory);
            Check(resumed.Observe(null, null, at.AddSeconds(3))!.TotalBytes == 3300, "A repeated checkpoint cannot duplicate bytes.");
            var next = resumed.Observe(400, 500, at.AddSeconds(4))!;
            Check(next.TotalBytes == 4200 && next.Since == at, "Restart preserves totals and their original start time.");
            first.Observe(1, 1, at.AddSeconds(40));
            Check(first.SaveFailed && new NetworkTrafficLedger(directory).Observe(null, null, at.AddSeconds(41))!.TotalBytes == 4200,
                "An obsolete writer cannot replace a newer checkpoint.");
            var pending = new NetworkTrafficLedger(directory);
            File.Delete(path);
            Check(pending.Observe(1, 1, at.AddSeconds(42))!.TotalBytes == 4202 && pending.SaveFailed && !File.Exists(path),
                "A missing checkpoint cannot silently reset previously loaded totals.");
            File.WriteAllText(path, "{broken}");
            var broken = new NetworkTrafficLedger(directory);
            Check(broken.Observe(100, 200, at.AddSeconds(42)) is null && broken.SaveFailed && File.ReadAllText(path) == "{broken}",
                "Malformed history is preserved, never replaced by zero totals.");
            Check(File.ReadAllText(sentinel) == "preserve original settings", "Network history never rewrites settings.");
            Check(NetworkUsageSampler.Total(1024L * 1024 * 1024) == "1 GB", "Cumulative traffic keeps byte units separate from per-second rates.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void SharedConversationRates(DateTimeOffset at)
    {
        var feed = new CodexLiveRateFeed(at,
            [new("a", "model", at.AddSeconds(-3), at.AddSeconds(-1), 60) { TurnId = "old" },
             new("b", "model", at.AddSeconds(-9), at.AddSeconds(-6), 90) { TurnId = "b" }],
            [new("a", "model", "tools", null, at) { TurnId = "new" },
             new("b", "model", "generating", at.AddSeconds(-2), at) { TurnId = "b" }], 0);
        var rate = new ObservedOutputRate().Observe(feed, at);
        CodexConversationTurn Turn(string session, string turn) => new(session, turn, "model", "high",
            at.AddSeconds(-10), at, 100, 10, false, true);
        var usage = new CodexTokenUsageSummary(100, 100, 2, at)
        { RecentTurns = [Turn("a", "old"), Turn("a", "new"), Turn("b", "b")], LiveOutputRate = rate };
        var view = CodexConversationView.Create(usage, new(), at);
        Check(rate.Total == 40 && view.Rows.Sum(row => row.LiveRate?.Total ?? 0) == rate.Total,
            "Total is the exact sum of conversation live values from one shared sample.");
        Check(view.Rows.Single(row => row.TurnId == "old").LiveRate is null
            && view.Rows.Single(row => row.TurnId == "new").LiveRate?.Measured == 20,
            "A dialogue window crossing a turn boundary is shown once on its latest row.");
        Check(view.Rows.Single(row => row.TurnId == "b").LiveRate?.Estimated == 20,
            "The per-conversation value exposes its estimated contribution.");
        Check(view.Rows.All(row => row.OutputTokensPerSecond == 10), "Adding live speed preserves whole-turn averages.");
        Check(CodexConversationView.Create(usage, new(Search: "missing"), at).Rows.Count == 0 && usage.LiveOutputRate.Total == 40,
            "Filtering does not change the global live sample or its sum.");
        Check(CodexConversationView.Create(usage, new(), at.AddSeconds(16)).Rows.All(row => row.LiveRate is null),
            "Expired per-conversation live values become unavailable with the global sample.");
        var stopped = new ObservedOutputRate().Observe(feed with
        { CapturedAt = at.AddSeconds(4), Sessions = feed.Sessions.Select(s => s with { Phase = "idle" }).ToArray() }, at.AddSeconds(4));
        Check(stopped.Total == 0 && stopped.Conversations.All(row => row.Total == 0),
            "Ended dialogues have zero live output after the common window, without discarding their average.");
        var sorted = CodexConversationView.Create(usage, new(SortColumn: 10), at).Rows;
        Check(sorted[^1].TurnId == "old", "Live sorting leaves unavailable historical values last.");
        var text = NativeText.For("zh-CN");
        Check(text.CodexTurnLiveSpeed(view.Rows.Single(row => row.TurnId == "b")) == "≈20",
            "Estimated per-conversation speed stays visibly marked.");
        Check(!System.Text.Json.JsonSerializer.Serialize(view.Rows[0]).Contains("LiveRate", StringComparison.OrdinalIgnoreCase),
            "Transient live speeds are never serialized into retained history.");
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
            Check(sample.TurnId == "turn" && reader.LiveRateFeed.Sessions.Single().TurnId == "turn",
                "Response samples and live sessions bind to the same exact turn identity.");
            Check(reader.LiveRateFeed.Sessions.Single().Phase == "tools", "Completed response enters the tools phase.");
            File.AppendAllText(path, Line(0, "response_item", new { type = "function_call_output" }) + Environment.NewLine);
            reader.Refresh([path], at);
            Check(reader.LiveRateFeed!.Sessions.Single().Phase == "generating", "Tool output begins the next generation interval.");
            File.AppendAllText(path, Line(0, "event_msg", Usage(190, 300)) + Environment.NewLine);
            reader.Refresh([path], at.AddSeconds(1));
            Check(reader.LiveRateFeed!.Sessions.Single().Phase == "generating", "Duplicate counters cannot end active generation.");
            Check(reader.LiveRateFeed!.Responses.Count == 1, "Unchanged polling never duplicates responses.");
            File.AppendAllText(path, Line(1, "event_msg", new { type = "task_started", turn_id = "pending-first-usage" }) + Environment.NewLine);
            var pending = reader.Refresh([path], at.AddSeconds(3));
            var live = new ObservedOutputRate().Observe(reader.LiveRateFeed, at.AddSeconds(3));
            var projected = CodexConversationView.Create(new CodexTokenUsageSummary(0, 0, 1, at.AddSeconds(3))
                { RecentTurns = pending, LiveOutputRate = live }, new(), at.AddSeconds(3));
            Check(pending.Count == 1 && pending[0].TurnId == "pending-first-usage"
                && pending[0].OutputTokensPerSecond is null && pending[0].NewInputTokens is null,
                "A generating turn before its first usage record remains available without inventing measured metrics.");
            Check(live.Total > 0 && Math.Abs(projected.Rows.Sum(row => row.LiveRate?.Total ?? 0) - live.Total!.Value) < .000001,
                "First-response estimates appear on the latest conversation row and cannot disappear from the summed values.");
            File.AppendAllText(path, Line(4, "event_msg", Usage(280, 400)) + Environment.NewLine);
            var measured = reader.Refresh([path], at.AddSeconds(4)).Single();
            Check(measured.TurnId == "pending-first-usage" && measured.OutputTokens == 90
                && measured.NewInputTokens == 10 && measured.OutputTokensPerSecond == 30,
                "The first usage replaces unknown metrics on the same turn without duplicating or losing its output.");
            File.AppendAllText(path, Line(5, "event_msg", new { type = "task_started", turn_id = "empty-terminal" }) + Environment.NewLine
                + Line(6, "event_msg", new { type = "task_complete", turn_id = "empty-terminal" }) + Environment.NewLine);
            var empty = reader.Refresh([path], at.AddSeconds(6)).Single();
            Check(!empty.HasRecordedUsage && empty.OutputTokensPerSecond is null && empty.NewInputTokens is null,
                "A terminal turn with no usage cannot manufacture measured metrics.");
            live = new ObservedOutputRate().Observe(reader.LiveRateFeed, at.AddSeconds(6));
            projected = CodexConversationView.Create(new CodexTokenUsageSummary(0, 0, 1, at.AddSeconds(6))
                { RecentTurns = [empty], LiveOutputRate = live }, new(), at.AddSeconds(6));
            Check(live.Total > 0 && Math.Abs(projected.Rows.Sum(row => row.LiveRate?.Total ?? 0) - live.Total!.Value) < .000001,
                "A usage-free terminal turn still displays the dialogue's remaining three-second output tail exactly once.");
            var home = Path.Combine(directory, "home");
            Directory.CreateDirectory(Path.Combine(home, "sessions"));
            File.Copy(path, Path.Combine(home, "sessions", "fixture.jsonl"));
            var ledger = new CodexTokenUsageLedger(new AppSettingsStore(Path.Combine(directory, "data")), () => [home]);
            var saved = ledger.Refresh(at.AddSeconds(6))!;
            Check(saved.RecentTurns.Single().HasRecordedUsage == false && saved.ConversationHistory.Count == 0,
                "Usage-free identity rows remain transient and are never added to durable measured history.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
