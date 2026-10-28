using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ZGSTokenBar.Core;

internal static class ConversationHistoryRefreshTests
{
    internal static void Run()
    {
        using var fixture = new Fixture();
        var index = new CodexTokenUsageIndex
        {
            Files = Enumerable.Range(0, 4096).Select(i => new CodexTokenUsageFileIndex(
                $"retained-{i}", 0, 0, true, 10, 10, null, 0)).ToList(),
        };
        fixture.Store.SaveCodexTokenUsageIndex(index);
        fixture.AppendTurn("first", 100);
        var ledger = fixture.Ledger();
        var first = ledger.Refresh(fixture.Now)!;
        if (first.ConversationHistory.Count != 1) throw new InvalidOperationException("Initial terminal turn was not persisted.");
        ledger.RefreshConversations(fixture.Now);
        var original = SHA256.HashData(File.ReadAllBytes(fixture.Store.CodexTokenUsageIndexPath));
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4; i++)
        {
            var same = ledger.RefreshConversations(fixture.Now.AddSeconds(i))!;
            if (same.LocalTokens != first.LocalTokens || same.ConversationHistory.Count != 1)
                throw new InvalidOperationException("Unchanged refresh must retain counts and history.");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (allocated > 2 * 1024 * 1024)
            throw new InvalidOperationException($"Unchanged conversation refresh must not scale with the full ledger: {allocated} bytes.");
        if (!original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.Store.CodexTokenUsageIndexPath))))
            throw new InvalidOperationException("Unchanged conversation refresh rewrote the ledger.");

        var external = fixture.Store.LoadCodexTokenUsageIndex().WithHistoricalBaseline(1_000, fixture.Now);
        external.ConversationHistory.Add(new("other-session", "external", null, null,
            fixture.Now.AddMinutes(-1), fixture.Now, 10, 10, true, true));
        fixture.Store.SaveCodexTokenUsageIndex(external);
        fixture.AppendTurn("second", 200);
        ledger.RefreshConversations(fixture.Now);
        var merged = fixture.Store.LoadCodexTokenUsageIndex();
        if (merged.HistoricalBaselineTokens != 1_000 || merged.Files.Count != external.Files.Count
            || !new[] { "first", "second", "external" }.All(id => merged.ConversationHistory.Any(t => t.TurnId == id)))
            throw new InvalidOperationException("Changed turns must merge the latest independent writer's ledger under its lock.");

        fixture.AppendTurn("retry", 300);
        using (new FileStream(fixture.Store.CodexTokenUsageIndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ledger.RefreshConversations(fixture.Now);
            if (fixture.Store.LoadCodexTokenUsageIndex().ConversationHistory.Any(t => t.TurnId == "retry"))
                throw new InvalidOperationException("Fixture failed to prevent the history commit.");
        }
        ledger.RefreshConversations(fixture.Now);
        if (!fixture.Store.LoadCodexTokenUsageIndex().ConversationHistory.Any(t => t.TurnId == "retry"))
            throw new InvalidOperationException("A failed history save must retry even without new source bytes.");

        using var retention = new Fixture();
        var nearExpiry = retention.Store.LoadCodexTokenUsageIndex();
        var at = retention.Now.AddDays(-CodexTokenUsageIndex.ConversationHistoryRetentionDays).AddSeconds(2);
        nearExpiry.ConversationHistory.Add(new("old-session", "expires", null, null, at, at, 1, 1, true, true));
        retention.Store.SaveCodexTokenUsageIndex(nearExpiry);
        retention.AppendTurn("current", 100);
        var aging = retention.Ledger();
        if (aging.Refresh(retention.Now)!.ConversationHistory.Count != 2)
            throw new InvalidOperationException("History inside the retention window must remain available.");
        aging.RefreshConversations(retention.Now.AddSeconds(3));
        if (retention.Store.LoadCodexTokenUsageIndex().ConversationHistory.Any(t => t.TurnId == "expires"))
            throw new InvalidOperationException("An unchanged recent turn must not suppress due history retention.");
    }

    internal static void Probe(string sourceCopy, string output)
    {
        if (!Path.GetFullPath(sourceCopy).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use an OS-temporary ledger copy for the refresh probe.");
        using var fixture = new Fixture(sourceCopy);
        fixture.AppendTurn("probe", 100);
        var ledger = fixture.Ledger();
        var initial = ledger.Refresh(fixture.Now)!;
        ledger.RefreshConversations(fixture.Now);
        var beforeDigest = SHA256.HashData(File.ReadAllBytes(fixture.Store.CodexTokenUsageIndexPath));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        const int iterations = 6;
        for (var i = 0; i < iterations; i++)
        {
            var summary = ledger.RefreshConversations(fixture.Now.AddSeconds(i))!;
            if (summary.LocalTokens != initial.LocalTokens || summary.ConversationHistory.Count != initial.ConversationHistory.Count)
                throw new InvalidOperationException("Probe refresh changed the retained observation.");
        }
        var milliseconds = watch.Elapsed.TotalMilliseconds / iterations;
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        var afterDigest = SHA256.HashData(File.ReadAllBytes(fixture.Store.CodexTokenUsageIndexPath));
        if (!beforeDigest.SequenceEqual(afterDigest)) throw new InvalidOperationException("Probe unexpectedly rewrote its ledger.");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            iterations, inputBytes = new FileInfo(sourceCopy).Length,
            millisecondsPerRefresh = milliseconds, allocatedBytesPerRefresh = allocated,
            initial.LocalTokens, turns = initial.ConversationHistory.Count,
            ledgerUnchanged = true, ledgerSha256 = Convert.ToHexString(afterDigest),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("tokenbar-history-refresh-").FullName;
        internal string Home => Path.Combine(Root, "home");
        internal DateTimeOffset Now { get; } = new(2026, 10, 28, 13, 0, 0, TimeSpan.Zero);
        internal AppSettingsStore Store { get; }
        private int _turns;

        internal Fixture(string? sourceCopy = null)
        {
            Store = new(Path.Combine(Root, "data"));
            Directory.CreateDirectory(Store.DataDirectory);
            if (sourceCopy is null) Store.SaveCodexTokenUsageIndex(new());
            else File.Copy(sourceCopy, Store.CodexTokenUsageIndexPath);
        }

        internal CodexTokenUsageLedger Ledger() => new(Store, () => [Home]);

        internal void AppendTurn(string id, long total)
        {
            var directory = Path.Combine(Home, "sessions", Now.ToString("yyyy/MM/dd"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "refresh.jsonl");
            var at = Now.AddMinutes(-5 + _turns++);
            object Usage(long amount) => new { total_tokens = amount, input_tokens = amount * 4 / 5, output_tokens = amount / 5, cached_input_tokens = 0 };
            var lines = new[]
            {
                JsonSerializer.Serialize(new { timestamp = at, type = "event_msg", payload = new { type = "task_started", turn_id = id, started_at = at } }),
                JsonSerializer.Serialize(new { timestamp = at, type = "turn_context", payload = new { turn_id = id, model = "gpt-6.1-sol", effort = "high" } }),
                JsonSerializer.Serialize(new { timestamp = at.AddSeconds(20), type = "event_msg", payload = new
                    { type = "token_count", info = new { total_token_usage = Usage(total), last_token_usage = Usage(100) } } }),
                JsonSerializer.Serialize(new { timestamp = at.AddSeconds(30), type = "event_msg", payload = new { type = "task_complete", turn_id = id, completed_at = at.AddSeconds(30) } }),
            };
            File.AppendAllText(path, string.Join('\n', lines) + "\n");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
