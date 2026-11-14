namespace ZGSTokenBar.App;

/// <summary>
/// Optional placement inside a verified gap of the primary taskbar. With no verified fit,
/// the full-size bar stays on the desktop side of the taskbar.
/// </summary>
internal sealed partial class BarForm
{
    private const int WmDestroy = 0x0002;
    private const int EmbedHungTicksBeforeFallback = 2;
    private const int LayoutPollMs = TaskbarRowStability.ReturnDelayMs;
    private const int LayoutUnavailablePollMs = 4000;
    private const int LayoutMaximumAgeMs = 3500;
    // Roughly half a standard app button; a centered group grows toward both sides.
    private const int RowBreathingRoom = 24;
    private static readonly TimeSpan EmbedRetryAfterFailure = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EmbedRetryAfterHang = TimeSpan.FromMinutes(2);
    private readonly System.Windows.Forms.Timer _embedWatchdog = new() { Interval = 1000 };
    private readonly WinEventDelegate _taskbarRowChangedHandler;
    private TaskbarCreatedListener? _taskbarCreatedListener;
    private nint _taskbarRowHook, _watchedTaskbar;
    private bool _taskbarRowProbePending;
    private long _taskbarRowProbeAfter;
    private long _taskbarRowPollAfter;
    private (nint Window, Rectangle Row, Rectangle Tray)? _taskbarRowKey;
    private (Rectangle[]? Occupied, long ReadAt) _taskbarRowSample;
    private readonly TaskbarRowStability _taskbarRowStability = new();
    private bool _embedRequested;
    private bool _embedTransition;
    private nint _embedParent;
    private nint _taskbarRowOverlayFor;
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

    internal Rectangle ScreenBounds => IsHandleCreated && TaskbarEmbedding.TryGetScreenBounds(Handle, out var bounds)
        ? bounds : new Rectangle(ScreenLocation, Size);

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
            _taskbarRowOverlayFor = 0;
            ResetTaskbarRowProbe();
            TaskbarEmbedStatus = "off";
            if (_taskbarDocked) SyncTaskbarPlacement();
        }
    }

    private bool _embedWatchdogWired;

    private bool TryGetSafeRowLocation(
        TaskbarPlacement.PlacementTrack track, Size size, Point anchor, Rectangle currentBounds, bool rowOverlay,
        out Point location, out string reason)
    {
        location = Point.Empty;
        reason = "";
        if (!_embedRequested) return false;
        if (!track.Primary) reason = "secondary-monitor";
        else if (!track.Horizontal || track.TrayBounds.IsEmpty || !track.ScreenBounds.Contains(track.TaskbarBounds))
            reason = "layout-unavailable";
        else if (rowOverlay && !TaskbarPlacement.HasReservedRow(track, Screen.FromHandle(track.TaskbarWindow).WorkingArea))
            reason = "unreserved-row";
        if (reason.Length != 0)
        {
            if (_watchedTaskbar != 0) ResetTaskbarRowProbe();
            return false;
        }

        WatchTaskbarRow(track.TaskbarWindow);
        var key = (track.TaskbarWindow, track.TaskbarBounds, track.TrayBounds);
        if (_taskbarRowKey != key)
        {
            InvalidateTaskbarRowSample();
            _taskbarRowKey = key;
        }
        var now = Environment.TickCount64;
        var observed = _taskbarRowSample;
        var elapsed = now - observed.ReadAt;
        if (!_taskbarRowProbePending && now >= _taskbarRowProbeAfter
            && (_taskbarRowStability.AwaitingLayout || now >= _taskbarRowPollAfter))
        {
            StartTaskbarRowProbe(key);
        }
        var occupied = elapsed < LayoutMaximumAgeMs ? observed.Occupied : null;
        var alreadyInRow = rowOverlay
            ? _taskbarRowOverlayFor == track.TaskbarWindow
            : EmbeddedInTaskbar && _embedParent == track.TaskbarWindow;
        var awaitingLayout = _taskbarRowStability.AwaitingLayout;
        var extraClearance = Scale(RowBreathingRoom);
        // Only enter the brief stale-layout grace period from a seat with room for an
        // ordinary button change. An exact fit is usable only while it is verified.
        if (alreadyInRow && occupied is not null
            && _taskbarRowStability.CanKeepCurrent(currentBounds.Size == size, now)
            && TaskbarPlacement.CurrentRowLocationFits(track, size, currentBounds.Location, occupied,
                awaitingLayout ? extraClearance : 0))
        {
            location = currentBounds.Location;
            return true;
        }

        // An unconfirmed layout can keep the existing seat briefly, but cannot justify a move or
        // an attachment. A missing reading or sustained event burst uses the full-size overlay.
        if (awaitingLayout || occupied is null)
        {
            _taskbarRowStability.ResetReturn();
            reason = "layout-unavailable";
            return false;
        }
        var rowAnchor = alreadyInRow ? currentBounds.Location : anchor;
        if (!TaskbarPlacement.TryGetRowLocation(track, size, rowAnchor, occupied, out location, extraClearance)
            && !TaskbarPlacement.TryGetRowLocation(track, size, rowAnchor, occupied, out location))
        {
            _taskbarRowStability.ResetReturn();
            reason = "no-clear-space";
            return false;
        }
        if (alreadyInRow || _taskbarRowStability.CanReturn(new Rectangle(location, size), observed.ReadAt))
            return true;
        reason = "waiting-for-space";
        return false;
    }

    private void StartTaskbarRowProbe((nint Window, Rectangle Row, Rectangle Tray) key)
    {
        _taskbarRowProbePending = true;
        var revision = _taskbarRowStability.LayoutRevision;
        var startedAt = Environment.TickCount64;
        _ = ReadTaskbarRowAsync();

        async Task ReadTaskbarRowAsync()
        {
            Rectangle[]? occupied = null;
            try
            {
                occupied = await Task.Run(() => TaskbarRowOccupancy.Read(key.Window, key.Row, key.Tray));
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
            finally
            {
                _taskbarRowProbePending = false;
            }
            if (IsDisposed || !IsHandleCreated || !_embedRequested || !_taskbarDocked) return;
            if (key == _taskbarRowKey && _taskbarRowStability.ConfirmLayout(revision))
            {
                var now = Environment.TickCount64;
                if (now - startedAt >= LayoutMaximumAgeMs) occupied = null;
                _taskbarRowSample = (occupied, startedAt);
                _taskbarRowPollAfter = occupied is null ? now + LayoutUnavailablePollMs : startedAt + LayoutPollMs;
            }
            SyncTaskbarPlacement();
        }
    }

    private void WatchTaskbarRow(nint taskbar)
    {
        if (taskbar == _watchedTaskbar) return;
        ResetTaskbarRowProbe();
        _watchedTaskbar = taskbar;
        if (TaskbarEmbedding.WindowThread(taskbar, out var processId) != 0)
        {
            // The accessibility event range includes children appearing, disappearing, moving,
            // and changed contents. Only the shell taskbar's own window tree can invalidate it.
            _taskbarRowHook = SetWinEventHook(0x8000, 0x800B, 0, _taskbarRowChangedHandler,
                processId, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        }
    }

    private void OnTaskbarRowChanged(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint time)
    {
        // Do not react to focus, hover names, or our own layered window updates.
        if (hook != _taskbarRowHook || type is not (0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x8004 or 0x800B)
            || !_embedRequested || !_taskbarDocked || IsDisposed || !IsHandleCreated
            || window == 0 || _watchedTaskbar == 0
            || (window != _watchedTaskbar && !TaskbarEmbedding.IsDescendant(_watchedTaskbar, window))) return;
        // Coalesce changes without immediately moving a confirmed, already embedded seat.
        // An in-flight result from before the event cannot validate the new geometry.
        var now = Environment.TickCount64;
        _taskbarRowStability.OnLayoutChanged(now);
        _taskbarRowProbeAfter = now + ShellSettleIntervalMs;
        if (Interlocked.Exchange(ref _taskbarSyncQueued, 1) != 0) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _taskbarSyncQueued, 0);
                SyncTaskbarPlacement();
                RestartShellSettleTimer();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _taskbarSyncQueued, 0); }
    }

    private void InvalidateTaskbarRowSample()
    {
        _taskbarRowSample = default;
        _taskbarRowProbeAfter = 0;
        _taskbarRowPollAfter = 0;
        _taskbarRowStability.ForgetLayout();
    }

    private void ResetTaskbarRowProbe()
    {
        if (_taskbarRowHook != 0) _ = UnhookWinEvent(_taskbarRowHook);
        _taskbarRowHook = 0;
        _watchedTaskbar = 0;
        _taskbarRowKey = null;
        InvalidateTaskbarRowSample();
    }

    /// <summary>
    /// Re-parenting and the style swap can drop a layered window's last frame; forget the
    /// unchanged-frame shortcut so the next render hands the pixels to the window again.
    /// </summary>
    private void RepushLayeredFrame()
    {
        _resetLayeredStyle = true;
        _glassFrameSize = Size.Empty;
        if (!GlassLayered) return;
        if (IsHandleCreated && !IsDisposed && Visible) RenderGlassLayered();
        QueueGlassRender();
    }

    /// <summary>Called from the docked placement path right before the bar is positioned.</summary>
    private void EnsureTaskbarEmbedding(string? resolvedMonitor)
    {
        if (_embedTransition) return;
        if (!_embedRequested || !_taskbarDocked || (_taskbarDragging && _taskbarDragMoved) || !IsHandleCreated || IsDisposed)
        {
            if (_embedParent != 0) DetachFromTaskbar(_embedRequested ? "undocked" : "off");
            return;
        }
        if (RemoteSession())
        {
            // A remote compositor can omit Explorer's layered child. The caller keeps
            // the bar top-level in that session, even when it can use a gap in the row.
            if (_embedParent != 0) DetachFromTaskbar("remote-session");
            TaskbarEmbedStatus = "fallback:remote-session";
            return;
        }
        var tray = TaskbarEmbedding.FindPrimaryTaskbar();
        var onPrimary = tray != 0 && (resolvedMonitor is null
            || string.Equals(Screen.FromHandle(tray).DeviceName, resolvedMonitor, StringComparison.OrdinalIgnoreCase));
        if (_embedParent != 0)
        {
            if (_embedParent == tray && onPrimary && TaskbarEmbedding.ParentOf(Handle) == tray
                && TaskbarEmbedding.IsChild(Handle)) return;
            DetachFromTaskbar(onPrimary ? "taskbar-changed" : "secondary-monitor");
            if (!onPrimary) return;
        }
        if (!onPrimary)
        {
            TaskbarEmbedStatus = tray == 0 ? "fallback:taskbar-missing" : "fallback:secondary-monitor";
            return;
        }
        if (_taskbarDragging || DateTime.UtcNow < _embedRetryAfter) return;
        (long Style, long ExStyle)? original;
        string failure;
        _embedTransition = true;
        try
        {
            // Put the child at its checked coordinates before showing it in the new parent.
            if (Visible) Hide();
            original = TaskbarEmbedding.Attach(Handle, tray, out failure);
            if (original is not null)
            {
                _embedOriginalStyle = original;
                _embedParent = tray;
            }
        }
        finally { _embedTransition = false; }
        if (original is null)
        {
            TaskbarEmbedStatus = "fallback:" + failure;
            _embedRetryAfter = DateTime.UtcNow + EmbedRetryAfterFailure;
            System.Diagnostics.Trace.WriteLine("ZGSTokenBar: taskbar embed fallback: " + failure);
            return;
        }
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
        _taskbarRowStability.ResetReturn();
        _embedTransition = true;
        try
        {
            if (IsHandleCreated && _embedOriginalStyle is { } original) TaskbarEmbedding.Detach(Handle, original);
            _embedOriginalStyle = null;
        }
        finally { _embedTransition = false; }
        TaskbarEmbedStatus = _embedRequested ? "fallback:" + reason : "off";
        TaskbarPlacement.InvalidateCache();
        RepushLayeredFrame();
    }

    private void EmbedWatchdogTick()
    {
        if (!_embedRequested || _embedTransition || IsDisposed) return;
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
                RequestTopologyRefresh(topologyMayHaveChanged: true);
                if (_taskbarDocked) SyncTaskbarPlacement();
                return;
            }
            if (!TaskbarEmbedding.IsAlive(_embedParent) || TaskbarEmbedding.ParentOf(Handle) != _embedParent
                || !TaskbarEmbedding.IsChild(Handle))
            {
                DetachFromTaskbar("taskbar-lost");
                _embedRetryAfter = DateTime.UtcNow + TimeSpan.FromSeconds(4);
                if (_taskbarDocked) SyncTaskbarPlacement();
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
                    if (_taskbarDocked) SyncTaskbarPlacement();
                }
                return;
            }
            _embedHungTicks = 0;
            SyncTaskbarPlacement();
            if (GlassLayered && _glassFrameSize != ClientSize) RepushLayeredFrame();
            return;
        }
        if (_taskbarDocked && !_hiddenToTray && (RemoteSession() || DateTime.UtcNow >= _embedRetryAfter))
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
        ResetTaskbarRowProbe();
        _embedRetryAfter = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        if (EmbeddedInTaskbar || _taskbarRowOverlayFor != 0 || _embedHandleLost) RequestTopologyRefresh(false);
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
        _embedTransition = true;
        _embedWatchdog.Stop();
        _embedWatchdog.Dispose();
        ResetTaskbarRowProbe();
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
