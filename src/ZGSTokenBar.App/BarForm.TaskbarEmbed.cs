namespace ZGSTokenBar.App;

/// <summary>
/// Optional "embed in taskbar" mode. While docked on the primary taskbar the bar becomes a child
/// of Shell_TrayWnd, positioned with the same track/relative position as the overlay. Anything
/// unexpected (setting off, undocked, secondary monitor, Explorer hung, DPI-awareness mismatch,
/// SetParent refused, Explorer restarted, Remote Desktop session) drops back to the top-level
/// topmost overlay, which is also the default.
/// </summary>
internal sealed partial class BarForm
{
    private const int WmDestroy = 0x0002;
    private const int EmbedHungTicksBeforeFallback = 2;
    private static readonly TimeSpan EmbedRetryAfterFailure = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EmbedRetryAfterHang = TimeSpan.FromMinutes(2);
    private readonly System.Windows.Forms.Timer _embedWatchdog = new() { Interval = 2000 };
    private TaskbarCreatedListener? _taskbarCreatedListener;
    private bool _embedRequested;
    private nint _embedParent;
    private (long Style, long ExStyle)? _embedOriginalStyle;
    private bool _embedHandleLost;
    private bool _embedClosing;
    private int _embedHungTicks;
    private DateTime _embedRetryAfter = DateTime.MinValue;

    internal bool EmbeddedInTaskbar => _embedParent != 0 && IsHandleCreated;

    /// <summary>Last embed outcome, for logs and acceptance checks.</summary>
    internal string TaskbarEmbedStatus { get; private set; } = "off";

    /// <summary>
    /// While embedded the bar cannot own popovers (that would make Shell_TrayWnd their owner and tie
    /// their lifetime to Explorer's), and a null owner is worse: WinForms then borrows the active
    /// window, which can be a window the popover itself owns ("a form cannot own itself or its
    /// owner"). Embedded popovers are owned by a hidden top-level window of this process instead.
    /// </summary>
    internal static IWin32Window PopoverOwnerFor(Control? owner) =>
        owner is null or BarForm { EmbeddedInTaskbar: true } ? PopoverOwnerWindow.Instance : owner;

    /// <summary>Window origin in screen coordinates whether embedded (child) or top-level.</summary>
    private Point ScreenLocation => EmbeddedInTaskbar ? PointToScreen(Point.Empty) : Location;

    private Rectangle ScreenBounds => new(ScreenLocation, Size);

    private void ApplyTaskbarEmbedSetting(bool enabled)
    {
        var requested = enabled && !_renderOnly;
        if (requested == _embedRequested) return;
        _embedRequested = requested;
        if (requested)
        {
            _taskbarCreatedListener ??= new TaskbarCreatedListener(OnTaskbarRecreated);
            if (!_embedWatchdogWired)
            {
                _embedWatchdog.Tick += (_, _) => EmbedWatchdogTick();
                _embedWatchdogWired = true;
            }
            _embedRetryAfter = DateTime.MinValue;
            _embedWatchdog.Start();
            TaskbarEmbedStatus = "pending";
            if (_taskbarDocked) SyncTaskbarPlacement();
        }
        else
        {
            _embedWatchdog.Stop();
            DetachFromTaskbar("off");
            TaskbarEmbedStatus = "off";
            if (_taskbarDocked) SyncTaskbarPlacement();
        }
    }

    private bool _embedWatchdogWired;

    /// <summary>
    /// Re-parenting and the style swap can drop a layered window's last frame; forget the
    /// unchanged-frame shortcut so the next render hands the pixels to the window again.
    /// </summary>
    private void RepushLayeredFrame()
    {
        _glassFrameSize = Size.Empty;
        if (GlassLayered) QueueGlassRender();
    }

    /// <summary>Called from the docked placement path right before the bar is positioned.</summary>
    private void EnsureTaskbarEmbedding(string? resolvedMonitor)
    {
        if (!_embedRequested || !_taskbarDocked || _taskbarDragging || !IsHandleCreated || IsDisposed)
        {
            if (_embedParent != 0) DetachFromTaskbar(_embedRequested ? "undocked" : "off");
            return;
        }
        if (RemoteSession())
        {
            // Over Remote Desktop the bar's layered child window is never composited into
            // Explorer's taskbar (the window exists, is visible and on top, yet the remote frame
            // shows only the taskbar), so a remote session always uses the top-level overlay.
            if (_embedParent != 0) DetachFromTaskbar("remote-session");
            TaskbarEmbedStatus = "fallback:remote-session";
            return;
        }
        var tray = TaskbarEmbedding.FindPrimaryTaskbar();
        var onPrimary = tray != 0 && (resolvedMonitor is null
            || string.Equals(Screen.FromHandle(tray).DeviceName, resolvedMonitor, StringComparison.OrdinalIgnoreCase));
        if (_embedParent != 0)
        {
            if (_embedParent == tray && onPrimary && TaskbarEmbedding.ParentOf(Handle) == tray) return;
            DetachFromTaskbar(onPrimary ? "taskbar-changed" : "secondary-monitor");
            if (!onPrimary) return;
        }
        if (!onPrimary)
        {
            TaskbarEmbedStatus = tray == 0 ? "fallback:taskbar-missing" : "fallback:secondary-monitor";
            return;
        }
        if (DateTime.UtcNow < _embedRetryAfter) return;
        var original = TaskbarEmbedding.Attach(Handle, tray, out var failure);
        if (original is null)
        {
            TaskbarEmbedStatus = "fallback:" + failure;
            _embedRetryAfter = DateTime.UtcNow + EmbedRetryAfterFailure;
            System.Diagnostics.Trace.WriteLine("ZGSTokenBar: taskbar embed fallback: " + failure);
            return;
        }
        _embedOriginalStyle = original;
        _embedParent = tray;
        _embedHungTicks = 0;
        TaskbarEmbedStatus = "embedded";
        TaskbarPlacement.InvalidateCache();
        RepushLayeredFrame();
        // Popovers already shown with the bar as owner keep that owner; drop them so the next
        // hover recreates them owner-less.
        HidePopovers();
    }

    private void DetachFromTaskbar(string reason)
    {
        if (_embedParent == 0) return;
        _embedParent = 0;
        if (IsHandleCreated && _embedOriginalStyle is { } original) TaskbarEmbedding.Detach(Handle, original);
        _embedOriginalStyle = null;
        TaskbarEmbedStatus = _embedRequested ? "fallback:" + reason : "off";
        TaskbarPlacement.InvalidateCache();
        RepushLayeredFrame();
        if (IsHandleCreated && Visible && _taskbarDocked && !_taskbarDragging) SyncTaskbarPlacement();
    }

    private void EmbedWatchdogTick()
    {
        if (!_embedRequested || IsDisposed) return;
        if (_embedHandleLost)
        {
            // Explorer went away and destroyed our child window with it; rebuild a top-level
            // window (overlay) and re-embed once the new taskbar answers.
            _embedHandleLost = false;
            _embedParent = 0;
            _embedOriginalStyle = null;
            TaskbarEmbedStatus = "fallback:explorer-restarted";
            _embedRetryAfter = DateTime.UtcNow + TimeSpan.FromSeconds(4);
            TaskbarPlacement.InvalidateCache();
            if (!IsHandleCreated && !_hiddenToTray)
            {
                Visible = false;
                Visible = true;
            }
            if (_taskbarDocked) SyncTaskbarPlacement();
            return;
        }
        if (_embedParent != 0)
        {
            if (RemoteSession())
            {
                // A console session was taken over by Remote Desktop: leave the taskbar now; the
                // watchdog re-embeds once the session is back on the console.
                DetachFromTaskbar("remote-session");
                return;
            }
            if (!TaskbarEmbedding.IsAlive(_embedParent) || TaskbarEmbedding.ParentOf(Handle) != _embedParent)
            {
                DetachFromTaskbar("taskbar-lost");
                _embedRetryAfter = DateTime.UtcNow + TimeSpan.FromSeconds(4);
                return;
            }
            if (TaskbarEmbedding.IsHung(_embedParent))
            {
                if (++_embedHungTicks >= EmbedHungTicksBeforeFallback)
                {
                    // Our input queue is shared with Explorer's while embedded; leave before a
                    // hung Explorer can freeze the bar too.
                    DetachFromTaskbar("explorer-hung");
                    _embedRetryAfter = DateTime.UtcNow + EmbedRetryAfterHang;
                }
                return;
            }
            _embedHungTicks = 0;
            TaskbarEmbedding.RaiseChild(Handle);
            return;
        }
        if (_taskbarDocked && Visible && DateTime.UtcNow >= _embedRetryAfter
            && (TaskbarEmbedStatus != "fallback:remote-session" || !RemoteSession()))
        {
            SyncTaskbarPlacement();
        }
    }

    /// <summary>
    /// True while this session is shown over Remote Desktop. Asked live (SM_REMOTESESSION), so a
    /// console session taken over by RDP, or handed back, is noticed by the next watchdog tick.
    /// </summary>
    private static bool RemoteSession() => SystemInformation.TerminalServerSession;

    private void OnTaskbarRecreated()
    {
        TaskbarPlacement.InvalidateCache();
        _embedRetryAfter = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        if (EmbeddedInTaskbar || _embedHandleLost) RequestTopologyRefresh(false);
    }

    /// <summary>Flags an Explorer-initiated destroy of the embedded window (not our own close).</summary>
    private void NoteEmbeddedDestroy(int message)
    {
        if (message == WmDestroy && _embedParent != 0 && !_embedClosing && !Disposing && !RecreatingHandle)
        {
            _embedHandleLost = true;
        }
    }

    private void DisposeTaskbarEmbedding()
    {
        _embedClosing = true;
        _embedWatchdog.Stop();
        _embedWatchdog.Dispose();
        _taskbarCreatedListener?.DestroyHandle();
        _taskbarCreatedListener = null;
        if (_embedParent != 0 && IsHandleCreated && _embedOriginalStyle is { } original)
        {
            TaskbarEmbedding.Detach(Handle, original);
        }
        _embedParent = 0;
    }

    /// <summary>
    /// Hidden top-level window that still receives the TaskbarCreated broadcast while the bar
    /// itself is a child window (broadcasts only reach top-level windows).
    /// </summary>
    private sealed class TaskbarCreatedListener : NativeWindow
    {
        private static readonly int TaskbarCreated = unchecked((int)RegisterWindowMessage("TaskbarCreated"));
        private readonly Action _onCreated;

        public TaskbarCreatedListener(Action onCreated)
        {
            _onCreated = onCreated;
            CreateHandle(new CreateParams { Caption = "ZGSTokenBar.TaskbarCreated", ExStyle = 0x00000080 });
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == TaskbarCreated) _onCreated();
            base.WndProc(ref message);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);
    }
}
