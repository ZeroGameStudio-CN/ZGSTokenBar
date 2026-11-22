using System.Drawing;

namespace ZGSTokenBar.Core;

public sealed record GlassDockInteraction(
    string? HoveredRingKey,
    bool ChipHovered,
    bool RouterHovered,
    bool IntakeHovered,
    bool SystemHovered,
    bool RateHovered,
    double? Cpu,
    double? Memory,
    double? Disk,
    double? Gpu);

/// <summary>
/// Pure-geometry 60 FPS motion controller for GlassDock pill width, chip glides, ring
/// reordering, quota arc & system bar tweening, and hover glow fades with mid-flight retargeting.
/// </summary>
public sealed class GlassDockMotion
{
    public const int DurationMilliseconds = 240;
    public const int HoverInMilliseconds = 140;
    public const int HoverOutMilliseconds = 110;

    private const string ChipHoverKey = "\0chip";
    private const string RouterHoverKey = "\0router";
    private const string IntakeHoverKey = "\0intake";
    private const string SystemHoverKey = "\0system";
    private const string RateHoverKey = "\0rate";

    public sealed record VisualRing(
        GlassDockRing Ring,
        float X,
        float Y,
        float Width,
        float Height,
        float Alpha,
        bool Exiting,
        double? DisplayedRemaining = null,
        float HoverAmount = 0f);

    public sealed record Frame(
        float Width,
        RectangleF? ChipBounds,
        float ChipAlpha,
        IReadOnlyList<VisualRing> Rings,
        bool Animating,
        float ChipHover = 0f,
        float RouterHover = 0f,
        float IntakeHover = 0f,
        float SystemHover = 0f,
        float RateHover = 0f,
        double? Cpu = null,
        double? Memory = null,
        double? Disk = null,
        double? Gpu = null);

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

    private sealed class ScalarTween
    {
        public double From { get; set; }
        public double To { get; set; }
        public double Current { get; set; }
        public long StartTick { get; set; }
        public bool Active { get; set; }
    }

    private readonly record struct HoverTrack(float From, float To, long StartTick, int DurationMs);

    private readonly int _durationMs;
    private readonly List<RingTrack> _tracks = [];
    private readonly Dictionary<string, ScalarTween> _ringValueTweens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HoverTrack> _hoverTracks = new(StringComparer.Ordinal);
    private ScalarTween? _cpuTween;
    private ScalarTween? _memoryTween;
    private ScalarTween? _diskTween;
    private ScalarTween? _gpuTween;
    private bool _initialized;
    private bool _layoutAnimating;
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
        _ringValueTweens.Clear();
        _hoverTracks.Clear();
        _cpuTween = null;
        _memoryTween = null;
        _diskTween = null;
        _gpuTween = null;
        _initialized = false;
        _layoutAnimating = false;
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

    public Frame Update(GlassDockPlan plan, long nowTick, bool animate) =>
        Update(plan, nowTick, animate, interaction: null);

    public Frame Update(GlassDockPlan plan, long nowTick, bool animate, GlassDockInteraction? interaction)
    {
        ExtractTargets(plan, out var targetRings, out var targetChipBounds);

        float layoutEased;
        if (!animate || !_initialized)
        {
            SnapToTarget(plan.Width, targetRings, targetChipBounds);
            layoutEased = 1f;
        }
        else
        {
            var progress = _layoutAnimating
                ? Math.Clamp((nowTick - _startTick) / (float)_durationMs, 0f, 1f)
                : 1f;
            var eased = _layoutAnimating ? EaseOutCubic(progress) : 1f;

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
                    _layoutAnimating = true;
                    layoutEased = 0f;
                }
                else
                {
                    _tracks.RemoveAll(track => track.Exiting);
                    SnapToTarget(plan.Width, targetRings, targetChipBounds);
                    layoutEased = 1f;
                }
            }
            else
            {
                RefreshTargetPayloads(targetRings);
                if (!_layoutAnimating || progress >= 1f)
                {
                    _tracks.RemoveAll(track => track.Exiting);
                    SnapToTarget(plan.Width, targetRings, targetChipBounds);
                    layoutEased = 1f;
                }
                else
                {
                    layoutEased = eased;
                }
            }
        }

        var anyValueAnimating = UpdateRingValueTweens(nowTick, animate);
        var anyMetricAnimating = false;
        var cpu = AdvanceScalarTween(ref _cpuTween, interaction?.Cpu, nowTick, animate, threshold: 0.25, ref anyMetricAnimating);
        var memory = AdvanceScalarTween(ref _memoryTween, interaction?.Memory, nowTick, animate, threshold: 0.25, ref anyMetricAnimating);
        var disk = AdvanceScalarTween(ref _diskTween, interaction?.Disk, nowTick, animate, threshold: 0.25, ref anyMetricAnimating);
        var gpu = AdvanceScalarTween(ref _gpuTween, interaction?.Gpu, nowTick, animate, threshold: 0.25, ref anyMetricAnimating);

        var anyHoverAnimating = false;
        var chipHover = AdvanceHover(ChipHoverKey, interaction?.ChipHovered == true, nowTick, animate, ref anyHoverAnimating);
        var routerHover = AdvanceHover(RouterHoverKey, interaction?.RouterHovered == true, nowTick, animate, ref anyHoverAnimating);
        var intakeHover = AdvanceHover(IntakeHoverKey, interaction?.IntakeHovered == true, nowTick, animate, ref anyHoverAnimating);
        var systemHover = AdvanceHover(SystemHoverKey, interaction?.SystemHovered == true, nowTick, animate, ref anyHoverAnimating);
        var rateHover = AdvanceHover(RateHoverKey, interaction?.RateHovered == true, nowTick, animate, ref anyHoverAnimating);

        IsAnimating = _layoutAnimating || anyValueAnimating || anyMetricAnimating || anyHoverAnimating;
        return BuildFrame(
            layoutEased,
            nowTick,
            animate,
            interaction,
            ref anyHoverAnimating,
            chipHover,
            routerHover,
            intakeHover,
            systemHover,
            rateHover,
            cpu,
            memory,
            disk,
            gpu);
    }

    private Frame BuildFrame(
        float layoutEased,
        long nowTick,
        bool animate,
        GlassDockInteraction? interaction,
        ref bool anyHoverAnimating,
        float chipHover,
        float routerHover,
        float intakeHover,
        float systemHover,
        float rateHover,
        double? cpu,
        double? memory,
        double? disk,
        double? gpu)
    {
        _currentWidth = _layoutAnimating ? Lerp(_fromWidth, _toWidth, layoutEased) : _toWidth;
        var chipBounds = _layoutAnimating ? InterpolateChip(layoutEased) : _toChip;
        var chipAlpha = _layoutAnimating ? Lerp(_fromChipAlpha, _toChipAlpha, layoutEased) : _toChipAlpha;
        if (chipAlpha <= 0.01f) chipBounds = null;

        var activeRingKeys = new HashSet<string>(_tracks.Count + 5, StringComparer.Ordinal)
        {
            ChipHoverKey,
            RouterHoverKey,
            IntakeHoverKey,
            SystemHoverKey,
            RateHoverKey,
        };
        var visualRings = new List<VisualRing>(_tracks.Count);
        foreach (var track in _tracks)
        {
            activeRingKeys.Add(track.Key);
            var x = _layoutAnimating ? Lerp(track.FromX, track.ToX, layoutEased) : track.ToX;
            var alpha = _layoutAnimating ? Lerp(track.FromAlpha, track.ToAlpha, layoutEased) : track.ToAlpha;
            var displayedRemaining = _ringValueTweens.TryGetValue(track.Key, out var valueTween)
                ? valueTween.Current
                : track.Ring.RemainingPercent;
            var hovered = !track.Exiting && string.Equals(interaction?.HoveredRingKey, track.Key, StringComparison.Ordinal);
            var hoverAmount = AdvanceHover(track.Key, hovered, nowTick, animate, ref anyHoverAnimating);
            visualRings.Add(new VisualRing(
                track.Ring,
                x,
                track.Y,
                track.Width,
                track.Height,
                alpha,
                track.Exiting,
                displayedRemaining,
                hoverAmount));
        }

        if (_hoverTracks.Count > activeRingKeys.Count)
        {
            foreach (var staleKey in _hoverTracks.Keys.Where(key => !activeRingKeys.Contains(key)).ToArray())
            {
                _hoverTracks.Remove(staleKey);
            }
        }

        IsAnimating = IsAnimating || anyHoverAnimating;
        return new Frame(
            _currentWidth,
            chipBounds,
            chipAlpha,
            visualRings,
            IsAnimating,
            chipHover,
            routerHover,
            intakeHover,
            systemHover,
            rateHover,
            cpu,
            memory,
            disk,
            gpu);
    }

    private bool UpdateRingValueTweens(long nowTick, bool animate)
    {
        var anyAnimating = false;
        var activeKeys = new HashSet<string>(_tracks.Count, StringComparer.Ordinal);
        foreach (var track in _tracks)
        {
            activeKeys.Add(track.Key);
            if (track.Exiting) continue;
            var tween = _ringValueTweens.GetValueOrDefault(track.Key);
            AdvanceScalarTween(ref tween, track.Ring.RemainingPercent, nowTick, animate, threshold: 0.05, ref anyAnimating);
            if (tween is null) _ringValueTweens.Remove(track.Key);
            else _ringValueTweens[track.Key] = tween;
        }

        if (_ringValueTweens.Count > activeKeys.Count)
        {
            foreach (var staleKey in _ringValueTweens.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
            {
                _ringValueTweens.Remove(staleKey);
            }
        }

        return anyAnimating;
    }

    private double? AdvanceScalarTween(
        ref ScalarTween? tween,
        double? target,
        long nowTick,
        bool animate,
        double threshold,
        ref bool anyAnimating)
    {
        if (target is not { } value || !double.IsFinite(value))
        {
            tween = null;
            return null;
        }

        if (!animate || tween is null)
        {
            tween = new ScalarTween { From = value, To = value, Current = value, StartTick = nowTick, Active = false };
            return value;
        }

        if (Math.Abs(value - tween.To) >= threshold)
        {
            var startValue = tween.Current;
            if (Math.Abs(value - startValue) >= threshold)
            {
                tween.From = startValue;
                tween.To = value;
                tween.StartTick = nowTick;
                tween.Active = true;
            }
            else
            {
                tween.From = value;
                tween.To = value;
                tween.Current = value;
                tween.Active = false;
            }
        }

        if (!tween.Active) return tween.Current;

        var progress = Math.Clamp((nowTick - tween.StartTick) / (float)_durationMs, 0f, 1f);
        if (progress >= 1f)
        {
            tween.From = tween.To;
            tween.Current = tween.To;
            tween.Active = false;
            return tween.Current;
        }

        var eased = EaseOutCubic(progress);
        tween.Current = tween.From + (tween.To - tween.From) * eased;
        anyAnimating = true;
        return tween.Current;
    }

    private float AdvanceHover(string key, bool hovered, long nowTick, bool animate, ref bool anyAnimating)
    {
        var desired = hovered ? 1f : 0f;
        if (!animate)
        {
            if (hovered) _hoverTracks[key] = new HoverTrack(1f, 1f, nowTick, HoverInMilliseconds);
            else _hoverTracks.Remove(key);
            return desired;
        }

        if (!_hoverTracks.TryGetValue(key, out var track))
        {
            if (!hovered) return 0f;
            _hoverTracks[key] = new HoverTrack(0f, 1f, nowTick, HoverInMilliseconds);
            anyAnimating = true;
            return 0f;
        }

        var current = SampleHover(track, nowTick);
        if (MathF.Abs(desired - track.To) > 0.01f)
        {
            var duration = desired > current ? HoverInMilliseconds : HoverOutMilliseconds;
            track = new HoverTrack(current, desired, nowTick, duration);
            _hoverTracks[key] = track;
        }

        var progress = Math.Clamp((nowTick - track.StartTick) / (float)track.DurationMs, 0f, 1f);
        if (progress >= 1f)
        {
            if (track.To <= 0.01f)
            {
                _hoverTracks.Remove(key);
                return 0f;
            }

            _hoverTracks[key] = new HoverTrack(track.To, track.To, nowTick, track.DurationMs);
            return track.To;
        }

        anyAnimating = true;
        return Lerp(track.From, track.To, EaseOutCubic(progress));
    }

    private static float SampleHover(HoverTrack track, long nowTick)
    {
        if (track.DurationMs <= 0 || MathF.Abs(track.From - track.To) <= 0.01f) return track.To;
        var progress = Math.Clamp((nowTick - track.StartTick) / (float)track.DurationMs, 0f, 1f);
        return Lerp(track.From, track.To, EaseOutCubic(progress));
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
        _layoutAnimating = false;
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
