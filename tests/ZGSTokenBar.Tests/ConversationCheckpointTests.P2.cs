using System.Security.Cryptography;
using ZGSTokenBar.Core;

internal static partial class ConversationCheckpointTests
{
    internal static void RunP2()
    {
        var errors = new List<Exception>();
        foreach (var test in new Action[] { RunningReplacement, CorruptHeaderRepair })
            try { test(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private static void RunningReplacement()
    {
        foreach (var persistent in new[] { false, true })
        foreach (var oldTimestamp in new[] { false, true })
        {
            using var f = new Fixture(); f.Write(f.Turn(outputTotal: 2000));
            if (oldTimestamp) File.SetLastWriteTimeUtc(f.Source, f.Now.AddHours(-1).UtcDateTime);
            var reader = persistent ? f.Reader() : new CodexConversationUsageReader();
            Check(CatchUp(reader, f).Single().OutputTokens == 380, "Replacement baseline is measured once.");
            var stamp = File.GetLastWriteTimeUtc(f.Source);
            var length = new FileInfo(f.Source).Length;
            var replacement = f.Source + ".replacement";
            File.WriteAllText(replacement, f.Turn(outputTotal: 3000));
            Check(new FileInfo(replacement).Length == length, "Replacement fixture preserves length.");
            File.SetLastWriteTimeUtc(replacement, stamp); File.Move(replacement, f.Source, true);
            Check(File.GetLastWriteTimeUtc(f.Source) == stamp, "Replacement fixture preserves timestamp.");
            var expected = CatchUp(new(), f).Single();
            Check(expected.OutputTokens == 580 && CatchUp(reader, f).Single() == expected,
                $"A running reader detects physical replacement with unchanged metadata (cache={persistent}, old={oldTimestamp}).");
            Check(CatchUp(reader, f).Single().OutputTokens == 580, "The rebuilt counter cannot be added again on an unchanged refresh.");
            if (persistent)
                Check(CatchUp(f.Reader(), f).Single() == expected, "The replacement's correct state survives another restart.");
        }
        using var deferred = new Fixture(); deferred.Write(deferred.Turn(outputTotal: 2000));
        CatchUp(deferred.Reader(), deferred);
        var noCold = new CodexConversationUsageReader(deferred.Cache) { RefreshColdBudgetBytes = 0, RefreshColdMilliseconds = 0 };
        CatchUp(noCold, deferred);
        var oldStamp = File.GetLastWriteTimeUtc(deferred.Source);
        var changed = deferred.Source + ".replacement";
        File.WriteAllText(changed, deferred.Turn(outputTotal: 3000));
        File.SetLastWriteTimeUtc(changed, oldStamp); File.Move(changed, deferred.Source, true);
        var pending = noCold.Refresh([deferred.Source], deferred.Now);
        Check(noCold.PendingSessions == 1 && pending.Count == 0 && noCold.LiveRateFeed?.Sessions.Count == 0,
            "A deferred replacement cannot leave stale counters or rates in the live feed.");
    }

    private static void CorruptHeaderRepair()
    {
        foreach (var kind in new[] { "magic", "short", "old-version", "checksum" })
        {
            using var f = new Fixture(); f.Write(f.Turn());
            var expected = CatchUp(f.Reader(), f).Single();
            var path = Directory.GetFiles(f.Cache, "*.checkpoint").Single();
            var bytes = File.ReadAllBytes(path);
            if (kind == "magic") bytes[0] ^= 0x20;
            else if (kind == "short") bytes = [1, 2, 3];
            else if (kind == "old-version") bytes[6] = (byte)'0';
            else bytes[^1] ^= 1;
            File.WriteAllBytes(path, bytes);
            var damagedDigest = SHA256.HashData(bytes);
            var rebuilding = f.Reader();
            Check(CatchUp(rebuilding, f).Single() == expected, "Bad checkpoint data must not affect source counters.");
            Check(!SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(damagedDigest)
                && rebuilding.CheckpointFailures > 0, $"A {kind} cache fault is reported and atomically repaired.");
            var next = f.Reader();
            Check(CatchUp(next, f).Single() == expected && next.RestoredSessions == 1 && next.ScannedBytes == 0,
                "The repaired checkpoint resumes without another full rescan.");
        }
    }
}
