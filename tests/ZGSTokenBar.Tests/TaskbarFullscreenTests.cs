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
        using var bar = new BarForm(new AppSettings { TaskbarDocked = true, EnableAnimations = false },
            QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true)
            { Location = new(-29000, -29000), Opacity = 0 };
        bar.ApplyTaskbarDpi(dpi);
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
        Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
        Place();
        var verifiedSeat = bar.ScreenBounds;
        var handle = bar.Handle;
        var failureTime = Environment.TickCount64;
        foreach (var sample in new (Rectangle[]? Occupied, long ReadAt)[]
        {
            ([apps, tray], failureTime - 10_000),
            (null, failureTime),
            (null, failureTime - 4_000),
        })
        {
            Set("_taskbarRowSample", sample);
            Place();
            Check(bar.Visible && TaskbarEmbedding.IsVisible(handle) && bar.Handle == handle
                && bar.NativeTaskbarParentVerified && bar.ScreenBounds == verifiedSeat,
                "Stale and repeated unavailable reads keep the same native child visible at its confirmed bounds.");
        }

        var rowStability = (TaskbarRowStability)typeof(BarForm).GetField("_taskbarRowStability", Instance)!.GetValue(bar)!;
        rowStability.OnLayoutChanged(failureTime - 750);
        Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, failureTime - 700));
        Place();
        Check(bar.Visible && bar.ScreenBounds == verifiedSeat,
            "A pending layout revision holds the seat beyond the old refresh grace.");
        rowStability.ConfirmLayout(rowStability.LayoutRevision);

        var obstruction = new Rectangle(verifiedSeat.Left, row.Top, verifiedSeat.Width + 8 * scale, row.Height);
        Set("_taskbarRowSample", (new Rectangle[] { obstruction, apps, tray }, Environment.TickCount64));
        Place();
        Check(bar.Visible && bar.Handle == handle && bar.TaskbarEmbedStatus == "embedded"
            && !bar.ScreenBounds.IntersectsWith(obstruction),
            "A new confirmed obstruction moves the same bar into a clear part of the row.");
        using (var sibling = new Form { ShowInTaskbar = false, Opacity = 0 })
        {
            sibling.Show();
            Check(TaskbarEmbedding.Attach(sibling.Handle, parent.Handle, out _) is not null
                && TaskbarPlacement.ShowAt(sibling.Handle, anchor, bar.Size)
                && !TaskbarEmbedding.IsTopSibling(bar.Handle),
                "Another child can cover even a clear, unchanged seat.");
            var currentSeat = bar.ScreenBounds;
            Place();
            Check(bar.ScreenBounds == currentSeat && TaskbarEmbedding.IsTopSibling(bar.Handle),
                "Placing an unchanged seat still restores the bar's child z-order when needed.");
        }
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

        if (dpi == 192)
        {
            var originalHandle = bar.Handle;
            foreach (var nextDpi in new[] { 96, 144, 192 })
            {
                Check(bar.ApplyTaskbarDpi(nextDpi, parent.Handle), "The current taskbar can change its rendering DPI.");
                Check(!TaskbarEmbedding.IsVisible(originalHandle) && bar.NativeTaskbarParentVerified
                    && bar.Handle == originalHandle && TaskbarEmbedding.ParentOf(originalHandle) == parent.Handle,
                    "DPI refitting hides the unverified frame while retaining the same native child.");
                Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
                Place();
                var bounds = bar.ScreenBounds;
                Check(TaskbarEmbedding.IsVisible(originalHandle) && bar.NativeTaskbarParentVerified
                    && row.Contains(bounds) && bounds.Height == (int)Math.Round(44 * nextDpi / 96d)
                    && bounds.Y == row.Top + (row.Height - bounds.Height) / 2,
                    "At the new scale the verified child returns centered in the same taskbar row.");
            }
        }

        // A display switch can detach the child, and Windows can reject SetParent on that row.
        // Start after that refusal, with a real top-level HWND and isolated Shell geometry.
        typeof(BarForm).GetMethod("DetachFromTaskbar", Instance)!.Invoke(bar, ["dpi-changed"]);
        Set("_embedIncompatibleTaskbar", parent.Handle);
        Set("_reservedTaskbarRowProbe", (Func<TaskbarPlacement.PlacementTrack, bool>)(t =>
            TaskbarPlacement.HasReservedRow(t, Rectangle.FromLTRB(screen.Left, screen.Top, screen.Right, row.Top))));
        Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64 - TaskbarRowStability.ReturnDelayMs));
        Place();
        Check(!bar.Visible, "After loss of the native parent, a single read cannot show the bar beside the row.");
        Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
        Place();
        Check(bar.Visible && !bar.EmbeddedInTaskbar && bar.TaskbarEmbedStatus == "overlay:embed-unavailable"
            && bar.ScreenBounds.Size == bar.Size && row.Contains(bar.ScreenBounds),
            "A reserved and verified row keeps the full-size top-level bar in place after attachment is refused.");
        var overlayBounds = bar.ScreenBounds;
        Set("_taskbarRowSample", ((Rectangle[]?)null, Environment.TickCount64));
        Place();
        Check(bar.Visible && TaskbarEmbedding.IsVisible(bar.Handle) && bar.ScreenBounds == overlayBounds
            && bar.TaskbarEmbedStatus == "overlay:embed-unavailable",
            "An unavailable layout keeps a visible top-level bar in its reserved, unchanged row.");
        fullscreen = true; Place();
        Check(!bar.Visible, "The verified top-level overlay also yields to fullscreen.");
        fullscreen = false; Place();
        Check(!bar.Visible, "The overlay stays hidden until its row has been checked again.");
        Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
        Place();
        Check(bar.Visible && bar.ScreenBounds == overlayBounds, "The overlay returns directly to the same row.");
        Set("_reservedTaskbarRowProbe", (Func<TaskbarPlacement.PlacementTrack, bool>)(_ => false));
        Place();
        Check(bar.Visible && !row.IntersectsWith(bar.ScreenBounds), "A top-level window cannot cover an unreserved row.");

        void Set(string field, object value) => typeof(BarForm).GetField(field, Instance)!.SetValue(bar, value);
        void Place() => typeof(BarForm).GetMethod("ApplyDockedTarget", Instance)!.Invoke(bar, [track, bar.Size, anchor, true]);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
