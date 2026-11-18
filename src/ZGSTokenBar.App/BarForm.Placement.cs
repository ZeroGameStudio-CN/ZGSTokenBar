using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Platform host: taskbar placement, display topology, drag-to-move, DPI/region and window messages.</summary>
internal sealed partial class BarForm
{
    private const int WmWtsSessionChange = 0x02B1;
    private bool _sessionNotificationsRegistered;
    private bool _placingTaskbar;
    private TaskbarPlacement.PlacementTrack? _adjacentTaskbarTrack;
    private Point _adjacentTaskbarAnchor;
    private int? _dockedRenderDpi;
    private bool _taskbarFullscreenSuspended;
    private Func<string?, bool> _fullscreenWindowProbe = TaskbarPlacement.ShouldHideForFullscreen;

    public void SyncTaskbarPlacement()
    {
        if (_taskbarDragging && _taskbarDragMoved)
        {
            // A completed layout probe or the watchdog also advances a held drag:
            // entering a clear row must not require another mouse-move or mouse-up.
            if (_embedTransition || _placingTaskbar || IsDisposed || !IsHandleCreated || _hiddenToTray) return;
            if ((Control.MouseButtons & MouseButtons.Left) != 0) MoveTaskbarDrag(Cursor.Position);
            else FinishTaskbarDrag();
            return;
        }
        if (!_taskbarDocked)
        {
            _adjacentTaskbarTrack = null;
            _taskbarRowOverlayFor = 0;
            if (_embedParent != 0) DetachFromTaskbar("undocked");
            if (_watchedTaskbar != 0) ResetTaskbarRowProbe();
        }
        if (_hiddenToTray
            || !_taskbarDocked
            || _embedTransition
            || _placingTaskbar
            || IsDisposed
            || !IsHandleCreated)
        {
            if (!_taskbarDocked)
            {
                _taskbarPlacementMisses = 0;
                _taskbarPlacementEstablished = false;
            }
            return;
        }
        using var probe = PerfTrace.Measure("placement.probe");
        var topology = _placementCoordinator.ActiveTopology ?? _pendingTopology;
        if (topology is null) return;
        var profile = _placementCoordinator.ActiveProfile
            ?? _placementCoordinator.Preview(topology, Size);
        if (!profile.IsDocked) return;

        var placementSize = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
        if (placementSize.Width <= 0 || placementSize.Height <= 0) placementSize = Size;
        var preference = _placementCoordinator.DockedPreference(topology, profile);
        if (SuspendForFullscreen(preference.PreferredMonitorName ?? _resolvedTaskbarMonitor
            ?? Screen.FromHandle(Handle).DeviceName)) return;
        if (TaskbarPlacement.TryGetTrack(placementSize, preference.PreferredMonitorName, out var track))
        {
            // A remote/display reconnect can leave WinForms' DeviceDpi at the old value.
            // The taskbar hosting this dock is the current authority for its visual scale.
            if (ApplyTaskbarDpi(TaskbarEmbedding.DpiOf(track.TaskbarWindow)))
            {
                placementSize = Size;
                if (!TaskbarPlacement.TryGetTrack(placementSize, preference.PreferredMonitorName, out track))
                {
                    if (Visible) Hide();
                    return;
                }
            }
            var position = _placementCoordinator.PositionForResolvedMonitor(
                topology, profile, track.MonitorName, preference.RelativePosition);
            _taskbarPlacementMisses = 0;
            _taskbarPlacementEstablished = true;
            _resolvedTaskbarMonitor = track.MonitorName;
            ApplyDockedTarget(track, placementSize, track.LocationAt(position));
            return;
        }

        if (_taskbarPlacementEstablished && !EmbeddedInTaskbar && _taskbarRowOverlayFor == 0
            && ++_taskbarPlacementMisses < TaskbarPlacementMissThreshold)
        {
            return;
        }
        _taskbarPlacementEstablished = false;
        _adjacentTaskbarTrack = null;
        if (_taskbarRowOverlayFor != 0) TaskbarEmbedStatus = "fallback:taskbar-missing";
        _taskbarRowOverlayFor = 0;
        if (EmbeddedInTaskbar) DetachFromTaskbar("taskbar-missing");
        HidePopovers();
        if (Visible) Hide();
    }

    private void ApplyDockedTarget(TaskbarPlacement.PlacementTrack track, Size placementSize, Point anchor,
        bool preserveAdjacentPosition = true)
    {
        _placingTaskbar = true;
        try
        {
            if (SuspendForFullscreen(track.MonitorName)) return;
            var oldBounds = ScreenBounds;
            var wasEmbedded = EmbeddedInTaskbar;
            var wasInRow = wasEmbedded || _taskbarRowOverlayFor != 0;
            var remote = _embedRequested && RemoteSession();
            if (remote && wasEmbedded) DetachFromTaskbar("remote-session");
            var rowCandidate = TryGetSafeRowLocation(track, placementSize, anchor, oldBounds, remote, out var location, out var reason);
            var rowOverlay = rowCandidate && remote;
            if (rowCandidate && !remote)
            {
                if (!EmbeddedInTaskbar || _embedParent != track.TaskbarWindow
                    || TaskbarEmbedding.ParentOf(Handle) != track.TaskbarWindow || !TaskbarEmbedding.IsChild(Handle))
                    using (PerfTrace.Measure("placement.embed")) EnsureTaskbarEmbedding(track);
                if (!EmbeddedInTaskbar)
                {
                    // After native attachment fails, a top-level window can use the same row
                    // only with a confirmed clear gap and a row reserved by the Shell.
                    rowCandidate = TryGetSafeRowLocation(track, placementSize, anchor, oldBounds, rowOverlay: true,
                        out location, out reason);
                    rowOverlay = rowCandidate;
                }
            }
            if (!rowCandidate && _embedRequested && (reason is "layout-unavailable" or "waiting-for-space"))
            {
                // A transient recheck is not a request to switch to a desktop overlay.
                // Keep any existing native parent; a fresh verified fit restores in the row.
                TaskbarEmbedStatus = "hidden:" + reason;
                if (Visible) Hide();
                return;
            }
            _taskbarFullscreenSuspended = false;
            if (!rowCandidate)
            {
                if (EmbeddedInTaskbar) DetachFromTaskbar(reason);
                if (_embedRequested && !string.IsNullOrEmpty(reason)) TaskbarEmbedStatus = "fallback:" + reason;
            }

            var inRow = rowOverlay || EmbeddedInTaskbar;
            _taskbarRowOverlayFor = 0;
            // Cross the taskbar edge without jumping sideways, and keep that anchor through
            // subsequent refreshes instead of snapping back to the saved preference.
            var adjacentAnchor = preserveAdjacentPosition && wasInRow ? oldBounds.Location :
                preserveAdjacentPosition && _adjacentTaskbarTrack == track && _adjacentTaskbarAnchor == anchor
                    ? oldBounds.Location : anchor;
            if (!inRow && !TaskbarPlacement.TryGetAdjacentLocation(track, placementSize, adjacentAnchor, out location))
            {
                if (_embedRequested) TaskbarEmbedStatus = "fallback:screen-too-small";
                if (Visible) Hide();
                return;
            }

            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            var wasVisible = Visible;
            using (PerfTrace.Measure("placement.show-at"))
            {
                if (!TaskbarPlacement.ShowAt(Handle, location, placementSize))
                {
                    // ShowAt could fail while Explorer is rebuilding its taskbar. Do not leave a
                    // child on the old coordinates, or show an overlay in the icon row.
                    if (EmbeddedInTaskbar) DetachFromTaskbar("placement-failed");
                    else if (rowOverlay) TaskbarEmbedStatus = "fallback:placement-failed";
                    if (!TaskbarPlacement.TryGetAdjacentLocation(track, placementSize, adjacentAnchor, out location)
                        || !TaskbarPlacement.ShowAt(Handle, location, placementSize))
                    {
                        if (Visible) Hide();
                        return;
                    }
                    inRow = false;
                }
            }
            // Showing before moving can leave a one-frame overlap while Explorer lays out its icons.
            if (!wasVisible) Show();
            if (EmbeddedInTaskbar) TaskbarEmbedStatus = "embedded";
            if (inRow && rowOverlay)
            {
                _taskbarRowOverlayFor = track.TaskbarWindow;
                TaskbarEmbedStatus = remote ? "overlay:remote-session" : "overlay:embed-unavailable";
            }
            _adjacentTaskbarTrack = inRow ? null : track;
            _adjacentTaskbarAnchor = anchor;
            if (EmbeddedInTaskbar && !wasEmbedded && wasVisible) RepushLayeredFrame();
            if (oldBounds != ScreenBounds || wasEmbedded != EmbeddedInTaskbar)
                using (PerfTrace.Measure("placement.reposition-popovers")) RepositionPopovers();
        }
        finally { _placingTaskbar = false; }
    }

    private bool SuspendForFullscreen(string? monitor)
    {
        if (!_fullscreenWindowProbe(monitor)) return false;
        if (!_taskbarFullscreenSuspended) InvalidateTaskbarRowSample();
        _taskbarFullscreenSuspended = true;
        TaskbarEmbedStatus = "hidden:fullscreen";
        if (Visible) Hide();
        return true;
    }

    public void ClampToVisibleScreen()
    {
        if (_taskbarDocked) return;
        var screen = Screen.FromRectangle(Bounds);
        var area = screen.WorkingArea;
        var x = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        var y = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Location = new Point(x, y);
    }

    public void HideToTray()
    {
        _hiddenToTray = true;
        _taskbarRowOverlayFor = 0;
        HidePopovers();
        if (Visible) Hide();
    }

    public void RestoreFromTray()
    {
        _hiddenToTray = false;
        if (_taskbarDocked) SyncTaskbarPlacement();
        else if (!Visible) Show();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        // A recreated handle is always a fresh top-level window.
        _embedParent = 0;
        _embedOriginalStyle = null;
        _taskbarRowOverlayFor = 0;
        base.OnHandleCreated(e);
        _glassFrameSize = Size.Empty;
        if (!_renderOnly)
        {
            _sessionNotificationsRegistered = WTSRegisterSessionNotification(Handle, 0);
            _foregroundChangedHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                0,
                _foregroundChangedHandler,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_foregroundChangedHook == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "ZGSTokenBar: foreground WinEvent hook unavailable; using placement timer fallback.");
            }
        }
        UpdateDpiAndRegion();
        ApplySnapshotLayout(updatePlacement: false);
        if (GlassLayered)
        {
            _ = WindowBackdrop.TryPrepareLayered(Handle);
            QueueGlassRender();
        }
        if (!_renderOnly) RequestTopologyRefresh(topologyMayHaveChanged: false);
        StartHoverCaptureIfRequested();
        StartPerfProbeIfRequested();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_sessionNotificationsRegistered)
        {
            _ = WTSUnRegisterSessionNotification(Handle);
            _sessionNotificationsRegistered = false;
        }
        if (_foregroundChangedHook != 0)
        {
            _ = UnhookWinEvent(_foregroundChangedHook);
            _foregroundChangedHook = 0;
        }
        _shellSettleTimer.Stop();
        _topologyRefreshPending = false;
        _topologyMayHaveChangedPending = false;
        _pendingTopology = null;
        ResetTaskbarRowProbe();
        InvalidateTopologyCapture(handleActive: false);
        base.OnHandleDestroyed(e);
    }

    private void OnForegroundChanged(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (!_taskbarDocked || IsDisposed || !IsHandleCreated) return;
        if (Interlocked.Exchange(ref _taskbarSyncQueued, 1) != 0) return;
        try
        {
            _ = BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _taskbarSyncQueued, 0);
                StabilizeTaskbarPlacement();
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _taskbarSyncQueued, 0);
            // The handle can be destroyed between the guard and BeginInvoke during shutdown.
        }
    }

    private void StabilizeTaskbarPlacement()
    {
        if (!_taskbarDocked || IsDisposed || !IsHandleCreated) return;
        SyncTaskbarPlacement();
        _shellSettleTimer.Stop();
        _shellSettleTimer.Start();
    }

    private void RequestTopologyRefresh(bool topologyMayHaveChanged)
    {
        if (_renderOnly || IsDisposed || !IsHandleCreated) return;
        _topologyRefreshPending = true;
        _topologyMayHaveChangedPending |= topologyMayHaveChanged;
        _pendingTopology = null;
        InvalidateTaskbarRowSample();
        if (_taskbarDragging) _deferredTopologyDuringDrag = null;
        TaskbarPlacement.InvalidateCache();
        CaptureTopologyCandidate();
    }

    private void HandleShellSettleTick()
    {
        if (_topologyRefreshPending)
        {
            CaptureTopologyCandidate();
            return;
        }
        if (_taskbarDocked) SyncTaskbarPlacement();
    }

    private void CaptureTopologyCandidate()
    {
        if (!_topologyRefreshPending || IsDisposed || !IsHandleCreated) return;
        var startCapture = false;
        lock (_topologyCaptureSync)
        {
            if (_topologyCaptureDisposed) return;
            _topologyCaptureActive = true;
            _topologyCaptureRequested = true;
            _topologyCaptureGeneration++;
            if (!_topologyCaptureRunning)
            {
                _topologyCaptureRunning = true;
                startCapture = true;
            }
        }
        if (startCapture) _ = Task.Run(CaptureTopologyLoop);
    }

    private void CaptureTopologyLoop()
    {
        while (true)
        {
            long generation;
            lock (_topologyCaptureSync)
            {
                if (_topologyCaptureDisposed || !_topologyCaptureActive)
                {
                    _topologyCaptureRunning = false;
                    return;
                }
                generation = _topologyCaptureGeneration;
                _topologyCaptureRequested = false;
            }

            DisplayTopologySnapshot? captured = null;
            try
            {
                captured = _captureTopology();
            }
            catch
            {
                // A failed native topology probe is retried by the settle timer.
            }

            lock (_topologyCaptureSync)
            {
                if (_topologyCaptureDisposed || !_topologyCaptureActive)
                {
                    _topologyCaptureRunning = false;
                    return;
                }
                if (_topologyCaptureRequested) continue;
                _topologyCaptureRunning = false;
            }

            PostTopologyCandidate(generation, captured);
            return;
        }
    }

    private void PostTopologyCandidate(long generation, DisplayTopologySnapshot? captured)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            _ = BeginInvoke((Action)(() => CommitTopologyCandidate(generation, captured)));
        }
        catch (InvalidOperationException)
        {
            // The handle can be destroyed between the guard and BeginInvoke.
        }
    }

    private void CommitTopologyCandidate(long generation, DisplayTopologySnapshot? captured)
    {
        lock (_topologyCaptureSync)
        {
            if (_topologyCaptureDisposed
                || !_topologyCaptureActive
                || generation != _topologyCaptureGeneration)
            {
                return;
            }
        }
        if (!_topologyRefreshPending || IsDisposed || !IsHandleCreated) return;
        if (captured is null)
        {
            RestartShellSettleTimer();
            return;
        }

        if (_pendingTopology is not null
            && string.Equals(
                _pendingTopology.IdentitySignature,
                captured.IdentitySignature,
                StringComparison.Ordinal))
        {
            _topologyRefreshPending = false;
            _topologyMayHaveChangedPending = false;
            _pendingTopology = null;
            ActivateTopology(captured);
            return;
        }

        _pendingTopology = captured;
        if (_placementCoordinator.ActiveTopology is null)
        {
            var preview = _placementCoordinator.Preview(captured, Size);
            _taskbarDocked = preview.IsDocked;
            if (_taskbarDocked) SyncTaskbarPlacement();
            else MoveToFloatingPosition(captured, preview);
        }
        RestartShellSettleTimer();
    }

    private void InvalidateTopologyCapture(bool handleActive)
    {
        lock (_topologyCaptureSync)
        {
            _topologyCaptureActive = handleActive;
            _topologyCaptureRequested = false;
            _topologyCaptureGeneration++;
        }
    }

    private void ActivateTopology(DisplayTopologySnapshot topology)
    {
        if (_taskbarDragging)
        {
            _deferredTopologyDuringDrag = topology;
            if (!string.Equals(_dragTopologyKey, topology.Key, StringComparison.Ordinal))
            {
                _topologyChangedDuringDrag = true;
            }
            return;
        }

        var previousKey = _placementCoordinator.ActiveTopology?.Key;
        var activation = _placementCoordinator.Activate(topology, Size);
        var dockStateChanged = _taskbarDocked != activation.Profile.IsDocked;
        _taskbarDocked = activation.Profile.IsDocked;
        _resolvedTaskbarMonitor = null;
        // The shell can still have had its old geometry when the first capture started.
        TaskbarPlacement.InvalidateCache();
        if (dockStateChanged || !string.Equals(previousKey, topology.Key, StringComparison.Ordinal)) HidePopovers();
        if (_taskbarDocked) SyncTaskbarPlacement();
        else
        {
            _adjacentTaskbarTrack = null;
            _taskbarRowOverlayFor = 0;
            ApplySnapshotLayout(updatePlacement: false);
            MoveToFloatingPosition(topology, activation.Profile);
            if (!Visible && !_hiddenToTray) Show();
        }
        if (activation.MigrationCommit is { } migration) PlacementCommitted?.Invoke(this, migration);
    }

    private void RestoreActiveFloatingPosition()
    {
        if (_taskbarDragging
            || _placementCoordinator.ActiveTopology is not { } topology
            || _placementCoordinator.ActiveProfile is not { } profile
            || profile.IsDocked)
        {
            return;
        }
        MoveToFloatingPosition(topology, profile);
    }

    private void MoveToFloatingPosition(
        DisplayTopologySnapshot topology,
        WindowPlacementProfile profile)
    {
        var location = _placementCoordinator.FloatingLocation(topology, profile, Size);
        if (IsHandleCreated && !TaskbarPlacement.MoveAt(Handle, location, Size)) Location = location;
        else if (!IsHandleCreated) Location = location;
        RepositionPopovers();
    }

    private void RestartShellSettleTimer()
    {
        _shellSettleTimer.Stop();
        _shellSettleTimer.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        HidePopovers();
        UpdateDpiAndRegion();
        TaskbarPlacement.InvalidateCache();
        ApplySnapshotLayout();
        RepushLayeredFrame();
        RequestTopologyRefresh(topologyMayHaveChanged: false);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) HidePopovers();
        else if (GlassLayered) RepushLayeredFrame();
    }

    protected override void OnInvalidated(InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        if (GlassLayered) QueueGlassRender();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (GlassLayered) QueueGlassRender();
    }

    private void ContinueTaskbarDrag(MouseEventArgs e)
    {
        if ((e.Button & MouseButtons.Left) == 0)
        {
            FinishTaskbarDrag();
            return;
        }

        var currentScreen = Cursor.Position;
        MoveTaskbarDrag(currentScreen);
    }

    private void MoveTaskbarDrag(Point currentScreen)
    {
        var deltaX = currentScreen.X - _taskbarDragStartScreen.X;
        var deltaY = currentScreen.Y - _taskbarDragStartScreen.Y;
        if (!_taskbarDragMoved)
        {
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(deltaX) < Math.Max(dragSize.Width, Scale(6))
                && Math.Abs(deltaY) < Math.Max(dragSize.Height, Scale(6)))
            {
                return;
            }

            HidePopovers();
            ClearHoverStateForTaskbarDrag();
            TaskbarPlacement.InvalidateCache();
            _taskbarDragMoved = true;
            _lastDragClickAt = DateTime.MinValue;
        }

        var requested = new Point(
            _taskbarDragStartLocation.X + deltaX,
            _taskbarDragStartLocation.Y + deltaY);
        var preferredMonitor = PreferredTaskbarMonitor();
        if (TaskbarPlacement.TryGetDragTrack(Size, currentScreen, preferredMonitor, out var inlineTrack, Scale(32))
            && TryMoveDragInRow(inlineTrack, requested, allowEntry: true)) return;

        // Leave the taskbar only after inline movement is no longer possible.
        if (_embedParent != 0) DetachFromTaskbar("dragging");
        _taskbarRowOverlayFor = 0;
        if (TaskbarPlacement.TryGetDockTarget(
                Size,
                requested,
                currentScreen,
                preferredMonitor,
                out var dockedLocation,
                out _,
                out var resolvedMonitor))
        {
            _taskbarDocked = true;
            _resolvedTaskbarMonitor = resolvedMonitor;
            _taskbarDragCurrentLocation = dockedLocation;
            if (!TaskbarPlacement.MoveAt(Handle, dockedLocation, Size)) Location = dockedLocation;
            if (!Visible && !_hiddenToTray) Show();
            return;
        }

        _taskbarDocked = false;
        _taskbarDragCurrentLocation = requested;
        if (!TaskbarPlacement.MoveAt(Handle, requested, Size)) Location = requested;
        if (!Visible && !_hiddenToTray) Show();
    }

    internal bool TryMoveDragInRow(TaskbarPlacement.PlacementTrack track, Point requested, bool allowEntry = false)
    {
        if (!_embedRequested || !IsHandleCreated || IsDisposed) return false;
        var alreadyInRow = EmbeddedInTaskbar && _embedParent == track.TaskbarWindow
            || _taskbarRowOverlayFor == track.TaskbarWindow;
        var intentionalEntry = allowEntry && _taskbarDragging && _taskbarDragMoved;
        if (!alreadyInRow && !intentionalEntry) return false;
        _taskbarDocked = true;
        _resolvedTaskbarMonitor = track.MonitorName;
        if (intentionalEntry) ApplyTaskbarDpi(TaskbarEmbedding.DpiOf(track.TaskbarWindow));
        var rowOverlay = !EmbeddedInTaskbar || _embedParent != track.TaskbarWindow;
        if (rowOverlay && TaskbarPlacement.ShouldHideForFullscreen(track.MonitorName)) return false;
        if (!TryGetSafeRowLocation(track, Size, requested, ScreenBounds, rowOverlay,
            out var location, out _, followAnchor: true, intentionalEntry: intentionalEntry)) return false;
        if (rowOverlay && EmbeddedInTaskbar) DetachFromTaskbar("drag-target");
        if (!TaskbarPlacement.MoveAt(Handle, location, Size)) return false;
        // Keep capture on a top-level row preview while held; commit the native
        // parent swap in place on release, without moving back above the row.
        if (rowOverlay)
        {
            _taskbarRowOverlayFor = track.TaskbarWindow;
            TaskbarEmbedStatus = RemoteSession() ? "overlay:remote-session" : "overlay:drag-preview";
        }
        if (!Visible && !_hiddenToTray) Show();
        _taskbarDragCurrentLocation = location;
        return true;
    }

    private string? PreferredTaskbarMonitor()
    {
        if (!string.IsNullOrWhiteSpace(_resolvedTaskbarMonitor)) return _resolvedTaskbarMonitor;
        if (_placementCoordinator.ActiveTopology is not { } topology
            || _placementCoordinator.ActiveProfile is not { } profile)
        {
            return null;
        }
        return _placementCoordinator.DockedPreference(topology, profile).PreferredMonitorName;
    }

    internal static bool CanCommitTaskbarDrag(
        string? dragTopologyKey,
        string? activeTopologyKey,
        bool topologyChangedDuringDrag,
        bool topologyRefreshPending,
        bool topologyMayHaveChangedPending,
        bool topologyCandidateChanged)
    {
        return !topologyChangedDuringDrag
            && (!topologyRefreshPending || !topologyMayHaveChangedPending)
            && !topologyCandidateChanged
            && dragTopologyKey is not null
            && string.Equals(dragTopologyKey, activeTopologyKey, StringComparison.Ordinal);
    }

    private void FinishTaskbarDrag()
    {
        if (!_taskbarDragging) return;
        var moved = _taskbarDragMoved;
        var deferredTopology = _deferredTopologyDuringDrag;
        var validTopology = CanCommitTaskbarDrag(
            _dragTopologyKey,
            _placementCoordinator.ActiveTopology?.Key,
            _topologyChangedDuringDrag,
            _topologyRefreshPending,
            _topologyMayHaveChangedPending,
            _pendingTopology is { } candidate
                && !string.Equals(_dragTopologyKey, candidate.Key, StringComparison.Ordinal));
        _taskbarDragging = false;
        _taskbarDragMoved = false;
        _dragTopologyKey = null;
        _topologyChangedDuringDrag = false;
        _deferredTopologyDuringDrag = null;
        if (Capture) Capture = false;
        if (!moved)
        {
            CompleteTaskbarDrag(deferredTopology, restorePlacement: false);
            // A plain click never detaches any more; if something did, put the bar straight back
            // into the taskbar instead of leaving a top-level window behind it until the watchdog.
            if (_embedRequested && _embedParent == 0 && _taskbarDocked && IsHandleCreated && Visible) SyncTaskbarPlacement();
            ScheduleEmbedHealthCheck("click");
            return;
        }

        HidePopovers();
        ClearHoverStateForTaskbarDrag();
        _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(250);
        if (!validTopology)
        {
            CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
            return;
        }
        if (_taskbarDocked)
        {
            if (!TaskbarPlacement.TryConstrain(
                    Size,
                    _taskbarDragCurrentLocation,
                    _resolvedTaskbarMonitor,
                    out _,
                    out var relativePosition,
                    out var resolvedMonitor))
            {
                CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
                return;
            }
            _resolvedTaskbarMonitor = resolvedMonitor;
            _adjacentTaskbarTrack = null;
            _taskbarRowStability.ResetReturn();
            var commit = _placementCoordinator.CommitDocked(resolvedMonitor, relativePosition, Size);
            if (commit is not null)
            {
                PlacementCommitted?.Invoke(this, commit);
                CompleteTaskbarDrag(deferredTopology, restorePlacement: false);
            }
            else CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
            SyncTaskbarPlacement();
            ScheduleEmbedHealthCheck("drag");
            return;
        }

        var floatingLocation = ClampFloatingLocation(_taskbarDragCurrentLocation);
        _adjacentTaskbarTrack = null;
        if (!TaskbarPlacement.MoveAt(Handle, floatingLocation, Size)) Location = floatingLocation;
        var floatingCommit = _placementCoordinator.CommitFloating(new Rectangle(floatingLocation, Size));
        if (floatingCommit is not null)
        {
            PlacementCommitted?.Invoke(this, floatingCommit);
            CompleteTaskbarDrag(deferredTopology, restorePlacement: false);
        }
        else CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
    }

    private void CompleteTaskbarDrag(
        DisplayTopologySnapshot? deferredTopology,
        bool restorePlacement)
    {
        if (deferredTopology is not null)
        {
            ActivateTopology(deferredTopology);
            return;
        }
        if (restorePlacement) RestoreActivePlacement();
    }

    private Point ClampFloatingLocation(Point requestedLocation)
    {
        var bounds = new Rectangle(requestedLocation, Size);
        var area = Screen.FromRectangle(bounds).WorkingArea;
        return new Point(
            Math.Clamp(requestedLocation.X, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(requestedLocation.Y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private void RestoreActivePlacement()
    {
        if (_placementCoordinator.ActiveTopology is not { } topology
            || _placementCoordinator.ActiveProfile is not { } profile)
        {
            return;
        }
        _taskbarDocked = profile.IsDocked;
        if (_taskbarDocked) SyncTaskbarPlacement();
        else MoveToFloatingPosition(topology, profile);
    }

    protected override void WndProc(ref Message message)
    {
        NoteEmbeddedDestroy(message.Msg);
        base.WndProc(ref message);
        if (message.Msg == WmWtsSessionChange)
        {
            // Reconnecting with the same resolution need not send WM_DISPLAYCHANGE. Rebuild
            // session-scoped placement and the layered frame even when the monitor names match.
            _embedRetryAfter = DateTime.MinValue;
            _embedIncompatibleTaskbar = 0;
            _taskbarRowOverlayFor = 0;
            if (_taskbarDragging) _topologyChangedDuringDrag = true;
            if (RemoteSession() && _embedParent != 0) DetachFromTaskbar("remote-session");
            HidePopovers();
            RepushLayeredFrame();
            RequestTopologyRefresh(topologyMayHaveChanged: true);
        }
        if (message.Msg == TaskbarCreatedMessage
            || message.Msg is WmSettingChange or WmDisplayChange)
        {
            if (message.Msg == WmDisplayChange) _embedIncompatibleTaskbar = 0;
            RequestTopologyRefresh(message.Msg == WmDisplayChange);
        }
    }

    private void RestorePosition(AppSettings settings)
    {
        if (_taskbarDocked) return;
        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            Location = new Point(x, y);
            ClampToVisibleScreen();
            return;
        }

        var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Scale(16));
    }

    private void UpdateDpiAndRegion()
    {
        _scale = Math.Max(1, EffectiveDpi() / 96f);
        UpdateWindowRegion();
    }

    internal int RenderDpi => EffectiveDpi();

    private int EffectiveDpi() => ResolveRenderDpi(_renderDpi, _taskbarDocked, _dockedRenderDpi,
        IsHandleCreated ? TaskbarEmbedding.DpiOf(Handle) : 0, DeviceDpi);

    internal static int ResolveRenderDpi(int? renderOverride, bool docked, int? dockedDpi,
        int nativeWindowDpi, int frameworkDpi) =>
        renderOverride ?? (docked ? dockedDpi : null)
        ?? (nativeWindowDpi >= 96 ? nativeWindowDpi : dockedDpi ?? frameworkDpi);

    /// <summary>Use one scale for dock geometry, pixels, hit targets and popover anchors.</summary>
    internal bool ApplyTaskbarDpi(int dpi)
    {
        if (!_taskbarDocked || _renderDpi is not null || dpi < 96
            || _dockedRenderDpi == dpi && Math.Abs(_scale - dpi / 96f) < .001f) return false;
        _dockedRenderDpi = dpi;
        _embedIncompatibleTaskbar = 0;
        HidePopovers();
        if (Visible) Hide();
        if (EmbeddedInTaskbar) DetachFromTaskbar("dpi-changed");
        _taskbarRowOverlayFor = 0;
        TaskbarPlacement.InvalidateCache();
        InvalidateTaskbarRowSample();
        _glassFrameSize = Size.Empty;
        _resetLayeredStyle = true;
        var placing = _placingTaskbar;
        _placingTaskbar = true;
        try { ApplySnapshotLayout(updatePlacement: false); }
        finally { _placingTaskbar = placing; }
        RepushLayeredFrame();
        return true;
    }

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = RoundedRectangle(
            new RectangleF(0, 0, ClientSize.Width, ClientSize.Height),
            Scale(GlassDockLayout.Corner));
        Region?.Dispose();
        Region = new Region(path);
    }

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSRegisterSessionNotification(nint window, uint flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(nint window);
}
