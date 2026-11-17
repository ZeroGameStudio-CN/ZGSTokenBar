using System.Drawing;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class IntakeServiceMonitorTests
{
    private static readonly IntakeServiceSource Source = new("fixture", "Intake fixture", "worker-fixture", "intake-fixture.service", "/var/lib/intake-fixture");
    internal static void Run(string? captures = null)
    {
        var context = SynchronizationContext.Current;
        try
        {
            Throws(() => IntakeServiceMonitorClient.ValidateSources([Source with { SshTarget = "-oProxyCommand=bad" }]));
            Throws(() => IntakeServiceMonitorClient.ValidateSources([Source with { StateDirectory = "/var/lib/../etc" }]));
            Throws(() => IntakeServiceMonitorClient.ValidateSources([Source with { Unit = "x.service;touch x" }]));
            Throws(() => IntakeServiceMonitorClient.ValidateSources([Source, Source]));
            Throws(() => IntakeServiceMonitorClient.ValidateReading(Source, Reading() with { SourceId = "other" }));
            Throws(() => IntakeServiceMonitorClient.ValidateReading(Source, Reading() with { ObservedAt = DateTimeOffset.UtcNow.AddMinutes(-10) }));
            foreach (var dpi in new[] { 96, 144, 192 })
                foreach (var chinese in new[] { true, false }) View(dpi, chinese, captures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static IntakeServiceReading Reading()
    {
        var a = new IntakeServiceRecord("a", "00000000-0000-4000-8000-000000000001", "LAB", "LAB-1", "First feedback", "precheck", "model_started", null);
        var b = new IntakeServiceRecord("b", "00000000-0000-4000-8000-000000000002", "OTHER", "OTHER-2", "Older archive", "archive", "archived", DateTimeOffset.UtcNow.AddHours(-1));
        return new("zgstokenbar.intake-monitor.v1", Source.Id, DateTimeOffset.UtcNow, true, "running", "fixture",
            ["LAB", "OTHER"], [a], [a, b], []);
    }

    internal static IntakeServiceMonitorForm UnobservedPanel() => new(true,
        new IntakeServiceMonitorClient([Source], (_, _) => Task.FromResult(Reading())), observe: false);

    private static void View(int dpi, bool chinese, string? captures)
    {
        var reads = 0; var fail = false; var partial = false;
        TaskCompletionSource<IntakeServiceReading>? pending = null;
        var client = new IntakeServiceMonitorClient([Source], (_, token) =>
        {
            reads++;
            if (fail) return Task.FromException<IntakeServiceReading>(new IOException("fixture"));
            if (partial) return Task.FromResult(Reading() with { Progress = [], History = [], Projects = [], Gaps = ["receipts_unavailable"] });
            return pending?.Task.WaitAsync(token) ?? Task.FromResult(Reading());
        });
        using var form = new IntakeServiceMonitorForm(chinese, client, observe: false) { Opacity = 0 };
        form.ConfigureFor(new(-28500, -28000, 48, 44), true, dpi / 96f,
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), new(-30000, -30000, 3840, 2160));
        form.Show(); Application.DoEvents();
        Pump(form.RefreshAsync());
        Check(form.Observation is { Projects: 2, History: 2, InFlight: 1 }, "History and progress appear directly without a work-item selection.");
        var buttons = Descendants(form).OfType<RouterButton>().ToArray();
        buttons.Single(b => b.Text == "LAB").PerformClick();
        Check(form.Observation.History == 1 && reads == 1, "Project selection filters the observation, without another business read.");
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
        partial = false; fail = true; Pump(form.RefreshAsync());
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
