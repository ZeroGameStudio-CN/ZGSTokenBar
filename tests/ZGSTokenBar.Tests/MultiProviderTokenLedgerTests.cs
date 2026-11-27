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
            TestExternalConversationTracker(tempRoot);
            TestClaudeOpenLogRefresh(tempRoot);
            TestMultiProviderActiveSessionsUi();
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

    private static void TestExternalConversationTracker(string tempRoot)
    {
        var now = new DateTimeOffset(2026, 11, 26, 15, 30, 0, TimeSpan.FromHours(8));
        var homeDir = Path.Combine(tempRoot, "conv-home");
        var agyRoot = Path.Combine(homeDir, ".gemini", "antigravity");
        var grokRoot = Path.Combine(homeDir, ".grok");
        var claudeRoot = Path.Combine(homeDir, ".claude");

        // 1. AGY: active session (with tool_calls & USER_INPUT tail), completed session, stale session, plus ignored transcript_full.jsonl.
        var agyActiveLogs = Path.Combine(agyRoot, "brain", "agy-active", ".system_generated", "logs");
        var agyDoneLogs = Path.Combine(agyRoot, "brain", "agy-done", ".system_generated", "logs");
        var agyStaleLogs = Path.Combine(agyRoot, "brain", "agy-stale", ".system_generated", "logs");
        var agyHistoryNonDoneLogs = Path.Combine(agyRoot, "brain", "agy-history-non-done", ".system_generated", "logs");
        var agyFollowupLogs = Path.Combine(agyRoot, "brain", "agy-followup", ".system_generated", "logs");
        Directory.CreateDirectory(agyActiveLogs);
        Directory.CreateDirectory(agyDoneLogs);
        Directory.CreateDirectory(agyStaleLogs);
        Directory.CreateDirectory(agyHistoryNonDoneLogs);
        Directory.CreateDirectory(agyFollowupLogs);

        var tActiveNext = now.AddSeconds(-10).ToUniversalTime().ToString("O");
        var tActive = now.AddSeconds(-20).ToUniversalTime().ToString("O");
        var tActiveStart = now.AddSeconds(-35).ToUniversalTime().ToString("O");
        File.WriteAllLines(Path.Combine(agyActiveLogs, "transcript.jsonl"),
        [
            $"{{\"type\":\"USER_INPUT\",\"created_at\":\"{tActiveStart}\",\"status\":\"DONE\",\"content\":\"<USER_REQUEST>\\nBuild multi-provider active sessions\\n</USER_REQUEST>\"}}",
            $"{{\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{tActive}\",\"status\":\"DONE\",\"model\":\"gemini-4-neon-xhigh\",\"input_tokens\":2000,\"cache_read_tokens\":8000,\"output_tokens\":400,\"tool_calls\":[{{\"name\":\"run_command\"}}]}}",
            $"{{\"type\":\"GENERIC\",\"created_at\":\"{tActiveNext}\",\"status\":\"RUNNING\"}}",
        ]);
        File.WriteAllText(Path.Combine(agyActiveLogs, "transcript_full.jsonl"),
            $"{{\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{tActive}\",\"input_tokens\":999999,\"output_tokens\":999999}}\n");

        var tDoneSame = now.AddMinutes(-1).ToUniversalTime().ToString("O");
        var agyDonePath = Path.Combine(agyDoneLogs, "transcript.jsonl");
        File.WriteAllLines(agyDonePath,
        [
            $"{{\"type\":\"USER_INPUT\",\"created_at\":\"{tDoneSame}\",\"status\":\"DONE\",\"content\":\"Check account email\"}}",
            $"{{\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{tDoneSame}\",\"status\":\"DONE\",\"model\":\"gemini-4-neon-xhigh\",\"input_tokens\":500,\"cache_read_tokens\":1500,\"output_tokens\":200}}",
        ]);
        File.SetLastWriteTimeUtc(agyDonePath, now.AddMinutes(-1).UtcDateTime);

        var tStale = now.AddMinutes(-5).ToUniversalTime().ToString("O");
        var agyStalePath = Path.Combine(agyStaleLogs, "transcript.jsonl");
        File.WriteAllLines(agyStalePath,
        [
            $"{{\"type\":\"USER_INPUT\",\"created_at\":\"{tStale}\",\"status\":\"DONE\",\"content\":\"Abandoned tool step\"}}",
            $"{{\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{tStale}\",\"status\":\"DONE\",\"model\":\"gemini-4-neon-xhigh\",\"input_tokens\":100,\"cache_read_tokens\":0,\"output_tokens\":50,\"tool_calls\":[{{\"name\":\"wait\"}}]}}",
        ]);
        File.SetLastWriteTimeUtc(agyStalePath, now.AddMinutes(-5).UtcDateTime);

        var tHistStart = now.AddSeconds(-50).ToUniversalTime().ToString("O");
        var tHistMid = now.AddSeconds(-45).ToUniversalTime().ToString("O");
        var tHistEnd = now.AddSeconds(-40).ToUniversalTime().ToString("O");
        var agyHistPath = Path.Combine(agyHistoryNonDoneLogs, "transcript.jsonl");
        File.WriteAllLines(agyHistPath,
        [
            $"{{\"type\":\"USER_INPUT\",\"created_at\":\"{tHistStart}\",\"status\":\"DONE\",\"content\":\"Recover after tool error\"}}",
            $"{{\"type\":\"GENERIC\",\"created_at\":\"{tHistMid}\",\"status\":\"RUNNING\"}}",
            $"{{\"type\":\"GENERIC\",\"created_at\":\"{tHistMid}\",\"status\":\"ERROR\"}}",
            $"{{\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{tHistEnd}\",\"status\":\"DONE\",\"model\":\"gemini-4-neon-xhigh\",\"input_tokens\":400,\"cache_read_tokens\":1200,\"output_tokens\":250,\"tool_calls\":[]}}",
        ]);
        File.SetLastWriteTimeUtc(agyHistPath, now.AddSeconds(-40).UtcDateTime);

        var agyFollowupPath = Path.Combine(agyFollowupLogs, "transcript.jsonl");
        File.WriteAllLines(agyFollowupPath,
        [
            $"{{\"type\":\"USER_INPUT\",\"created_at\":\"{now.AddSeconds(-40).ToUniversalTime():O}\",\"status\":\"DONE\",\"content\":\"Explain rate pipeline\"}}",
            $"{{\"type\":\"PLANNER_RESPONSE\",\"created_at\":\"{now.AddSeconds(-20).ToUniversalTime():O}\",\"status\":\"DONE\",\"model\":\"gemini-4-neon-xhigh\",\"input_tokens\":600,\"cache_read_tokens\":1400,\"output_tokens\":600,\"tool_calls\":[]}}",
        ]);
        File.SetLastWriteTimeUtc(agyFollowupPath, now.AddSeconds(-5).UtcDateTime);

        // 2. Grok: active (updates.jsonl without turn_completed) and completed (usage.json + summary.json + elapsed_ms fallback).
        var grokActiveDir = Path.Combine(grokRoot, "sessions", "proj-a", "grok-active");
        var grokDoneDir = Path.Combine(grokRoot, "sessions", "proj-b", "grok-done");
        var grokElapsedDir = Path.Combine(grokRoot, "sessions", "proj-c", "grok-elapsed-ms");
        Directory.CreateDirectory(grokActiveDir);
        Directory.CreateDirectory(grokDoneDir);
        Directory.CreateDirectory(grokElapsedDir);

        File.WriteAllText(Path.Combine(grokActiveDir, "summary.json"),
            $$$"""
            {"generated_title":"Investigate Grok active stream","current_model_id":"grok-4.7","reasoning_effort":"high","last_active_at":"{{{now.AddSeconds(-15).ToUniversalTime():O}}}","info":{"cwd":"D:\\projects\\ZGSTokenBar"}}
            """);
        File.WriteAllLines(Path.Combine(grokActiveDir, "updates.jsonl"),
        [
            $"{{\"timestamp\":{now.AddSeconds(-15).ToUnixTimeSeconds()},\"params\":{{\"update\":{{\"sessionUpdate\":\"agent_message_chunk\",\"text\":\"working\"}}}}}}",
        ]);

        File.WriteAllText(Path.Combine(grokDoneDir, "summary.json"),
            """
            {"generated_title":"Grok finished task","current_model_id":"grok-4.7-build","reasoning_effort":"medium","info":{"cwd":"D:\\projects\\Other"}}
            """);
        File.WriteAllText(Path.Combine(grokDoneDir, "usage.json"),
            $$"""
            {
              "updatedAt": "{{now.AddMinutes(-1).ToUniversalTime():O}}",
              "turns": [
                {
                  "turnNumber": 2,
                  "startedAt": "{{now.AddMinutes(-2).ToUniversalTime():O}}",
                  "endedAt": "{{now.AddMinutes(-1).ToUniversalTime():O}}",
                  "inputTokens": 4500,
                  "cachedReadTokens": 3600,
                  "outputTokens": 600,
                  "primaryModelId": "grok-4.7-build"
                }
              ]
            }
            """);

        File.WriteAllText(Path.Combine(grokElapsedDir, "summary.json"),
            """
            {"generated_title":"Grok elapsed_ms turn","current_model_id":"grok-4.7","reasoning_effort":"high","info":{"cwd":"D:\\projects\\ZGSTokenBar"}}
            """);
        File.WriteAllText(Path.Combine(grokElapsedDir, "usage.json"),
            $$"""
            {
              "updatedAt": "{{now.AddSeconds(-30).ToUniversalTime():O}}",
              "turns": [
                {
                  "turnNumber": 1,
                  "endedAt": "{{now.AddSeconds(-30).ToUniversalTime():O}}",
                  "inputTokens": 2000,
                  "cachedReadTokens": 1000,
                  "outputTokens": 500,
                  "primaryModelId": "grok-4.7"
                }
              ]
            }
            """);
        File.WriteAllLines(Path.Combine(grokElapsedDir, "updates.jsonl"),
        [
            $"{{\"timestamp\":{now.AddSeconds(-30).ToUnixTimeSeconds()},\"params\":{{\"update\":{{\"sessionUpdate\":\"turn_completed\",\"elapsed_ms\":5000,\"usage\":{{\"inputTokens\":2000,\"cachedReadTokens\":1000,\"outputTokens\":500}}}}}}}}",
        ]);

        // 3. Claude Code: active (tool_use tail) and completed (end_turn + lastPrompt + message.id dedup).
        var claudeProjDir = Path.Combine(claudeRoot, "projects", "work");
        Directory.CreateDirectory(claudeProjDir);
        File.WriteAllLines(Path.Combine(claudeProjDir, "claude-active.jsonl"),
        [
            $"{{\"type\":\"user\",\"timestamp\":\"{now.AddSeconds(-25).ToUniversalTime():O}\",\"cwd\":\"D:\\\\projects\\\\ZGSTokenBar\",\"message\":{{\"role\":\"user\",\"content\":\"Audit provider auth\"}}}}",
            $"{{\"type\":\"assistant\",\"timestamp\":\"{now.AddSeconds(-10).ToUniversalTime():O}\",\"effort\":\"high\",\"requestId\":\"req-a\",\"message\":{{\"id\":\"msg-a\",\"model\":\"claude-opus-5-5\",\"stop_reason\":\"tool_use\",\"usage\":{{\"input_tokens\":300,\"cache_creation_input_tokens\":200,\"cache_read_input_tokens\":1500,\"output_tokens\":120}}}}}}",
        ]);
        File.WriteAllLines(Path.Combine(claudeProjDir, "claude-done.jsonl"),
        [
            $"{{\"type\":\"user\",\"timestamp\":\"{now.AddMinutes(-3).ToUniversalTime():O}\",\"cwd\":\"D:\\\\projects\\\\ZGSTokenBar\",\"message\":{{\"role\":\"user\",\"content\":\"Initial prompt\"}}}}",
            $"{{\"type\":\"assistant\",\"timestamp\":\"{now.AddMinutes(-2).ToUniversalTime():O}\",\"message\":{{\"id\":\"msg-d\",\"model\":\"claude-opus-5-5\",\"stop_reason\":null,\"usage\":{{\"input_tokens\":100,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":900,\"output_tokens\":20}}}}}}",
            $"{{\"type\":\"assistant\",\"timestamp\":\"{now.AddMinutes(-2).ToUniversalTime():O}\",\"message\":{{\"id\":\"msg-d\",\"model\":\"claude-opus-5-5\",\"stop_reason\":\"end_turn\",\"usage\":{{\"input_tokens\":100,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":900,\"output_tokens\":300}}}}}}",
            "{\"type\":\"last-prompt\",\"lastPrompt\":\"Refined Claude title\"}",
        ]);
        File.WriteAllLines(Path.Combine(claudeProjDir, "claude-followup.jsonl"),
        [
            $"{{\"type\":\"user\",\"timestamp\":\"{now.AddSeconds(-30).ToUniversalTime():O}\",\"cwd\":\"D:\\\\projects\\\\ZGSTokenBar\",\"message\":{{\"role\":\"user\",\"content\":\"First turn\"}}}}",
            $"{{\"type\":\"assistant\",\"timestamp\":\"{now.AddSeconds(-20).ToUniversalTime():O}\",\"message\":{{\"id\":\"msg-cf\",\"model\":\"claude-opus-5-5\",\"stop_reason\":\"end_turn\",\"usage\":{{\"input_tokens\":200,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":800,\"output_tokens\":200}}}}}}",
            $"{{\"type\":\"user\",\"timestamp\":\"{now.AddSeconds(-5).ToUniversalTime():O}\",\"cwd\":\"D:\\\\projects\\\\ZGSTokenBar\",\"message\":{{\"role\":\"user\",\"content\":\"Follow-up pending\"}}}}",
        ]);

        var paths = new UsagePaths(homeDir, Path.Combine(tempRoot, "roaming"), Path.Combine(tempRoot, "local"));
        var env = new MapUsageEnvironment(new Dictionary<string, string>(StringComparer.Ordinal));
        var tracker = new ExternalConversationTracker(paths, env);
        var snapshot = tracker.RefreshSnapshot(now);
        var turns = snapshot.Turns;
        var feed = snapshot.LiveRateFeed!;
        var rate = new ObservedOutputRate().Observe(feed, now);

        var agyActive = turns.Single(t => t.SessionId == "agy-active");
        Check(agyActive.ProviderId == ExternalProviderTokenLedger.AntigravityProviderId
            && agyActive.InstanceLabel == "AGY"
            && agyActive.State == "running"
            && agyActive.Title == "Build multi-provider active sessions"
            && agyActive.InputTokens == 10_000
            && agyActive.CachedInputTokens == 8_000
            && agyActive.OutputTokens == 400,
            "AGY active session detects running tool_calls, strips <USER_REQUEST>, excludes transcript_full, and sums tokens.");
        var agyActiveRate = rate.Conversations.Single(c => c.SessionId == "agy-active" && c.ProviderId == ExternalProviderTokenLedger.AntigravityProviderId);
        Check(Math.Abs(agyActiveRate.Measured - 40.0) < 0.01
            && feed.Sessions.Single(s => s.SessionId == "agy-active").Phase == "tools",
            "AGY tool-calling PLANNER_RESPONSE followed by GENERIC RUNNING yields exact tok/s sample and tools phase.");

        var agyFollowup1 = turns.Single(t => t.SessionId == "agy-followup");
        var agyFollowupSample1 = feed.Responses.Single(r => r.SessionId == "agy-followup");
        Check(agyFollowup1.Model == "gemini-4-neon-xhigh"
            && Math.Abs(agyFollowupSample1.Output / (agyFollowupSample1.End - agyFollowupSample1.Start).TotalSeconds - 40.0) < 0.01,
            "AGY final PLANNER_RESPONSE uses file mtime as response end.");

        var agyDone = turns.Single(t => t.SessionId == "agy-done");
        Check(agyDone.State == "completed" && agyDone.OutputTokensPerSecond is null,
            "AGY completed turn with zero-second created_at delta guards against bogus high tok/s.");

        var agyStale = turns.Single(t => t.SessionId == "agy-stale");
        Check(agyStale.State == "stale",
            "AGY unfinished session older than 2 minutes becomes stale instead of active.");

        var agyHistoryNonDone = turns.Single(t => t.SessionId == "agy-history-non-done");
        Check(agyHistoryNonDone.State == "completed",
            "AGY turn ending with PLANNER_RESPONSE DONE and empty tool_calls is completed despite earlier RUNNING/ERROR steps.");

        var grokActive = turns.Single(t => t.SessionId == "grok-active");
        Check(grokActive.ProviderId == ExternalProviderTokenLedger.GrokProviderId
            && grokActive.InstanceLabel == "Grok"
            && grokActive.State == "running"
            && grokActive.Title == "Investigate Grok active stream"
            && grokActive.ProjectName == "ZGSTokenBar"
            && grokActive.Model == "grok-4.7"
            && grokActive.Effort == "high",
            "Grok active session extracts summary metadata and detects non-completed updates.jsonl.");

        var grokDone = turns.Single(t => t.SessionId == "grok-done");
        Check(grokDone.State == "completed" && grokDone.InputTokens == 4500 && grokDone.CachedInputTokens == 3600
            && grokDone.OutputTokens == 600 && grokDone.OutputTokensPerSecond is { } grokSpeed && Math.Abs(grokSpeed - 10.0) < 0.01,
            "Grok completed turn parses usage.json tokens, cache hit, and duration.");

        var grokElapsed = turns.Single(t => t.SessionId == "grok-elapsed-ms");
        Check(grokElapsed.State == "completed"
            && grokElapsed.OutputTokensPerSecond is { } grokElapsedSpeed
            && grokElapsedSpeed > 0
            && Math.Abs(grokElapsedSpeed - 100.0) < 0.01,
            "Grok completed turn falls back to updates.jsonl turn_completed elapsed_ms when usage.json omits startedAt.");
        var grokRate = rate.Conversations.Single(c => c.SessionId == "grok-elapsed-ms" && c.ProviderId == ExternalProviderTokenLedger.GrokProviderId);
        Check(Math.Abs(grokRate.Measured - 100.0) < 0.01,
            "Grok turn_completed emits live response sample for ObservedOutputRate.");

        var claudeActive = turns.Single(t => t.SessionId == "claude-active");
        Check(claudeActive.ProviderId == ExternalProviderTokenLedger.ClaudeProviderId
            && claudeActive.InstanceLabel == "Claude"
            && claudeActive.State == "running"
            && claudeActive.Title == "Audit provider auth"
            && claudeActive.InputTokens == 2000 && claudeActive.CachedInputTokens == 1500 && claudeActive.OutputTokens == 120,
            "Claude active session detects tool_use stop_reason and aggregates input + cache.");
        var claudeActiveRate = rate.Conversations.Single(c => c.SessionId == "claude-active" && c.ProviderId == ExternalProviderTokenLedger.ClaudeProviderId);
        Check(Math.Abs(claudeActiveRate.Measured - 8.0) < 0.01
            && feed.Sessions.Single(s => s.SessionId == "claude-active").Phase == "tools",
            "Claude active tool_use emits response sample and tools phase.");

        var claudeDone = turns.Single(t => t.SessionId == "claude-done");
        Check(claudeDone.State == "completed" && claudeDone.Title == "Refined Claude title" && claudeDone.OutputTokens == 300,
            "Claude completed turn deduplicates streaming message.id and prefers lastPrompt title.");

        var claudeFollowup = turns.Single(t => t.SessionId == "claude-followup");
        var claudeFollowupRate = rate.Conversations.Single(c => c.SessionId == "claude-followup" && c.ProviderId == ExternalProviderTokenLedger.ClaudeProviderId);
        Check(claudeFollowup.State == "running"
            && claudeFollowup.Model == "claude-opus-5-5"
            && Math.Abs(claudeFollowupRate.Measured - 20.0) < 0.01,
            "Claude follow-up user turn preserves session model and retains recent response rate.");

        // Follow-up USER_INPUT on agy-followup must preserve model and retain cached response End timestamp.
        var tFollowupStart = now.AddSeconds(8);
        File.AppendAllText(agyFollowupPath,
            $"{{\"type\":\"USER_INPUT\",\"created_at\":\"{tFollowupStart.ToUniversalTime():O}\",\"status\":\"DONE\",\"content\":\"Second prompt\"}}\n");
        File.SetLastWriteTimeUtc(agyFollowupPath, tFollowupStart.UtcDateTime);
        var poll2At = now.AddSeconds(10);
        var snapshot2 = tracker.RefreshSnapshot(poll2At);
        var agyFollowup2 = snapshot2.Turns.Single(t => t.SessionId == "agy-followup");
        var agyFollowupSample2 = snapshot2.LiveRateFeed!.Responses.Single(r => r.SessionId == "agy-followup");
        var rate2 = new ObservedOutputRate().Observe(snapshot2.LiveRateFeed, poll2At);
        Check(agyFollowup2.Model == "gemini-4-neon-xhigh"
            && agyFollowup2.State == "running"
            && agyFollowupSample2.End == agyFollowupSample1.End
            && Math.Abs(rate2.Conversations.Single(c => c.SessionId == "agy-followup").Measured - 40.0) < 0.01
            && snapshot2.LiveRateFeed.Sessions.Single(s => s.SessionId == "agy-followup").Phase == "generating",
            "AGY follow-up USER_INPUT preserves last known model, retains prior response End without corruption, and reports generating phase.");

        // Unchanged stat cache reuses parsed turn and transitions active -> stale as clock advances.
        var aged = tracker.Refresh(now.AddMinutes(3));
        Check(aged.Single(t => t.SessionId == "agy-active").State == "stale"
            && aged.Single(t => t.SessionId == "claude-active").State == "stale"
            && !aged.Any(t => t.SessionId == "grok-active"),
            "Stat-cached active external sessions become stale (or drop if tokenless) when clock advances past running timeout.");
    }

    private static void TestClaudeOpenLogRefresh(string tempRoot)
    {
        var home = Path.Combine(tempRoot, "claude-open-home");
        var project = Path.Combine(home, ".claude", "projects", "work");
        Directory.CreateDirectory(project);
        var now = DateTimeOffset.UtcNow;
        var activePath = Path.Combine(project, "active.jsonl");
        var resumedPath = Path.Combine(project, "resumed.jsonl");
        File.WriteAllLines(activePath, [UserLine(now.AddSeconds(-8)), AssistantLine(now.AddSeconds(-4), 40)]);
        File.SetLastWriteTimeUtc(activePath, now.AddMinutes(-1).UtcDateTime);
        File.WriteAllLines(resumedPath, [UserLine(now.AddDays(-35)), AssistantLine(now.AddDays(-35), 20)]);
        File.SetLastWriteTimeUtc(resumedPath, now.AddDays(-35).UtcDateTime);
        for (var i = 0; i < 16; i++)
        {
            var path = Path.Combine(project, $"recent-{i}.jsonl");
            File.WriteAllLines(path, [UserLine(now.AddMinutes(-3)), AssistantLine(now.AddMinutes(-2), 30)]);
            File.SetLastWriteTimeUtc(path, now.AddMinutes(-2).UtcDateTime);
        }

        var tracker = new ExternalConversationTracker(new UsagePaths(home, home, home),
            new MapUsageEnvironment(new Dictionary<string, string>()));
        var initial = tracker.RefreshSnapshot(now).Turns;
        Check(initial.Single(t => t.SessionId == "active").OutputTokens == 40
            && initial.All(t => t.SessionId != "resumed"), "The newest Claude logs fill the shortlist before an old session resumes.");
        for (var i = 0; i < 16; i++)
            File.SetLastWriteTimeUtc(Path.Combine(project, $"recent-{i}.jsonl"), now.AddSeconds(-30).UtcDateTime);

        const FileShare share = FileShare.ReadWrite | FileShare.Delete;
        using var active = new FileStream(activePath, FileMode.Append, FileAccess.Write, share);
        using var resumed = new FileStream(resumedPath, FileMode.Append, FileAccess.Write, share);
        var next = AssistantLine(now.AddSeconds(1), 125);
        Append(active, next[..20]);
        Check(tracker.RefreshSnapshot(now.AddSeconds(1)).Turns.Single(t => t.SessionId == "active").OutputTokens == 40,
            "A tracked Claude log stays ranked ahead of newer directory entries while its writer appends an incomplete line.");
        Append(active, next[20..] + "\n");
        Check(tracker.RefreshSnapshot(now.AddSeconds(2)).Turns.Single(t => t.SessionId == "active").OutputTokens == 125,
            "Completing a line in a still-open Claude log refreshes the tracked session on the next poll.");

        Append(resumed, UserLine(now.AddSeconds(3)) + "\n" + AssistantLine(now.AddSeconds(4), 60) + "\n");
        var first = tracker.RefreshSnapshot(now.AddSeconds(4)).Turns;
        var second = tracker.RefreshSnapshot(now.AddSeconds(5)).Turns;
        Check((first.Any(t => t.SessionId == "resumed" && t.OutputTokens == 60)
            || second.Any(t => t.SessionId == "resumed" && t.OutputTokens == 60))
            && second.Count <= 16,
            "An old Claude log writing again can join the bounded shortlist within two polls while its writer is open.");

        Append(resumed, AssistantLine(now.AddSeconds(6), 210) + "\n");
        Check(tracker.RefreshSnapshot(now.AddSeconds(6)).Turns.Single(t => t.SessionId == "resumed").OutputTokens == 210,
            "Subsequent writes to the resumed log refresh on the next poll even if its directory entry still looks old.");

        static string UserLine(DateTimeOffset at) => System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "user",
            timestamp = at,
            cwd = @"D:\fixture",
            message = new { role = "user", content = "Fixture prompt" }
        });

        static string AssistantLine(DateTimeOffset at, int output) => System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = at,
            message = new
            {
                id = "fixture-reply",
                model = "claude-opus-5-5",
                stop_reason = "end_turn",
                usage = new { input_tokens = 10, output_tokens = output }
            }
        });

        static void Append(FileStream stream, string text)
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
            stream.Flush();
        }
    }

    private static void TestMultiProviderActiveSessionsUi()
    {
        var at = DateTimeOffset.UtcNow;
        var codexTurns = Enumerable.Range(1, 10)
            .Select(i => new CodexConversationTurn($"codex-{i}", $"t-{i}", "gpt-5.5", "high", at.AddSeconds(-20), at, 500, 10, false, true)
            {
                InstanceLabel = $"Codex · {i}",
                InstanceNumber = i,
                LiveRate = new ConversationOutputRate($"codex-{i}", $"t-{i}", 50 - i, 0) { RecordedAt = at },
                Title = $"Codex task {i}",
            });
        var agyTurn = new CodexConversationTurn("agy-1", "t-agy", "gemini-4-neon-xhigh", null, at.AddSeconds(-5), at, 420, 0, false, false)
        {
            ProviderId = ExternalProviderTokenLedger.AntigravityProviderId,
            InstanceLabel = "AGY",
            Title = "AGY live conversation",
            InputTokens = 10_000,
            CachedInputTokens = 8_000,
        };
        var grokTurn = new CodexConversationTurn("grok-1", "t-grok", "grok-4.7", "high", at.AddSeconds(-8), at, 200, 0, false, false)
        {
            ProviderId = ExternalProviderTokenLedger.GrokProviderId,
            InstanceLabel = "Grok",
            Title = "Grok live conversation",
            InputTokens = 5_000,
            CachedInputTokens = 4_000,
        };
        var claudeTurn = new CodexConversationTurn("claude-1", "t-claude", "claude-opus-5-5", "high", at.AddSeconds(-12), at, 150, 0, false, false)
        {
            ProviderId = ExternalProviderTokenLedger.ClaudeProviderId,
            InstanceLabel = "Claude",
            Title = "Claude live conversation",
            InputTokens = 3_000,
            CachedInputTokens = 2_000,
        };

        var allTurns = codexTurns.Concat([agyTurn, grokTurn, claudeTurn]).ToArray();
        var selected = CodexConversationDisplay.ActivityRows(allTurns, 10);
        Check(selected.Count == 10
            && selected.Any(t => t.ProviderId == ExternalProviderTokenLedger.AntigravityProviderId)
            && selected.Any(t => t.ProviderId == ExternalProviderTokenLedger.GrokProviderId)
            && selected.Any(t => t.ProviderId == ExternalProviderTokenLedger.ClaudeProviderId),
            "ActivityRows preserves representative seats for active AGY, Grok, and Claude alongside 10 Codex sessions.");

        var context = SynchronizationContext.Current;
        try
        {
            var agyDef = UsageProviderRegistry.Find(UsageProviderIds.Antigravity)!.Definition;
            var grokDef = UsageProviderRegistry.Find(UsageProviderIds.Grok)!.Definition;
            var claudeCard = new QuotaCard("claude.account", ProviderKind.Claude, "Claude", "Max", "#d97757", true,
                [new QuotaWindow("5h", 35, at.AddHours(3), TimeSpan.FromHours(5))]) { HasAccountIdentity = true };
            using var form = new BarForm(
                new AppSettings { Locale = "zh-CN", EnableAnimations = false },
                new QuotaSnapshot([claudeCard], [], at),
                activeProviders: new HashSet<ProviderKind> { ProviderKind.Claude }, // Codex explicitly inactive!
                renderOnly: true,
                renderDpi: 96);
            form.SetUsageBoard(new UsageBoard(
            [
                new UsageProviderSnapshot(agyDef.Id, agyDef.DisplayName, agyDef.Accent, agyDef.Order,
                [
                    new UsageAccountSnapshot("antigravity", "user@example.com", null, "agy-cli",
                        [new UsageWindow("argon-7d", "Argon · 7d", 25, at.AddDays(4), TimeSpan.FromDays(7))],
                        null, null, [], UsageFreshness.Current, at, null),
                ]),
                new UsageProviderSnapshot(grokDef.Id, grokDef.DisplayName, grokDef.Accent, grokDef.Order,
                [
                    new UsageAccountSnapshot("grok", "grok@example.com", "SuperGrok", "grok-auth",
                        [new UsageWindow("weekly", "Weekly", 60, at.AddDays(2), TimeSpan.FromDays(7))],
                        null, null, [], UsageFreshness.Current, at, null),
                ]),
            ]));
            form.SetExternalConversations([agyTurn, grokTurn, claudeTurn]);
            var liveRows = form.LiveConversationsForAcceptance();
            Check(liveRows.Count == 3
                && Math.Abs(liveRows.Single(t => t.SessionId == "agy-1").InstanceRemainingPercent!.Value - 75) < 0.01
                && Math.Abs(liveRows.Single(t => t.SessionId == "grok-1").InstanceRemainingPercent!.Value - 40) < 0.01
                && Math.Abs(liveRows.Single(t => t.SessionId == "claude-1").InstanceRemainingPercent!.Value - 65) < 0.01,
                "BarForm surfaces external active sessions even when Codex is inactive and attaches each provider's remaining quota.");

            using var popover = new SystemUsagePopoverForm();
            var content = new SystemUsagePopoverContent(form.LiveMetrics, false, true) { Conversations = allTurns };
            foreach (var dpi in new[] { 96, 144 })
            {
                using var bitmap = popover.RenderForTest(content, NativeText.For("zh-CN"),
                    QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), dpi);
                Check(bitmap.Width > 0 && bitmap.Height > 0
                    && popover.SessionRowCountForAcceptance == 10
                    && popover.ActiveSessionCountForAcceptance == 13,
                    "Mixed-provider popover renders cleanly and reports true total active count beyond 10.");
            }
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
