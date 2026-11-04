using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed partial class BarForm
{
    private const int CodexStrategyButtonWidth = 27;
    private CodexUsageStrategy? _codexStrategy;
    private CodexStrategyPopoverForm? _codexStrategyPopover;
    private RectangleF _codexStrategyBounds;
    private bool _hoverCodexStrategy;
    internal RectangleF CodexStrategyBoundsForAcceptance => _renderOnly
        ? _codexStrategyBounds
        : throw new InvalidOperationException("Strategy hit testing is render-only.");

    private CodexUsageStrategy CodexStrategy => _codexStrategy ??=
        CodexUsageStrategy.Create(_codexAccounts, _snapshot.CodexAccounts, _utcNow(), _quotaPaceEstimates);

    private int CodexStrategyExtraWidth(string areaId, bool collapsed) =>
        !collapsed && areaId == MiniAreaIds.Codex && CodexStrategy.Accounts.Count > 0
            ? CodexStrategyButtonWidth
            : 0;

    private RectangleF CodexQuotaBounds(string areaId, RectangleF bounds, bool collapsed) =>
        RectangleF.FromLTRB(bounds.Left, bounds.Top,
            bounds.Right - CodexStrategyExtraWidth(areaId, collapsed), bounds.Bottom);

    private RectangleF ResolveCodexStrategyBounds()
    {
        var x = (float)TaskbarMiniLayoutMath.OuterPadding;
        foreach (var area in _visibleTaskbarAreas)
        {
            var width = TaskbarAreaWidth(area);
            var extra = CodexStrategyExtraWidth(area.AreaId, AreaLayout(area.AreaId).Collapsed);
            if (extra > 0)
            {
                var content = TaskbarAreaContentBounds(new RectangleF(x, 4, width, 36));
                return RectangleF.FromLTRB(content.Right - extra, content.Top, content.Right, content.Bottom);
            }
            x += width + TaskbarMiniLayoutMath.ModuleGap;
        }
        return RectangleF.Empty;
    }

    private void DrawCodexStrategyButton(Graphics graphics, RectangleF bounds)
    {
        _codexStrategyBounds = bounds;
        var count = CodexStrategy.Urgent.Count;
        var color = count > 0 ? Color.FromArgb(251, 191, 108) : Color.FromArgb(148, 163, 184);
        var highlight = _hoverCodexStrategy || _codexStrategyPopover?.IsOpen == true
            ? 1f : HoverProgress(HoverTarget.CodexStrategy);
        if (highlight > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb((int)(34 * highlight), color));
            graphics.FillRoundedRectangle(fill, RectangleF.Inflate(bounds, -2, -3), 4);
        }
        bounds = PressedIconBounds(bounds, HoverTarget.CodexStrategy);
        var centerX = bounds.Left + (count > 0 ? 8 : bounds.Width / 2);
        var centerY = bounds.Top + bounds.Height / 2;
        using var pen = new Pen(color, 1);
        graphics.DrawEllipse(pen, centerX - 4, centerY - 4, 8, 8);
        graphics.DrawLine(pen, centerX, centerY - 2.5f, centerX, centerY);
        graphics.DrawLine(pen, centerX, centerY, centerX + 2, centerY + 1);
        if (count == 0) return;
        using var brush = new SolidBrush(color);
        DrawString(graphics, count < 10 ? count.ToString() : "9+", _accountOrdinalFont, brush,
            new RectangleF(bounds.Left + 12, bounds.Top, bounds.Width - 13, bounds.Height), StringAlignment.Center);
    }

    private void RefreshCodexStrategy()
    {
        _codexStrategy = null;
        if (_codexStrategyPopover?.IsOpen == true) UpdateCodexStrategyContent();
    }

    private void UpdateCodexStrategyHover(bool hovered)
    {
        if (_popoverPinned) return;
        if (_codexStrategyPopover?.IsOpen == true)
        {
            _hoverCodexStrategy = hovered;
            if (hovered)
            {
                _popoverLeaveStarted = null;
                _popoverHoverTimer.Stop();
            }
            else _popoverLeaveStarted ??= DateTime.UtcNow;
            _popoverStateTimer.Start();
            return;
        }
        if (_hoverCodexStrategy == hovered)
        {
            if (hovered) _popoverLeaveStarted = null;
            return;
        }

        _popoverHoverTimer.Stop();
        _hoverCodexStrategy = hovered;
        if (!hovered) return;
        HidePopovers();
        _hoverCodexStrategy = true;
        _popoverHoverTimer.Start();
        Invalidate();
    }

    private void ToggleCodexStrategyPopover()
    {
        if (_popoverPinned && _codexStrategyPopover?.IsOpen == true)
        {
            HideCodexStrategyPopover();
            return;
        }

        ShowCodexStrategyPopover(pinned: true);
    }

    private void ShowCodexStrategyPopover(bool pinned)
    {
        if (_codexStrategyPopover?.IsOpen != true) HidePopovers();
        _popoverHoverTimer.Stop();
        _codexStrategy = null;
        UpdateCodexStrategyContent();
        if (_codexStrategyPopover?.IsOpen != true) return;
        _hoverCodexStrategy = true;
        _popoverPinned = pinned;
        _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverStateTimer.Start();
        Invalidate();
    }

    private void UpdateCodexStrategyContent()
    {
        var anchor = ResolveCodexStrategyBounds();
        if (anchor.IsEmpty)
        {
            HideCodexStrategyPopover();
            return;
        }
        _codexStrategyPopover ??= new CodexStrategyPopoverForm();
        _codexStrategyPopover.ShowFor(this, QuotaTargetScreenBounds(anchor),
            CodexStrategy, _text, _backgroundTheme, _scale, _animationsEnabled);
    }

    private void MonitorCodexStrategyPopover()
    {
        RefreshCodexStrategyClock();
        if (_codexStrategyPopover?.IsOpen != true) return;
        var cursor = Cursor.Position;
        var mouseDown = Control.MouseButtons != MouseButtons.None;
        var escapeDown = IsEscapeDown();
        var anchor = ResolveCodexStrategyBounds();
        var inside = !anchor.IsEmpty && (QuotaTargetScreenBounds(anchor).Contains(cursor)
            || _codexStrategyPopover!.ContainsScreenPoint(cursor));
        var newClick = mouseDown && !_popoverMouseWasDown;
        var escapePressed = escapeDown && !_popoverEscapeWasDown;
        _popoverMouseWasDown = mouseDown;
        _popoverEscapeWasDown = escapeDown;
        if (inside) _popoverLeaveStarted = null;
        else _popoverLeaveStarted ??= DateTime.UtcNow;
        if (anchor.IsEmpty || ShouldDismissPopover(_popoverPinned, inside, newClick, escapePressed,
                _popoverLeaveStarted is { } left ? DateTime.UtcNow - left : null))
            HideCodexStrategyPopover();
    }

    internal void RefreshCodexStrategyClock()
    {
        var now = _utcNow();
        var observed = CodexStrategy.EvaluatedAt;
        if (now >= observed && now - observed < TimeSpan.FromSeconds(1)) return;
        RefreshCodexStrategy();
        Invalidate();
    }

    private void HideCodexStrategyPopover()
    {
        if (_codexStrategyPopover?.IsOpen == true)
        {
            _popoverPinned = false;
            _popoverStateTimer.Stop();
            _popoverLeaveStarted = null;
        }
        if (_hoverCodexStrategy) _popoverHoverTimer.Stop();
        _hoverCodexStrategy = false;
        _codexStrategyPopover?.HidePopover();
        Invalidate();
    }
}
