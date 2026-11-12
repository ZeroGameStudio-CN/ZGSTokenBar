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
        _animatedWindowKeys = changedKeys;
        _snapshotAnimationStarted = DateTime.UtcNow;
        _snapshotAnimating = true;
        EnsureAnimationTimer();
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
