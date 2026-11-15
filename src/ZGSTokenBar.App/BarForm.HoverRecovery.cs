namespace ZGSTokenBar.App;

internal sealed partial class BarForm
{
    private bool _hoverRecoveryQueued;
    private string? _dismissedHoverPlugin;

    private void QueueHoverRecovery()
    {
        if (_renderOnly || _hoverRecoveryQueued || !IsHandleCreated || IsDisposed) return;
        _hoverRecoveryQueued = true;
        try
        {
            BeginInvoke(() =>
            {
                _hoverRecoveryQueued = false;
                if (!IsDisposed && IsHandleCreated) RecoverStationaryHover(Cursor.Position);
            });
        }
        catch (InvalidOperationException) { _hoverRecoveryQueued = false; }
    }

    // A data/layout refresh can clear a pending hover without any new mouse-move message.
    internal void RecoverStationaryHover(Point cursor)
    {
        if (!IsHandleCreated || IsDisposed || _hiddenToTray || _taskbarDragging) return;
        if (_pluginPopover is { Visible: false } && _pluginPopoverId is not null) HidePluginPopover();
        if (_popoverPinned || DateTime.UtcNow < _suppressClickUntil) return;
        var client = PointToClient(cursor);
        if (!ClientRectangle.Contains(client)) { _dismissedHoverPlugin = null; return; }
        var logical = ToLogical(client);
        if (TaskbarPluginTargetAt(logical) is { } target && IsMonitorPlugin(target.Card.PluginId)) TrackMiniHover(logical);
    }
}
