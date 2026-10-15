using System.Text.Json;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.Plugin.Codex;
using ZGSTokenBar.Plugin.CodexLocal;
using ZGSTokenBar.PluginSdk;

internal static class PersistenceRegressionTests
{
    internal static void SettingsTransientRead()
    {
        using var fixture = new LedgerFixture();
        var store = fixture.Store;
        var original = new AppSettings { Locale = "en", OpenAtLogin = true, RefreshMinutes = 17 };
        original.SetPluginEnabled("test.persistence.probe", false);
        store.Save(original);
        var originalBytes = File.ReadAllBytes(store.SettingsPath);
        AppSettings fallback;
        using (new FileStream(store.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Require(!store.TryLoad(out fallback), "a busy settings file is not a successful default load");
        }
        var probe = new CredentialProbe();
        Require(!PluginAutoDiscovery.ApplyAsync(store, fallback, [probe]).GetAwaiter().GetResult(),
            "discovery waits for the original settings to become readable");
        Require(probe.StartCalls == 0, "pending settings do not start discovery providers");
        var rejected = false;
        try { store.Save(fallback); }
        catch (IOException) { rejected = true; }
        Require(rejected, "releasing the file lock alone must not authorize saving fallback defaults");
        Require(originalBytes.SequenceEqual(File.ReadAllBytes(store.SettingsPath)), "original settings remain intact");
        Require(!File.Exists(store.SettingsPath + ".corrupt.bak"), "a transient read error is not corruption");

        Require(store.TryLoad(out var recovered), "the same store can retry without restarting");
        Require(recovered.Locale == "en" && recovered.OpenAtLogin && recovered.RefreshMinutes == 17,
            "retry restores the original preferences");
        Require(!recovered.IsPluginEnabled(probe.Manifest.Id), "an explicit disabled provider remains disabled");
        Require(!PluginAutoDiscovery.ApplyAsync(store, recovered, [probe]).GetAwaiter().GetResult(),
            "discovery respects the recovered explicit decision");
        recovered.RefreshMinutes = 19;
        store.Save(recovered);
        Require(store.Load().RefreshMinutes == 19, "saving is available after successful recovery");

        var fresh = new AppSettingsStore(Path.Combine(fixture.Root, "new-install"));
        Require(fresh.TryLoad(out var defaults), "a genuinely missing settings file still permits first run");
        fresh.Save(defaults);
        Require(File.Exists(fresh.SettingsPath), "first-run settings can be created");
    }

    internal static void LedgerTransactions()
    {
        using var fixture = new LedgerFixture();
        var first = fixture.Ledger(fixture.HomeA);
        var second = fixture.Ledger(fixture.HomeB);
        first.Snapshot(fixture.Now);
        second.Snapshot(fixture.Now);
        var original = fixture.WriteSession(fixture.HomeA, "first", 100);
        Require(first.Refresh(fixture.Now)?.LocalTokens == 100, "first writer records its session");
        File.Move(original, Path.Combine(fixture.Root, "cold-history.jsonl"));
        fixture.WriteSession(fixture.HomeB, "second", 200);
        Require(second.Refresh(fixture.Now)?.LocalTokens == 300,
            "a second writer reloads durable sessions even after their source moves away");
        Require(first.Refresh(fixture.Now)?.LocalTokens == 300,
            "an older in-memory instance cannot replace the second writer's sessions");
        Require(fixture.PersistedTotal() == 300 && fixture.Store.LoadCodexTokenUsageIndex().Files.Count == 2,
            "both sessions survive restart without adding duplicate totals");

        using var parallel = new LedgerFixture();
        parallel.WriteSession(parallel.HomeA, "a", 100);
        parallel.WriteSession(parallel.HomeB, "b", 200);
        using var firstEntered = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var firstWriter = new CodexTokenUsageLedger(parallel.Store, () =>
        {
            firstEntered.Set();
            Require(release.Wait(TimeSpan.FromSeconds(5)), "first transaction was released");
            return [parallel.HomeA];
        });
        var secondWriter = new CodexTokenUsageLedger(new AppSettingsStore(parallel.DataRoot), () =>
        {
            secondEntered.Set();
            return [parallel.HomeB];
        });
        var writeA = Task.Run(() => firstWriter.Refresh(parallel.Now));
        Task<CodexTokenUsageSummary?>? writeB = null;
        try
        {
            Require(firstEntered.Wait(TimeSpan.FromSeconds(5)), "first writer enters its transaction");
            writeB = Task.Run(() =>
            {
                secondStarted.Set();
                return secondWriter.Refresh(parallel.Now);
            });
            Require(secondStarted.Wait(TimeSpan.FromSeconds(5)), "second writer starts");
            Require(!secondEntered.Wait(TimeSpan.FromMilliseconds(100)),
                "another instance cannot read its input index before the first commit");
        }
        finally
        {
            release.Set();
            writeA.GetAwaiter().GetResult();
            writeB?.GetAwaiter().GetResult();
        }
        Require(parallel.PersistedTotal() == 300, "overlapping writers commit both sessions");
    }

    internal static void LedgerSaveRetry()
    {
        using var fixture = new LedgerFixture();
        var ledger = fixture.Ledger(fixture.HomeA);
        var source = fixture.WriteSession(fixture.HomeA, "retry", 100);
        using (new FileStream(fixture.Store.CodexTokenUsageIndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Require(ledger.Refresh(fixture.Now)?.LocalTokens == 100, "a failed save still exposes the observation");
            Require(fixture.PersistedTotal() == 0, "the fixture blocked replacement of the ledger");
        }
        File.Move(source, Path.Combine(fixture.Root, "removed-from-scan.jsonl"));
        Require(ledger.Refresh(fixture.Now)?.LocalTokens == 100, "pending data survives disappearance of its source");
        Require(fixture.PersistedTotal() == 100, "the pending save retries without any new log data");
        Require(fixture.Ledger(fixture.HomeA).Snapshot(fixture.Now)?.LocalTokens == 100,
            "the retried observation survives restart");

        using var combined = new LedgerFixture(baseline: 1_000);
        var pending = combined.Ledger(combined.HomeA);
        var pendingSource = combined.WriteSession(combined.HomeA, "pending", 100);
        using (new FileStream(combined.Store.CodexTokenUsageIndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Require(pending.Refresh(combined.Now)?.LocalTokens == 1_100, "pending observation retains the historical baseline");
        File.Move(pendingSource, Path.Combine(combined.Root, "retained-pending.jsonl"));
        combined.WriteSession(combined.HomeB, "other", 200);
        Require(combined.Ledger(combined.HomeB).Refresh(combined.Now)?.LocalTokens == 1_200,
            "another writer can commit while the first has uncommitted data");
        Require(pending.Refresh(combined.Now)?.LocalTokens == 1_300 && combined.PersistedTotal() == 1_300,
            "retry merges session records and uses the fixed baseline exactly once");

        using var overlapping = new LedgerFixture();
        var stale = overlapping.Ledger(overlapping.HomeA);
        var staleSource = overlapping.WriteSession(overlapping.HomeA, "same-session", 100);
        using (new FileStream(overlapping.Store.CodexTokenUsageIndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Require(stale.Refresh(overlapping.Now)?.LocalTokens == 100, "older same-session state is pending");
        File.Move(staleSource, Path.Combine(overlapping.Root, "older-copy.jsonl"));
        overlapping.WriteSession(overlapping.HomeB, "same-session", 100, 200);
        Require(overlapping.Ledger(overlapping.HomeB).Refresh(overlapping.Now)?.LocalTokens == 200,
            "another writer commits the extended session");
        Require(stale.Refresh(overlapping.Now)?.LocalTokens == 200 && overlapping.PersistedTotal() == 200,
            "retry keeps the newest session watermark without double counting its old copy");
    }

    internal static void BuiltinDataRootIsolation()
    {
        using var fixture = new LedgerFixture();
        var unrelated = new AppSettingsStore(Path.Combine(fixture.Root, "unrelated-default"));
        unrelated.SaveCodexTokenUsageIndex(new CodexTokenUsageIndex().WithHistoricalBaseline(9_000, fixture.Now));
        var before = File.ReadAllBytes(unrelated.CodexTokenUsageIndexPath);
        var previous = Environment.GetEnvironmentVariable(AppSettingsStore.DataDirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(AppSettingsStore.DataDirectoryEnvironmentVariable, unrelated.DataDirectory);
        try
        {
            fixture.WriteSession(fixture.HomeA, "isolated", 100);
            var plugin = new CodexLocalPlugin(store => new CodexTokenUsageLedger(store, () => [fixture.HomeA]));
            IZgsPlugin[] plugins = [new CodexPlugin(), plugin];
            var profile = new EffectiveProfile(1, "test", [], plugins.Select(item =>
                new ProfilePlugin(item.Manifest.Id, item.Manifest.Version, true, item.Manifest.Order,
                    new Dictionary<string, JsonElement>())).ToArray());
            var host = new ZgsTokenBarHost(plugins, profile, "test", fixture.DataRoot, persistProfileState: false);
            try
            {
                host.StartAsync().AsTask().GetAwaiter().GetResult();
                host.RefreshPluginAsync(plugin.Manifest.Id, "test", CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Require(fixture.PersistedTotal() == 100, "the actual built-in plugin writes the explicit host data root");
                Require(before.SequenceEqual(File.ReadAllBytes(unrelated.CodexTokenUsageIndexPath)),
                    "the default data root remains byte-for-byte unchanged");
                Require(!File.Exists(Path.Combine(fixture.DataRoot, "plugin-data", plugin.Manifest.Id, "codex-token-usage-index.json")),
                    "the built-in shares the existing host ledger instead of creating a second ledger");
            }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally { Environment.SetEnvironmentVariable(AppSettingsStore.DataDirectoryEnvironmentVariable, previous); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CredentialProbe : BuiltinPluginBase, ILocalCredentialProbe
    {
        public int StartCalls { get; private set; }
        public override PluginManifest Manifest { get; } = new(1, "test.persistence.probe", "1.0.0", 1, 0,
            PluginRuntime.Builtin, false, "persistence-probe", ["local-credentials"], false, 0, []);
        public override ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
        {
            StartCalls++;
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class LedgerFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"zgstokenbar-persistence-{Guid.NewGuid():N}");
        public string DataRoot => Path.Combine(Root, "data");
        public string HomeA => Path.Combine(Root, "home-a");
        public string HomeB => Path.Combine(Root, "home-b");
        public DateTimeOffset Now { get; } = new(2026, 10, 15, 4, 30, 0, TimeSpan.Zero);
        public AppSettingsStore Store { get; }

        public LedgerFixture(long? baseline = null)
        {
            Store = new AppSettingsStore(DataRoot);
            var index = new CodexTokenUsageIndex();
            Store.SaveCodexTokenUsageIndex(baseline is { } value ? index.WithHistoricalBaseline(value, Now.AddHours(-1)) : index);
        }

        public CodexTokenUsageLedger Ledger(string home) => new(new AppSettingsStore(DataRoot), () => [home]);
        public long PersistedTotal() => new CodexTokenUsageReader(Store.LoadCodexTokenUsageIndex()).Snapshot(Now)?.LocalTokens ?? 0;

        public string WriteSession(string home, string name, params long[] totals)
        {
            var directory = Path.Combine(home, "sessions");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name + ".jsonl");
            var previous = 0L;
            var lines = totals.Select((total, index) =>
            {
                var last = total - previous;
                previous = total;
                return JsonSerializer.Serialize(new
                {
                    timestamp = Now.AddMinutes(-5).AddSeconds(index).ToString("O"),
                    type = "event_msg",
                    payload = new
                    {
                        type = "token_count",
                        info = new
                        {
                            total_token_usage = Usage(total),
                            last_token_usage = Usage(last),
                        },
                    },
                });
            });
            File.WriteAllText(path, string.Join('\n', lines) + "\n");
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
            return path;
        }

        private static object Usage(long total) => new
        {
            total_tokens = total,
            input_tokens = total * 4 / 5,
            cached_input_tokens = total / 5,
            cache_write_input_tokens = 0L,
            output_tokens = total / 5,
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
