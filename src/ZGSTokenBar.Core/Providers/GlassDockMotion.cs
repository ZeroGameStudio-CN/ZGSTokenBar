using System.Drawing;

namespace ZGSTokenBar.Core;

/// <summary>
/// Pure-geometry 60 FPS motion controller for GlassDock pill width, chip glides, ring
/// reordering, and overflow enter/exit transitions with mid-flight retargeting.
/// </summary>
public sealed class GlassDockMotion
{
    public const int DurationMilliseconds = 240;

    public sealed record VisualRing(
        GlassDockRing Ring,
        float X,
        float Y,
        float Width,
        float Height,
        float Alpha,
        bool Exiting);

    public sealed record Frame(
        float Width,
        RectangleF? ChipBounds,
        float ChipAlpha,
        IReadOnlyList<VisualRing> Rings,
        bool Animating);

    private sealed class RingTrack
    {
        public RingTrack(
            string key,
            GlassDockRing ring,
            float fromX,
            float toX,
            float y,
            float width,
            float height,
            float fromAlpha,
            float toAlpha,
            bool exiting)
        {
            Key = key;
            Ring = ring;
            FromX = fromX;
            ToX = toX;
            Y = y;
            Width = width;
            Height = height;
            FromAlpha = fromAlpha;
            ToAlpha = toAlpha;
            Exiting = exiting;
        }

        public string Key { get; }
        public GlassDockRing Ring { get; set; }
        public float FromX { get; set; }
        public float ToX { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float FromAlpha { get; set; }
        public float ToAlpha { get; set; }
        public bool Exiting { get; set; }
    }

    private readonly int _durationMs;
    private readonly List<RingTrack> _tracks = [];
    private bool _initialized;
    private long _startTick;
    private float _fromWidth;
    private float _toWidth;
    private float _currentWidth;
    private RectangleF? _fromChip;
    private RectangleF? _toChip;
    private float _fromChipAlpha;
    private float _toChipAlpha;

    public GlassDockMotion(int durationMilliseconds = DurationMilliseconds)
    {
        _durationMs = Math.Max(1, durationMilliseconds);
    }

    public bool HasSnapshot => _initialized;
    public bool IsAnimating { get; private set; }
    public float CurrentWidth => _currentWidth;
    public int TargetWidth => (int)MathF.Round(_toWidth);

    public void Reset()
    {
        _tracks.Clear();
        _initialized = false;
        IsAnimating = false;
        _startTick = 0;
        _fromWidth = 0f;
        _toWidth = 0f;
        _currentWidth = 0f;
        _fromChip = null;
        _toChip = null;
        _fromChipAlpha = 0f;
        _toChipAlpha = 0f;
    }

    public Frame Update(GlassDockPlan plan, long nowTick, bool animate)
    {
        ExtractTargets(plan, out var targetRings, out var targetChipBounds);

        if (!animate || !_initialized)
        {
            SnapToTarget(plan.Width, targetRings, targetChipBounds);
            return BuildFrame(1f, animating: false);
        }

        var progress = IsAnimating
            ? Math.Clamp((nowTick - _startTick) / (float)_durationMs, 0f, 1f)
            : 1f;
        var eased = IsAnimating ? EaseOutCubic(progress) : 1f;

        if (HasTargetChanged(plan.Width, targetRings, targetChipBounds))
        {
            var curWidth = Lerp(_fromWidth, _toWidth, eased);
            var curChip = InterpolateChip(eased);
            var curChipAlpha = Lerp(_fromChipAlpha, _toChipAlpha, eased);
            var enterAnchorX = curChip?.X ?? targetChipBounds?.X ?? FallbackAnchorX(curWidth);
            var exitAnchorX = targetChipBounds?.X ?? curChip?.X ?? FallbackAnchorX(plan.Width);

            var currentByKey = new Dictionary<string, (RingTrack Track, float X, float Alpha)>(_tracks.Count, StringComparer.Ordinal);
            foreach (var track in _tracks)
            {
                currentByKey[track.Key] = (track, Lerp(track.FromX, track.ToX, eased), Lerp(track.FromAlpha, track.ToAlpha, eased));
            }

            var nextTracks = new List<RingTrack>(Math.Max(targetRings.Count, _tracks.Count));
            var targetKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, ring, slot) in targetRings)
            {
                targetKeys.Add(key);
                if (currentByKey.TryGetValue(key, out var existing))
                {
                    nextTracks.Add(new RingTrack(
                        key,
                        ring,
                        existing.X,
                        slot.X,
                        slot.Y,
                        slot.Width,
                        slot.Height,
                        existing.Alpha,
                        1f,
                        exiting: false));
                }
                else
                {
                    nextTracks.Add(new RingTrack(
                        key,
                        ring,
                        enterAnchorX,
                        slot.X,
                        slot.Y,
                        slot.Width,
                        slot.Height,
                        0f,
                        1f,
                        exiting: false));
                }
            }

            foreach (var track in _tracks)
            {
                if (targetKeys.Contains(track.Key)) continue;
                var current = currentByKey[track.Key];
                if (current.Alpha <= 0.01f) continue;
                nextTracks.Add(new RingTrack(
                    track.Key,
                    track.Ring,
                    current.X,
                    exitAnchorX,
                    track.Y,
                    track.Width,
                    track.Height,
                    current.Alpha,
                    0f,
                    exiting: true));
            }

            _tracks.Clear();
            _tracks.AddRange(nextTracks);

            _fromWidth = curWidth;
            _toWidth = plan.Width;
            _fromChip = targetChipBounds is null ? curChip : (curChip ?? targetChipBounds);
            _toChip = targetChipBounds ?? curChip;
            _fromChipAlpha = curChip.HasValue ? curChipAlpha : 0f;
            _toChipAlpha = targetChipBounds.HasValue ? 1f : 0f;

            if (HasMotionDelta())
            {
                _startTick = nowTick;
                IsAnimating = true;
                return BuildFrame(0f, animating: true);
            }

            _tracks.RemoveAll(track => track.Exiting);
            SnapToTarget(plan.Width, targetRings, targetChipBounds);
            return BuildFrame(1f, animating: false);
        }

        RefreshTargetPayloads(targetRings);

        if (!IsAnimating || progress >= 1f)
        {
            _tracks.RemoveAll(track => track.Exiting);
            SnapToTarget(plan.Width, targetRings, targetChipBounds);
            return BuildFrame(1f, animating: false);
        }

        return BuildFrame(eased, animating: true);
    }

    private Frame BuildFrame(float eased, bool animating)
    {
        _currentWidth = animating ? Lerp(_fromWidth, _toWidth, eased) : _toWidth;
        var chipBounds = animating ? InterpolateChip(eased) : _toChip;
        var chipAlpha = animating ? Lerp(_fromChipAlpha, _toChipAlpha, eased) : _toChipAlpha;
        if (chipAlpha <= 0.01f) chipBounds = null;

        var visualRings = new List<VisualRing>(_tracks.Count);
        foreach (var track in _tracks)
        {
            var x = animating ? Lerp(track.FromX, track.ToX, eased) : track.ToX;
            var alpha = animating ? Lerp(track.FromAlpha, track.ToAlpha, eased) : track.ToAlpha;
            visualRings.Add(new VisualRing(
                track.Ring,
                x,
                track.Y,
                track.Width,
                track.Height,
                alpha,
                track.Exiting));
        }

        return new Frame(_currentWidth, chipBounds, chipAlpha, visualRings, animating);
    }

    private void SnapToTarget(
        int width,
        List<(string Key, GlassDockRing Ring, RectangleF Slot)> targetRings,
        RectangleF? targetChipBounds)
    {
        _tracks.Clear();
        foreach (var (key, ring, slot) in targetRings)
        {
            _tracks.Add(new RingTrack(key, ring, slot.X, slot.X, slot.Y, slot.Width, slot.Height, 1f, 1f, exiting: false));
        }

        _initialized = true;
        IsAnimating = false;
        _fromWidth = width;
        _toWidth = width;
        _currentWidth = width;
        _fromChip = targetChipBounds;
        _toChip = targetChipBounds;
        _fromChipAlpha = targetChipBounds.HasValue ? 1f : 0f;
        _toChipAlpha = _fromChipAlpha;
    }

    private bool HasTargetChanged(
        int targetWidth,
        List<(string Key, GlassDockRing Ring, RectangleF Slot)> targetRings,
        RectangleF? targetChipBounds)
    {
        if (targetWidth != (int)MathF.Round(_toWidth)) return true;

        var hadTargetChip = _toChip.HasValue && _toChipAlpha > 0.5f;
        if (targetChipBounds.HasValue != hadTargetChip) return true;
        if (targetChipBounds is { } tc && _toChip is { } oc
            && (MathF.Abs(tc.X - oc.X) > 0.25f || MathF.Abs(tc.Width - oc.Width) > 0.25f))
        {
            return true;
        }

        var activeCount = 0;
        for (var i = 0; i < _tracks.Count; i++)
        {
            var track = _tracks[i];
            if (track.Exiting) continue;
            if (activeCount >= targetRings.Count) return true;
            var target = targetRings[activeCount];
            if (!string.Equals(track.Key, target.Key, StringComparison.Ordinal)
                || MathF.Abs(track.ToX - target.Slot.X) > 0.25f
                || MathF.Abs(track.Width - target.Slot.Width) > 0.25f)
            {
                return true;
            }
            activeCount++;
        }

        return activeCount != targetRings.Count;
    }

    private void RefreshTargetPayloads(List<(string Key, GlassDockRing Ring, RectangleF Slot)> targetRings)
    {
        var index = 0;
        for (var i = 0; i < _tracks.Count && index < targetRings.Count; i++)
        {
            if (_tracks[i].Exiting) continue;
            _tracks[i].Ring = targetRings[index].Ring;
            index++;
        }
    }

    private bool HasMotionDelta()
    {
        if (MathF.Abs(_fromWidth - _toWidth) > 0.25f) return true;
        if (MathF.Abs(_fromChipAlpha - _toChipAlpha) > 0.01f) return true;
        if (_fromChip is { } fc && _toChip is { } tc && MathF.Abs(fc.X - tc.X) > 0.25f) return true;
        foreach (var track in _tracks)
        {
            if (MathF.Abs(track.FromX - track.ToX) > 0.25f || MathF.Abs(track.FromAlpha - track.ToAlpha) > 0.01f)
            {
                return true;
            }
        }

        return false;
    }

    private RectangleF? InterpolateChip(float eased)
    {
        if (_fromChip is { } fc && _toChip is { } tc)
        {
            return new RectangleF(
                Lerp(fc.X, tc.X, eased),
                Lerp(fc.Y, tc.Y, eased),
                Lerp(fc.Width, tc.Width, eased),
                Lerp(fc.Height, tc.Height, eased));
        }

        return _toChip ?? _fromChip;
    }

    private static void ExtractTargets(
        GlassDockPlan plan,
        out List<(string Key, GlassDockRing Ring, RectangleF Slot)> targetRings,
        out RectangleF? targetChipBounds)
    {
        targetRings = new List<(string, GlassDockRing, RectangleF)>(plan.Visible.Count);
        targetChipBounds = null;
        var itemIndex = 0;
        foreach (var slot in plan.Slots)
        {
            if (slot.Chip)
            {
                targetChipBounds = slot.Bounds;
                continue;
            }

            if (itemIndex < plan.Visible.Count)
            {
                var ring = plan.Visible[itemIndex++];
                targetRings.Add((KeyOf(ring), ring, slot.Bounds));
            }
        }
    }

    public static string KeyOf(GlassDockRing ring) =>
        !string.IsNullOrWhiteSpace(ring.StableId)
            ? ring.StableId
            : ring.ProviderId + ":" + ring.AccountNumber + ":" + ring.AccountLabel;

    private static float FallbackAnchorX(float pillWidth) => Math.Max(
        GlassDockLayout.PadLeft,
        pillWidth - GlassDockLayout.PadRight - GlassDockLayout.SystemWidth - GlassDockLayout.DividerGap - GlassDockLayout.ItemWidth);

    private static float EaseOutCubic(float progress) => 1f - MathF.Pow(1f - Math.Clamp(progress, 0f, 1f), 3f);

    private static float Lerp(float from, float to, float eased) => from + (to - from) * eased;
}
