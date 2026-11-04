using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class ConversationPresentationCacheTests
{
    private static CodexConversationTurn Turn(string id, DateTimeOffset at) =>
        new(id, id, "model", "high", at.AddMinutes(-2), at.AddSeconds(-10), 100, 10, true, true)
        { InputTokens = 200, CachedInputTokens = 100, ServiceTier = "standard", ProjectName = "Alpha" };

    private static CodexTokenUsageSummary Usage(DateTimeOffset at, CodexConversationTurn[] turns,
        params ConversationOutputRate[] rates) => new(100, 200, turns.Length, at)
        {
            ConversationHistory = turns,
            LiveOutputRate = new(at, rates.Sum(rate => rate.Total), rates.Sum(rate => rate.Measured),
                rates.Sum(rate => rate.Estimated), 1, 0, turns.Length, 0) { Conversations = rates },
        };

    internal static void Run()
    {
        var now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        var finished = Turn("finished", now);
        var running = Turn("running", now) with { Completed = false, ElapsedSeconds = 2 };
        var rows = new[] { finished, running };
        var cache = new ConversationPresentationCache();
        cache.Update(Usage(now, rows, new ConversationOutputRate("running", "running", 10, 0)), new(), now, TimeZoneInfo.Utc);
        var summary = cache.View!.Summary;
        var models = cache.ModelSpeeds;
        var groups = cache.ModelGroups;
        for (var index = 1; index <= 100; index++)
        {
            var at = now.AddSeconds(index);
            var refreshed = new[] { finished with { }, running with { ElapsedSeconds = index + 2 } };
            cache.Update(Usage(at, refreshed, new ConversationOutputRate("running", "running", index, 0)), new(), at, TimeZoneInfo.Utc);
        }
        Check(cache.RebuildCount == 1 && ReferenceEquals(summary, cache.View!.Summary)
            && ReferenceEquals(models, cache.ModelSpeeds) && ReferenceEquals(groups, cache.ModelGroups),
            "Repeated live samples reuse filtered aggregates and model groups.");
        Check(cache.View.Rows.Single(row => row.TurnId == "running").LiveRate!.Total == 100
            && cache.View.Rows.Single(row => row.TurnId == "running").ElapsedSeconds == 102,
            "Cached ordering still receives current live values and running durations.");

        cache.Update(Usage(now.AddSeconds(101), [finished with { OutputTokens = 300 }, running]), new(), now.AddSeconds(101), TimeZoneInfo.Utc);
        Check(cache.RebuildCount == 2 && cache.View!.Summary.OutputTokens == 400,
            "New recorded usage invalidates the cached aggregate.");
        cache.Update(Usage(now.AddSeconds(102), rows), new(Project: "missing"), now.AddSeconds(102), TimeZoneInfo.Utc);
        Check(cache.View!.Rows.Count == 0, "Filter changes rebuild the selected rows.");

        var midnight = DateTimeOffset.Parse("2026-11-04T23:59:00Z");
        var daily = new ConversationPresentationCache();
        var dailyRows = new[] { Turn("daily", midnight) };
        daily.Update(Usage(midnight, dailyRows), new(CodexHistoryPeriod.Today), midnight, TimeZoneInfo.Utc);
        daily.Update(Usage(midnight, dailyRows), new(CodexHistoryPeriod.Today), midnight.AddMinutes(2), TimeZoneInfo.Utc);
        Check(daily.RebuildCount == 2 && daily.View!.Rows.Count == 0, "Midnight invalidates calendar-day filtering.");

        var future = new ConversationPresentationCache();
        var futureRows = new[] { finished with { ObservedAt = now.AddMinutes(6) } };
        future.Update(Usage(now, futureRows), new(), now, TimeZoneInfo.Utc);
        Check(future.View!.Summary.SpeedSamples == 0, "Future measurements begin ineligible.");
        future.Update(Usage(now, futureRows), new(), now.AddMinutes(2), TimeZoneInfo.Utc);
        Check(future.RebuildCount == 2 && future.View!.Summary.SpeedSamples == 1,
            "Time-based sample eligibility invalidates the cache without a source edit.");

        var liveOnly = new CodexConversationTurn("live", "live", "model", null, now, now, 0, 0, false, false);
        var expiring = Usage(now, [liveOnly], new ConversationOutputRate("live", "live", 1, 0));
        var expiry = new ConversationPresentationCache();
        expiry.Update(expiring, new(), now, TimeZoneInfo.Utc);
        expiry.Update(expiring, new(), now.AddSeconds(16), TimeZoneInfo.Utc);
        Check(expiry.View!.Rows.Count == 0, "An expired live-only row does not remain in cached results.");

        var sorted = new ConversationPresentationCache();
        var order = new CodexConversationQuery(SortColumn: 10);
        sorted.Update(Usage(now, rows, new("finished", "finished", 20, 0), new("running", "running", 10, 0)), order, now, TimeZoneInfo.Utc);
        sorted.Update(Usage(now.AddSeconds(1), rows, new("finished", "finished", 5, 0), new("running", "running", 30, 0)), order, now.AddSeconds(1), TimeZoneInfo.Utc);
        Check(sorted.View!.Rows[0].TurnId == "running", "Live-rate sorting follows new measurements.");
    }

    internal static void Benchmark(string outputPath)
    {
        var now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        var rows = Enumerable.Range(0, 500).Select(index => Turn(index.ToString(), now.AddSeconds(-index))
            with { Model = "model-" + index % 6 }).ToArray();
        var cache = new ConversationPresentationCache();
        var query = new CodexConversationQuery();
        CodexTokenUsageSummary Sample(int index) => Usage(now.AddMilliseconds(index), rows,
            new ConversationOutputRate(rows[0].SessionId, rows[0].TurnId, index, 0));
        for (var index = 0; index < 20; index++)
        {
            var sample = Sample(index);
            var view = CodexConversationView.Create(sample, query, sample.CapturedAt, TimeZoneInfo.Utc);
            _ = CodexConversationSpeeds.GroupModels(CodexConversationSpeeds.BuildRows(view.Rows, sample.CapturedAt));
            cache.Update(sample, query, sample.CapturedAt, TimeZoneInfo.Utc);
        }
        const int iterations = 200;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 20; index < iterations + 20; index++)
        {
            var sample = Sample(index);
            var view = CodexConversationView.Create(sample, query, sample.CapturedAt, TimeZoneInfo.Utc);
            _ = CodexConversationSpeeds.GroupModels(CodexConversationSpeeds.BuildRows(view.Rows, sample.CapturedAt));
        }
        watch.Stop();
        var fullMilliseconds = watch.Elapsed.TotalMilliseconds;
        var fullBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (var index = iterations + 20; index < iterations * 2 + 20; index++)
        {
            var sample = Sample(index);
            cache.Update(sample, query, sample.CapturedAt, TimeZoneInfo.Utc);
        }
        watch.Stop();
        var report = new
        {
            syntheticRows = rows.Length, iterations, fullMilliseconds,
            cachedMilliseconds = watch.Elapsed.TotalMilliseconds, fullBytes,
            cachedBytes = GC.GetAllocatedBytesForCurrentThread() - before,
            cachedAggregateRebuilds = cache.RebuildCount,
        };
        File.WriteAllText(outputPath, System.Text.Json.JsonSerializer.Serialize(report));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
