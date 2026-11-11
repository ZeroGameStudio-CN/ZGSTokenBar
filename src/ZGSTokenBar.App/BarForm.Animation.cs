using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Animations: snapshot value tweening, pulse, hover progress.</summary>
internal sealed partial class BarForm
{
    private void StartSnapshotAnimation(QuotaSnapshot previous, QuotaSnapshot next)
    {
        if (!_animationsEnabled)
        {
            _snapshotAnimating = false;
            _animatedWindowKeys.Clear();
            return;
        }

        var from = SnapshotValues(previous);
        var to = SnapshotValues(next);
        var changedKeys = to
            .Where(item => from.TryGetValue(item.Key, out var value)
                && Math.Abs(value - item.Value) >= .05)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (changedKeys.Count == 0)
        {
            _snapshotAnimating = false;
            _animatedWindowKeys.Clear();
            if (!_refreshing) _animationTimer.Stop();
            return;
        }

        _animationFrom = from;
        _animatedWindowKeys = changedKeys;
        _snapshotAnimationStarted = DateTime.UtcNow;
        _snapshotAnimating = true;
        EnsureAnimationTimer();
    }

    private double? AnimatedUsed(string cardKey, QuotaWindow window)
    {
        if (window.UsedPercent is not { } target || !_snapshotAnimating) return window.UsedPercent;
        if (!_animationFrom.TryGetValue(WindowKey(cardKey, window.Label), out var from)) return target;
        var progress = Math.Clamp(
            (DateTime.UtcNow - _snapshotAnimationStarted).TotalMilliseconds / SnapshotValueAnimationMs,
            0,
            1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        return from + (target - from) * eased;
    }

    private float SnapshotPulse(string windowKey)
    {
        if (!_snapshotAnimating || !_animatedWindowKeys.Contains(windowKey)) return 0;
        var progress = Math.Clamp(
            (DateTime.UtcNow - _snapshotAnimationStarted).TotalMilliseconds / SnapshotPulseAnimationMs,
            0,
            1);
        return (float)Math.Sin(progress * Math.PI);
    }

    private void AdvanceAnimation()
    {
        if (_refreshing) _refreshRotation = (_refreshRotation + 10) % 360;
        if (_snapshotAnimating && DateTime.UtcNow - _snapshotAnimationStarted >= TimeSpan.FromMilliseconds(SnapshotPulseAnimationMs))
        {
            _snapshotAnimating = false;
            _animatedWindowKeys.Clear();
        }

        var hoverAnimating = AdvanceHoverAnimation();

        Invalidate();
        if (!_refreshing && !_snapshotAnimating && !hoverAnimating) _animationTimer.Stop();
    }

    private void EnsureAnimationTimer()
    {
        if (_animationsEnabled && !_animationTimer.Enabled) _animationTimer.Start();
    }

    private bool AdvanceHoverAnimation()
    {
        var animating = false;
        foreach (var target in AnimatedHoverTargets)
        {
            var current = _hoverProgress.GetValueOrDefault(target);
            var destination = _hoverTarget == target ? 1f : 0f;
            var next = current + (destination - current) * HoverAnimationStep;
            if (Math.Abs(destination - next) < .01f) next = destination;
            _hoverProgress[target] = next;
            if (next != destination) animating = true;
        }
        return animating;
    }

    private float HoverProgress(HoverTarget target) => _animationsEnabled
        ? _hoverProgress.GetValueOrDefault(target)
        : _hoverTarget == target ? 1 : 0;

    private RectangleF PressedIconBounds(RectangleF bounds, HoverTarget target)
    {
        if (_pressedTarget == target) bounds.Offset(0, .7f);
        return bounds;
    }

    private static Dictionary<string, double> SnapshotValues(QuotaSnapshot snapshot)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var card in snapshot.Cards)
        {
            foreach (var window in card.Windows)
            {
                if (window.UsedPercent is { } used)
                {
                    values[WindowKey(card.Key, window.Label)] = used;
                }
            }
        }

        return values;
    }

    private static string WindowKey(string cardKey, string windowLabel) => $"{cardKey}\0{windowLabel}";
}
