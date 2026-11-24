using System.Drawing;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.Tests;

internal static class MultiProviderTokenLedgerTests
{
    internal static void Run()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"zgstokenbar-multiprovider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var syncContext = SynchronizationContext.Current;
        try
        {
            TestParsersIncrementalCacheAndDeduplication(tempRoot);
            TestAggregatorBreakdownAndFormatting();
            TestBarFormCombinedTotal();
            Check(ReferenceEquals(syncContext, SynchronizationContext.Current),
                "Multi-provider token tests cannot leak a UI synchronization context.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); }
            catch { }
        }
    }

    private static void TestParsersIncrementalCacheAndDeduplication(string tempRoot)
    {
        var dataDir = Path.Combine(tempRoot, "data");
        var homeDir = Path.Combine(tempRoot, "home");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(homeDir);

        var paths = new UsagePaths(homeDir, Path.Combine(tempRoot, "roaming"), Path.Combine(tempRoot, "local"));
        var env = new MapUsageEnvironment(new Dictionary<string, string>(StringComparer.Ordinal));

        var localToday = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(localToday.Date);
        var yesterday = today.AddDays(-1);
        var todayIso = new DateTimeOffset(today.Year, today.Month, today.Day, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(localToday)).ToString("O");
        var yesterdayIso = new DateTimeOffset(yesterday.Year, yesterday.Month, yesterday.Day, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(localToday)).ToString("O");

        // 1. AGY: transcript.jsonl counts input + cache_read + output, ignores transcript_full.jsonl.
        var agyLogsDir = Path.Combine(homeDir, ".gemini", "antigravity", "brain", "conv-1", ".system_generated", "logs");
        Directory.CreateDirectory(agyLogsDir);
        var agyTranscript = Path.Combine(agyLogsDir, "transcript.jsonl");
        File.WriteAllLines(agyTranscript,
        [
            "{\"type\":\"USER_INPUT\",\"content\":\"hello\"}",
            $"{{\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{todayIso}\",\"input_tokens\":1000,\"cache_read_tokens\":4000,\"output_tokens\":250}}",
            $"{{\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{yesterdayIso}\",\"input_tokens\":500,\"cache_read_tokens\":1500,\"output_tokens\":100}}"
        ]);
        File.WriteAllText(Path.Combine(agyLogsDir, "transcript_full.jsonl"),
            $"{{\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{todayIso}\",\"input_tokens\":9999999,\"output_tokens\":9999999}}\n");

        // 2. Grok:
        //    - session-a has ONLY updates.jsonl initially (then later gets usage.json -> updates.jsonl must be superseded).
        //    - session-b has BOTH usage.json and updates.jsonl -> usage.json only (no double-count), and cachedReadTokens is NOT added on top of inputTokens.
        var grokSessionA = Path.Combine(homeDir, ".grok", "sessions", "proj-a", "sess-a");
        var grokSessionB = Path.Combine(homeDir, ".grok", "sessions", "proj-b", "sess-b");
        Directory.CreateDirectory(grokSessionA);
        Directory.CreateDirectory(grokSessionB);
        var unixToday = new DateTimeOffset(today.Year, today.Month, today.Day, 11, 0, 0, TimeZoneInfo.Local.GetUtcOffset(localToday)).ToUnixTimeSeconds();
        var grokUpdatesA = Path.Combine(grokSessionA, "updates.jsonl");
        File.WriteAllLines(grokUpdatesA,
        [
            "{\"method\":\"_x.ai/session/update\",\"params\":{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"text\":\"hi\"}}}",
            $"{{\"method\":\"_x.ai/session/update\",\"timestamp\":{unixToday},\"params\":{{\"update\":{{\"sessionUpdate\":\"turn_completed\",\"usage\":{{\"totalTokens\":1200,\"inputTokens\":1000,\"outputTokens\":200}}}}}}}}"
        ]);

        var grokUsageB = Path.Combine(grokSessionB, "usage.json");
        var grokUpdatesB = Path.Combine(grokSessionB, "updates.jsonl");
        File.WriteAllText(grokUsageB,
            $$"""
            {
              "updatedAt": "{{todayIso}}",
              "session": { "totalTokens": 5000, "inputTokens": 4500, "outputTokens": 500, "cachedReadTokens": 4000 },
              "turns": [
                { "startedAt": "{{yesterdayIso}}", "endedAt": "{{yesterdayIso}}", "totalTokens": 2000, "inputTokens": 1800, "outputTokens": 200, "cachedReadTokens": 1500 },
                { "startedAt": "{{todayIso}}", "endedAt": "{{todayIso}}", "totalTokens": 3000, "inputTokens": 2700, "outputTokens": 300, "cachedReadTokens": 2500 }
              ]
            }
            """);
        File.WriteAllText(grokUpdatesB,
            $"{{\"method\":\"_x.ai/session/update\",\"timestamp\":{unixToday},\"params\":{{\"update\":{{\"sessionUpdate\":\"turn_completed\",\"usage\":{{\"totalTokens\":99999}}}}}}}}\n");

        // 3. Claude: streaming chunks sharing message.id must deduplicate to the max chunk total, not sum chunks.
        var claudeProjDir = Path.Combine(homeDir, ".claude", "projects", "work");
        Directory.CreateDirectory(claudeProjDir);
        var claudeLog = Path.Combine(claudeProjDir, "session-1.jsonl");
        File.WriteAllLines(claudeLog,
        [
            $"{{\"timestamp\":\"{todayIso}\",\"requestId\":\"req-1\",\"message\":{{\"id\":\"msg-1\",\"usage\":{{\"input_tokens\":100,\"cache_creation_input_tokens\":200,\"cache_read_input_tokens\":700,\"output_tokens\":5}}}}}}",
            $"{{\"timestamp\":\"{todayIso}\",\"requestId\":\"req-1\",\"message\":{{\"id\":\"msg-1\",\"usage\":{{\"input_tokens\":100,\"cache_creation_input_tokens\":200,\"cache_read_input_tokens\":700,\"output_tokens\":50}}}}}}",
            $"{{\"timestamp\":\"{yesterdayIso}\",\"requestId\":\"req-2\",\"message\":{{\"id\":\"msg-2\",\"usage\":{{\"input_tokens\":50,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":150,\"output_tokens\":25}}}}}}"
        ]);

        var ledger = new ExternalProviderTokenLedger(dataDir, paths, env);
        var first = ledger.Refresh(localToday) ?? throw new InvalidOperationException("Expected external summary.");
        Check(first.ByProviderTotal[ExternalProviderTokenLedger.AntigravityProviderId] == (1000 + 4000 + 250) + (500 + 1500 + 100),
            "AGY sums input + cache_read + output across days and skips transcript_full.");
        Check(first.ByProviderTotal[ExternalProviderTokenLedger.GrokProviderId] == 1200 + 5000,
            "Grok combines updates.jsonl fallback and usage.json without double-counting co-located updates.jsonl.");
        Check(first.ByProviderDay[ExternalProviderTokenLedger.GrokProviderId][yesterday] == 2000,
            "Grok turns attribute historical day usage without double-counting cachedReadTokens.");
        Check(first.ByProviderTotal[ExternalProviderTokenLedger.ClaudeProviderId] == (100 + 200 + 700 + 50) + (50 + 150 + 25),
            "Claude deduplicates streaming chunks sharing message.id and sums all four usage components.");

        var ledgerPath = Path.Combine(dataDir, ExternalProviderTokenLedger.FileName);
        Check(File.Exists(ledgerPath), "External provider ledger persists to its own JSON file.");
        Check(!File.Exists(Path.Combine(dataDir, "codex-token-usage-index.json")),
            "External provider ledger never creates or touches codex-token-usage-index.json.");

        // Unchanged refresh reuses stat cache without corrupting totals; fresh ledger Snapshot reads persisted file cleanly.
        var stampBefore = new FileInfo(ledgerPath).LastWriteTimeUtc;
        var second = ledger.Refresh(localToday.AddMinutes(1))!;
        Check(second.TotalTokens == first.TotalTokens, "Unchanged stat refresh retains identical totals.");
        Check(new FileInfo(ledgerPath).LastWriteTimeUtc == stampBefore,
            "Unchanged stat refresh does not rewrite the persisted ledger file.");

        var restored = new ExternalProviderTokenLedger(dataDir, paths, env).Snapshot()!;
        Check(restored.TotalTokens == first.TotalTokens, "Cold Snapshot loads totals directly without rescanning logs.");

        // Now session-a finalizes usage.json with 1500 tokens: updates.jsonl (1200) must be superseded, not added!
        File.WriteAllText(Path.Combine(grokSessionA, "usage.json"),
            $$"""
            {
              "updatedAt": "{{todayIso}}",
              "session": { "totalTokens": 1500, "inputTokens": 1200, "outputTokens": 300 }
            }
            """);
        var third = ledger.Refresh(localToday.AddMinutes(2))!;
        Check(third.ByProviderTotal[ExternalProviderTokenLedger.GrokProviderId] == 1500 + 5000,
            "Promoting a Grok session from updates.jsonl to usage.json evicts updates.jsonl instead of double-counting.");

        // Test environment variable override precedence (ANTIGRAVITY_DATA_DIR / GROK_HOME / CLAUDE_CONFIG_DIR).
        var customDataDir = Path.Combine(tempRoot, "custom-data");
        var customAgy = Path.Combine(tempRoot, "custom-agy");
        var customLogs = Path.Combine(customAgy, "brain", "c2", ".system_generated", "logs");
        Directory.CreateDirectory(customLogs);
        File.WriteAllText(Path.Combine(customLogs, "transcript.jsonl"),
            $"{{\"created_at\":\"{todayIso}\",\"input_tokens\":42,\"output_tokens\":8}}\n");
        var overrideEnv = new MapUsageEnvironment(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ANTIGRAVITY_DATA_DIR"] = customAgy,
            ["GROK_HOME"] = Path.Combine(tempRoot, "missing-grok"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(tempRoot, "missing-claude"),
        });
        var overridden = new ExternalProviderTokenLedger(customDataDir, paths, overrideEnv).Refresh(localToday)!;
        Check(overridden.TotalTokens == 50
            && overridden.ByProviderTotal[ExternalProviderTokenLedger.AntigravityProviderId] == 50
            && !overridden.ByProviderTotal.ContainsKey(ExternalProviderTokenLedger.GrokProviderId),
            "Environment overrides redirect discovery roots cleanly.");
    }

    private static void TestAggregatorBreakdownAndFormatting()
    {
        var today = new DateOnly(2026, 11, 24);
        var weekDay = today.AddDays(-1);
        var monthDay = today.AddDays(-15);
        var oldDay = today.AddDays(-60);
        var codex = new CodexTokenUsageSummary(
            TodayTokens: 1_000_000_000,
            LocalTokens: 880_000_000_000,
            SessionCount: 10,
            CapturedAt: DateTimeOffset.UtcNow)
        {
            ActivityDays =
            [
                new(oldDay.ToString("yyyy-MM-dd"), CodexSpendPeriod.Empty with { InputTokens = 800_000_000_000 }),
                new(monthDay.ToString("yyyy-MM-dd"), CodexSpendPeriod.Empty with { InputTokens = 4_000_000_000 }),
                new(weekDay.ToString("yyyy-MM-dd"), CodexSpendPeriod.Empty with { InputTokens = 2_000_000_000 }),
                new(today.ToString("yyyy-MM-dd"), CodexSpendPeriod.Empty with { InputTokens = 1_000_000_000 }),
            ],
        };
        var external = new ExternalProviderTokenSummary(
            new Dictionary<string, IReadOnlyDictionary<DateOnly, long>>
            {
                [ExternalProviderTokenLedger.AntigravityProviderId] = new Dictionary<DateOnly, long>
                {
                    [today] = 100_000_000,
                    [weekDay] = 50_000_000,
                    [oldDay] = 50_000_000,
                },
                [ExternalProviderTokenLedger.GrokProviderId] = new Dictionary<DateOnly, long>
                {
                    [weekDay] = 500_000_000,
                },
                [ExternalProviderTokenLedger.ClaudeProviderId] = new Dictionary<DateOnly, long>(),
            },
            new Dictionary<string, long>
            {
                [ExternalProviderTokenLedger.AntigravityProviderId] = 200_000_000,
                [ExternalProviderTokenLedger.GrokProviderId] = 500_000_000,
            },
            DateTimeOffset.UtcNow);

        var agg = MultiProviderTokenAggregator.Build(codex, external, today)!;
        Check(agg.TodayTokens == 1_100_000_000, "Today total combines Codex + AGY.");
        Check(agg.AllTimeTotal == 880_700_000_000, "All-time total combines Codex + all external tools.");
        Check(agg.Activity.ThisWeekTokens == 3_650_000_000, "This-week total merges Codex + external daily buckets.");

        var todayShares = agg.Breakdown.For(TokenBreakdownPeriod.Today);
        Check(todayShares.Count == 2
            && todayShares[0].ProviderId == ExternalProviderTokenLedger.CodexProviderId
            && todayShares[1].ProviderId == ExternalProviderTokenLedger.AntigravityProviderId,
            "Zero-token providers in a period (Grok/Claude today) are omitted cleanly.");

        var weekShares = agg.Breakdown.For(TokenBreakdownPeriod.Week);
        Check(weekShares.Count == 3
            && weekShares[0].ProviderId == ExternalProviderTokenLedger.CodexProviderId
            && weekShares[1].ProviderId == ExternalProviderTokenLedger.GrokProviderId
            && weekShares[2].ProviderId == ExternalProviderTokenLedger.AntigravityProviderId,
            "Week breakdown sorts descending by token volume.");

        var allShares = agg.Breakdown.For(TokenBreakdownPeriod.AllTime);
        var agyShare = allShares.Single(s => s.ProviderId == ExternalProviderTokenLedger.AntigravityProviderId);
        Check(SystemUsagePopoverForm.FormatSharePercent(agyShare.SharePercent, allShares.Count) == "<0.1%",
            "Tiny non-zero all-time share formats as <0.1%.");
        Check(SystemUsagePopoverForm.FormatSharePercent(allShares[0].SharePercent, allShares.Count) == "99.9%",
            "99.92% rounds naturally to 99.9%.");
        Check(SystemUsagePopoverForm.FormatSharePercent(99.97, allShares.Count) == ">99.9%",
            "Dominant multi-provider share rounds safely as >99.9% rather than misleading 100%.");
        Check(SystemUsagePopoverForm.FormatSharePercent(100, 1) == "100%",
            "Single-provider period displays 100%.");

        var segments = SystemUsagePopoverForm.LayoutBreakdownBar(new RectangleF(20, 395, 260, 6), allShares);
        Check(segments.Count == 3 && segments.All(s => s.Rect.Width >= 4.9f),
            "Stacked bar preserves visible minimum segment width for small non-zero shares.");
    }

    private static void TestBarFormCombinedTotal()
    {
        var context = SynchronizationContext.Current;
        try
        {
            var at = DateTimeOffset.UtcNow;
            using var form = new BarForm(new AppSettings { Locale = "zh-CN", EnableAnimations = false },
                new QuotaSnapshot([], [], at), renderOnly: true, renderDpi: 96);
            form.SetCodexTokenUsage(new CodexTokenUsageSummary(1000, 500_000, 2, at));
            Check(form.DisplayedTokenTotal == 500_000, "Codex-only baseline matches previous contract.");

            var external = new ExternalProviderTokenSummary(
                new Dictionary<string, IReadOnlyDictionary<DateOnly, long>>
                {
                    [ExternalProviderTokenLedger.GrokProviderId] = new Dictionary<DateOnly, long>
                    {
                        [DateOnly.FromDateTime(at.LocalDateTime)] = 250_000,
                    },
                },
                new Dictionary<string, long>
                {
                    [ExternalProviderTokenLedger.GrokProviderId] = 250_000,
                },
                at);
            form.SetExternalProviderTokens(external);
            Check(form.DisplayedTokenTotal == 750_000, "BarForm DisplayedTokenTotal combines Codex + external tokens.");
            Check(form.LiveMetrics.CumulativeTokens == 750_000, "SystemUsageSnapshot CumulativeTokens mirrors combined total.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
