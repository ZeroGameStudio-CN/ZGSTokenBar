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
        if (_taskbarDragging)
        {
            ContinueTaskbarDrag(e);
            return;
        }

        var logical = ToLogical(e.Location);
        {
            var onChip = !_glassChipBounds.IsEmpty && _glassChipBounds.Contains(logical);
            if (onChip != _glassChipHovered)
            {
                _glassChipHovered = onChip;
                Invalidate();
            }
            if (onChip)
            {
                UpdateSystemUsageHover(false);
                UpdateCodexAccountsHover(null);
                UpdateQuotaHover(null);
                _hoverPluginTarget = null;
                UpdateHintHover(HoverTarget.None);
                _hoverTarget = HoverTarget.None;
                Cursor = Cursors.Hand;
                return;
            }
        }

        var systemUsageTarget = SystemUsageTargetAt(logical);
        var codexAccountTarget = !systemUsageTarget
            ? TaskbarCodexAccountTargetAt(logical)
            : null;
        var pluginTarget = codexAccountTarget is null
            && !systemUsageTarget
            ? TaskbarPluginTargetAt(logical)
            : null;
        var pluginChanged = !string.Equals(
            _hoverPluginTarget?.Card.Card.Id,
            pluginTarget?.Card.Card.Id,
            StringComparison.Ordinal);
        _hoverPluginTarget = pluginTarget;
        if (pluginChanged && _hintPopover?.Visible == true) HideHintPopover();
        var quotaTarget = codexAccountTarget is null
            && pluginTarget is null && !systemUsageTarget
            ? TaskbarQuotaTargetAt(logical)
            : null;
        UpdateSystemUsageHover(systemUsageTarget);
        UpdateCodexAccountsHover(codexAccountTarget);
        UpdateQuotaHover(quotaTarget);
        var next = codexAccountTarget is not null
                    ? HoverTarget.CodexAccounts
                    : quotaTarget is not null
                        ? HoverTarget.QuotaWindow
                        : pluginTarget is not null
                            ? HoverTarget.Plugin
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
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_taskbarDragging) return;
        UpdateSystemUsageHover(false);
        UpdateCodexAccountsHover(null);
        UpdateQuotaHover(null);
        _hoverPluginTarget = null;
        UpdateHintHover(HoverTarget.None);
        _hoverTarget = HoverTarget.None;
        Cursor = Cursors.Default;
        _glassChipHovered = false;
        EnsureAnimationTimer();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            HidePopovers();
            if (e.Button == MouseButtons.Right) ShowGlassMenu(e.Location);
            return;
        }
        var logical = ToLogical(e.Location);
        // The overflow chip is a button: pressing it never starts a bar drag.
        if (_glassChipBounds.Contains(logical)) return;

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
        FinishTaskbarDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
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
        if (_taskbarDragMoved || DateTime.UtcNow <= _suppressClickUntil) return;
        var logical = ToLogical(e.Location);
        if (!_glassChipBounds.IsEmpty && _glassChipBounds.Contains(logical))
        {
            SetGlassExpanded(!_glassExpanded, notify: true);
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
        var quotaTarget = TaskbarQuotaTargetAt(logical);
        if (quotaTarget is not null)
        {
            TogglePinnedQuotaPopover(quotaTarget);
            return;
        }
        HidePopovers();
    }

    private MiniQuotaTarget? TaskbarQuotaTargetAt(PointF point) =>
        _taskbarWindowBounds.FirstOrDefault(target => target.Bounds.Contains(point));

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
}
