using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class TaskbarInlineDragTests
{
    internal static void Run()
    {
        foreach (var (dpi, width) in new[] { (96, 1920), (192, 3840), (96, 1024), (192, 1024) })
        {
            var scale = dpi / 96;
            using var parent = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Bounds = new(-30000, -30000, width, 48 * scale) };
            parent.Show();
            using var bar = new BarForm(new AppSettings { EnableAnimations = false },
                QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: dpi)
                { Location = new(-29900, -30000), StartPosition = FormStartPosition.Manual };
            bar.Show();
            Application.DoEvents();
            var original = TaskbarEmbedding.Attach(bar.Handle, parent.Handle, out var failure);
            Check(original is not null, "The isolated taskbar owns the native child: " + failure);
            Check(TaskbarEmbedding.TryGetScreenBounds(parent.Handle, out var row), "Fixture taskbar has native bounds");
            var screen = new Rectangle(row.Left, row.Bottom - 1080 * scale, row.Width, 1080 * scale);
            // Hosted desktops can constrain offscreen top-level windows to a smaller
            // native width. Keep targets inside the actual fixture, independent of that limit.
            var trayWidth = Math.Min(200 * scale, row.Width / 6);
            var tray = new Rectangle(row.Right - trayWidth, row.Top, trayWidth, row.Height);
            var apps = new Rectangle(row.Left + row.Width * 3 / 5, row.Top, row.Width / 5, row.Height);
            var track = new TaskbarPlacement.PlacementTrack("fixture", screen, true, true,
                row.Left + 6, tray.Left - bar.Width - 6, row.Top + (row.Height - bar.Height) / 2)
                { TaskbarWindow = parent.Handle, TaskbarBounds = row, TrayBounds = tray };
            Set("_embedRequested", true); Set("_embedParent", parent.Handle); Set("_embedOriginalStyle", original);
            Set("_watchedTaskbar", parent.Handle);
            Set("_taskbarRowKey", (parent.Handle, row, tray));
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
            Set("_taskbarRowPollAfter", long.MaxValue); Set("_taskbarRowProbeAfter", long.MaxValue);
            var safeLeft = row.Left + 6;
            var safeRight = apps.Left - 6 - 24 * scale - bar.Width;
            Check(safeRight > safeLeft + 40, $"Fixture has horizontal room: dpi={dpi}, row={row}, bar={bar.Size}");
            var start = new Point(safeLeft + (safeRight - safeLeft) / 10, track.Fixed);
            Check(TaskbarPlacement.ShowAt(bar.Handle, start, bar.Size), "Fixture starts in the row");

            foreach (var fraction in new[] { .2, .4, .7, .3 })
            {
                var requested = new Point(safeLeft + (int)((safeRight - safeLeft) * fraction), track.Fixed);
                Check(bar.TryMoveDragInRow(track, requested), "Horizontal dragging uses a verified inline target");
                Check(bar.ScreenBounds.Location == requested && bar.ScreenBounds.Y == start.Y,
                    $"The native window follows horizontally: dpi={dpi}, requested={requested}, actual={bar.ScreenBounds}, row={row}");
                Check(TaskbarEmbedding.IsChild(bar.Handle) && TaskbarEmbedding.ParentOf(bar.Handle) == parent.Handle
                    && bar.Visible, "Dragging retains the original native parent and visibility");
            }
            Check(bar.TryMoveDragInRow(track, new Point(apps.Left, track.Fixed)), "Dragging toward buttons clamps to clear space");
            Check(!bar.ScreenBounds.IntersectsWith(apps) && !bar.ScreenBounds.IntersectsWith(tray),
                "Inline movement cannot cover native buttons or the tray");
            var cursor = new Point(bar.ScreenBounds.Left + 20, row.Top + row.Height / 2);
            Check(TaskbarPlacement.SelectDockTrack([track], cursor, null, 32 * scale) is not null,
                "A pointer inside the taskbar retains its dock");
            Check(TaskbarPlacement.SelectDockTrack([track], cursor with { Y = row.Top - 33 * scale },
                null, 32 * scale) is null, "A deliberate outward pull leaves the scaled capture zone");

            // Re-enter while the button is still held. This uses the real native
            // top-level window without sending pointer input to the desktop.
            TaskbarEmbedding.Detach(bar.Handle, original!.Value);
            Set("_embedParent", (nint)0); Set("_embedOriginalStyle", null);
            Set("_taskbarRowOverlayFor", (nint)0); Set("_taskbarDocked", false);
            Set("_taskbarDragging", true); Set("_taskbarDragMoved", true);
            var floating = new Point(start.X, row.Top - 120 * scale);
            Check(TaskbarPlacement.MoveAt(bar.Handle, floating, bar.Size), "Fixture leaves the row");
            var destination = new Point(safeLeft + (safeRight - safeLeft) / 2, track.Fixed);
            Check(!bar.TryMoveDragInRow(track, destination), "Background placement cannot bypass deliberate-entry admission");
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64 - 4000));
            Check(!bar.TryMoveDragInRow(track, destination, allowEntry: true),
                "A stale snapshot cannot admit a held drag into native controls");
            Set("_taskbarRowSample", (new Rectangle[] { row }, Environment.TickCount64));
            Check(!bar.TryMoveDragInRow(track, destination, allowEntry: true),
                "A crowded row never admits the whole bar");
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
            Check(bar.TryMoveDragInRow(track, destination, allowEntry: true)
                && bar.ScreenBounds.Location == destination && bar.Visible,
                "A fresh clear gap places the held drag in the row before mouse-up");
            Check(!bar.EmbeddedInTaskbar && bar.TaskbarEmbedStatus.StartsWith("overlay:"),
                "The held preview retains capture on its top-level window");
            Set("_taskbarDragging", false); Set("_taskbarDragMoved", false);
            object?[] arguments = [track, bar.Size, destination, bar.ScreenBounds, false, null, null, false, false];
            var accepted = (bool)typeof(BarForm).GetMethod("TryGetSafeRowLocation",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bar, arguments)!;
            Check(accepted && (Point)arguments[5]! == destination,
                "Release keeps the verified preview coordinates instead of returning to the adjacent overlay");

            void Set(string name, object? value) =>
                typeof(BarForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bar, value);
        }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
