using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Mouse input and hit-testing: hover, click, resize and reorder of mini areas.</summary>
internal sealed partial class BarForm
{
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_reorderingMiniAreaId is not null)
        {
            ContinueMiniAreaReorder(e);
            return;
        }
        if (_resizingMiniAreaId is not null)
        {
            ContinueMiniAreaResize(e);
            return;
        }
        if (_taskbarDragging)
        {
            ContinueTaskbarDrag(e);
            return;
        }

        var logical = ToLogical(e.Location);
        if (_glassDock)
        {
            _glassLeftAt = null;
            var onChip = !_glassChipBounds.IsEmpty && _glassChipBounds.Contains(logical);
            if (onChip != _glassChipHovered)
            {
                _glassChipHovered = onChip;
                if (onChip && !_glassExpanded) _glassChipTimer.Start();
                else _glassChipTimer.Stop();
                Invalidate();
            }
            var onVisibility = !_glassVisibilityBounds.IsEmpty && _glassVisibilityBounds.Contains(logical);
            if (onVisibility != _glassVisibilityHovered)
            {
                _glassVisibilityHovered = onVisibility;
                Invalidate();
            }
            if (onChip || onVisibility)
            {
                UpdateSystemUsageHover(false);
                UpdateRadarHover(null);
                UpdateCodexAccountsHover(null);
                UpdateQuotaHover(null);
                _hoverPluginTarget = null;
                UpdateHintHover(HoverTarget.None);
                _hoverTarget = HoverTarget.None;
                Cursor = Cursors.Hand;
                return;
            }
        }

        var resizeTarget = TaskbarResizeTargetAt(logical);
        if (resizeTarget is not null)
        {
            _hoverMiniAreaId = null;
            _hoverReorderAreaId = null;
            _hoverResizeAreaId = resizeTarget.AreaId;
            _hoverPluginTarget = null;
            UpdateSystemUsageHover(false);
            UpdateRadarHover(null);
            UpdateCodexAccountsHover(null);
            UpdateQuotaHover(null);
            UpdateHintHover(HoverTarget.None);
            UpdateCodexStrategyHover(false);
            _hoverTarget = HoverTarget.None;
            Cursor = Cursors.SizeWE;
            Invalidate();
            return;
        }
        _hoverResizeAreaId = null;
        var reorderTarget = TaskbarReorderTargetAt(logical);
        if (reorderTarget is not null)
        {
            _hoverMiniAreaId = null;
            _hoverReorderAreaId = reorderTarget.AreaId;
            _hoverPluginTarget = null;
            UpdateSystemUsageHover(false);
            UpdateRadarHover(null);
            UpdateCodexAccountsHover(null);
            UpdateQuotaHover(null);
            _hoverTarget = HoverTarget.MiniReorder;
            Cursor = Cursors.SizeAll;
            EnsureAnimationTimer();
            Invalidate();
            UpdateHintHover(HoverTarget.MiniReorder);
            UpdateCodexStrategyHover(false);
            return;
        }
        _hoverReorderAreaId = null;
        var systemUsageTarget = SystemUsageTargetAt(logical);
        var miniAreaTarget = TaskbarCollapseTargetAt(logical);
        var miniCollapseTarget = miniAreaTarget is not null;
        _hoverMiniAreaId = miniAreaTarget?.AreaId;
        var radarTarget = !systemUsageTarget && !miniCollapseTarget
            ? TaskbarRadarTargetAt(logical)
            : null;
        var codexAccountTarget = radarTarget is null && !systemUsageTarget && !miniCollapseTarget
            ? TaskbarCodexAccountTargetAt(logical)
            : null;
        var pluginTarget = radarTarget is null && codexAccountTarget is null
            && !systemUsageTarget && !miniCollapseTarget
            ? TaskbarPluginTargetAt(logical)
            : null;
        var pluginChanged = !string.Equals(
            _hoverPluginTarget?.Card.Card.Id,
            pluginTarget?.Card.Card.Id,
            StringComparison.Ordinal);
        _hoverPluginTarget = pluginTarget;
        if (pluginChanged && _hintPopover?.Visible == true) HideHintPopover();
        var quotaTarget = radarTarget is null && codexAccountTarget is null
            && pluginTarget is null && !miniCollapseTarget
            ? TaskbarQuotaTargetAt(logical)
            : null;
        if (systemUsageTarget || miniCollapseTarget) quotaTarget = null;
        UpdateSystemUsageHover(systemUsageTarget);
        UpdateRadarHover(radarTarget);
        UpdateCodexAccountsHover(codexAccountTarget);
        UpdateQuotaHover(quotaTarget);
        var next = _codexStrategyBounds.Contains(logical)
            ? HoverTarget.CodexStrategy
            : _refreshBounds.Contains(logical)
                ? HoverTarget.Refresh
            : miniCollapseTarget
                ? HoverTarget.MiniCollapse
            : _settingsBounds.Contains(logical)
                ? HoverTarget.Settings
                : codexAccountTarget is not null
                    ? HoverTarget.CodexAccounts
                    : quotaTarget is not null
                        ? HoverTarget.QuotaWindow
                        : pluginTarget is not null
                            ? HoverTarget.Plugin
                        : radarTarget is not null
                            ? HoverTarget.Radar
                            : systemUsageTarget
                                ? HoverTarget.SystemUsage
                                : HoverTarget.None;
        if (next != _hoverTarget)
        {
            _hoverTarget = next;
            Cursor = next != HoverTarget.None ? Cursors.Hand : Cursors.SizeAll;
            EnsureAnimationTimer();
            Invalidate();
        }
        else Cursor = next != HoverTarget.None ? Cursors.Hand : Cursors.SizeAll;
        UpdateHintHover(next);
        UpdateCodexStrategyHover(next == HoverTarget.CodexStrategy);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_taskbarDragging || _reorderingMiniAreaId is not null || _resizingMiniAreaId is not null) return;
        UpdateSystemUsageHover(false);
        UpdateRadarHover(null);
        UpdateCodexAccountsHover(null);
        UpdateQuotaHover(null);
        _hoverPluginTarget = null;
        _hoverMiniAreaId = null;
        _hoverReorderAreaId = null;
        _hoverResizeAreaId = null;
        UpdateHintHover(HoverTarget.None);
        UpdateCodexStrategyHover(false);
        _hoverTarget = HoverTarget.None;
        _pressedTarget = HoverTarget.None;
        Cursor = Cursors.Default;
        if (_glassDock)
        {
            _glassChipHovered = false;
            _glassVisibilityHovered = false;
            _glassChipTimer.Stop();
            if (_glassExpanded)
            {
                _glassLeftAt = DateTime.UtcNow;
                _glassCollapseTimer.Start();
            }
        }
        EnsureAnimationTimer();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            HidePopovers();
            if (e.Button == MouseButtons.Right && _glassDock) ShowGlassMenu(e.Location);
            return;
        }
        var logical = ToLogical(e.Location);
        if (TaskbarResizeTargetAt(logical) is { } resizeTarget)
        {
            BeginMiniAreaResize(resizeTarget, e.Location);
            return;
        }
        if (TaskbarReorderTargetAt(logical) is { } reorderTarget)
        {
            BeginMiniAreaReorder(reorderTarget, e.Location);
            return;
        }
        // The overflow and show/hide chips are buttons: pressing them never starts a bar drag
        // (which used to detach the bar from the taskbar and re-embed it on release).
        if (_glassDock && (_glassChipBounds.Contains(logical) || _glassVisibilityBounds.Contains(logical))) return;
        var pressedTarget = ControlTargetAt(logical);
        if (pressedTarget != HoverTarget.None)
        {
            _pressedTarget = pressedTarget;
            _pressedMiniAreaId = TaskbarCollapseTargetAt(logical)?.AreaId;
            Invalidate();
            return;
        }

        var now = DateTime.UtcNow;
        var doubleClick = now - _lastDragClickAt <= TimeSpan.FromMilliseconds(SystemInformation.DoubleClickTime)
            && Math.Abs(e.X - _lastDragClickPosition.X) <= SystemInformation.DoubleClickSize.Width
            && Math.Abs(e.Y - _lastDragClickPosition.Y) <= SystemInformation.DoubleClickSize.Height;
        _lastDragClickAt = now;
        _lastDragClickPosition = e.Location;
        if (doubleClick)
        {
            HidePopovers();
            _lastDragClickAt = DateTime.MinValue;
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _taskbarDragging = true;
        _taskbarDragMoved = false;
        // Detaching from the taskbar (to drag as a top-level overlay) waits until the pointer really
        // moves past the drag threshold, so a plain click never reparents the window.
        _taskbarDragStartLocation = ScreenLocation;
        _taskbarDragCurrentLocation = _taskbarDragStartLocation;
        _taskbarDragStartScreen = Cursor.Position;
        _dragTopologyKey = _placementCoordinator.ActiveTopology?.Key;
        _topologyChangedDuringDrag = false;
        _deferredTopologyDuringDrag = null;
        Capture = true;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (_resizingMiniAreaId is not null)
        {
            FinishMiniAreaResize(commit: true);
            return;
        }
        if (_reorderingMiniAreaId is not null)
        {
            FinishMiniAreaReorder(commit: true);
            return;
        }
        if (_pressedTarget != HoverTarget.None)
        {
            _pressedTarget = HoverTarget.None;
            _pressedMiniAreaId = null;
            Invalidate();
        }
        FinishTaskbarDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_pressedTarget != HoverTarget.None)
        {
            _pressedTarget = HoverTarget.None;
            _pressedMiniAreaId = null;
            Invalidate();
        }
        if (_resizingMiniAreaId is not null && !Capture)
        {
            FinishMiniAreaResize(commit: (Control.MouseButtons & MouseButtons.Left) == 0);
        }
        if (_reorderingMiniAreaId is not null && !Capture)
        {
            if ((Control.MouseButtons & MouseButtons.Left) != 0)
            {
                Capture = true;
                Cursor = Cursors.SizeAll;
            }
            else
            {
                FinishMiniAreaReorder(commit: true);
            }
        }
        if (_taskbarDragging && !Capture)
        {
            if ((Control.MouseButtons & MouseButtons.Left) != 0)
            {
                Capture = true;
                Cursor = Cursors.SizeAll;
            }
            else
            {
                FinishTaskbarDrag();
            }
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (DateTime.UtcNow <= _suppressClickUntil) return;
        var logical = ToLogical(e.Location);
        if (_glassDock && !_glassVisibilityBounds.IsEmpty && _glassVisibilityBounds.Contains(logical))
        {
            if (_dockVisibilityPopover?.Visible == true) HideDockVisibilityPopover();
            else ShowDockVisibilityPopover(QuotaTargetScreenBounds(_glassVisibilityBounds));
            return;
        }
        if (_glassDock && !_glassChipBounds.IsEmpty && _glassChipBounds.Contains(logical))
        {
            // The hover timer may already have opened the overflow under this very click; the
            // click then confirms it instead of snapping it shut again.
            var hoverOpenedJustNow = _glassExpanded && _glassHoverExpandedAt is { } at
                && DateTime.UtcNow - at < TimeSpan.FromMilliseconds(1200);
            if (!hoverOpenedJustNow) SetGlassExpanded(!_glassExpanded);
            _glassHoverExpandedAt = null;
            return;
        }
        if (TaskbarReorderTargetAt(logical) is not null) return;
        if (TaskbarCollapseTargetAt(logical) is { } miniArea)
        {
            ToggleMiniAreaCollapsed(miniArea.AreaId);
            return;
        }
        if (_codexStrategyBounds.Contains(logical))
        {
            ToggleCodexStrategyPopover();
            return;
        }
        if (SystemUsageTargetAt(logical))
        {
            TogglePinnedSystemUsagePopover();
            return;
        }
        if (TaskbarPluginTargetAt(logical) is { } pluginTarget)
        {
            if (RequestPluginPopover(pluginTarget.Card.PluginId, QuotaTargetScreenBounds(pluginTarget.Bounds), pinned: true, toggle: true)) return;
            HidePopovers();
            PluginActivated?.Invoke(this, pluginTarget.Card.PluginId);
            return;
        }
        var radarTarget = TaskbarRadarTargetAt(logical);
        if (radarTarget is not null)
        {
            TogglePinnedRadarPopover(radarTarget);
            return;
        }
        var quotaTarget = TaskbarQuotaTargetAt(logical);
        if (quotaTarget is not null)
        {
            TogglePinnedQuotaPopover(quotaTarget);
            return;
        }
        HidePopovers();
        if (_refreshBounds.Contains(logical)) RefreshRequested?.Invoke(this, EventArgs.Empty);
        else if (_settingsBounds.Contains(logical)) SettingsRequested?.Invoke(this, EventArgs.Empty);
        else if (_glassDock) OverviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BeginMiniAreaResize(MiniAreaTarget target, Point location)
    {
        HidePopovers();
        _resizingMiniAreaId = target.AreaId;
        _hoverResizeAreaId = target.AreaId;
        _resizeStartScreenX = PointToScreen(location).X;
        _resizeStartWidth = AreaLayout(target.AreaId).Width ?? AreaDefaultWidth(target.AreaId);
        _resizeStartLayout = AreaLayout(target.AreaId);
        _resizeMoved = false;
        Capture = true;
        Cursor = Cursors.SizeWE;
    }

    private void ContinueMiniAreaResize(MouseEventArgs e)
    {
        if (_resizingMiniAreaId is not { } areaId) return;
        if ((e.Button & MouseButtons.Left) == 0)
        {
            FinishMiniAreaResize(commit: true);
            return;
        }
        var delta = (int)Math.Round((PointToScreen(e.Location).X - _resizeStartScreenX) / _scale);
        var width = TaskbarMiniLayoutMath.NormalizeAreaContentWidth(_resizeStartWidth + delta, areaId);
        if (width == (AreaLayout(areaId).Width ?? AreaDefaultWidth(areaId))) return;
        _resizeMoved = true;
        ApplyMiniAreaLayout(
            areaId,
            collapsed: null,
            width,
            preserveAnchor: true,
            commitPlacement: false);
        Cursor = Cursors.SizeWE;
    }

    private void FinishMiniAreaResize(bool commit)
    {
        if (_resizingMiniAreaId is not { } areaId) return;
        var previous = _resizeStartLayout ?? AreaLayout(areaId);
        var moved = _resizeMoved;
        _resizingMiniAreaId = null;
        _hoverResizeAreaId = null;
        _resizeStartLayout = null;
        _resizeMoved = false;
        if (Capture) Capture = false;
        Cursor = Cursors.Default;
        if (!commit)
        {
            _miniAreaLayouts[areaId] = previous;
            ApplySnapshotLayout();
            Invalidate();
            return;
        }
        if (!moved || AreaLayout(areaId) == previous) return;
        if (!_renderOnly) PreserveMiniProviderAreaAnchor(ScreenLocation);
        _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(120);
        MiniAreaLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void BeginMiniAreaReorder(MiniAreaTarget target, Point location)
    {
        HidePopovers();
        _reorderingMiniAreaId = target.AreaId;
        _reorderBeforeAreaId = null;
        _reorderStartScreen = PointToScreen(location);
        _reorderMoved = false;
        _hoverReorderAreaId = target.AreaId;
        Capture = true;
        Cursor = Cursors.SizeAll;
        Invalidate();
    }

    private void ContinueMiniAreaReorder(MouseEventArgs e)
    {
        if (_reorderingMiniAreaId is not { } areaId) return;
        if ((e.Button & MouseButtons.Left) == 0)
        {
            FinishMiniAreaReorder(commit: true);
            return;
        }
        if (IsEscapeDown())
        {
            FinishMiniAreaReorder(commit: false);
            return;
        }

        var current = PointToScreen(e.Location);
        var deltaX = current.X - _reorderStartScreen.X;
        var deltaY = current.Y - _reorderStartScreen.Y;
        if (!_reorderMoved)
        {
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(deltaX) < Math.Max(1, dragSize.Width / 2)
                && Math.Abs(deltaY) < Math.Max(1, dragSize.Height / 2))
            {
                return;
            }
            _reorderMoved = true;
            _lastDragClickAt = DateTime.MinValue;
        }

        var nextBefore = ReorderBeforeAreaAt(ToLogical(e.Location), areaId);
        if (!string.Equals(_reorderBeforeAreaId, nextBefore, StringComparison.Ordinal))
        {
            _reorderBeforeAreaId = nextBefore;
            Invalidate();
        }
        Cursor = Cursors.SizeAll;
    }

    private string? ReorderBeforeAreaAt(PointF point, string sourceAreaId) =>
        _taskbarAreaBounds
            .Where(target => target.Reorderable
                && !string.Equals(target.AreaId, sourceAreaId, StringComparison.Ordinal))
            .OrderBy(target => target.Bounds.Left)
            .FirstOrDefault(target => point.X < target.Bounds.Left + target.Bounds.Width / 2)
            ?.AreaId;

    private void FinishMiniAreaReorder(bool commit)
    {
        if (_reorderingMiniAreaId is not { } areaId) return;
        if (commit && IsEscapeDown()) commit = false;
        var moved = _reorderMoved;
        var beforeAreaId = _reorderBeforeAreaId;
        _reorderingMiniAreaId = null;
        _reorderBeforeAreaId = null;
        _reorderMoved = false;
        _hoverReorderAreaId = null;
        if (Capture) Capture = false;
        Cursor = Cursors.Default;
        if (!commit || !moved)
        {
            Invalidate();
            return;
        }
        if (!ApplyMiniAreaOrderMove(areaId, beforeAreaId, preserveAnchor: true))
        {
            Invalidate();
            return;
        }
        _suppressClickUntil = DateTime.UtcNow.AddMilliseconds(180);
        MiniAreaOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    private MiniQuotaTarget? TaskbarQuotaTargetAt(PointF point) =>
        _taskbarWindowBounds.FirstOrDefault(target => target.Bounds.Contains(point));

    private MiniRadarTarget? TaskbarRadarTargetAt(PointF point) =>
        _taskbarRadarBounds.FirstOrDefault(target => target.Bounds.Contains(point));

    private (RectangleF Bounds, PluginMiniCardView Card)? TaskbarPluginTargetAt(PointF point)
    {
        foreach (var target in _taskbarPluginBounds)
        {
            if (target.Bounds.Contains(point)) return target;
        }
        return null;
    }

    private (RectangleF Bounds, QuotaCard Card)? TaskbarCodexAccountTargetAt(PointF point)
    {
        foreach (var target in _taskbarCodexAccountBounds)
        {
            if (target.Bounds.Contains(point)) return target;
        }

        return null;
    }

    private bool SystemUsageTargetAt(PointF point)
    {
        var isLive = _liveMetricsBounds.Contains(point);
        if (!isLive && !_systemUsageBounds.Contains(point)) return false;
        if (_popoverPinned && _systemUsagePopover?.Visible == true) return isLive == _systemAnchorIsLive;
        if (isLive != _systemAnchorIsLive)
        {
            HideSystemUsagePopover();
            _systemAnchorIsLive = isLive;
        }
        return true;
    }

    private HoverTarget ControlTargetAt(PointF point)
    {
        if (_codexStrategyBounds.Contains(point)) return HoverTarget.CodexStrategy;
        if (_refreshBounds.Contains(point)) return HoverTarget.Refresh;
        if (_settingsBounds.Contains(point)) return HoverTarget.Settings;
        if (TaskbarCollapseTargetAt(point) is not null) return HoverTarget.MiniCollapse;
        return HoverTarget.None;
    }

    private MiniAreaTarget? TaskbarCollapseTargetAt(PointF point) =>
        _taskbarAreaBounds.FirstOrDefault(target => target.HandleBounds.Contains(point));

    private MiniAreaTarget? TaskbarReorderTargetAt(PointF point) =>
        _taskbarAreaBounds.FirstOrDefault(target =>
            target.Reorderable && target.ReorderBounds.Contains(point));

    private MiniAreaTarget? TaskbarResizeTargetAt(PointF point) =>
        _taskbarAreaBounds.FirstOrDefault(target => target.ResizeBounds.Contains(point));

    private MiniAreaTarget? HoveredMiniArea() => _hoverMiniAreaId is null
        ? null
        : _taskbarAreaBounds.FirstOrDefault(target =>
            string.Equals(target.AreaId, _hoverMiniAreaId, StringComparison.Ordinal));

    private MiniAreaTarget? HoveredReorderArea() => _hoverReorderAreaId is null
        ? null
        : _taskbarAreaBounds.FirstOrDefault(target =>
            string.Equals(target.AreaId, _hoverReorderAreaId, StringComparison.Ordinal));
}
