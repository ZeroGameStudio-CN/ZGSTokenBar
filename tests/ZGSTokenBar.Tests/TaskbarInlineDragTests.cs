using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class TaskbarInlineDragTests
{
    internal static void Run()
    {
        foreach (var dpi in new[] { 96, 192 })
        {
            var scale = dpi / 96;
            using var parent = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Bounds = new(-30000, -30000, 1920 * scale, 48 * scale) };
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
            var tray = new Rectangle(row.Right - 200 * scale, row.Top, 200 * scale, row.Height);
            var apps = new Rectangle(row.Left + 800 * scale, row.Top, 400 * scale, row.Height);
            var track = new TaskbarPlacement.PlacementTrack("fixture", screen, true, true,
                row.Left + 6, tray.Left - bar.Width - 6, row.Top + (row.Height - bar.Height) / 2)
                { TaskbarWindow = parent.Handle, TaskbarBounds = row, TrayBounds = tray };
            Set("_embedRequested", true); Set("_embedParent", parent.Handle); Set("_embedOriginalStyle", original);
            Set("_watchedTaskbar", parent.Handle);
            Set("_taskbarRowKey", (parent.Handle, row, tray));
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
            Set("_taskbarRowPollAfter", long.MaxValue); Set("_taskbarRowProbeAfter", long.MaxValue);
            var start = new Point(row.Left + 100 * scale, track.Fixed);
            Check(TaskbarPlacement.ShowAt(bar.Handle, start, bar.Size), "Fixture starts in the row");

            foreach (var x in new[] { 160, 300, 450, 220 })
            {
                var requested = new Point(row.Left + x * scale, track.Fixed);
                Check(bar.TryMoveDragInRow(track, requested), "Horizontal dragging uses a verified inline target");
                Check(bar.ScreenBounds.Location == requested && bar.ScreenBounds.Y == start.Y,
                    "The native window follows horizontally without jumping above the taskbar");
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

            void Set(string name, object? value) =>
                typeof(BarForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bar, value);
        }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
