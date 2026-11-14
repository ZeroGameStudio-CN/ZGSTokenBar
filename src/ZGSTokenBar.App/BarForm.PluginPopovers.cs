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
    private IntakeHoverCardForm? _intakeHover;
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
        if (pinned && pluginId == MiniAreaIds.Intake) return false; // The click opens its separate window.
        if (_pluginPopover?.Visible != true || _pluginPopoverId != pluginId) HidePopovers();
        var request = new PluginPopoverRequest(pluginId, anchor, pinned, _scale, _backgroundTheme);
        if (!pinned && pluginId == MiniAreaIds.Intake)
        {
            _intakeHover ??= new IntakeHoverCardForm();
            _intakeHover.ShowFor(this, anchor, _text, _scale);
            request.Popup = _intakeHover;
        }
        else
        {
            using (PerfTrace.Measure($"plugin-popover.open {pluginId} pinned={pinned}"))
                PluginPopoverRequested?.Invoke(this, request);
            if (!pinned && request.Popup is null) ShowPluginHint(request);
        }
        if (request.Popup is not { Visible: true } popup) return false;
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
        if (popup is IPluginPopoverContent { InteractionOpen: true }) return;
        var inside = _pluginPopoverAnchor.Contains(cursor) || popup.Bounds.Contains(cursor);
        var newClick = mouseDown && !_popoverMouseWasDown;
        var newEscape = escapeDown && !_popoverEscapeWasDown;
        _popoverMouseWasDown = mouseDown; _popoverEscapeWasDown = escapeDown;
        if (!_popoverPinned)
        {
            if (inside) _popoverLeaveStarted = null;
            else _popoverLeaveStarted ??= now;
        }
        if (ShouldDismissPopover(_popoverPinned, inside, newClick, newEscape,
            _popoverLeaveStarted is { } left ? now - left : null)) HidePluginPopover();
    }

    internal static bool ShouldDismissPopover(bool pinned, bool inside, bool newClick, bool newEscape, TimeSpan? outsideFor) =>
        pinned ? newEscape || newClick && !inside : !inside && outsideFor >= TimeSpan.FromMilliseconds(150);
}
