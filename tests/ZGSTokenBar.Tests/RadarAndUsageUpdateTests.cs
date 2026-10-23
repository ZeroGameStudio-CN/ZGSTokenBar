using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class RadarAndUsageUpdateTests
{
    internal static void Run()
    {
        TrialParsingAndFallback();
        ConversationAccounting();
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

    private static void CurrentPricing()
    {
        var usage = new CodexModelTokenUsage("gpt-6.1-sol", 1000000, 500000, 100000, CacheWriteInputTokens: 100000);
        Check(CodexPricingCatalog.Estimate(usage).ApiEquivalentUsd == 2.1m, "6.1 Sol cache read/write components are disjoint");
        Check(CodexPricingCatalog.Estimate(usage with { ServiceTier = "fast" }).ApiEquivalentUsd == 4.2m, "Fast multiplies the applicable prices");
        Check(CodexPricingCatalog.Estimate(usage with { ServiceTier = "ultrafast" }).ApiEquivalentUsd == 12.6m, "6.1 Sol Ultrafast rate is explicit");
        Check(CodexPricingCatalog.Estimate(usage with { IsLongContext = true }).ApiEquivalentUsd == 3.7m, "long context uses separate input/output multipliers");
        Check(CodexPricingCatalog.Estimate(usage with { Model = "gpt-6-astra", ServiceTier = "ultrafast" }).ApiEquivalentUsd is null,
            "unverified tier is unpriced");
        foreach (var model in new[] { "gpt-6-astra", "gpt-6-sol", "gpt-6-luna" })
            Check(CodexPricingCatalog.Estimate(usage with { Model = model }).IsPriced, "6-series standard pricing exists");
        Check(CodexPricingCatalog.Estimate(usage with { Model = "unknown-model" }).ApiEquivalentUsd is null, "unknown models remain unpriced");
    }

    private static CodexTokenUsageSummary Usage() => new(123456, 592341074667, 1234, Now,
        1000, 800, 100, 80, CodexSpendPeriod.Empty, CodexSpendPeriod.Empty, CodexSpendPeriod.Empty)
    {
        RecentTurns = Enumerable.Range(1, 4).Select(index => new CodexConversationTurn(
            $"{index:00000000}-0000-0000-0000-000000000000", "turn-" + index, "gpt-6.1-sol", "high",
            Now.AddSeconds(-60 * index), Now, 300 * index, 60 * index, true, true)).ToArray(),
    };

    private static void Layouts()
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            var layout = RadarPopoverLayout.CreateGrouped(dpi, ["gpt-6.1-sol"], new HashSet<string>(), false,
                hasSpendSummary: true, maximumBodyHeight: dpi * 6, recentTurnCount: 4);
            Check(layout.RecentTurnsBounds.Bottom <= layout.FooterSpendBounds.Top, "turn panel does not overlap spend");
            Check(layout.TableViewport.Bottom <= layout.RecentTurnsBounds.Top, "scroll viewport excludes the turn panel");
            var token = RadarPopoverLayout.CreateTokenUsage(dpi, 4);
            Check(token.RecentTurnsBounds.Bottom <= token.BodySize.Height, "local panel fits at all supported DPI");
        }
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
                    new HashSet<string>(), false, hasSpendSummary: true, recentTurnCount: 4)
                : RadarPopoverLayout.CreateTokenUsage(dpi, 4);
            using var bitmap = new Bitmap(layout.BodySize.Width, layout.BodySize.Height + layout.TailSize);
            bitmap.SetResolution(dpi, dpi);
            using var graphics = Graphics.FromImage(bitmap);
            using var renderer = new RadarPopoverRenderer();
            renderer.Draw(graphics, layout, PopoverTailSide.Bottom, layout.BodySize.Width / 2,
                new RadarViewState(snapshot, Now, false, null), radar ? presentation : null, null,
                NativeText.For(locale), usage);
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
