using ZGSTokenBar.App;

internal static class RuntimeHealthCheckTests
{
    internal static void Run()
    {
        const long minute = RuntimeHealthCheck.IntervalMs;
        var check = new RuntimeHealthCheck();
        IReadOnlyList<string> Sample(long m, bool? healthy, long version = 0, bool notify = true) =>
            check.Observe(m * minute, [new("fixture", healthy, version)], notify);

        Check(Sample(1, false).Count == 0 && Sample(2, false).Count == 0, "A brief failure never notifies.");
        Check(Sample(3, true).Count == 0 && check.Issues.Count == 0, "Recovery is silent and clears pending failures.");
        Check(Sample(4, false, 10).Count == 0 && Sample(5, false, 10).Count == 0
            && Sample(6, false, 10).Count == 0 && check.Issues.Count == 0, "One cached failure is not three failed reads.");
        Check(Sample(7, false, 11).Count == 0, "Two distinct failed observations are still quiet.");
        Check(Sample(8, false, 12, notify: false).Count == 0 && check.Issues.SequenceEqual(["fixture"]),
            "Fullscreen defers the notification while keeping a confirmed diagnostic.");
        Check(Sample(9, false, 13).SequenceEqual(["fixture"]), "A confirmed incident notifies once when presentation permits.");
        for (var i = 10; i <= 21; i++)
            Check(Sample(i, false, i + 5).Count == 0, "A continuing incident never sends repeated reminders.");
        Check(Sample(22, true).Count == 0 && check.Issues.Count == 0, "Only healthy evidence rearms an incident.");
        Sample(23, false);
        Check(Sample(30, false).Count == 0 && Sample(31, false).Count == 0,
            "A long gap cannot count disconnected samples across sleep.");
        Check(Sample(32, false).SequenceEqual(["fixture"]), "A later persistent failure can be reported again.");
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
