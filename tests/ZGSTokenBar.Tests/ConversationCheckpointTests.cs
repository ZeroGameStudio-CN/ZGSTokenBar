using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.Core;

internal static partial class ConversationCheckpointTests
{
    internal static void Run()
    {
        RunP2();
        WarmAppendAndMetadata();
        PartialProgressAndUtf8();
        InvalidationAndCorruption();
        StaleWriterAndUnavailableCache();
        LedgerIsolation();
        ProcessRestart();
    }

    private static void WarmAppendAndMetadata()
    {
        using var f = new Fixture();
        f.Write(f.Turn());
        var first = f.Reader();
        var before = CatchUp(first, f).Single();
        var cacheBytes = File.ReadAllBytes(Directory.GetFiles(f.Cache, "*.checkpoint").Single());
        Check(!Encoding.UTF8.GetString(cacheBytes).Contains(Fixture.PrivateText, StringComparison.Ordinal), "Checkpoint must not retain prompt/tool bodies.");
        var warm = f.Reader();
        var restored = CatchUp(warm, f).Single();
        Check(restored == before && warm.ScannedBytes == 0 && warm.RestoredSessions == 1
            && warm.RestoredBytes == new FileInfo(f.Source).Length, "A new reader restores a complete turn without rescanning its source.");
        var oldRate = new ObservedOutputRate().Observe(first.LiveRateFeed, f.Now);
        var newRate = new ObservedOutputRate().Observe(warm.LiveRateFeed, f.Now);
        Check(oldRate.RecordedTotal == newRate.RecordedTotal && oldRate.Estimated == newRate.Estimated,
            "Restart restores response intervals and phases without changing T/s.");
        var noCold = new CodexConversationUsageReader(f.Cache) { RefreshColdBudgetBytes = 0, RefreshColdMilliseconds = 0 };
        Check(CatchUp(noCold, f).Single() == before && noCold.RestoredSessions == 1,
            "An exhausted cold-scan budget cannot prevent an already saved session from reopening.");
        var append = f.Line(-1, "event_msg", new { type = "task_started", turn_id = "next" })
            + f.Line(-1, "turn_context", new { turn_id = "next", model = "gpt-6.1-sol", effort = "max", service_tier = "flex" })
            + f.Line(0, "event_msg", f.Usage(1300, 300));
        File.AppendAllText(f.Source, append);
        var resumed = f.Reader();
        var turn = CatchUp(resumed, f).Single();
        Check(turn.TurnId == "next" && turn.OutputTokens == 60 && turn.InputTokens == 240
            && resumed.ScannedBytes == Encoding.UTF8.GetByteCount(append), "Restart reads only appended bytes and retains the previous counter baseline.");
        Check(CatchUp(noCold, f).Single().OutputTokens == 60,
            "Small appends to a caught-up session remain live even when the cold budget is exhausted.");
        Check(CatchUp(f.Reader(), f).Single() == turn, "Repeated restarts cannot count the appended usage twice.");
    }

    private static void PartialProgressAndUtf8()
    {
        using var f = new Fixture();
        var partial = f.Line(-3, "response_item", new { type = "function_call_output", output = string.Concat(Enumerable.Repeat("中文🙂" + Fixture.PrivateText, 6000)) });
        f.Write(f.Turn(complete: false) + partial + f.Line(-1, "event_msg", f.Usage(1300, 300)));
        var expected = CatchUp(new(), f).Single();
        const int step = 64 * 1024;
        var first = f.Reader(step);
        first.Refresh([f.Source], f.Now);
        Check(first.PendingSessions == 1 && first.ScannedBytes == step, "The cold fixture stops at an incomplete source prefix.");
        var prior = first.ScannedBytes;
        for (var restart = 0; restart < 3; restart++)
        {
            var next = f.Reader(step);
            next.Refresh([f.Source], f.Now);
            Check(next.RestoredBytes >= prior && next.ScannedBytes <= step,
                "Even an unfinished parse resumes at the durable byte position across repeated readers.");
            prior = next.RestoredBytes + next.ScannedBytes;
        }
        var finished = f.Reader(step);
        Check(CatchUp(finished, f).Single() == expected, "Restored UTF-8 carry and parser state produce the same counters as a fresh scan.");
        Check(Directory.GetFiles(f.Cache, "*.checkpoint").All(path => !Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(Fixture.PrivateText, StringComparison.Ordinal)),
            "A partial UTF-8 line is reconstructed from the source rather than persisted as text.");

        using var unterminated = new Fixture();
        var usage = unterminated.Line(0, "event_msg", unterminated.Usage(1600, 600));
        unterminated.Write(unterminated.Turn(complete: false) + usage[..^1]);
        CatchUp(unterminated.Reader(), unterminated);
        File.AppendAllText(unterminated.Source, "\n");
        var after = CatchUp(unterminated.Reader(), unterminated).Single();
        Check(after.OutputTokens == 300, "A saved unterminated JSON line becomes one usage event when its newline arrives.");
    }

    private static void InvalidationAndCorruption()
    {
        using var f = new Fixture();
        f.Write(f.Turn()); CatchUp(f.Reader(), f);
        f.Write(f.Turn(outputTotal: 1500));
        var changed = f.Reader();
        Check(CatchUp(changed, f).Single() == CatchUp(new(), f).Single() && changed.RestoredSessions == 0,
            "Same-length rewrites cannot reuse a stale counter state.");
        var length = new FileInfo(f.Source).Length;
        var stamp = File.GetLastWriteTimeUtc(f.Source);
        var replacement = f.Source + ".replacement";
        File.WriteAllText(replacement, f.Turn(outputTotal: 1800));
        Check(new FileInfo(replacement).Length == length, "Replacement fixture retains source length.");
        File.SetLastWriteTimeUtc(replacement, stamp);
        File.Move(replacement, f.Source, true);
        var replaced = f.Reader();
        Check(CatchUp(replaced, f).Single() == CatchUp(new(), f).Single() && replaced.RestoredSessions == 0,
            "A different physical file is rejected even when length and mtime are preserved.");
        Check(CatchUp(changed, f).Single() == CatchUp(replaced, f).Single(),
            "A warm reader verifies physical identity even when the replacement preserves length and mtime.");
        f.Write(f.Line(-1, "event_msg", new { type = "task_started", turn_id = "truncated" })
            + f.Line(0, "event_msg", f.Usage(100, 100)));
        var truncated = f.Reader();
        Check(CatchUp(truncated, f).Single().TurnId == "truncated" && truncated.RestoredSessions == 0, "A truncated log rebuilds only that source.");
        var cache = Directory.GetFiles(f.Cache, "*.checkpoint").Single();
        var bytes = File.ReadAllBytes(cache); bytes[^1] ^= 1; File.WriteAllBytes(cache, bytes);
        var corrupt = f.Reader();
        Check(CatchUp(corrupt, f).Single() == CatchUp(new(), f).Single() && corrupt.RestoredSessions == 0,
            "A corrupt checkpoint falls back to the source without changing its counters.");
        var repaired = f.Reader(); CatchUp(repaired, f);
        Check(repaired.RestoredSessions == 1 && repaired.ScannedBytes == 0, "A source-validated rebuild repairs the derived checkpoint.");
        var future = new byte[600 * 1024]; Encoding.ASCII.GetBytes("ZGSCV02\n").CopyTo(future, 0);
        File.WriteAllBytes(cache, future);
        Check(CatchUp(f.Reader(), f).Single() == CatchUp(new(), f).Single() && File.ReadAllBytes(cache).SequenceEqual(future),
            "An older reader preserves an unsupported future cache instead of overwriting it.");
    }

    private static void StaleWriterAndUnavailableCache()
    {
        using var f = new Fixture();
        f.Write(f.Turn(complete: false) + string.Concat(Enumerable.Repeat(f.Line(-2, "response_item",
            new { type = "function_call_output", output = Fixture.PrivateText + new string('x', 900) }), 1400))
            + f.Line(0, "event_msg", f.Usage(1600, 600)));
        var older = f.Reader(64 * 1024); older.Refresh([f.Source], f.Now);
        var newer = f.Reader(); var expected = CatchUp(newer, f).Single();
        older.Refresh([f.Source], f.Now);
        var third = f.Reader();
        Check(CatchUp(third, f).Single() == expected && third.ScannedBytes == 0, "A slower reader cannot overwrite a later durable byte position.");
        var blocked = Path.Combine(f.Root, "blocked"); File.WriteAllText(blocked, "hold");
        var unavailable = new CodexConversationUsageReader(Path.Combine(blocked, "cache"));
        Check(CatchUp(unavailable, f).Single() == expected && unavailable.CheckpointFailures > 0,
            "An unavailable cache cannot block source monitoring or change accounting.");
    }

    private static void LedgerIsolation()
    {
        using var f = new Fixture(); f.Write(f.Turn());
        var store = new AppSettingsStore(Path.Combine(f.Root, "data"));
        var ledger = new CodexTokenUsageLedger(store, () => [f.Home]);
        var initial = ledger.Refresh(f.Now)!;
        for (var i = 0; initial.PendingConversationCount > 0 && i < 100; i++) initial = ledger.RefreshConversations(f.Now)!;
        var bytes = SHA256.HashData(File.ReadAllBytes(store.CodexTokenUsageIndexPath));
        var restarted = new CodexTokenUsageLedger(store, () => [f.Home]);
        var result = restarted.Refresh(f.Now)!;
        Check(result.LocalTokens == initial.LocalTokens && result.ConversationCheckpointRestoredCount == 1
            && result.ConversationScannedBytes == 0 && result.PendingConversationCount == 0
            && SHA256.HashData(File.ReadAllBytes(store.CodexTokenUsageIndexPath)).SequenceEqual(bytes),
            "Ledger restart uses the sidecar checkpoint while preserving the exact cumulative ledger bytes.");
    }

    internal static void Worker(string source, string cache, string output, string mode)
    {
        foreach (var path in new[] { source, cache, output })
            if (!Path.GetFullPath(path).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Checkpoint workers use isolated OS-temporary data only.");
        var reader = new CodexConversationUsageReader(cache) { RefreshColdBudgetBytes = 64 * 1024, RefreshColdMilliseconds = int.MaxValue };
        reader.Refresh([source], DateTimeOffset.UtcNow);
        File.WriteAllText(output + ".partial", JsonSerializer.Serialize(new { reader.ScannedBytes, reader.RestoredBytes, reader.RestoredSessions, reader.PendingSessions }));
        File.Move(output + ".partial", output);
        if (mode == "hold") Thread.Sleep(Timeout.Infinite);
    }

    private static void ProcessRestart()
    {
        using var f = new Fixture();
        f.Write(f.Turn(complete: false) + string.Concat(Enumerable.Repeat(f.Line(-2, "response_item",
            new { type = "function_call_output", output = new string('x', 1500) }), 700))
            + f.Line(0, "event_msg", f.Usage(1600, 600)));
        var first = Child("normal", "one.json");
        var killed = Child("hold", "two.json");
        var third = Child("normal", "three.json");
        Check(first.Scanned == 64 * 1024 && first.Restored == 0 && killed.Restored >= first.Scanned
            && third.Restored >= killed.Restored + killed.Scanned,
            "A real new process resumes after normal exit and after a forcibly terminated worker.");

        (long Scanned, long Restored) Child(string mode, string file)
        {
            var output = Path.Combine(f.Root, file);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--conversation-checkpoint-worker", f.Source, f.Cache, output, mode }) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Checkpoint child did not start.");
            try
            {
                var until = Environment.TickCount64 + 15000;
                while (!File.Exists(output) && !child.HasExited && Environment.TickCount64 < until) Thread.Sleep(20);
                Check(File.Exists(output), "The worker durably records its checkpoint before the termination barrier.");
                if (mode == "hold") child.Kill(entireProcessTree: true); // This fixture's exact child only.
                Check(child.WaitForExit(5000) && (mode == "hold" || child.ExitCode == 0), "The checkpoint worker exits within its bound.");
                using var doc = JsonDocument.Parse(File.ReadAllText(output));
                return (doc.RootElement.GetProperty("ScannedBytes").GetInt64(), doc.RootElement.GetProperty("RestoredBytes").GetInt64());
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } }
        }
    }

    private static IReadOnlyList<CodexConversationTurn> CatchUp(CodexConversationUsageReader reader, Fixture f)
    {
        IReadOnlyList<CodexConversationTurn> rows = [];
        for (var i = 0; i < 200; i++)
        {
            rows = reader.Refresh([f.Source], f.Now);
            if (reader.PendingSessions == 0) return rows;
        }
        throw new InvalidOperationException("Bounded checkpoint fixture could not catch up.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        internal const string PrivateText = "DO_NOT_CACHE_PROMPT_OR_TOOL_CONTENT";
        internal string Root { get; } = Directory.CreateTempSubdirectory("tokenbar-conversation-checkpoint-").FullName;
        internal string Home => Path.Combine(Root, "home");
        internal string Cache => Path.Combine(Root, "cache");
        internal string Source => Path.Combine(Home, "sessions", "2026", "11", "16", "rollout-00000000-0000-0000-0000-000000000001.jsonl");
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        private long _writes;
        internal CodexConversationUsageReader Reader(int? budget = null) => new(Cache)
            { RefreshColdBudgetBytes = budget ?? CodexConversationUsageReader.DefaultRefreshColdBudgetBytes, RefreshColdMilliseconds = int.MaxValue };
        internal void Write(string data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Source)!); File.WriteAllText(Source, data);
            File.SetLastWriteTimeUtc(Source, Now.AddSeconds(Interlocked.Increment(ref _writes)).UtcDateTime);
        }
        internal string Line(int second, string type, object payload) => JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(second), type, payload },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
        internal object Usage(long total, long last) => new { type = "token_count", info = new { total_token_usage = Tokens(total), last_token_usage = Tokens(last) } };
        private static object Tokens(long total) => new { total_tokens = total, input_tokens = total * 4 / 5,
            cached_input_tokens = total / 5, cache_write_input_tokens = 0L, output_tokens = total / 5 };
        internal string Turn(bool complete = true, long outputTotal = 1000) => Line(-10, "event_msg", Usage(100, 100))
            + Line(-9, "event_msg", new { type = "task_started", turn_id = "original" })
            + Line(-9, "turn_context", new { turn_id = "original", model = "gpt-6.1-sol", effort = "high", service_tier = "standard", cwd = "C:/Fixture" })
            + Line(-8, "response_item", new { type = "reasoning", content = PrivateText })
            + Line(-2, "event_msg", Usage(outputTotal, outputTotal - 100))
            + (complete ? Line(-1, "event_msg", new { type = "task_complete", turn_id = "original", time_to_first_token_ms = 120 }) : "");
        public void Dispose() => Directory.Delete(Root, true);
    }
}
