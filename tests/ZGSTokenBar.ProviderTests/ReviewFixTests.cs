using System.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.Tests;

public static class ReviewFixTests
{
    public static void Run()
    {
        PriorityMapping();
        SessionDiscovery();
        AttachInstances();
        ActivityOrder();
        PlanHistory();
        AuditOptimizations();
    }

    private static void PriorityMapping()
    {
        var high = RouterQueuePriority.Describe(" high ", "解除共享编译阻塞");
        Check(high.Label == "高" && high.Tone == "high" && high.ToolTip.Contains("高优先级") && high.ToolTip.Contains("解除共享编译阻塞"),
            "high priority keeps its reason");
        var normal = RouterQueuePriority.Describe("NORMAL", null);
        Check(normal.Label == "普通" && normal.Tone == "normal" && normal.ToolTip == "普通优先级", "normal is visible");
        var low = RouterQueuePriority.Describe("low", "  ");
        Check(low.Label == "低" && low.Tone == "low" && low.ToolTip == "低优先级", "blank reason is omitted");
        var missing = RouterQueuePriority.Describe("  ", "ignored");
        Check(missing.Label == "—" && missing.Tone == "missing" && missing.ToolTip.Length == 0, "missing priority is a dash");
        var unknown = RouterQueuePriority.Describe("urgent_1", "later");
        Check(unknown.Label == "urgent_1" && unknown.Tone == "unknown" && unknown.ToolTip == "urgent_1 · later",
            "compact unknown token stays visible");
        var noisy = RouterQueuePriority.Describe("not a priority", "x");
        Check(noisy.Label == "—" && noisy.Tone == "missing", "unstructured priority is not invented");
    }

    private static void SessionDiscovery()
    {
        var root = TempDirectory();
        try
        {
            var now = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
            var home = Path.Combine(root, "overflow");
            var folder = Path.Combine(home, "sessions", "2026", "11", "19");
            Directory.CreateDirectory(folder);
            for (var index = 0; index <= 4096; index++)
            {
                var tokens = index == 4096 ? 9 : 1;
                File.WriteAllText(Path.Combine(folder, $"f{index:D5}.jsonl"), TokenLine(tokens));
            }

            var reader = new CodexTokenUsageReader();
            var first = reader.Refresh([home], now);
            Check(first.Summary!.LocalTokens == 4104, "4097 logs keep the newest file and defer one oldest file");
            var second = reader.Refresh([home], now);
            Check(second.Summary!.LocalTokens == 4105 && second.Summary.SessionCount == 4097,
                "the next refresh rotates to the deferred log without double counting");

            var crowded = Path.Combine(root, "home-a", "sessions");
            var other = Path.Combine(root, "home-b", "sessions");
            Directory.CreateDirectory(crowded);
            Directory.CreateDirectory(other);
            for (var index = 0; index < 4096; index++)
                File.WriteAllText(Path.Combine(crowded, $"a{index:D5}.jsonl"), TokenLine(1));
            File.WriteAllText(Path.Combine(other, "b00001.jsonl"), TokenLine(100));
            var homes = new CodexTokenUsageReader();
            var starved = homes.Refresh([Path.Combine(root, "home-a"), Path.Combine(root, "home-b")], now);
            Check(starved.Summary!.LocalTokens == 4195, "another home is admitted inside the fresh slice");
            var rotated = homes.Refresh([Path.Combine(root, "home-a"), Path.Combine(root, "home-b")], now);
            Check(rotated.Summary!.LocalTokens == 4196, "the crowded home's deferred file follows on the next refresh");

            var left = Path.Combine(root, "dup-a", "sessions");
            var right = Path.Combine(root, "dup-b", "sessions");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            File.WriteAllText(Path.Combine(left, "same.jsonl"), TokenLine(10));
            File.WriteAllText(Path.Combine(right, "same.jsonl"), TokenLine(10));
            File.WriteAllText(Path.Combine(right, "extra.jsonl"), TokenLine(7));
            var duplicate = new CodexTokenUsageReader().Refresh(
                [Path.Combine(root, "dup-a"), Path.Combine(root, "dup-b")], now);
            Check(duplicate.Summary!.LocalTokens == 17 && duplicate.Summary.SessionCount == 2,
                "the same filename in two homes counts once and the other home's unique file remains");

            var hidden = Path.Combine(root, "hidden", "sessions");
            Directory.CreateDirectory(Path.Combine(hidden, "z-new"));
            File.WriteAllText(Path.Combine(hidden, "z-new", "unique.jsonl"), TokenLine(9));
            for (var index = 0; index < 4096; index++)
            {
                var oldFolder = Path.Combine(hidden, $"a-{index:D4}");
                Directory.CreateDirectory(oldFolder);
                File.WriteAllText(Path.Combine(oldFolder, "dup.jsonl"), TokenLine(1));
            }

            var visible = new CodexTokenUsageReader().Refresh([Path.Combine(root, "hidden")], now);
            Check(visible.Summary!.LocalTokens == 10 && visible.Summary.SessionCount == 2,
                "repeated older names do not consume the unique-file cap ahead of a newer log");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AttachInstances()
    {
        var root = TempDirectory();
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var defaultHome = Path.Combine(root, "default-home");
            var otherHome = Path.Combine(root, "other-home");
            var thirdHome = Path.Combine(root, "third-home");
            Directory.CreateDirectory(defaultHome);
            Directory.CreateDirectory(otherHome);
            Directory.CreateDirectory(thirdHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", defaultHome);
            var at = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
            var activeKey = CodexQuotaService.StableCardKey("active");
            var oneKey = CodexQuotaService.StableCardKey("cockpit:acc-one");
            var twoKey = CodexQuotaService.StableCardKey("cockpit:acc-two");
            var activeCard = Card(activeKey, "Codex", 20, at);
            var oneCard = Card(oneKey, "Codex · 1", 20, at);
            var twoCard = Card(twoKey, "Codex · 2", 40, at);
            var homes = new[] { otherHome, defaultHome, thirdHome };
            var sharedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Normalize(otherHome)] = "acc-one",
                [Normalize(defaultHome)] = "acc-one",
                [Normalize(thirdHome)] = "acc-two",
            };

            var unknown = CodexConversationDisplay.AttachInstances(
                [Turn("unknown", otherHome)],
                Snapshot([oneCard], at),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                [otherHome]);
            Check(unknown[0].InstanceRemainingPercent is null && unknown[0].InstanceLabel == "Codex · 1",
                "an unknown account does not inherit the only card");

            var mismatched = CodexConversationDisplay.AttachInstances(
                [Turn("mismatch", otherHome)],
                Snapshot([twoCard], at, Account("acc-two", twoKey, at)),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Normalize(otherHome)] = "acc-one" },
                [otherHome]);
            Check(mismatched[0].InstanceRemainingPercent is null, "a known account does not bind a different account's only card");

            var activeOnly = CodexConversationDisplay.AttachInstances(
                [Turn("native", defaultHome)],
                Snapshot([activeCard], at),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                [defaultHome]);
            Check(activeOnly[0].InstanceRemainingPercent is { } native && Math.Abs(native - 80) < .001,
                "the default home still matches its active card");

            var wrongDefault = CodexConversationDisplay.AttachInstances(
                [Turn("wrong-default", defaultHome)],
                Snapshot([oneCard], at),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                [defaultHome]);
            Check(wrongDefault[0].InstanceRemainingPercent is null,
                "the default home does not guess a non-active card");

            var shared = CodexConversationDisplay.AttachInstances(
                [Turn("left", otherHome), Turn("right", defaultHome), Turn("third", thirdHome)],
                Snapshot([oneCard, twoCard], at, Account("acc-one", oneKey, at), Account("acc-two", twoKey, at)),
                sharedMap,
                homes);
            Check(shared[0].InstanceNumber == 1 && shared[1].InstanceNumber == 2
                && shared[0].InstanceRemainingPercent is { } left && shared[1].InstanceRemainingPercent is { } right
                && Math.Abs(left - right) < .001 && Math.Abs(left - 80) < .001
                && shared[2].InstanceNumber == 3 && shared[2].InstanceRemainingPercent is { } third && Math.Abs(third - 60) < .001,
                "shared and distinct accounts keep their own instance numbers and matching quota");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            Directory.Delete(root, true);
        }
    }

    private static void ActivityOrder()
    {
        var at = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        var mixed = CodexConversationDisplay.ActivityRows(
        [
            Activity("running-100", false, 1, 100, at),
            Activity("running-80", false, 1, 80, at.AddSeconds(-1)),
            Activity("running-30", false, 2, 30, at),
            Activity("finished-5", true, 2, 5, at.AddSeconds(1)),
            Activity("filler", true, 3, 0, at.AddSeconds(2)),
        ]);
        Check(mixed.Select(turn => turn.SessionId).SequenceEqual(["running-100", "running-80", "running-30", "finished-5", "filler"]),
            "running rows stay ahead of finished rows and faster rows stay ahead inside the group");

        var representative = CodexConversationDisplay.ActivityRows(
        [
            Activity("running-fast", false, 1, 100, at),
            Activity("running-next", false, 1, 90, at),
            Activity("finished-other", true, 2, 5, at),
        ]);
        Check(representative.Take(3).Select(turn => turn.SessionId).SequenceEqual(["running-fast", "running-next", "finished-other"]),
            "an instance representative cannot jump ahead of a later running row");

        var finished = CodexConversationDisplay.ActivityRows(
        [
            Activity("finished-30", true, 1, 30, at),
            Activity("finished-100", true, 2, 100, at),
            Activity("finished-80", true, 1, 80, at),
        ]);
        Check(finished.Select(turn => turn.LiveRate!.Measured).SequenceEqual([100d, 80d, 30d]),
            "finished rows from different instances stay in descending speed order");
    }

    private static void PlanHistory()
    {
        var account = "acc-pro-1";
        var t0 = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(8));
        var t1 = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(8));
        var t2 = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.FromHours(8));
        var paidReset = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.FromHours(8));
        var freeReset = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var history = new CodexPlanChangeHistory();
        Check(history.Observe(Observation(account, t0, "pro", [Week(paidReset)])), "first paid observation is stored");
        Check(history.Observe(Observation(account, t1, "Pro", null)), "a later poll with no windows moves the observation time");
        Check(!history.Observe(Observation(account, t1.AddHours(1), null, [Week(freeReset)])), "a missing plan does not create a change");
        Check(!history.Observe(Observation(account, t0, "free", [Week(freeReset)])), "an older poll is ignored");
        Check(history.Observe(Observation(account, t2, "free", [Week(freeReset)])), "the first free poll is a transition");
        var transition = history.FirstTransition(account, "pro", "free");
        Check(transition is not null
            && transition.PreviousObservedAt == t1
            && transition.ObservedAt == t2
            && transition.PreviousWindows.Any(window => window.Label == "7d" && window.ResetsAt == paidReset)
            && transition.PreviousWindows.All(window => window.ResetsAt != freeReset),
            "the transition keeps the previous paid reset and the poll interval");
        Check(CodexPlanChangeHistory.RemainingUntilPreviousWeekReset(transition!) == paidReset - t2,
            "remaining time uses the old weekly reset and the first free observation");

        var missing = new CodexPlanChangeHistory();
        missing.Observe(Observation(account, t0, "pro", null));
        missing.Observe(Observation(account, t2, "free", [Week(freeReset)]));
        Check(CodexPlanChangeHistory.RemainingUntilPreviousWeekReset(missing.FirstTransition(account, "pro", "free")!) is null,
            "a missing previous reset is not replaced with the free reset");
        Check(!new CodexPlanChangeHistory().Observe(Observation("user@example", t0, "pro", [Week(paidReset)])),
            "an email is not a stable account id");

        var reset = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        var seen = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        var zoned = Span(seen.AddDays(-1).ToOffset(TimeSpan.FromHours(8)), seen.ToOffset(TimeSpan.FromHours(8)), reset.ToOffset(TimeSpan.FromHours(8)));
        var universal = Span(seen.AddDays(-1), seen, reset);
        Check(zoned == universal && universal == reset - seen, "reset remaining is the same instant across offsets");

        var retained = new CodexPlanChangeHistory();
        var newest = "";
        for (var index = 0; index < 40; index++)
        {
            var id = $"acct-{index:D2}-" + new string('a', 100);
            var stamp = t0.AddMinutes(index);
            retained.Observe(Observation(id, stamp, "pro", [Week(paidReset)]));
            retained.Observe(Observation(id, stamp.AddMinutes(1), "free", [Week(freeReset)]));
            newest = id;
        }

        for (var index = 0; index < 20; index++)
            retained.Observe(Observation(newest, t2.AddMinutes(index), index % 2 == 0 ? "pro" : "free", [Week(index % 2 == 0 ? paidReset : freeReset)]));
        var exported = retained.Export();
        Check(exported.Accounts.Count <= CodexPlanChangeHistory.MaximumAccounts
            && exported.Accounts.Sum(accountRecord => accountRecord.Transitions.Count) <= CodexPlanChangeHistory.MaximumTransitions
            && exported.Accounts.All(accountRecord => accountRecord.Transitions.Count <= CodexPlanChangeHistory.MaximumTransitionsPerAccount)
            && exported.Accounts.Any(accountRecord => accountRecord.AccountId == newest
                && accountRecord.Transitions.Any(item => item.FromPlan == "pro" && item.ToPlan == "free")),
            "retention drops the oldest accounts and transitions first");

        var directory = TempDirectory();
        try
        {
            var store = new AppSettingsStore(directory);
            store.SaveCodexPlanChangeHistory(exported);
            var path = store.CodexPlanChangeHistoryPath;
            var json = File.ReadAllText(path);
            Check(Encoding.UTF8.GetByteCount(json) <= CodexPlanChangeHistory.MaximumFileBytes, "history file stays bounded");
            Check(!json.Contains("email", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("accessToken", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("conversation", StringComparison.OrdinalIgnoreCase),
                "history does not store email, token or conversation text");
            var loaded = CodexPlanChangeHistory.Load(store.LoadCodexPlanChangeHistory());
            Check(loaded.FirstTransition(newest, "pro", "free") is not null, "history reloads the retained transition");
            File.WriteAllText(path, "{");
            Check(store.LoadCodexPlanChangeHistory().Accounts.Count == 0 && File.Exists(path + ".corrupt.bak"),
                "corrupt history is preserved and replaced with an empty record");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void AuditOptimizations()
    {
        var now = DateTimeOffset.UtcNow;
        var cardA = new QuotaCard("card-a", ProviderKind.Codex, "Codex A", "pro", "#10a37f", true,
            [new QuotaWindow("5h", 20, now.AddHours(3), TimeSpan.FromHours(5))]) { CapturedAt = now.AddMinutes(-20) };
        var cardB = new QuotaCard("card-b", ProviderKind.Codex, "Codex B", "pro", "#10a37f", true,
            [new QuotaWindow("5h", 10, now.AddHours(4), TimeSpan.FromHours(5))]) { CapturedAt = now.AddMinutes(-20) };
        var tracker = new QuotaPaceTracker(new QuotaRateHistory
        {
            Samples =
            [
                new("card-a", "5h", TimeSpan.FromHours(5).Ticks, now.AddMinutes(-20), 10, now.AddHours(3)),
                new("card-b", "5h", TimeSpan.FromHours(5).Ticks, now.AddMinutes(-20), 5, now.AddHours(4)),
            ],
        });
        Check(tracker.SampleCount == 2, "indexed tracker preserves initial sample count");
        var snapshot = new QuotaSnapshot(
            [
                cardA with { CapturedAt = now, Windows = [new("5h", 25, now.AddHours(3), TimeSpan.FromHours(5))] },
                cardB with { CapturedAt = now, Windows = [new("5h", 12, now.AddHours(4), TimeSpan.FromHours(5))] },
            ],
            [new ProviderHealth(ProviderKind.Codex, true, "ok", ProviderHealthCode.Current)],
            now);
        Check(tracker.Observe(snapshot, now) && tracker.SampleCount == 4, "indexed tracker appends per-series samples");
        Check(tracker.Observe(snapshot with
        {
            CapturedAt = now.AddSeconds(10),
            Cards = [snapshot.Cards[0] with { CapturedAt = now.AddSeconds(10), Windows = [new("5h", 1, now.AddHours(8), TimeSpan.FromHours(5))] }],
        }, now.AddSeconds(10)) && tracker.SampleCount == 3, "new cycle clears only the affected series");
        var root = TempDirectory();
        try
        {
            var store = new AppSettingsStore(root);
            var exported = tracker.Export(now.AddSeconds(10));
            store.SaveQuotaRateHistory(exported);
            var reloaded = store.LoadQuotaRateHistory(now.AddSeconds(10));
            Check(reloaded.Samples.Count == 3, "streamed quota rate history round-trips cleanly");

            var settings = new AppSettings();
            for (var i = 0; i < AppSettings.MaximumPlacementProfiles + 10; i++)
            {
                var key = "topology-v1:" + i.ToString("x64");
                settings.PlacementProfiles[key] = new WindowPlacementProfile();
            }
            var firstKey = "topology-v1:" + 0.ToString("x64");
            settings.TrimPlacementProfiles(keepTopologyKey: firstKey);
            Check(settings.PlacementProfiles.Count == AppSettings.MaximumPlacementProfiles
                && settings.PlacementProfiles.ContainsKey(firstKey),
                "placement profiles cap preserves requested topology key");

            Directory.CreateDirectory(root);
            var deadPid = 999_999_999;
            var staleTmp = Path.Combine(root, $".codex-token-usage-index.json.{deadPid}.{Guid.NewGuid():N}.tmp");
            var freshTmp = Path.Combine(root, $".codex-token-usage-index.json.{deadPid}.{Guid.NewGuid():N}.tmp");
            var liveTmp = Path.Combine(root, $".codex-token-usage-index.json.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            var keepJson = Path.Combine(root, "settings.json");
            File.WriteAllText(staleTmp, "x");
            File.SetLastWriteTimeUtc(staleTmp, DateTime.UtcNow.AddDays(-2));
            File.WriteAllText(freshTmp, "x");
            File.WriteAllText(liveTmp, "x");
            File.SetLastWriteTimeUtc(liveTmp, DateTime.UtcNow.AddDays(-2));
            File.WriteAllText(keepJson, "{}");
            var cleaned = store.CleanupOrphanedTempFiles();
            Check(cleaned == 1 && !File.Exists(staleTmp) && File.Exists(freshTmp) && File.Exists(liveTmp) && File.Exists(keepJson),
                "orphaned temp cleanup removes only dead-PID stale atomic temp files");

            var planHistory = new CodexPlanChangeHistory();
            var t0 = now.AddMinutes(-5);
            Check(planHistory.Observe([Observation("acct-1", t0, "pro", [Week(now.AddDays(5))])], out var initialStructural) && initialStructural,
                "initial plan observation is structural");
            Check(planHistory.Observe([Observation("acct-1", t0.AddMinutes(1), "pro", [Week(now.AddDays(5))])], out var heartbeatStructural)
                && !heartbeatStructural,
                "pure timestamp heartbeat under same plan/windows is not structural");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static TimeSpan? Span(DateTimeOffset previous, DateTimeOffset observed, DateTimeOffset reset)
    {
        var history = new CodexPlanChangeHistory();
        history.Observe(Observation("acct-zone", previous, "pro", [Week(reset)]));
        history.Observe(Observation("acct-zone", observed, "free", [Week(observed.AddDays(7))]));
        return CodexPlanChangeHistory.RemainingUntilPreviousWeekReset(history.FirstTransition("acct-zone", "pro", "free")!);
    }

    private static CodexPlanObservation Observation(string account, DateTimeOffset at, string? plan, IReadOnlyList<QuotaWindow>? windows) =>
        new(account, at, plan, windows);

    private static QuotaWindow Week(DateTimeOffset reset) => new("7d", 10, reset, TimeSpan.FromDays(7));

    private static string TokenLine(int tokens) =>
        "{\"timestamp\":\"2026-11-01T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":"
        + tokens + ",\"input_tokens\":" + tokens + ",\"cached_input_tokens\":0}}}}";

    private static CodexConversationTurn Turn(string id, string home) =>
        new(id, "turn", "model", "medium", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 1, false, true)
        { CodexHome = home };

    private static CodexConversationTurn Activity(string id, bool completed, int instance, double measured, DateTimeOffset at) =>
        new(id, "turn", "model", "medium", at, at, 10, 1, completed, true)
        {
            InstanceNumber = instance,
            LiveRate = measured > 0 ? new ConversationOutputRate(id, "turn", measured, 0) : null,
        };

    private static QuotaSnapshot Snapshot(QuotaCard[] cards, DateTimeOffset at, params CodexAccountQuota[] accounts) =>
        new(cards, [new ProviderHealth(ProviderKind.Codex, true, "fixture", ProviderHealthCode.Current)], at)
        { CodexAccounts = accounts };

    private static QuotaCard Card(string key, string label, double used, DateTimeOffset at) =>
        new(key, ProviderKind.Codex, label, "pro", "#10a37f", true, [new QuotaWindow("5h", used, at.AddHours(5), TimeSpan.FromHours(5))]);

    private static CodexAccountQuota Account(string id, string cardKey, DateTimeOffset at) =>
        new(id, [new QuotaWindow("5h", 20, at.AddHours(5), TimeSpan.FromHours(5))], at) { CardKey = cardKey };

    private static string Normalize(string path) => CockpitCodexInstanceActivity.NormalizeDirectory(path)!;

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "zgstokenbar-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
