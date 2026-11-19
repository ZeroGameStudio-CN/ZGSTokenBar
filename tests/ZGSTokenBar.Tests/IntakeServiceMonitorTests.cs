using System.Drawing;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Intake;
using ZGSTokenBar.Plugin.Router;

internal static class IntakeServiceMonitorTests
{
    private static readonly IntakeServiceSource Source = new("fixture", "Intake fixture", "worker-fixture", "intake-fixture.service", "/var/lib/intake-fixture");
    internal static void Run(string? captures = null)
    {
        var context = SynchronizationContext.Current;
        try
        {
            CheckInvalidConfigurations();
            Throws(() => IntakeServiceMonitorClient.ValidateSources([Source with { SshTarget = "-oProxyCommand=bad" }]));
            Throws(() => IntakeServiceMonitorClient.ValidateSources([Source with { StateDirectory = "/var/lib/../etc" }]));
            Throws(() => IntakeServiceMonitorClient.ValidateSources([Source with { Unit = "x.service;touch x" }]));
            Throws(() => IntakeServiceMonitorClient.ValidateSources([Source, Source]));
            Throws(() => IntakeServiceMonitorClient.ValidateReading(Source, Reading() with { SourceId = "other" }));
            Throws(() => IntakeServiceMonitorClient.ValidateReading(Source, Reading() with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-10) }));
            var alternate = Source with { Id = "second", Unit = "second-intake.service" };
            var client = new IntakeServiceMonitorClient([Source, alternate], (_, _) => Task.FromResult(Reading()));
            var a = new IntakeServiceResult(Source, Reading(), null);
            var b = new IntakeServiceResult(alternate, Reading() with { SourceId = alternate.Id, ProcessingCount = 2 }, null);
            Check(client.ProcessingCount([a, b]) == 3 && client.ProcessingCount([a, b with { Reading = null }]) is null,
                "A global count requires fresh active readings from every registered source.");
            Check(client.ProcessingCount([a, b with { Source = Source with { Id = alternate.Id } }]) == 1,
                "Duplicate profiles for the same service do not double-count its operations.");
            ReviewProjection(captures);
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                CaptureCount(dpi, captures);
                foreach (var chinese in new[] { true, false }) View(dpi, chinese, captures);
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static void CaptureCount(int dpi, string? captures)
    {
        using var bar = new BarForm(new AppSettings { EnableAnimations = false },
            QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: dpi);
        bar.SetPluginMiniCards([
            new(RouterPlugin.Id, "Router", RouterModuleTests.Card("running", 7), new Dictionary<string, string>()),
            new(IntakeObserverPlugin.Id, "自动分拣", IntakeObserverPlugin.Launcher, new Dictionary<string, string>())]);
        var bounds = bar.ClientSize;
        byte[] Render(int? count, bool save = false)
        {
            bar.SetIntakeProcessingCount(count);
            using var image = new Bitmap(bar.Width, bar.Height);
            bar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            Check(bar.ClientSize == bounds, "A count update cannot change the taskbar footprint.");
            using var stream = new MemoryStream();
            image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            if (save && captures is not null)
            {
                Directory.CreateDirectory(captures);
                var label = count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unconfirmed";
                File.WriteAllBytes(Path.Combine(captures, $"intake-count-{label}-{dpi}.png"), stream.ToArray());
            }
            return stream.ToArray();
        }
        var launcher = Render(null);
        bar.SetIntakeServiceConfigured(true);
        var idle = Render(0, save: true);
        Check(launcher.SequenceEqual(idle), "An unconfigured launcher has the neutral icon rather than a failure state.");
        Check(!Render(null, save: true).SequenceEqual(idle), "An unconfirmed service visibly dims the icon.");
        Check(!idle.SequenceEqual(Render(12, save: true)), "A positive active count highlights the icon and paints the badge.");
        Check(Render(100, save: true).SequenceEqual(Render(101)), "Large counts are capped at 99+.");
    }

    private static void CheckInvalidConfigurations()
    {
        var directory = Directory.CreateTempSubdirectory("tokenbar-intake-config-");
        var profile = IntakeServiceMonitorClient.ProfilePath(directory.FullName);
        try
        {
            foreach (var json in new[] { """{"schemaVersion":2,"sources":[]}""",
                         """{"schemaVersion":1.5,"sources":[]}""", """{"schemaVersion":1,"sources":[]}""" })
            {
                File.WriteAllText(profile, json);
                var client = new IntakeServiceMonitorClient(directory.FullName);
                Check(client.ConfigurationError is not null && client.Sources.Count == 0,
                    "An invalid on-disk profile becomes an unavailable state with no SSH targets.");
            }
        }
        finally { File.Delete(profile); directory.Delete(); }
    }

    private static IntakeServiceReading Reading()
    {
        var a = new IntakeServiceRecord("a", "00000000-0000-4000-8000-000000000001", "LAB", "LAB-1", "First feedback", "precheck", "model_started", null);
        var b = new IntakeServiceRecord("b", "00000000-0000-4000-8000-000000000002", "OTHER", "OTHER-2", "Older archive", "archive", "archived", DateTimeOffset.UtcNow.AddHours(-1));
        return new("zgstokenbar.intake-monitor.v1", Source.Id, DateTimeOffset.UtcNow, true, "running", "fixture",
            ["LAB", "OTHER"], [a], [a, b], []) { ProcessingCount = 1 };
    }

    internal static IntakeServiceMonitorForm UnobservedPanel() => new(true,
        new IntakeServiceMonitorClient([Source], (_, _) => Task.FromResult(Reading())), observe: false);

    private static void ReviewProjection(string? captures)
    {
        static IntakeServiceRecord Receipt(int i, string kind, string phase, string review, string reason, string status) =>
            new($"receipt-{i}", $"00000000-0000-4000-8000-{i:D12}", "LAB", $"LAB-{i}", "", kind, phase, null)
            { ReviewState = review, ReasonCode = reason, SourceStatus = status };
        var protection = Enumerable.Range(1, 8)
            .Select(i => Receipt(i, "archive", "needs_attention", "archive_protected", "archive_thread_changed_or_active", "passed")).ToArray();
        var closed = Enumerable.Range(9, 4)
            .Select(i => Receipt(i, "precheck", "needs_review", "source_closed", "need_evidence", "passed")).ToArray();
        var snapshot = Reading() with { Projects = ["LAB"], Progress = protection, History = [.. protection, .. closed], ProcessingCount = 0 };
        var client = new IntakeServiceMonitorClient([Source], (_, _) => Task.FromResult(snapshot));
        using var form = new IntakeServiceMonitorForm(true, client, observe: false) { Opacity = 0 };
        form.ConfigureFor(new(-28500, -28000, 48, 44), true, 1.5f,
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), new(-30000, -30000, 3840, 2160));
        form.Show();
        Pump(form.RefreshAsync());
        var progress = Descendants(form).OfType<RouterGrid>().Single(grid => grid.ColumnCount == 3);
        var history = Descendants(form).OfType<RouterGrid>().Single(grid => grid.ColumnCount == 4);
        Check(form.Observation is { PendingReview: 0, ProtectedArchive: 8, Unconfirmed: 0, ClosedHistory: 4, InFlight: 0 }
            && progress.RowCount == 8 && progress.Rows.Cast<DataGridViewRow>().All(r => Equals(r.Cells[2].Value, "归档保护"))
            && history.Rows.Cast<DataGridViewRow>().Count(r => Equals(r.Cells[3].Value, "原卡已结案")) == 4,
            "Archive guards have their own count and passed reviews remain in history with their original diagnostics.");
        if (captures is not null)
        {
            Directory.CreateDirectory(captures);
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(captures, "intake-review-projection.png"));
        }

        var real = Receipt(13, "precheck", "needs_review", "attention", "need_evidence", "pending_triage");
        var unknown = Receipt(14, "precheck", "needs_review", "unconfirmed", "need_evidence", "");
        snapshot = snapshot with { Progress = [real, unknown, .. protection], History = [.. snapshot.History, real, unknown] };
        Pump(form.RefreshAsync());
        Check(form.Observation is { PendingReview: 1, ProtectedArchive: 8, Unconfirmed: 1, ClosedHistory: 4 }
            && Equals(progress.Rows[0].Cells[2].Value, "待核对") && Equals(progress.Rows[1].Cells[2].Value, "待确认"),
            "Known work and uncertain source state remain visible with distinct statuses.");
        snapshot = snapshot with { Gaps = ["receipts_unavailable"], Progress = [], History = [], ProcessingCount = null };
        Pump(form.RefreshAsync());
        Check(form.Observation is { PendingReview: null, ProtectedArchive: null, Unconfirmed: null, ClosedHistory: 4 }
            && progress.RowCount == 10, "A partial read keeps previous rows without asserting zero or complete totals.");
    }

    private static void View(int dpi, bool chinese, string? captures)
    {
        var reads = 0; var fail = false; var partial = false; var malformed = false;
        TaskCompletionSource<IntakeServiceReading>? pending = null;
        var client = new IntakeServiceMonitorClient([Source], (_, token) =>
        {
            reads++;
            if (fail) return Task.FromException<IntakeServiceReading>(new IOException("fixture"));
            if (malformed) return Task.FromResult(Reading() with { Schema = "unexpected" });
            if (partial) return Task.FromResult(Reading() with { Progress = [], History = [], Projects = [], ProcessingCount = null, Gaps = ["receipts_unavailable"] });
            return pending?.Task.WaitAsync(token) ?? Task.FromResult(Reading());
        });
        using var form = new IntakeServiceMonitorForm(chinese, client, observe: false) { Opacity = 0 };
        int? observed = null;
        form.ProcessingObserved += count => observed = count;
        form.ConfigureFor(new(-28500, -28000, 48, 44), true, dpi / 96f,
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), new(-30000, -30000, 3840, 2160));
        form.Show(); Application.DoEvents();
        var progress = Descendants(form).OfType<RouterGrid>().Single(grid => grid.ColumnCount == 3);
        Check(progress.EmptyTitle.Contains(chinese ? "正在读取" : "Reading", StringComparison.Ordinal),
            "Before the first observation the surface shows loading instead of an empty queue.");
        malformed = true;
        Pump(form.RefreshAsync());
        Check(progress.EmptyTitle.Contains(chinese ? "不可确认" : "unconfirmed", StringComparison.Ordinal)
            && form.Observation.History == 0, "A malformed first observation leaves the queue unconfirmed.");
        malformed = false;
        Pump(form.RefreshAsync());
        Check(form.Observation is { Projects: 2, History: 2, InFlight: 1 } && observed == 1,
            "A full panel observation also feeds the global number without another service read.");
        var buttons = Descendants(form).OfType<RouterButton>().ToArray();
        buttons.Single(b => b.Text == "LAB").PerformClick();
        Check(form.Observation.History == 1 && reads == 2, "Project selection filters the observation, without another business read.");
        buttons = Descendants(form).OfType<RouterButton>().ToArray();
        var normalHeight = progress.Height;
        buttons.Single(b => b.Text == "OTHER").PerformClick();
        Check(progress.Height < normalHeight && form.Observation.History == 1 && reads == 2,
            "Confirmed empty progress leaves room for history without another service read.");
        buttons = Descendants(form).OfType<RouterButton>().ToArray();
        buttons.Single(b => b.Text == (chinese ? "全部项目" : "All projects")).PerformClick();
        Check(form.Observation.History == 2, "All projects remains available and is the default.");
        var switched = 0; form.WorkItemsRequested += (_, _) => switched++;
        Descendants(form).OfType<RouterButton>().Single(b => b.Text == (chinese ? "工单详情" : "Work items")).PerformClick();
        Check(switched == 1, "The existing work-item detail route remains reachable.");
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = form.RefreshAsync();
        Check(!refresh.IsCompleted && form.Observation.History == 2, "An in-flight refresh keeps the existing tables visible.");
        pending.SetResult(Reading()); Pump(refresh); pending = null;
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = form.RefreshAsync();
        form.Hide(); form.Show(); pending = null;
        var beforeReentry = reads;
        Pump(form.RefreshAsync()); Pump(interrupted); Application.DoEvents();
        Check(reads > beforeReentry, "Rapid reopen restarts the cancelled observation without waiting for the next poll tick.");
        if (captures is not null)
        {
            Directory.CreateDirectory(captures);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(captures, $"intake-service-{(chinese ? "zh" : "en")}-{dpi}.png"));
        }
        partial = true; Pump(form.RefreshAsync());
        Check(form.Observation.History == 2 && form.Observation.Status.Contains(chinese ? "不完整" : "incomplete", StringComparison.Ordinal),
            "A partial service read retains history and cannot claim a successful empty queue.");
        partial = false; malformed = true; Pump(form.RefreshAsync());
        Check(form.Observation.History == 2 && form.Observation.Status.Contains(chinese ? "保留上次记录" : "Keeping previous records", StringComparison.Ordinal),
            "A non-conforming reply retains earlier rows and reports unavailability through the UI.");
        malformed = false; fail = true; Pump(form.RefreshAsync());
        Check(form.Observation.History == 2 && form.Observation.Status.Contains(chinese ? "不可用" : "unavailable", StringComparison.Ordinal),
            "A failed observation is unavailable with retained history, never an empty or healthy service.");
    }
    private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
        .SelectMany(control => new[] { control }.Concat(Descendants(control)));
    private static void Pump(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(2); }
        if (!task.IsCompleted) throw new InvalidOperationException("Fixture timed out.");
        task.GetAwaiter().GetResult(); Application.DoEvents();
    }
    private static void Throws(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid monitor input was accepted."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
