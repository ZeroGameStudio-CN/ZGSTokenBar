using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Platform host: taskbar placement, display topology, drag-to-move, DPI/region and window messages.</summary>
internal sealed partial class BarForm
{
    public void SyncTaskbarPlacement()
    {
        if (!_taskbarDocked && _embedParent != 0) DetachFromTaskbar("undocked");
        if (_hiddenToTray
            || !_taskbarDocked
            || _taskbarDragging
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
        if (TaskbarPlacement.ShouldHideForFullscreen())
        {
            _taskbarPlacementMisses = 0;
            _taskbarPlacementEstablished = false;
            HidePopovers();
            if (Visible) Hide();
            return;
        }
        var topology = _placementCoordinator.ActiveTopology ?? _pendingTopology;
        if (topology is null) return;
        var profile = _placementCoordinator.ActiveProfile
            ?? _placementCoordinator.Preview(topology, Size);
        if (!profile.IsDocked) return;

        var placementSize = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
        if (placementSize.Width <= 0 || placementSize.Height <= 0) placementSize = Size;
        var preference = _placementCoordinator.DockedPreference(topology, profile);
        var placementPosition = preference.RelativePosition;
        if (TaskbarPlacement.TryGetTarget(
                placementSize,
                placementPosition,
                preference.PreferredMonitorName,
                out var location,
                out var resolvedMonitor))
        {
            var resolvedPosition = _placementCoordinator.PositionForResolvedMonitor(
                topology,
                profile,
                resolvedMonitor,
                placementPosition);
            if (resolvedMonitor is { } monitor
                && resolvedPosition != placementPosition
                && TaskbarPlacement.TryGetTarget(
                    placementSize,
                    resolvedPosition,
                    monitor,
                    out var savedLocation,
                    out _))
            {
                location = savedLocation;
            }
            _taskbarPlacementMisses = 0;
            _taskbarPlacementEstablished = true;
            _resolvedTaskbarMonitor = resolvedMonitor;
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            if (!Visible) Show();
            using (PerfTrace.Measure("placement.embed")) EnsureTaskbarEmbedding(resolvedMonitor);
            using (PerfTrace.Measure("placement.show-at"))
                if (!TaskbarPlacement.ShowAt(Handle, location, placementSize) && !EmbeddedInTaskbar) Location = location;
            using (PerfTrace.Measure("placement.reposition-popovers")) RepositionPopovers();
            return;
        }

        if (_taskbarPlacementEstablished
            && ++_taskbarPlacementMisses < TaskbarPlacementMissThreshold)
        {
            return;
        }
        _taskbarPlacementEstablished = false;
        HidePopovers();
        if (Visible) Hide();
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
        HidePopovers();
        if (Visible) Hide();
    }

    public void RestoreFromTray()
    {
        _hiddenToTray = false;
        if (!Visible) Show();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        // A recreated handle is always a fresh top-level window.
        _embedParent = 0;
        _embedOriginalStyle = null;
        base.OnHandleCreated(e);
        _glassFrameSize = Size.Empty;
        if (!_renderOnly)
        {
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
        if (_foregroundChangedHook != 0)
        {
            _ = UnhookWinEvent(_foregroundChangedHook);
            _foregroundChangedHook = 0;
        }
        _shellSettleTimer.Stop();
        _topologyRefreshPending = false;
        _topologyMayHaveChangedPending = false;
        _pendingTopology = null;
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
        if (dockStateChanged || !string.Equals(previousKey, topology.Key, StringComparison.Ordinal)) HidePopovers();
        if (_taskbarDocked) SyncTaskbarPlacement();
        else
        {
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
        ApplySnapshotLayout();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) HidePopovers();
        else if (GlassLayered) QueueGlassRender();
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
        var deltaX = currentScreen.X - _taskbarDragStartScreen.X;
        var deltaY = currentScreen.Y - _taskbarDragStartScreen.Y;
        if (!_taskbarDragMoved)
        {
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(deltaX) < Math.Max(1, dragSize.Width / 2)
                && Math.Abs(deltaY) < Math.Max(1, dragSize.Height / 2))
            {
                return;
            }

            if (_embedParent != 0) DetachFromTaskbar("dragging");
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
        if (TaskbarPlacement.TryGetDockTarget(
                Size,
                requested,
                currentScreen,
                preferredMonitor,
                out var dockedLocation,
                out var relativePosition,
                out var resolvedMonitor))
        {
            _taskbarDocked = true;
            _resolvedTaskbarMonitor = resolvedMonitor;
            _taskbarDragCurrentLocation = dockedLocation;
            if (!TaskbarPlacement.MoveAt(Handle, dockedLocation, Size)) Location = dockedLocation;
            return;
        }

        _taskbarDocked = false;
        _taskbarDragCurrentLocation = requested;
        if (!TaskbarPlacement.MoveAt(Handle, requested, Size)) Location = requested;
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
                    out var location,
                    out var relativePosition,
                    out var resolvedMonitor))
            {
                CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
                return;
            }
            _resolvedTaskbarMonitor = resolvedMonitor;
            if (!TaskbarPlacement.ShowAt(Handle, location, Size)) Location = location;
            var commit = _placementCoordinator.CommitDocked(resolvedMonitor, relativePosition, Size);
            if (commit is not null)
            {
                PlacementCommitted?.Invoke(this, commit);
                CompleteTaskbarDrag(deferredTopology, restorePlacement: false);
            }
            else CompleteTaskbarDrag(deferredTopology, restorePlacement: true);
            return;
        }

        var floatingLocation = ClampFloatingLocation(_taskbarDragCurrentLocation);
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
        if (message.Msg == TaskbarCreatedMessage
            || message.Msg is WmSettingChange or WmDisplayChange)
        {
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

    private int EffectiveDpi() => _renderDpi ?? DeviceDpi;

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = RoundedRectangle(
            new RectangleF(0, 0, ClientSize.Width, ClientSize.Height),
            Scale(GlassDockLayout.Corner));
        Region?.Dispose();
        Region = new Region(path);
    }
}
