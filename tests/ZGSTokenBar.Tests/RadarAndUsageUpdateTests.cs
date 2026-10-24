using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;
using ZGSTokenBar.Plugin.CodexLocal;

internal static class RadarAndUsageUpdateTests
{
    internal static void Run()
    {
        TrialParsingAndFallback();
        ConversationAccounting();
        LongConversationContinuity();
        ConversationLifecycle();
        ConversationMetrics();
        ProjectionAndPaging();
        CurrentPricing();
        Layouts();
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T08:00:00Z");
    private static readonly string[] Tasks = ["a", "b", "c", "d", "e"];

    private static string TrialHtml(DateTimeOffset observedAt) => "<script type=\"application/json\" data-dgr-facts>"
        + JsonSerializer.Serialize(new
        {
            settings = new[] { new { model = "gpt-6.1-sol", effort = "high" }, new { model = "gpt-6-astra", effort = "low" } },
            tasks = Tasks.Select(selector => new { selector }),
            snapshot = new
            {
                observed_at = observedAt,
                results = new
                {
                    codex = new Dictionary<string, object>
                    {
                        ["gpt-6.1-sol|high"] = Tasks.Select((key, index) => (key, index))
                            .ToDictionary(pair => pair.key, pair => new
                            {
                                status = pair.index == 4 ? "pending" : "scored",
                                reward = pair.index == 0 ? 0.5 : 1,
                                seconds = 60 * (pair.index + 1),
                            }),
                        ["gpt-6-astra|low"] = new { a = new { status = "scored", reward = -1, seconds = 1 } },
                    },
                    pi = new Dictionary<string, object>
                    {
                        ["gpt-6.1-sol|high"] = new { a = new { status = "scored", reward = 0, seconds = 999 } },
                    },
                },
            },
        }) + "</script>";

    private static ProviderRadarSnapshot Snapshot() => new(ProviderKind.Codex, "legacy", Now.AddDays(-30), Now,
        new RadarModel("gpt-5.6-sol", "Legacy", "max", 130, "green", 100, 112, 1, 120, null),
        [new RadarModel("deepseek-v4-flash", "DeepSeek", "max", 85, null, 50, 100, 0.1, 60, null)])
    {
        TrialFeed = RadarTrialParser.Parse(TrialHtml(Now)),
    };

    private static void TrialParsingAndFallback()
    {
        var trial = RadarTrialParser.Parse(TrialHtml(Now))!;
        Check(trial.Models.Count == 1 && trial.Models[0].ValidTasks == 4, "only valid scored Codex tasks contribute");
        Check(trial.Models[0].Score == 87.5 && trial.Models[0].AverageTaskSeconds == 150,
            "fractional rewards use /100; pending and Pi outcomes cannot skew Codex");
        Check(trial.Models[0].Passed is null, "fractional rewards are not a pass counter");
        Check(RadarTrialParser.Parse("<html>changed</html>") is null, "missing marker remains unavailable");
        var snapshot = Snapshot();
        var presentation = RadarPresentation.BuildCodex(snapshot);
        Check(presentation.Rows.Single().Model.Model == "gpt-6.1-sol", "new trial replaces only the Codex display");
        Check(presentation.IqLeader is null && presentation.Recommendations.Count == 0, "small trial makes no recommendations");
        Check(RadarPresentation.DeepSeekOnly(RadarPresentation.Build(snapshot)).Rows.Count == 1,
            "DeepSeek retains the separately scored historical feed");
        var cached = JsonSerializer.Deserialize<ProviderRadarSnapshot>(JsonSerializer.Serialize(snapshot))!;
        Check(RadarPresentation.BuildCodex(cached).TrialObservedAt == Now, "trial survives cache serialization");
        using var handler = new TrialHandler { Html = TrialHtml(Now) };
        var clock = Now;
        using var service = new RadarService(handler, () => clock);
        var fetched = service.FetchAsync().GetAwaiter().GetResult();
        Check(fetched.TrialFeed?.Models[0].Score == 87.5, "live assembly attaches the current trial");
        service.FetchAsync().GetAwaiter().GetResult();
        Check(handler.HomeCalls == 1, "home fetch is cached for ten minutes");
        clock = clock.AddMinutes(11);
        handler.Html = "changed";
        Check(service.FetchAsync().GetAwaiter().GetResult().TrialFeed?.ObservedAt == Now,
            "changed upstream preserves the last usable trial and its original date");
    }

    private static string Event(DateTimeOffset at, object payload, string type = "event_msg") =>
        JsonSerializer.Serialize(new { timestamp = at, type, payload }) + "\n";

    private static string Tokens(DateTimeOffset at, long output, long? last) => Event(at, new
    {
        type = "token_count",
        info = new
        {
            total_token_usage = new { total_tokens = output + 10000, input_tokens = 10000, cached_input_tokens = 5000, output_tokens = output },
            last_token_usage = last is null ? null : new { total_tokens = last + 100, input_tokens = 100, cached_input_tokens = 0, output_tokens = last },
        },
    });

    private static void ConversationAccounting()
    {
        var root = Path.Combine(Path.GetTempPath(), "zgstokenbar-turns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "rollout-2026-10-23-00000000-0000-0000-0000-000000000001.jsonl");
            var content = Tokens(Now.AddSeconds(-1), 1000, 100)
                + Event(Now, new { type = "task_started", turn_id = "turn-one" })
                + Event(Now, new { turn_id = "turn-one", model = "gpt-6.1-sol", effort = "high" }, "turn_context")
                + Tokens(Now.AddSeconds(10), 1200, 200)
                + Tokens(Now.AddSeconds(11), 1200, 200)
                + Tokens(Now.AddSeconds(30), 1300, 100)
                + Event(Now.AddSeconds(60), new { type = "task_complete", turn_id = "turn-one" });
            File.WriteAllText(path, content, new UTF8Encoding(false));
            var reader = new CodexConversationUsageReader();
            var turn = reader.Refresh([path], Now.AddSeconds(70)).Single();
            Check(turn.OutputTokens == 300 && turn.OutputTokensPerSecond == 5, "counter deltas deduplicate repeated token events");
            Check(turn.Completed && turn.Model == "gpt-6.1-sol", "completed turn retains model and terminal timing");
            reader.Refresh([path], Now.AddSeconds(80));
            Check(reader.ParsedFiles == 1, "unchanged files are not reparsed");
            File.AppendAllText(path, Tokens(Now.AddSeconds(62), 1360, 60));
            turn = reader.Refresh([path], Now.AddSeconds(80)).Single();
            Check(turn.OutputTokens == 360 && turn.OutputTokensPerSecond == 6,
                "late token usage updates preserve the terminal wall time");
            File.AppendAllText(path, Event(Now.AddSeconds(80), new { type = "task_started", turn_id = "two" })
                + Tokens(Now.AddSeconds(90), 40, 40));
            turn = reader.Refresh([path], Now.AddSeconds(100)).Single();
            Check(turn.OutputTokens == 40 && turn.OutputTokensPerSecond == 2, "counter resets count only the new request");
            File.AppendAllText(path, "{\"timestamp\":");
            turn = reader.Refresh([path], Now.AddSeconds(100)).Single();
            Check(turn.OutputTokens == 40, "an unfinished JSONL line is ignored");
            File.WriteAllText(path, Event(Now, new { type = "task_started", turn_id = "unknown" })
                + Tokens(Now.AddSeconds(10), 5000, null));
            Check(reader.Refresh([path], Now.AddSeconds(20)).Count == 0,
                "a missing initial counter cannot fabricate speed");
            File.WriteAllText(path, new string(' ', 3 * 1024 * 1024) + "\n" + Tokens(Now, 5000, 50));
            Check(reader.Refresh([path], Now).Count == 0, "a bounded tail without turn start has no inferred timing");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void LongConversationContinuity()
    {
        var root = Path.Combine(Path.GetTempPath(), "zgstokenbar-incremental-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "rollout-00000000-0000-0000-0000-000000000002.jsonl");
            var start = Event(Now, new { type = "task_started", turn_id = "long-turn" })
                + Event(Now, new { turn_id = "long-turn", model = "gpt-6.1-sol", effort = "max", cwd = "D:/projects/项目" }, "turn_context")
                + Tokens(Now.AddSeconds(1), 100, 100);
            File.WriteAllText(path, start, new UTF8Encoding(false));
            File.AppendAllText(path, new string(' ', CodexConversationUsageReader.ScanBudgetBytes) + "\n"
                + Tokens(Now.AddSeconds(50), 300, 200)
                + Event(Now.AddSeconds(60), new { type = "task_complete", turn_id = "long-turn" }));
            var reader = new CodexConversationUsageReader();
            Check(reader.Refresh([path], Now.AddSeconds(70)).Count == 0 && reader.PendingSessions == 1,
                "cold scan is bounded and does not publish an old partial turn");
            using (var busy = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(reader.Refresh([path], Now.AddSeconds(70)).Count == 0 && reader.PendingSessions == 1,
                    "busy catch-up retains its pending state without exposing an old turn");
            var turn = reader.Refresh([path], Now.AddSeconds(70)).Single();
            Check(reader.PendingSessions == 0 && turn.OutputTokens == 300 && turn.OutputTokensPerSecond == 5,
                "long conversation keeps the true start across bounded scans");
            Check(turn.ProjectName == "项目", "only the project basename is retained");
            var scanned = reader.ScannedBytes;
            var append = Event(Now.AddSeconds(80), new { type = "task_started", turn_id = "next" })
                + Tokens(Now.AddSeconds(90), 400, 100);
            var bytes = Encoding.UTF8.GetBytes(append);
            using (var stream = new FileStream(path, FileMode.Append)) stream.Write(bytes.AsSpan(0, bytes.Length - 2));
            reader.Refresh([path], Now.AddSeconds(100));
            using (var stream = new FileStream(path, FileMode.Append)) stream.Write(bytes.AsSpan(bytes.Length - 2));
            turn = reader.Refresh([path], Now.AddSeconds(100)).Single();
            Check(turn.OutputTokens == 100 && turn.OutputTokensPerSecond == 5,
                "partial appends are resumed exactly once");
            Check(reader.ScannedBytes - scanned == bytes.Length, "append refresh parses only new bytes");
            scanned = reader.ScannedBytes;
            reader.Refresh([path], Now.AddSeconds(101));
            Check(reader.ScannedBytes == scanned, "an unchanged long file performs no new scan");
            File.WriteAllText(path, Event(Now, new { type = "task_started", turn_id = "replacement" })
                + Tokens(Now.AddSeconds(10), 10, 10));
            Check(reader.Refresh([path], Now.AddSeconds(20)).Single().OutputTokens == 10,
                "file replacement clears the old cumulative cursor");
            File.WriteAllText(path, new string(' ', CodexConversationUsageReader.ScanBudgetBytes) + "\n"
                + Tokens(Now.AddSeconds(-1), 0, 0) + start + Tokens(Now.AddSeconds(50), 300, 200)
                + Event(Now.AddSeconds(60), new { type = "task_complete", turn_id = "long-turn" }));
            var cold = new CodexConversationUsageReader();
            Check(cold.Refresh([path], Now.AddSeconds(70)).Single().OutputTokens == 300
                && cold.PendingSessions == 0 && cold.ScannedBytes < CodexConversationUsageReader.ScanBudgetBytes,
                "a complete recent turn bootstraps without replaying a large historical file");
            File.WriteAllText(path, Tokens(Now, 100, 100)
                + new string(' ', CodexConversationUsageReader.ScanBudgetBytes) + "\n"
                + Event(Now.AddSeconds(20), new { type = "task_started", turn_id = "repeated-baseline" })
                + Tokens(Now.AddSeconds(21), 100, 100) + Tokens(Now.AddSeconds(30), 150, 50));
            var uncertain = new CodexConversationUsageReader();
            Check(uncertain.Refresh([path], Now.AddSeconds(40)).Count == 0 && uncertain.PendingSessions == 1,
                "tail bootstrap requires a counter baseline preceding the current turn");
            Check(uncertain.Refresh([path], Now.AddSeconds(40)).Single().OutputTokens == 50,
                "historical replay excludes an unchanged prior-turn token snapshot");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ConversationLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "zgstokenbar-turn-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "lifecycle.jsonl");
            File.WriteAllText(path, Event(Now.AddSeconds(3), new { type = "task_started", turn_id = "one", started_at = Now })
                + Tokens(Now.AddSeconds(10), 300, 300));
            var reader = new CodexConversationUsageReader();
            var turn = reader.Refresh([path], Now.AddSeconds(20)).Single();
            Check(turn.OutputTokensPerSecond == 15, "explicit turn start excludes log delivery delay");
            Check(reader.Refresh([path], Now.AddMinutes(3)).Single().OutputTokensPerSecond is null,
                "unfinished dormant sessions do not pretend to be live speeds");
            File.AppendAllText(path, Event(Now.AddSeconds(70), new
            {
                type = "task_complete", turn_id = "one", completed_at = Now.AddSeconds(60), time_to_first_token_ms = 1200,
            }));
            turn = reader.Refresh([path], Now.AddMinutes(4)).Single();
            Check(turn.Completed && turn.OutputTokensPerSecond == 5 && turn.FirstTokenSeconds == 1.2,
                "explicit completion timing and first token latency remain separate");
            File.AppendAllText(path, Event(Now.AddMinutes(5), new { type = "task_started", turn_id = "two" })
                + Tokens(Now.AddMinutes(5).AddSeconds(10), 400, 100)
                + Event(Now.AddMinutes(5).AddSeconds(20), new { type = "turn_aborted", turn_id = "two" }));
            turn = reader.Refresh([path], Now.AddMinutes(6)).Single();
            Check(turn.Interrupted && turn.ElapsedSeconds == 20 && turn.State == "interrupted",
                "interrupted turn freezes its elapsed time with an explicit state");
            File.WriteAllText(path, Tokens(Now, 100, 100)
                + Event(Now.AddSeconds(20), new { type = "task_started", turn_id = "late-prior" })
                + Tokens(Now.AddSeconds(10), 120, 20)
                + Tokens(Now.AddSeconds(30), 150, 30));
            turn = reader.Refresh([path], Now.AddSeconds(40)).Single();
            Check(turn.OutputTokens == 30, "late prior-turn counters are not charged to the new turn");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ConversationMetrics()
    {
        var root = Path.Combine(Path.GetTempPath(), "zgstokenbar-turn-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "metrics.jsonl");
            string UsageEvent(int seconds, long input, long cached, long output, long lastInput, long lastCached, long lastOutput) =>
                Event(Now.AddSeconds(seconds), new
                {
                    type = "token_count",
                    info = new
                    {
                        total_token_usage = new { input_tokens = input, cached_input_tokens = cached, output_tokens = output,
                            cache_write_input_tokens = 0, total_tokens = input + output },
                        last_token_usage = new { input_tokens = lastInput, cached_input_tokens = lastCached, output_tokens = lastOutput,
                            cache_write_input_tokens = 0, total_tokens = lastInput + lastOutput },
                    },
                });
            var last = UsageEvent(20, 2500, 1800, 250, 1500, 1000, 150);
            File.WriteAllText(path, Event(Now, new { type = "task_started", turn_id = "priced" })
                + Event(Now, new { turn_id = "priced", model = "gpt-6.1-sol", service_tier = "default" }, "turn_context")
                + UsageEvent(10, 1000, 800, 100, 1000, 800, 100) + last + last);
            var reader = new CodexConversationUsageReader();
            var turn = reader.Refresh([path], Now.AddSeconds(30)).Single();
            Check(turn.InputTokens == 2500 && turn.NewInputTokens == 700 && turn.CachedInputTokens == 1800
                && turn.OutputTokens == 250, "table counts use deltas and separate new input from cache hits");
            Check(turn.ApiEquivalentUsd == 0.00408m && !turn.CostIsPartial,
                "per-request costs are summed without repricing the combined context or duplicate events");
            File.AppendAllText(path, Event(Now.AddSeconds(31), new { turn_id = "priced", model = "unknown-model" }, "turn_context")
                + UsageEvent(40, 4000, 3000, 400, 1500, 1200, 150));
            turn = reader.Refresh([path], Now.AddSeconds(50)).Single();
            Check(turn.NewInputTokens == 1000 && turn.CachedInputTokens == 3000
                && turn.ApiEquivalentUsd == 0.00408m && turn.CostIsPartial && turn.Model is null,
                "mixed-model turns keep measured counts and an explicit cost lower bound");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ProjectionAndPaging()
    {
        var usage = Usage();
        var plugin = new CodexLocalPlugin();
        var projection = CorePluginProjection.CodexUsage(plugin.Manifest.Id, usage, Now);
        Check(PluginValidation.ValidateSnapshot(plugin.Manifest, projection).Count == 0,
            "all recent conversations fit the public contribution contract");
        Check(projection.Details.Count == usage.RecentTurns.Count + 1, "CLI exposes every conversation, not only the first page");
        var first = projection.Details[1].Sections[0].Rows;
        Check(first.Single(row => row.LabelKey == "usage.turn.average-output-tps").Value.Number == usage.RecentTurns[0].OutputTokensPerSecond,
            "CLI and UI use the same rate");
        var snapshot = Snapshot();
        var radar = CorePluginProjection.Radar("zgstokenbar.intelligence.radar", snapshot);
        Check(radar.Radar.Count == 2 && radar.Radar[0].Rows.Single().Model.Contains("6.1-sol"), "API exports current trial and separate history");
        Check(radar.Radar[0].Rows[0].SampleCount == 4 && radar.Radar[0].Footer[0].Value.Integer == 100,
            "API includes the same sample count and score scale");
        Check(RadarPresentation.BuildCodex(snapshot, historical: true).Rows[0].Model.Model == "gpt-5.6-sol",
            "history remains accessible without mixing score scales");
        var routing = RadarRoutingSnapshotBuilder.Build(snapshot);
        Check(routing.IsHistorical && routing.Benchmark == "legacy-150" && routing.SourceUpdatedAt == snapshot.SourceUpdatedAt,
            "routing export identifies its historical benchmark and source time");
        var unread = new RadarAlertState();
        Check(RadarAlertTracker.HasUnread(unread, snapshot, RadarSurfaceIds.Codex)
            && !RadarAlertTracker.HasUnread(unread, snapshot, RadarSurfaceIds.DeepSeek), "trial update belongs to its own surface");
        RadarAlertTracker.RecordViewed(unread, snapshot, RadarSurfaceIds.Codex);
        Check(!RadarAlertTracker.HasUnread(unread, snapshot, RadarSurfaceIds.Codex), "viewing a trial records its own identity");
        Check(RadarAlertTracker.HasUnread(unread, snapshot with
        {
            TrialFeed = snapshot.TrialFeed! with { ObservedAt = Now.AddHours(1) },
        }, RadarSurfaceIds.Codex), "new trial is unread even when the old benchmark is unchanged");
        var last = CodexConversationPage.Create(6, 10);
        Check(last.Offset == 6 && last.Count == 4 && !last.HasNext && last.HasPrevious, "last conversation page is bounded");
        Check(CodexConversationPage.Create(8, 3).Offset == 0, "shrinking lists clamp the current page");
        var customPage = CodexConversationPage.Create(3, 10, 3);
        Check(customPage.Offset == 3 && customPage.Count == 3 && customPage.PageCount == 4,
            "conversation paging honors an adaptive page length");
        var layout = RadarPopoverLayout.CreateTokenUsage(192, 10);
        var point = new Point(layout.RecentTurnsBounds.Left + 5, layout.RecentTurnsBounds.Top + 75);
        Check(layout.RecentTurnIndexAt(point, 6, 10) == 6, "DPI-aware row hit testing maps to the selected page");
    }

    private static void CurrentPricing()
    {
        var usage = new CodexModelTokenUsage("gpt-6.1-sol", 1000000, 500000, 100000, CacheWriteInputTokens: 100000);
        Check(CodexPricingCatalog.Estimate(usage).ApiEquivalentUsd == 2.1m, "6.1 Sol cache read/write components are disjoint");
        Check(CodexPricingCatalog.Estimate(usage with { ServiceTier = "fast" }).ApiEquivalentUsd == 4.2m, "Fast multiplies the applicable prices");
        Check(CodexPricingCatalog.Estimate(usage with { ServiceTier = "ultrafast" }).ApiEquivalentUsd == 12.6m, "6.1 Sol Ultrafast rate is explicit");
        Check(CodexPricingCatalog.Estimate(usage with { IsLongContext = true }).ApiEquivalentUsd == 3.7m, "long context uses separate input/output multipliers");
        Check(CodexPricingCatalog.Estimate(usage with { Model = "gpt-6-astra", ServiceTier = "ultrafast" }).ApiEquivalentUsd == 64.5m,
            "Astra Ultrafast uses its verified API prices");
        Check(CodexPricingCatalog.Estimate(usage with { Model = "gpt-6-luna", ServiceTier = "ultrafast" }).ApiEquivalentUsd is null,
            "unsupported tier remains unpriced");
        foreach (var model in new[] { "gpt-6-astra", "gpt-6-sol", "gpt-6-luna" })
            Check(CodexPricingCatalog.Estimate(usage with { Model = model }).IsPriced, "6-series standard pricing exists");
        Check(CodexPricingCatalog.Estimate(usage with { Model = "unknown-model" }).ApiEquivalentUsd is null, "unknown models remain unpriced");
    }

    private static CodexTokenUsageSummary Usage() => new(123456, 592341074667, 1234, Now,
        1000, 800, 100, 80, CodexSpendPeriod.Empty, CodexSpendPeriod.Empty, CodexSpendPeriod.Empty)
    {
        RecentTurns = Enumerable.Range(1, 10).Select(index => new CodexConversationTurn(
            $"00000000-0000-0000-0000-{index:000000000000}", "turn-" + index, "gpt-6.1-sol", "high",
            Now.AddSeconds(-60 * index), Now, 300 * index, 60 * index, true, true)
        {
            ProjectName = index % 2 == 0 ? "ZGSTokenBar" : "POB",
            FirstTokenSeconds = 1.2,
            InputTokens = 120000 + index * 1321,
            CachedInputTokens = 110000 + index * 120,
            CacheWriteInputTokens = 0,
            ApiEquivalentUsd = 0.0134m * index,
        }).ToArray(),
    };

    private static void Layouts()
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            var layout = RadarPopoverLayout.CreateGrouped(dpi, ["gpt-6.1-sol"], new HashSet<string>(), false,
                hasSpendSummary: true, maximumBodyHeight: dpi * 6, recentTurnCount: 4);
            Check(layout.HasRecentColumn, "Radar uses a separate recent-conversation column");
            Check(layout.RecentTurnsBounds.Left > layout.TableViewport.Right, "turn panel clears the compact Radar column");
            Check(layout.RecentTurnsBounds.Bottom <= layout.BodySize.Height, "turn panel fits the split body");
            Check(layout.RecentColumns.Count == 7 && layout.RecentColumns[^1].Right == layout.RecentTurnsBounds.Right,
                "split recent-conversation columns fit without overlap at each DPI");
            Check(layout.RecentPageSize == 4, "four recent conversations fit on the split page");
            using var headerFont = new Font("Segoe UI", (float)(7 * dpi / 96d), FontStyle.Bold, GraphicsUnit.Pixel);
            foreach (var text in new[] { NativeText.For("zh-CN"), NativeText.For("en") })
            {
                var headers = text.CodexConversationHeaders;
                for (var index = 0; index < headers.Length; index++)
                {
                    var width = System.Windows.Forms.TextRenderer.MeasureText(
                        headers[index], headerFont, Size.Empty,
                        System.Windows.Forms.TextFormatFlags.NoPadding
                        | System.Windows.Forms.TextFormatFlags.NoPrefix
                        | System.Windows.Forms.TextFormatFlags.SingleLine).Width;
                    Check(width <= layout.RecentColumns[index].Width,
                        $"{dpi} DPI {text.Locale} recent header {index} fits");
                }
            }
            var token = RadarPopoverLayout.CreateTokenUsage(dpi, 4);
            Check(token.RecentTurnsBounds.Bottom <= token.BodySize.Height, "local panel fits at all supported DPI");
            Check(token.RecentColumns.Count == 7 && token.RecentColumns[^1].Right == token.RecentTurnsBounds.Right,
                "seven compact metric columns fit without overlap at each DPI");
        }

        var compactRadar = RadarPopoverLayout.CreateGrouped(
            96,
            ["gpt-6.1-sol"],
            new HashSet<string>(),
            false,
            hasSpendSummary: true,
            recentTurnCount: 12);
        var tallRadar = RadarPopoverLayout.CreateGrouped(
            96,
            Enumerable.Repeat<string?>("gpt-6.1-sol", 12).ToArray(),
            new HashSet<string>(),
            false,
            hasSpendSummary: true,
            recentTurnCount: 12);
        Check(compactRadar.RecentPageSize < tallRadar.RecentPageSize,
            "recent page length expands with the available Radar height");
        Check(tallRadar.RecentPageSize == 12,
            "a tall Radar page shows all twelve recent conversations when they fit");
    }

    internal static void Capture(string directory)
    {
        Directory.CreateDirectory(directory);
        var snapshot = Snapshot();
        var presentation = RadarPresentation.BuildCodex(snapshot);
        var usage = Usage();
        foreach (var dpi in new[] { 96, 192 })
        foreach (var locale in new[] { "zh-CN", "en" })
        foreach (var radar in new[] { true, false })
        {
            var layout = radar
                ? RadarPopoverLayout.CreateGrouped(dpi, presentation.Rows.Select(row => row.Model.Model).ToArray(),
                    new HashSet<string>(), false, hasSpendSummary: true, recentTurnCount: usage.RecentTurns.Count, showLegend: false)
                : RadarPopoverLayout.CreateTokenUsage(dpi, usage.RecentTurns.Count);
            using var bitmap = new Bitmap(layout.BodySize.Width, layout.BodySize.Height + layout.TailSize);
            bitmap.SetResolution(dpi, dpi);
            using var graphics = Graphics.FromImage(bitmap);
            using var renderer = new RadarPopoverRenderer();
            renderer.Draw(graphics, layout, PopoverTailSide.Bottom, layout.BodySize.Width / 2,
                new RadarViewState(snapshot, Now, false, null), radar ? presentation : null, null,
                NativeText.For(locale), usage, recentTurnOffset: 0, canSwitchBenchmark: radar);
            bitmap.Save(Path.Combine(directory, $"{(radar ? "radar-trial" : "local-turns")}-{locale}-{dpi}.png"), ImageFormat.Png);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TrialHandler : HttpMessageHandler
    {
        public string Html = "";
        public int HomeCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "public radar sends no credentials");
            var home = request.RequestUri == RadarService.SiteUri;
            if (home) HomeCalls++;
            var text = home ? Html : request.RequestUri == RadarService.SummaryUri
                ? """{"model_iq":{"latest":{"date":"2026-09-13","model":"gpt-5.6-sol","reasoning_effort":"max","score":130}}}"""
                : request.RequestUri == RadarService.MeasurementsUri ? "{\"points\":[]}" : "{\"groups\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(text, Encoding.UTF8, home ? "text/html" : "application/json"),
            });
        }
    }
}
