using System.Drawing;
using System.Reflection;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class TaskbarFreshSeatTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run()
    {
        var context = SynchronizationContext.Current;
        try
        {
            // Read-only placement decisions against a synthetic row: no real taskbar or input.
            var row = new Rectangle(0, 1000, 1920, 48);
            var tray = new Rectangle(1700, 1000, 220, 48);
            var apps = new Rectangle(1200, 1000, 400, 48);
            var seat = new Rectangle(18, 1002, 600, 44);
            nint parent = 123;
            var track = new TaskbarPlacement.PlacementTrack("fixture", new(0, 0, 1920, 1048), true, true, 6, 1094, 1002)
                { TaskbarWindow = parent, TaskbarBounds = row, TrayBounds = tray };
            using var bar = new BarForm(new AppSettings(), QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: 96);
            Set("_embedRequested", true); Set("_taskbarDocked", true); Set("_watchedTaskbar", parent);
            Set("_taskbarRowOverlayFor", parent); Set("_taskbarRowKey", (parent, row, tray));
            Set("_taskbarRowProbePending", true);
            var now = Environment.TickCount64;
            var state = (TaskbarRowStability)typeof(BarForm).GetField("_taskbarRowStability", Instance)!.GetValue(bar)!;
            state.OnLayoutChanged(now - 550);
            var obsolete = state.LayoutRevision;
            state.OnLayoutChanged(now - 50);
            Check(!state.ConfirmLayout(obsolete), "A read crossed by a later notice cannot authorize attachment.");
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, now - 80));
            Check(Safe(seat.Size), "A fresh clear reading keeps the existing unchanged seat during a layout-notice burst.");
            Check(state.AwaitingLayout, "Retaining a seat never confirms an obsolete layout revision.");
            Set("_taskbarRowOverlayFor", (nint)0);
            Check(!Safe(seat.Size), "A fresh but unconfirmed read cannot attach or move into a row.");
            Set("_taskbarRowOverlayFor", parent);
            Set("_taskbarRowSample", (new Rectangle[] { new(550, 1000, 300, 48), tray }, Environment.TickCount64));
            Check(!Safe(seat.Size), "A real button overlap still leaves the row immediately.");
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64 - 700));
            Check(!Safe(seat.Size), "Missing fresh evidence cannot retain stale geometry indefinitely.");
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
            Check(!Safe(new Size(seat.Width + 20, seat.Height)), "An unverified resize cannot use the old seat.");
            Set("_taskbarRowSample", ((Rectangle[]?)null, Environment.TickCount64));
            Check(!Safe(seat.Size), "An unavailable read is never a successful clear gap.");

            void Set(string name, object value) => typeof(BarForm).GetField(name, Instance)!.SetValue(bar, value);
            bool Safe(Size size)
            {
                object?[] args = [track, size, seat.Location, seat, false, Point.Empty, "", false, false];
                return (bool)typeof(BarForm).GetMethod("TryGetSafeRowLocation", Instance)!.Invoke(bar, args)!;
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
