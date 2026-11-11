using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed partial class BarForm
{
    private System.Windows.Forms.Timer? _perfProbeTimer;

    /// <summary>
    /// Developer latency probe (ZGSTOKENBAR_PERF_PROBE=1 with ZGSTOKENBAR_PERF_TRACE): 20 s after
    /// start it drives the overflow expand/collapse and the Router hover/open paths five times each,
    /// exactly as the mouse handlers call them (without moving the cursor), and traces each step.
    /// </summary>
    private void StartPerfProbeIfRequested()
    {
        if (_renderOnly || !PerfTrace.Enabled || _perfProbeTimer is not null
            || Environment.GetEnvironmentVariable("ZGSTOKENBAR_PERF_PROBE") != "1") return;
        var step = 0;
        _perfProbeTimer = new System.Windows.Forms.Timer { Interval = 20_000 };
        _perfProbeTimer.Tick += (_, _) =>
        {
            _perfProbeTimer!.Interval = 700;
            var round = step / 6;
            if (round >= 5)
            {
                if (RunProbeStabilityStep(step - 30)) { step++; return; }
                _perfProbeTimer.Stop();
                PerfTrace.Note("probe.done");
                return;
            }
            var capture = round == 0 ? Environment.GetEnvironmentVariable("ZGSTOKENBAR_PROBE_CAPTURE") : null;
            switch (step % 6)
            {
                case 0:
                    if (capture is not null) SaveProbeImage(capture, "bar-collapsed.png", RenderGlassBitmap());
                    using (PerfTrace.Measure("probe.expand")) SetGlassExpanded(true);
                    break;
                case 1:
                    if (capture is not null)
                    {
                        SaveProbeImage(capture, "bar-expanded.png", RenderGlassBitmap());
                    }
                    using (PerfTrace.Measure("probe.collapse")) SetGlassExpanded(false);
                    break;
                case 2:
                    if (capture is not null)
                    {
                        _systemAnchorIsLive = true;
                        using (PerfTrace.Measure("probe.rate-hover")) ShowSystemUsagePopover(pinned: false);
                        SaveProbeImage(capture, "rate-hover.png", FormBitmap(_systemUsagePopover));
                        HideSystemUsagePopover();
                    }
                    using (PerfTrace.Measure("probe.router-hover"))
                        RequestPluginPopover(MiniAreaIds.Router, RouterProbeAnchor(), pinned: false);
                    break;
                case 3:
                    if (capture is not null) SaveProbeImage(capture, "router-hover.png", FormBitmap(_pluginPopover));
                    using (PerfTrace.Measure("probe.router-hide")) HidePluginPopover();
                    break;
                case 4:
                    using (PerfTrace.Measure("probe.router-open")) RequestPluginPopover(MiniAreaIds.Router, RouterProbeAnchor(), pinned: true);
                    break;
                default:
                    if (capture is not null) SaveProbeImage(capture, "router-panel.png", FormBitmap(_pluginPopover));
                    using (PerfTrace.Measure("probe.router-close")) HidePluginPopover();
                    break;
            }
            step++;
        };
        _perfProbeTimer.Start();
    }

    /// <summary>Raised by the probe to run the real Settings dialog save path (open, Save, close).</summary>
    internal event EventHandler<bool>? ProbeSettingsSaveRequested;

    /// <summary>
    /// Stability phase of the probe: a synthetic left click (down/up at one point, as the mouse
    /// handlers receive it; the real cursor never moves) and a real Settings save, each followed
    /// by embed/visibility samples so "the bar vanished" shows up in the trace.
    /// </summary>
    private bool RunProbeStabilityStep(int index)
    {
        switch (index)
        {
            case 0: NoteProbeState("before-click"); return true;
            case 1:
            {
                var point = ProbeClickPoint();
                using (PerfTrace.Measure("probe.click"))
                {
                    OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                    OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
                }
                NoteProbeState("click+0");
                return true;
            }
            case 2: NoteProbeState("click+0.7s"); HidePopovers(); return true;
            case 3: NoteProbeState("click+1.4s"); return true;
            case 4:
                using (PerfTrace.Measure("probe.settings-save")) ProbeSettingsSaveRequested?.Invoke(this, false);
                NoteProbeState("save+0");
                return true;
            case 5: NoteProbeState("save+0.7s"); return true;
            case 6: NoteProbeState("save+1.4s"); return true;
            case 7: NoteProbeState("save+2.1s"); return true;
            case 8: NoteProbeState("save+2.8s"); return true;
            case 9:
                using (PerfTrace.Measure("probe.settings-edit")) ProbeSettingsSaveRequested?.Invoke(this, true);
                NoteProbeState("edit+0");
                return true;
            case 10: NoteProbeState("edit+0.7s"); return true;
            case 11: NoteProbeState("edit+1.4s"); return true;
            case 12: return true;
            case 13: NoteProbeState("restore+0.7s"); return true;
            case 14: NoteProbeState("restore+1.4s"); return true;
            default: return false;
        }
    }

    private Point ProbeClickPoint()
    {
        // Centre of the first ring: a plain click on a quota item (not a chip, not Router).
        if (_cardBounds.Count > 0)
        {
            var b = _cardBounds[0].Bounds;
            return new Point((int)((b.X + b.Width / 2) * _scale), (int)((b.Y + b.Height / 2) * _scale));
        }
        return new Point(Scale(30), ClientSize.Height / 2);
    }

    private void NoteProbeState(string tag) => PerfTrace.Note(
        $"probe.state {tag} visible={Visible} embedded={EmbeddedInTaskbar} status={TaskbarEmbedStatus} docked={_taskbarDocked} "
        + $"handle={(IsHandleCreated ? Handle.ToString() : "none")} bounds={Bounds} screen={ScreenLocation}");

    /// <summary>The bar frame exactly as the layered window gets it (offscreen; no screen capture).</summary>
    private Bitmap RenderGlassBitmap()
    {
        var bitmap = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform(_scale, _scale);
        DrawGlassDock(graphics);
        return bitmap;
    }

    private static Bitmap? FormBitmap(Form? form)
    {
        if (form is not { Visible: true, IsDisposed: false } || form.ClientSize.Width <= 0) return null;
        var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    private static void SaveProbeImage(string directory, string name, Bitmap? bitmap)
    {
        if (bitmap is null) { PerfTrace.Note($"probe.capture {name} skipped"); return; }
        using (bitmap)
        {
            try
            {
                Directory.CreateDirectory(directory);
                bitmap.Save(Path.Combine(directory, name), System.Drawing.Imaging.ImageFormat.Png);
                PerfTrace.Note($"probe.capture {name}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                PerfTrace.Note($"probe.capture {name} failed {error.GetType().Name}");
            }
        }
    }

    private Rectangle RouterProbeAnchor()
    {
        var target = _taskbarPluginBounds.FirstOrDefault(item => item.Card.PluginId == MiniAreaIds.Router);
        return target.Card is null ? Bounds : QuotaTargetScreenBounds(target.Bounds);
    }
}
