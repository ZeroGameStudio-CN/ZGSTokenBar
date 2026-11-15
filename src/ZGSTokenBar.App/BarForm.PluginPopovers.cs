using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal interface IPluginPopoverContent
{
    bool InteractionOpen { get; }
    void SetPinned(bool pinned);
}

internal sealed class PluginPopoverRequest(string pluginId, Rectangle anchor, bool pinned, float scale,
    QuotaBackgroundTheme theme, bool animate = false, bool immediate = false) : EventArgs
{
    public string PluginId { get; } = pluginId;
    public Rectangle Anchor { get; } = anchor;
    public bool Pinned { get; } = pinned;
    public float Scale { get; } = scale;
    public QuotaBackgroundTheme Theme { get; } = theme;
    public bool Animate { get; } = animate;
    public bool Immediate { get; } = immediate;
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
        var previous = _pluginPopover;
        var switching = !pinned && !_popoverPinned && previous is MonitorPanelForm { Visible: true }
            && IsMonitorPlugin(pluginId) && _pluginPopoverId != pluginId;
        if (!switching && (_pluginPopover?.Visible != true || _pluginPopoverId != pluginId)) HidePopovers();
        // Keep the old full panel painted until the replacement has a native frame, then swap once.
        var request = new PluginPopoverRequest(pluginId, anchor, pinned, _scale, _backgroundTheme,
            _animationsEnabled, switching);
        using (PerfTrace.Measure($"plugin-popover.open {pluginId} pinned={pinned}"))
            PluginPopoverRequested?.Invoke(this, request);
        if (!pinned && request.Popup is null) ShowPluginHint(request);
        if (request.Popup is not { Visible: true } popup) return false;
        if (switching && previous is MonitorPanelForm old && !ReferenceEquals(old, popup))
        {
            popup.Update();
            old.HideImmediately();
        }
        if (popup is MonitorPanelForm panel) panel.ResumeHover();
        _pluginPopover = popup; _pluginPopoverId = pluginId; _pluginPopoverAnchor = anchor;
        _popoverPinned = pinned; _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverHoverTimer.Stop(); _popoverStateTimer.Start();
        return true;
    }

    private void ShowPluginHint(PluginPopoverRequest request)
    {
        var card = _pluginMiniCards.FirstOrDefault(item => item.PluginId == request.PluginId);
        if (card is null) return;
        _hintPopover ??= new TaskbarHintPopoverForm();
        _hintPopover.ApplyTheme(request.Theme);
        _hintPopover.ShowFor(this, request.Anchor, card.Title, PluginHint(card), request.Scale, _animationsEnabled);
        request.Popup = _hintPopover;
    }

    private void RefreshPluginHintContent()
    {
        if (_pluginPopover is not TaskbarHintPopoverForm { Visible: true } hint || _pluginPopoverId is null)
            return;
        var card = _pluginMiniCards.FirstOrDefault(item => item.PluginId == _pluginPopoverId);
        if (card is not null) hint.UpdateContent(card.Title, PluginHint(card));
    }

    internal string? VisiblePluginPopoverId => _pluginPopover?.Visible == true ? _pluginPopoverId : null;

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
        if (!_pluginPopover.IsDisposed)
        {
            if (_pluginPopover is TaskbarHintPopoverForm hint) hint.HideImmediately();
            else if (_pluginPopover is MonitorPanelForm panel) panel.HideImmediately();
            else _pluginPopover.Hide();
        }
        _pluginPopover = null;
        _pluginPopoverId = null;
        _popoverPinned = false;
        _popoverLeaveStarted = null;
        if (_quotaPopover?.Visible != true && _systemUsagePopover?.Visible != true)
            _popoverStateTimer.Stop();
    }

    internal void HidePluginPopover(string pluginId)
    {
        if (_pluginPopoverId == pluginId) HidePluginPopover();
    }

    internal void MonitorPluginPopover(Point cursor, bool mouseDown, bool escapeDown, DateTime now)
    {
        if (_pluginPopover is not { Visible: true } popup) return;
        var inside = popup is MonitorPanelForm monitor ? monitor.ContainsHoverPoint(cursor)
            : _pluginPopoverAnchor.Contains(cursor) || popup.Bounds.Contains(cursor);
        var newClick = mouseDown && !_popoverMouseWasDown;
        var newEscape = escapeDown && !_popoverEscapeWasDown;
        _popoverMouseWasDown = mouseDown; _popoverEscapeWasDown = escapeDown;
        if (popup is IPluginPopoverContent { InteractionOpen: true })
        {
            _popoverLeaveStarted = null;
            if (popup is MonitorPanelForm interactive) interactive.ResumeHover();
            return;
        }
        if (!_popoverPinned)
        {
            if (inside)
            {
                _popoverLeaveStarted = null;
                if (popup is MonitorPanelForm panel) panel.ResumeHover();
            }
            else _popoverLeaveStarted ??= now;
        }
        if (ShouldDismissPopover(_popoverPinned, inside, newClick, newEscape,
            _popoverLeaveStarted is { } left ? now - left : null))
        {
            if (!_popoverPinned && !newEscape && popup is MonitorPanelForm panel) panel.DismissHover();
            else HidePluginPopover();
        }
    }

    private static bool IsMonitorPlugin(string? id) => id is MiniAreaIds.Router or MiniAreaIds.Intake;

    internal static bool ShouldDismissPopover(bool pinned, bool inside, bool newClick, bool newEscape, TimeSpan? outsideFor) =>
        newEscape || (pinned ? newClick && !inside : !inside && outsideFor >= TimeSpan.FromMilliseconds(150));
}
