using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Text.Json;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class MonitorPanelMotionTests
{
    internal static void Run(string? evidence = null)
    {
        var frames = new List<object>();
        HandoffPainting(frames);
        var anchor = new Rectangle(420, 1086, 52, 52);
        var panel = new Rectangle(0, 200, 1520, 864);
        Check(TaskbarPopoverMath.ContainsHoverPoint(anchor, panel, new(445, 1075), 12),
            "The taskbar gap connects the icon to its full panel");
        Check(!TaskbarPopoverMath.ContainsHoverPoint(anchor, panel, new(800, 1075), 12),
            "The bridge does not retain an unrelated part of the taskbar");
        Check(!TaskbarPopoverMath.ContainsHoverPoint(anchor, panel with { Height = 500 }, new(445, 900), 12),
            "A distant popup cannot create a large sticky hover area");
        Check(TaskbarPopoverMath.ContainsHoverPoint(new(400, 400, 40, 40), new(450, 200, 300, 400), new(445, 420), 6),
            "Sideways placement retains only its short connecting gap");

        foreach (var dpi in new[] { 96, 192 })
        {
            using var owner = new BarForm(new AppSettings { EnableAnimations = false },
                QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: dpi)
            { Location = new(-30000, -30000), StartPosition = FormStartPosition.Manual, Opacity = 0 };
            owner.Show();
            foreach (var make in new Func<MonitorPanelForm>[] {
                () => new RouterMonitorForm(observe: false), () => new IntakeObserverForm(true, observeOnShow: false), IntakeServiceMonitorTests.UnobservedPanel })
            {
                using var popup = make();
                var scale = dpi / 96f;
                popup.Prewarm(scale, QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
                popup.ConfigureFor(new(-29500, -28000, 52, 52), false, scale,
                    QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), new(-30000, -30000, 3840, 2160));
                popup.ShowPrepared(owner, animate: true);
                var handle = popup.Handle;
                var bounds = popup.Bounds;
                Capture("entrance-start");
                Check(popup.Visible && popup.Opacity is > 0 and < 1 && !popup.Pinned,
                    "The prepared full hover begins as a non-activating translucent window");
                Check(GetLayeredWindowAttributes(handle, out _, out var alpha, out var flags) && alpha < 255 && (flags & 2) != 0,
                    "The native window compositor receives the entrance opacity");
                Wait(() => popup.Opacity >= .999, "entrance", () => Capture("entrance"));
                Check(popup.Handle == handle && popup.Bounds == bounds, "Entrance does not recreate or move the window");
                popup.DismissHover();
                // A busy native pump can finish a 90 ms fade in one dispatch. Supply a
                // compositor midpoint directly so reversal is deterministic without slowing production animation.
                popup.Opacity = .6;
                Capture("exit-midpoint");
                var exitingOpacity = popup.Opacity;
                popup.ConfigureFor(new(-29500, -28000, 52, 52), false, scale,
                    QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), new(-30000, -30000, 3840, 2160));
                Check(popup.HoverDismissing && popup.Opacity == exitingOpacity,
                    "A geometry/data refresh cannot cancel or restart a pending exit");
                popup.ResumeHover();
                Check(!popup.HoverDismissing && popup.Visible && popup.Opacity == exitingOpacity,
                    "Re-entry reverses from the current opacity without blinking or restarting a new window");
                Wait(() => popup.Opacity >= .999, "re-entry");
                popup.DismissHover(); popup.SetPinned(true);
                Check(popup.Visible && !popup.HoverDismissing && popup.Opacity == 1 && popup.Handle == handle,
                    "Pinning settles the same window immediately even while it is fading out");
                popup.DismissHover();
                Check(popup.Visible, "Hover exit cannot dismiss a pinned panel");
                popup.SetPinned(false); popup.DismissHover();
                Check(!popup.ContainsHoverPoint(Cursor.Position), "The isolated exit fixture keeps the real pointer outside its offscreen window");
                Wait(() => !popup.Visible, "unpin-exit");
                popup.ShowPrepared(owner, animate: false);
                Check(popup.Visible && popup.Opacity == 1, "Disabling animation keeps the direct show path");
                popup.DismissHover();
                Check(!popup.Visible, "Disabling animation keeps the direct hide path");
                popup.ShowPrepared(owner, animate: true, immediate: true);
                Check(popup.Visible && popup.Opacity == 1, "A ready sibling panel can replace the previous native frame immediately");
                popup.DismissHover();
                Check(popup.HoverDismissing, "An immediate sibling handoff still keeps the smooth exit enabled");
                popup.HideImmediately();
                popup.ShowPrepared(owner, animate: true); popup.HideImmediately();
                Application.DoEvents();
                Check(!popup.Visible && !popup.HoverDismissing,
                    "Explicit close or module disable cancels a pending entrance without a delayed reappearance");

                void Capture(string phase)
                {
                    if (evidence is null) return;
                    var layered = GetLayeredWindowAttributes(popup.Handle, out _, out var currentAlpha, out _);
                    frames.Add(new { panel = popup.GetType().Name, dpi, phase, at = Environment.TickCount64,
                        opacity = popup.Opacity, nativeAlpha = layered ? (int?)currentAlpha : null, popup.Bounds });
                }
                void Wait(Func<bool> condition, string phase, Action? sample = null) => PumpUntil(condition, sample, () =>
                {
                    if (evidence is not null) File.WriteAllText(evidence, JsonSerializer.Serialize(frames));
                    return $"{popup.GetType().Name}, dpi={dpi}, phase={phase}, opacity={popup.Opacity}, visible={popup.Visible}, exiting={popup.HoverDismissing}, bounds={popup.Bounds}";
                });
            }
        }
        if (evidence is not null) File.WriteAllText(evidence, JsonSerializer.Serialize(frames));
    }

    private static void PumpUntil(Func<bool> condition, Action? sample = null, Func<string>? detail = null)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < deadline) { Application.DoEvents(); sample?.Invoke(); Thread.Sleep(5); }
        Check(condition(), "The bounded native hover transition must settle: " + (condition() ? "" : detail?.Invoke()));
    }

    private static void HandoffPainting(List<object> frames)
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var owner = new BarForm(new AppSettings { EnableAnimations = false },
                QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: dpi)
            { Location = new(-30000, -30000), StartPosition = FormStartPosition.Manual, Opacity = 0 };
            owner.Show();
            foreach (var make in new Func<MonitorPanelForm>[] {
                () => new RouterMonitorForm(observe: false), () => new IntakeObserverForm(true, observeOnShow: false), IntakeServiceMonitorTests.UnobservedPanel })
            {
                using var popup = make();
                var scale = dpi / 96f;
                popup.Prewarm(scale, owner.UiTheme);
                popup.ConfigureFor(new(-29500, -28000, 52, 52), false, scale, owner.UiTheme,
                    new(-30000, -30000, 3840, 2160));
                var grids = Descendants(popup).OfType<DataGridView>().ToArray();
                var painted = new HashSet<Control>();
                var visibleOpacity = -1d;
                byte? visibleAlpha = null;
                popup.VisibleChanged += (_, _) =>
                {
                    if (!popup.Visible) return;
                    visibleOpacity = popup.Opacity;
                    visibleAlpha = GetLayeredWindowAttributes(popup.Handle, out _, out var alpha, out _) ? alpha : null;
                };
                foreach (var grid in grids) grid.Paint += (_, _) => painted.Add(grid);
                var handle = popup.Handle;
                foreach (var animate in new[] { true, false, true })
                {
                    painted.Clear();
                    var start = Environment.TickCount64;
                    popup.ShowPrepared(owner, animate, immediate: true);
                    frames.Add(new { panel = popup.GetType().Name, dpi, phase = "painted-handoff", animate,
                        visibleOpacity, visibleAlpha, paintedGrids = painted.Count, grids = grids.Length,
                        milliseconds = Environment.TickCount64 - start });
                    Check(visibleOpacity == 0 && visibleAlpha == 0,
                        "A sibling's native window stays fully transparent while its first frame is unpainted.");
                    Check(grids.Length > 0 && grids.All(painted.Contains),
                        "Every native child grid is painted synchronously before a handoff exposes the replacement.");
                    Check(popup.Visible && popup.Opacity == 1 && popup.Handle == handle,
                        "Cold and reused sibling panels become opaque without recreating their native handle.");
                    popup.HideImmediately();
                }
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>()
        .SelectMany(control => new[] { control }.Concat(Descendants(control)));
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
}
