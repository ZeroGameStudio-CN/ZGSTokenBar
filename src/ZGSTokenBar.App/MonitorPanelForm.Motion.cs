namespace ZGSTokenBar.App;

internal abstract partial class MonitorPanelForm
{
    private readonly System.Windows.Forms.Timer _hoverMotionTimer = new() { Interval = 16 };
    private bool _animateHover, _hoverExiting;
    private long _hoverMotionStarted;
    private double _hoverMotionFrom, _hoverMotionDuration;
    private Rectangle _hoverAnchor;

    internal bool HoverDismissing => _hoverExiting && _hoverMotionTimer.Enabled;
    internal bool ContainsHoverPoint(Point cursor) => TaskbarPopoverMath.ContainsHoverPoint(
        _hoverAnchor, Bounds, cursor, Math.Max(1, (int)Math.Round(6 * _popoverScale)));

    internal void DismissHover()
    {
        if (!Visible || HoverDismissing || _pinned) return;
        if (!_animateHover) { HideImmediately(); return; }
        StartHoverMotion(exiting: true);
    }

    internal void ResumeHover()
    {
        if (Visible && HoverDismissing) StartHoverMotion(exiting: false);
    }

    internal void HideImmediately()
    {
        var animated = _animateHover || _hoverMotionTimer.Enabled;
        _hoverMotionTimer.Stop();
        _hoverExiting = false;
        if (Visible) Hide();
        if (animated) Opacity = 1;
    }

    private void FinishHoverMotion()
    {
        var transitioning = _hoverMotionTimer.Enabled;
        _hoverMotionTimer.Stop();
        _hoverExiting = false;
        if (transitioning) Opacity = 1;
    }

    private void StartHoverMotion(bool exiting)
    {
        _hoverMotionFrom = Opacity;
        _hoverExiting = exiting;
        _hoverMotionStarted = Environment.TickCount64;
        _hoverMotionDuration = Math.Max(16, (exiting ? 90 : 120) * (exiting ? Opacity : 1 - Opacity));
        _hoverMotionTimer.Start();
    }

    private void AdvanceHoverMotion()
    {
        if (IsDisposed) return;
        if (!Visible) { _hoverMotionTimer.Stop(); return; }
        // Re-entering during the exit reverses the same native window from its current opacity.
        if (_hoverExiting && (InteractionOpen || ContainsHoverPoint(Cursor.Position))) { ResumeHover(); return; }
        var progress = Math.Clamp((Environment.TickCount64 - _hoverMotionStarted) / _hoverMotionDuration, 0, 1);
        Opacity = _hoverExiting
            ? TaskbarPopoverMath.FadeOut(_hoverMotionFrom, TaskbarPopoverMath.ExitEase(progress))
            : TaskbarPopoverMath.FadeIn(_hoverMotionFrom, TaskbarPopoverMath.EntranceEase(progress));
        if (progress < 1) return;
        if (_hoverExiting) HideImmediately();
        else FinishHoverMotion();
    }
}
