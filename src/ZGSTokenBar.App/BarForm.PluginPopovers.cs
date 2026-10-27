using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal interface IPluginPopoverContent
{
    bool InteractionOpen { get; }
    void SetPinned(bool pinned);
}

internal sealed class PluginPopoverRequest(string pluginId, Rectangle anchor, bool pinned, float scale, QuotaBackgroundTheme theme) : EventArgs
{
    public string PluginId { get; } = pluginId;
    public Rectangle Anchor { get; } = anchor;
    public bool Pinned { get; } = pinned;
    public float Scale { get; } = scale;
    public QuotaBackgroundTheme Theme { get; } = theme;
    public Form? Popup { get; set; }
}

internal sealed partial class BarForm
{
    private Form? _pluginPopover;
    private string? _pluginPopoverId;
    private Rectangle _pluginPopoverAnchor;
    public event EventHandler<PluginPopoverRequest>? PluginPopoverRequested;

    internal bool OpenPluginPopover(string pluginId, bool pinned = true)
    {
        var target = _taskbarPluginBounds.FirstOrDefault(item => item.Card.PluginId == pluginId);
        var anchor = target.Card is null ? Bounds : QuotaTargetScreenBounds(target.Bounds);
        return RequestPluginPopover(pluginId, anchor, pinned);
    }

    private bool RequestPluginPopover(string pluginId, Rectangle anchor, bool pinned, bool toggle = false)
    {
        if (toggle && pinned && _popoverPinned && _pluginPopover?.Visible == true && _pluginPopoverId == pluginId)
        { HidePluginPopover(); return true; }
        if (!pinned && _popoverPinned) return true;
        if (_pluginPopover?.Visible != true || _pluginPopoverId != pluginId) HidePopovers();
        var request = new PluginPopoverRequest(pluginId, anchor, pinned, _scale, _backgroundTheme);
        PluginPopoverRequested?.Invoke(this, request);
        if (request.Popup is not { Visible: true } popup) return false;
        _pluginPopover = popup; _pluginPopoverId = pluginId; _pluginPopoverAnchor = anchor;
        _popoverPinned = pinned; _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverHoverTimer.Stop(); _popoverStateTimer.Start();
        return true;
    }

    internal void PinPluginPopover(bool pinned)
    {
        if (_pluginPopover is not { Visible: true }) return;
        _popoverPinned = pinned;
        if (_pluginPopover is IPluginPopoverContent content) content.SetPinned(pinned);
        _popoverLeaveStarted = null;
    }

    internal void HidePluginPopover()
    {
        if (_pluginPopover is null) return;
        if (!_pluginPopover.IsDisposed) _pluginPopover.Hide(); _pluginPopoverId = null; _popoverPinned = false;
        _popoverLeaveStarted = null;
    }

    private void MonitorPluginPopover()
    {
        if (_pluginPopover is not { Visible: true } popup) return;
        if (popup is IPluginPopoverContent { InteractionOpen: true }) return;
        var cursor = Cursor.Position;
        var inside = _pluginPopoverAnchor.Contains(cursor) || popup.Bounds.Contains(cursor);
        var mouseDown = Control.MouseButtons != MouseButtons.None;
        var escapeDown = IsEscapeDown();
        var newClick = mouseDown && !_popoverMouseWasDown;
        var newEscape = escapeDown && !_popoverEscapeWasDown;
        _popoverMouseWasDown = mouseDown; _popoverEscapeWasDown = escapeDown;
        if (!_popoverPinned)
        {
            if (inside) _popoverLeaveStarted = null;
            else _popoverLeaveStarted ??= DateTime.UtcNow;
        }
        if (ShouldDismissPluginPopover(_popoverPinned, inside, newClick, newEscape,
            _popoverLeaveStarted is { } left ? DateTime.UtcNow - left : null)) HidePluginPopover();
    }

    internal static bool ShouldDismissPluginPopover(bool pinned, bool inside, bool newClick, bool newEscape, TimeSpan? outsideFor) =>
        pinned ? newEscape || newClick && !inside : !inside && outsideFor >= TimeSpan.FromMilliseconds(150);
}
