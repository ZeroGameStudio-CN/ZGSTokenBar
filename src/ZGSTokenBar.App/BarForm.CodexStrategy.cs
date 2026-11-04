using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed partial class BarForm
{
    private const float CodexStrategyButtonWidth = 27;
    private CodexUsageStrategy? _codexStrategy;
    private CodexStrategyPopoverForm? _codexStrategyPopover;
    private RectangleF _codexStrategyBounds;
    internal RectangleF CodexStrategyBoundsForAcceptance => _renderOnly
        ? _codexStrategyBounds
        : throw new InvalidOperationException("Strategy hit testing is render-only.");

    private CodexUsageStrategy CodexStrategy => _codexStrategy ??=
        CodexUsageStrategy.Create(_codexAccounts, _snapshot.CodexAccounts, DateTimeOffset.UtcNow);

    private RectangleF CodexQuotaBounds(string areaId, RectangleF bounds, bool collapsed) =>
        !collapsed && areaId == MiniAreaIds.Codex && CodexStrategy.Accounts.Count > 0
            ? RectangleF.FromLTRB(bounds.Left, bounds.Top, bounds.Right - CodexStrategyButtonWidth, bounds.Bottom)
            : bounds;

    private void DrawCodexStrategyButton(Graphics graphics, RectangleF bounds)
    {
        _codexStrategyBounds = bounds;
        var count = CodexStrategy.Urgent.Count;
        var color = count > 0 ? Color.FromArgb(251, 191, 108) : Color.FromArgb(148, 163, 184);
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
        if (_codexStrategyPopover?.Visible == true) ShowCodexStrategyPopover();
    }

    private void ToggleCodexStrategyPopover()
    {
        if (_codexStrategyPopover?.Visible == true)
        {
            HideCodexStrategyPopover();
            return;
        }

        HidePopovers();
        _codexStrategy = null;
        ShowCodexStrategyPopover();
        _popoverPinned = true;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverStateTimer.Start();
    }

    private void ShowCodexStrategyPopover()
    {
        if (_codexStrategyBounds.IsEmpty) return;
        _codexStrategyPopover ??= new CodexStrategyPopoverForm();
        _codexStrategyPopover.ShowFor(this, QuotaTargetScreenBounds(_codexStrategyBounds),
            CodexStrategy, _text, _backgroundTheme, _scale);
    }

    private void MonitorCodexStrategyPopover()
    {
        var cursor = Cursor.Position;
        var mouseDown = Control.MouseButtons != MouseButtons.None;
        var escapeDown = IsEscapeDown();
        var outsideClick = mouseDown && !_popoverMouseWasDown
            && !QuotaTargetScreenBounds(_codexStrategyBounds).Contains(cursor)
            && !_codexStrategyPopover!.Bounds.Contains(cursor);
        var escapePressed = escapeDown && !_popoverEscapeWasDown;
        _popoverMouseWasDown = mouseDown;
        _popoverEscapeWasDown = escapeDown;
        if (_codexStrategyBounds.IsEmpty || outsideClick || escapePressed) HideCodexStrategyPopover();
    }

    private void HideCodexStrategyPopover()
    {
        if (_codexStrategyPopover?.Visible != true) return;
        _codexStrategyPopover.Hide();
        _popoverPinned = false;
        _popoverStateTimer.Stop();
        Invalidate();
    }
}
