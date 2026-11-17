using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class TaskbarFullscreenTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run()
    {
        var context = SynchronizationContext.Current;
        try
        {
            var screen = new Rectangle(0, 0, 1920, 1080);
            Check(TaskbarPlacement.CoversMonitor(screen, screen), "Fullscreen geometry is recognized even when a browser retains maximized state.");
            Check(!TaskbarPlacement.CoversMonitor(new(0, 0, 1920, 1032), screen), "Ordinary maximization into the working area is not fullscreen.");
            Check(!TaskbarPlacement.CoversMonitor(Rectangle.Empty, screen), "Missing geometry is not fullscreen evidence.");
            foreach (var dpi in new[] { 96, 192 }) Cycle(dpi);
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static void Cycle(int dpi)
    {
        var scale = dpi / 96;
        using var parent = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Bounds = new(-30000, -30000, 1920 * scale, 48 * scale), Opacity = 0 };
        parent.Show();
        using var bar = new BarForm(new AppSettings { EnableAnimations = false }, QuotaSnapshot.Empty(DateTimeOffset.UtcNow),
            renderOnly: true, renderDpi: dpi) { Location = new(-29000, -29000), Opacity = 0 };
        bar.Show();
        var original = TaskbarEmbedding.Attach(bar.Handle, parent.Handle, out var reason);
        Check(original is not null, "Synthetic parent accepts the isolated bar: " + reason);
        Check(TaskbarEmbedding.TryGetScreenBounds(parent.Handle, out var row), "Fixture has real native geometry.");
        var trayWidth = Math.Min(200 * scale, row.Width / 6);
        var tray = new Rectangle(row.Right - trayWidth, row.Top, trayWidth, row.Height);
        var apps = new Rectangle(row.Left + row.Width * 3 / 5, row.Top, row.Width / 5, row.Height);
        var screen = new Rectangle(row.Left, row.Bottom - 1080 * scale, row.Width, 1080 * scale);
        var track = new TaskbarPlacement.PlacementTrack("fixture", screen, true, true, row.Left + 6, tray.Left - bar.Width - 6,
            row.Top + (row.Height - bar.Height) / 2) { TaskbarWindow = parent.Handle, TaskbarBounds = row, TrayBounds = tray };
        var anchor = new Point(row.Left + 8, track.Fixed);
        Check(TaskbarPlacement.ShowAt(bar.Handle, anchor, bar.Size), "Fixture places the child in its synthetic row.");
        Set("_embedRequested", true); Set("_embedParent", parent.Handle); Set("_embedOriginalStyle", original!);
        Set("_watchedTaskbar", parent.Handle); Set("_taskbarRowKey", (parent.Handle, row, tray));
        Set("_taskbarRowProbePending", true); Set("_taskbarRowProbeAfter", long.MaxValue); Set("_taskbarRowPollAfter", long.MaxValue);
        var fullscreen = false;
        Set("_fullscreenWindowProbe", (Func<string?, bool>)(_ => fullscreen));
        for (var cycle = 0; cycle < 4; cycle++)
        {
            fullscreen = true; Place();
            Check(!bar.Visible && bar.EmbeddedInTaskbar && TaskbarEmbedding.ParentOf(bar.Handle) == parent.Handle,
                "Fullscreen hides the existing native child without a visible adjacent fallback.");
            fullscreen = false; Place();
            Check(!bar.Visible && TaskbarEmbedding.ParentOf(bar.Handle) == parent.Handle,
                "Leaving fullscreen waits hidden for fresh layout instead of detaching and floating.");
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
            Place();
            Check(bar.Visible && bar.EmbeddedInTaskbar && TaskbarEmbedding.ParentOf(bar.Handle) == parent.Handle,
                "A verified free seat restores the same native child directly.");
            Check(TaskbarEmbedding.TryGetScreenBounds(bar.Handle, out var actual) && row.Contains(actual),
                "Restoration stays within the taskbar row at each DPI.");
        }
        void Set(string field, object value) => typeof(BarForm).GetField(field, Instance)!.SetValue(bar, value);
        void Place() => typeof(BarForm).GetMethod("ApplyDockedTarget", Instance)!.Invoke(bar, [track, bar.Size, anchor, true]);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
