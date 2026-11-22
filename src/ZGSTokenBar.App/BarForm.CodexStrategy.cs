using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed partial class BarForm
{
    private CodexUsageStrategy? _codexStrategy;
    private CodexStrategyPopoverForm? _codexStrategyPopover;
    private Rectangle _codexStrategyAnchor;

    private CodexUsageStrategy CodexStrategy => _codexStrategy ??=
        CodexUsageStrategy.Create(
            _codexAccounts,
            _snapshot.CodexAccounts,
            _utcNow(),
            _quotaPaceEstimates,
            _codexQuotaTokenSummaries);

    internal bool HasCodexStrategy =>
        CodexStrategy.Accounts.Count > 0 || CodexStrategy.Renewals.Count > 0;

    internal void ShowCodexStrategyPopover(bool pinned = true, Rectangle? anchorScreen = null)
    {
        if (!HasCodexStrategy) return;
        if (_codexStrategyPopover?.IsOpen != true) HidePopovers();
        _popoverHoverTimer.Stop();
        var anchor = ResolveCodexStrategyAnchor(anchorScreen);
        if (anchor.IsEmpty) return;

        _codexStrategyAnchor = anchor;
        _codexStrategy = null;
        _codexStrategyPopover ??= new CodexStrategyPopoverForm();
        _codexStrategyPopover.ShowFor(
            this,
            anchor,
            CodexStrategy,
            _text,
            _backgroundTheme,
            _scale,
            _animationsEnabled);
        if (_codexStrategyPopover?.IsOpen != true) return;

        _popoverPinned = pinned;
        _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverStateTimer.Start();
        Invalidate();
    }

    private Rectangle ResolveCodexStrategyAnchor(Rectangle? explicitAnchor)
    {
        if (explicitAnchor is { IsEmpty: false } provided) return provided;
        var codexRing = _cardBounds.FirstOrDefault(item => item.Card.Provider == ProviderKind.Codex);
        if (!codexRing.Bounds.IsEmpty) return QuotaTargetScreenBounds(codexRing.Bounds);
        return IsHandleCreated ? RectangleToScreen(ClientRectangle) : Rectangle.Empty;
    }

    private void RefreshCodexStrategy()
    {
        _codexStrategy = null;
        if (_codexStrategyPopover?.IsOpen == true)
        {
            ShowCodexStrategyPopover(_popoverPinned, _codexStrategyAnchor);
        }
    }

    private void MonitorCodexStrategyPopover(Point cursor, bool mouseDown, bool escapeDown, DateTime now)
    {
        RefreshCodexStrategyClock();
        if (_codexStrategyPopover?.IsOpen != true) return;
        var anchor = !_codexStrategyAnchor.IsEmpty
            ? _codexStrategyAnchor
            : ResolveCodexStrategyAnchor(null);
        var inside = (!anchor.IsEmpty && anchor.Contains(cursor))
            || _codexStrategyPopover.ContainsScreenPoint(cursor);
        var newClick = mouseDown && !_popoverMouseWasDown;
        var newEscape = escapeDown && !_popoverEscapeWasDown;
        _popoverMouseWasDown = mouseDown;
        _popoverEscapeWasDown = escapeDown;
        if (inside) _popoverLeaveStarted = null;
        else _popoverLeaveStarted ??= now;
        if (ShouldDismissPopover(_popoverPinned, inside, newClick, newEscape,
                _popoverLeaveStarted is { } left ? now - left : null))
        {
            HideCodexStrategyPopover();
        }
    }

    internal void RefreshCodexStrategyClock()
    {
        var now = _utcNow();
        var observed = CodexStrategy.EvaluatedAt;
        if (now >= observed && now - observed < TimeSpan.FromSeconds(1)) return;
        RefreshCodexStrategy();
    }

    private void HideCodexStrategyPopover()
    {
        if (_codexStrategyPopover?.IsOpen == true)
        {
            _popoverPinned = false;
            _popoverStateTimer.Stop();
            _popoverLeaveStarted = null;
        }
        _codexStrategyPopover?.HidePopover();
        Invalidate();
    }
}
