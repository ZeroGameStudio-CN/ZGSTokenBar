namespace ZGSTokenBar.Core;

public sealed class QuotaPaceTracker
{
    internal static readonly TimeSpan HistoryRetention = TimeSpan.FromHours(26);
    internal static readonly TimeSpan ImportedSampleInterval = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan CycleClockTolerance = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan RecentFallbackAge = TimeSpan.FromHours(2);
    internal static readonly TimeSpan MinimumFallbackTrendSpan = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ResponsiveFallbackMaximumSpan = TimeSpan.FromMinutes(12);
    internal const double MinimumCycleFractionForProjection = .03;
    internal const double ResetDropThreshold = 2;
    internal const int MaximumSamplesPerSeries = 2048;
    internal const int MaximumSamples = 32768;
    internal const double AcceleratingRecentWeight = .55;
    internal const double SlowingRecentWeight = .20;
    internal const double NormalAcceleratingRecentWeight = .45;
    internal const double NormalSlowingRecentWeight = .15;
    internal const double ProvisionalAcceleratingRecentWeight = .35;
    internal const double ProvisionalSlowingRecentWeight = .10;

    private static readonly TrendWindow[] LongTrendWindows =
    [
        new(
            TimeSpan.FromHours(24),
            TimeSpan.FromHours(18),
            TimeSpan.FromHours(26),
            6,
            1,
            QuotaTrendConfidence.Stable,
            true),
        new(
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(4.5),
            TimeSpan.FromHours(8),
            4,
            .5,
            QuotaTrendConfidence.Stable,
            true),
    ];

    private static readonly TrendWindow[] TrendWindows =
    [
        new(
            TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(45),
            TimeSpan.FromMinutes(75),
            2,
            .5,
            QuotaTrendConfidence.Stable),
        new(
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(24),
            TimeSpan.FromMinutes(40),
            3,
            1,
            QuotaTrendConfidence.Normal),
        new(
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(12),
            TimeSpan.FromMinutes(20),
            3,
            1,
            QuotaTrendConfidence.Provisional),
    ];

    private readonly List<QuotaRateSample> _samples;

    internal int SampleCount => _samples.Count;

    public QuotaPaceTracker(QuotaRateHistory? history = null)
    {
        _samples = ApplySampleLimits(history?.Samples ?? []).ToList();
    }

    public bool Observe(QuotaSnapshot snapshot, DateTimeOffset now)
    {
        var changed = false;
        var freshProviders = snapshot.Health
            .Where(health =>
                health.Connected
                && health.Code is ProviderHealthCode.Current or ProviderHealthCode.Unknown)
            .Select(health => health.Provider)
            .ToHashSet();

        foreach (var card in snapshot.Cards)
        {
            if (!freshProviders.Contains(card.Provider)) continue;
            var capturedAt = card.CapturedAt ?? snapshot.CapturedAt;
            if (!IsFreshCapture(capturedAt, snapshot.CapturedAt, now)) continue;

            foreach (var window in card.Windows)
            {
                if (window.UsedPercent is not { } used
                    || !double.IsFinite(used)
                    || used is < 0 or > 100
                    || window.Duration <= TimeSpan.Zero)
                {
                    continue;
                }

                var sample = new QuotaRateSample(
                    card.Key,
                    NormalizeWindowLabel(window),
                    window.Duration.Ticks,
                    capturedAt,
                    used,
                    window.ResetsAt,
                    QuotaRateSampleSource.Live);
                var seriesKey = SeriesIdentity(sample);
                var latest = _samples
                    .Where(existing => SeriesIdentity(existing) == seriesKey)
                    .MaxBy(existing => existing.CapturedAt);
                if (latest is not null && capturedAt <= latest.CapturedAt)
                {
                    if (capturedAt == latest.CapturedAt && latest.Source != QuotaRateSampleSource.Live)
                    {
                        _samples.Remove(latest);
                        _samples.Add(sample);
                        changed = true;
                    }
                    continue;
                }

                if (latest is not null && StartsNewCycle(latest, sample))
                {
                    _samples.RemoveAll(existing => SeriesIdentity(existing) == seriesKey);
                }

                _samples.Add(sample);
                changed = true;
            }
        }

        changed |= Trim(now);
        return changed;
    }

    public bool MergeImported(IEnumerable<QuotaRateSample> imported, DateTimeOffset now)
    {
        var changed = false;
        foreach (var candidate in Deduplicate(imported)
                     .Where(sample => sample.Source == QuotaRateSampleSource.CodexRollout)
                     .Where(IsStructurallyValid)
                     .Where(sample => sample.CapturedAt >= now - HistoryRetention)
                     .Where(sample => sample.CapturedAt <= now.AddMinutes(1)))
        {
            var seriesKey = SeriesIdentity(candidate);
            var liveAnchor = _samples
                .Where(sample =>
                    sample.Source == QuotaRateSampleSource.Live
                    && SeriesIdentity(sample) == seriesKey)
                .MaxBy(sample => sample.CapturedAt);
            if (liveAnchor is null
                || candidate.CapturedAt >= liveAnchor.CapturedAt
                || candidate.UsedPercent > liveAnchor.UsedPercent + 1
                || !SameCycle(candidate, liveAnchor))
            {
                continue;
            }

            var existing = _samples.FirstOrDefault(sample =>
                SeriesIdentity(sample) == seriesKey
                && sample.CapturedAt == candidate.CapturedAt);
            if (existing is not null)
            {
                if (existing.Source == QuotaRateSampleSource.Live) continue;
                if (existing == candidate) continue;
                _samples.Remove(existing);
            }

            _samples.Add(candidate);
            changed = true;
        }

        changed |= Trim(now);
        return changed;
    }

    public QuotaPaceEstimate Estimate(
        QuotaCard card,
        QuotaWindow window,
        DateTimeOffset now,
        int refreshMinutes)
    {
        if (window.UsedPercent is not { } used || !double.IsFinite(used))
        {
            return new QuotaPaceEstimate(QuotaPaceStatus.Unavailable);
        }
        if (QuotaDisplayFormatting.WeeklyBlockReset(card, window, now) is not null)
        {
            return new QuotaPaceEstimate(QuotaPaceStatus.WeeklyBlocked);
        }
        if (card.CapturedAt is not { } capturedAt)
        {
            return new QuotaPaceEstimate(QuotaPaceStatus.WaitingForFreshData);
        }

        var cycle = CalculateCycle(window, used, capturedAt);
        if (used >= 100)
        {
            return new QuotaPaceEstimate(QuotaPaceStatus.Exhausted, cycle);
        }

        var maximumAge = TimeSpan.FromMinutes(Math.Max(15, Math.Clamp(refreshMinutes, 1, 60) * 2));
        var age = now - capturedAt;
        var validUntil = capturedAt + maximumAge;
        var fallbackAge = TimeSpan.FromTicks(Math.Min(
            window.Duration.Ticks,
            Math.Max(maximumAge.Ticks, RecentFallbackAge.Ticks)));
        var stale = age > maximumAge;
        if (age < TimeSpan.Zero
            || age > fallbackAge
            || window.ResetsAt is { } elapsedReset && elapsedReset <= now)
        {
            return new QuotaPaceEstimate(
                QuotaPaceStatus.WaitingForFreshData,
                ValidUntil: validUntil);
        }

        var seriesKey = SeriesIdentity(card, window);
        var series = Deduplicate(_samples
                .Where(sample => SeriesIdentity(sample) == seriesKey))
            .OrderBy(sample => sample.CapturedAt)
            .ToArray();
        if (series.Length == 0)
        {
            return CycleFallback(cycle, validUntil, stale);
        }

        var latest = series[^1];
        if (latest.CapturedAt != capturedAt || Math.Abs(latest.UsedPercent - used) > .01)
        {
            return stale
                ? CycleFallback(cycle, validUntil, true)
                : new QuotaPaceEstimate(
                    QuotaPaceStatus.WaitingForFreshData,
                    cycle,
                    ValidUntil: validUntil);
        }

        if (window.Duration >= TimeSpan.FromDays(1))
        {
            var baselineTrend = LongTrendWindows
                .Select(trendWindow => CalculateTrend(series, latest, trendWindow))
                .FirstOrDefault(trend => trend is not null);
            if (baselineTrend is not null)
            {
                var standardRecentTrends = TrendWindows
                    .Select(trendWindow => CalculateTrend(series, latest, trendWindow))
                    .Where(trend => trend is not null)
                    .Select(trend => trend!)
                    .ToArray();
                var responsiveFallback = CalculateFallbackTrend(
                    series,
                    latest,
                    ResponsiveFallbackMaximumSpan);
                var recentTrend = standardRecentTrends.FirstOrDefault(trend => trend.Meaningful)
                    ?? (responsiveFallback?.Meaningful == true ? responsiveFallback : null)
                    ?? standardRecentTrends.FirstOrDefault()
                    ?? responsiveFallback;
                if (!baselineTrend.Meaningful && recentTrend?.Meaningful != true)
                {
                    return new QuotaPaceEstimate(
                        QuotaPaceStatus.NoMeaningfulConsumption,
                        cycle,
                        ObservedSpan: baselineTrend.ObservedSpan,
                        ValidUntil: validUntil);
                }

                var rate = baselineTrend.PositiveRate;
                if (recentTrend is not null)
                {
                    rate = BlendWithRecent(rate, recentTrend);
                }

                return ProjectTrend(
                    rate,
                    baselineTrend.ObservedSpan,
                    baselineTrend.Confidence,
                    latest,
                    window,
                    cycle,
                    validUntil);
            }
        }

        foreach (var trendWindow in TrendWindows)
        {
            var trend = CalculateTrend(series, latest, trendWindow);
            if (trend is null) continue;
            if (!trend.Meaningful)
            {
                return new QuotaPaceEstimate(
                    QuotaPaceStatus.NoMeaningfulConsumption,
                    cycle,
                    ObservedSpan: trend.ObservedSpan,
                    ValidUntil: validUntil);
            }

            return ProjectTrend(
                trend.PositiveRate,
                trend.ObservedSpan,
                trend.Confidence,
                latest,
                window,
                cycle,
                validUntil);
        }

        var fallbackTrend = CalculateFallbackTrend(series, latest);
        if (fallbackTrend is not null)
        {
            return ProjectTrend(
                fallbackTrend.PositiveRate,
                fallbackTrend.ObservedSpan,
                fallbackTrend.Confidence,
                latest,
                window,
                cycle,
                validUntil);
        }

        return CycleFallback(cycle, validUntil, stale);
    }

    public QuotaRateHistory Export(DateTimeOffset now)
    {
        Trim(now);
        return new QuotaRateHistory
        {
            Samples = Deduplicate(_samples)
                .OrderBy(sample => sample.CapturedAt)
                .ToList(),
        };
    }

    public static string SeriesKey(QuotaCard card, QuotaWindow window) =>
        $"{card.Key}\0{NormalizeWindowLabel(window)}\0{window.Duration.Ticks}";

    public static bool IsFreshCapture(
        DateTimeOffset capturedAt,
        DateTimeOffset snapshotAt,
        DateTimeOffset now) =>
        capturedAt <= now.AddMinutes(1)
        && capturedAt >= now.AddMinutes(-1)
        && (capturedAt - snapshotAt).Duration() <= TimeSpan.FromMinutes(1);

    internal static string NormalizeWindowLabel(QuotaWindow window)
    {
        var label = window.Label.Trim().ToLowerInvariant();
        if (label == "fable") return "fable";
        if (window.Duration == TimeSpan.FromHours(5) && label is "primary" or "5h") return "5h";
        return label is "1w" or "week" or "7d" ? "7d" : label;
    }

    internal static bool SameCycle(QuotaRateSample left, QuotaRateSample right)
    {
        if (left.ResetsAt is null || right.ResetsAt is null)
        {
            return left.ResetsAt is null
                && right.ResetsAt is null
                && right.UsedPercent >= left.UsedPercent;
        }

        return (right.ResetsAt.Value - left.ResetsAt.Value).Duration() <= ResetTolerance;
    }

    private static QuotaPaceEstimate CycleFallback(
        QuotaCyclePace? cycle,
        DateTimeOffset validUntil,
        bool stale = false)
    {
        if (cycle?.ProjectedExhaustedAt is not null)
        {
            return new QuotaPaceEstimate(
                cycle.ResetsBeforeExhaustion
                    ? QuotaPaceStatus.ResetsBeforeExhaustion
                    : QuotaPaceStatus.ProjectedExhaustion,
                cycle,
                ValidUntil: validUntil);
        }

        return new QuotaPaceEstimate(
            stale ? QuotaPaceStatus.WaitingForFreshData : QuotaPaceStatus.Learning,
            cycle,
            ValidUntil: validUntil);
    }

    private static QuotaCyclePace? CalculateCycle(
        QuotaWindow window,
        double used,
        DateTimeOffset capturedAt)
    {
        if (window.Duration <= TimeSpan.Zero
            || window.ResetsAt is not { } reset
            || reset <= capturedAt)
        {
            return null;
        }

        var cycleStart = reset - window.Duration;
        var elapsed = capturedAt - cycleStart;
        if (elapsed < -CycleClockTolerance || elapsed > window.Duration + CycleClockTolerance)
        {
            return null;
        }

        elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        var elapsedFraction = Math.Clamp(elapsed.TotalSeconds / window.Duration.TotalSeconds, 0, 1);
        var expectedUsed = elapsedFraction * 100;
        var delta = used - expectedUsed;
        if (elapsedFraction < MinimumCycleFractionForProjection
            || elapsed <= TimeSpan.Zero
            || used <= 0)
        {
            return new QuotaCyclePace(expectedUsed, delta);
        }

        var rate = used / elapsed.TotalHours;
        if (!double.IsFinite(rate) || rate <= 0)
        {
            return new QuotaCyclePace(expectedUsed, delta);
        }

        var remaining = Math.Clamp(100 - used, 0, 100);
        DateTimeOffset projectedAt;
        try
        {
            projectedAt = capturedAt.AddHours(remaining / rate);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new QuotaCyclePace(expectedUsed, delta, rate);
        }

        var remainingHours = (reset - capturedAt).TotalHours;
        double? safeMultiplier = remainingHours > 0
            ? (remaining / remainingHours) / rate
            : null;
        if (safeMultiplier is not null && !double.IsFinite(safeMultiplier.Value))
        {
            safeMultiplier = null;
        }

        return new QuotaCyclePace(
            expectedUsed,
            delta,
            rate,
            projectedAt,
            projectedAt >= reset,
            safeMultiplier);
    }

    private static double RegressionSlope(IReadOnlyList<QuotaRateSample> samples)
    {
        if (samples.Count < 2) return 0;
        var start = samples[0].CapturedAt;
        var meanX = samples.Average(sample => (sample.CapturedAt - start).TotalHours);
        var meanY = samples.Average(sample => sample.UsedPercent);
        var numerator = 0d;
        var denominator = 0d;
        foreach (var sample in samples)
        {
            var x = (sample.CapturedAt - start).TotalHours - meanX;
            numerator += x * (sample.UsedPercent - meanY);
            denominator += x * x;
        }
        return denominator <= 0 ? 0 : numerator / denominator;
    }

    private static double BlendWithRecent(
        double baselineRate,
        TrendCalculation recentTrend)
    {
        var recentRate = recentTrend.PositiveRate;
        var accelerating = recentRate > baselineRate;
        var recentWeight = (recentTrend.Confidence, accelerating) switch
        {
            (QuotaTrendConfidence.Stable, true) => AcceleratingRecentWeight,
            (QuotaTrendConfidence.Stable, false) => SlowingRecentWeight,
            (QuotaTrendConfidence.Normal, true) => NormalAcceleratingRecentWeight,
            (QuotaTrendConfidence.Normal, false) => NormalSlowingRecentWeight,
            (QuotaTrendConfidence.Provisional, true) => ProvisionalAcceleratingRecentWeight,
            _ => ProvisionalSlowingRecentWeight,
        };
        return baselineRate + (recentRate - baselineRate) * recentWeight;
    }

    private static TrendCalculation? CalculateTrend(
        IReadOnlyList<QuotaRateSample> series,
        QuotaRateSample latest,
        TrendWindow trendWindow)
    {
        var baselines = series
            .Take(series.Count - 1)
            .Select(sample => (Sample: sample, Span: latest.CapturedAt - sample.CapturedAt))
            .Where(candidate =>
                candidate.Span >= trendWindow.MinimumSpan
                && candidate.Span <= trendWindow.MaximumSpan
                && SameCycle(candidate.Sample, latest))
            .ToArray();
        if (baselines.Length == 0) return null;

        var baseline = baselines.MinBy(candidate =>
            Math.Abs((candidate.Span - trendWindow.TargetSpan).TotalSeconds));
        var segment = series
            .Where(sample =>
                sample.CapturedAt >= baseline.Sample.CapturedAt
                && sample.CapturedAt <= latest.CapturedAt
                && SameCycle(sample, latest))
            .OrderBy(sample => sample.CapturedAt)
            .ToArray();
        if (segment.Length < trendWindow.MinimumPoints) return null;

        var delta = latest.UsedPercent - baseline.Sample.UsedPercent;
        var rate = trendWindow.UseEndpointAverage
            ? delta / baseline.Span.TotalHours
            : RegressionSlope(segment);
        return new TrendCalculation(
            baseline.Span,
            delta,
            double.IsFinite(rate) ? rate : 0,
            trendWindow.MinimumDelta,
            trendWindow.Confidence);
    }

    private static TrendCalculation? CalculateFallbackTrend(
        IReadOnlyList<QuotaRateSample> series,
        QuotaRateSample latest,
        TimeSpan? maximumSpan = null)
    {
        var maximumFallbackSpan = maximumSpan ?? RecentFallbackAge;
        var candidates = series
            .Take(series.Count - 1)
            .Select(sample => (
                Sample: sample,
                Span: latest.CapturedAt - sample.CapturedAt,
                Delta: latest.UsedPercent - sample.UsedPercent))
            .Where(candidate =>
                candidate.Span >= MinimumFallbackTrendSpan
                && candidate.Span <= maximumFallbackSpan
                && candidate.Delta >= .5
                && SameCycle(candidate.Sample, latest))
            .ToArray();
        if (candidates.Length == 0) return null;
        var baseline = candidates.MaxBy(candidate => candidate.Span);

        return new TrendCalculation(
            baseline.Span,
            baseline.Delta,
            baseline.Delta / baseline.Span.TotalHours,
            .5,
            QuotaTrendConfidence.Provisional);
    }

    private static QuotaPaceEstimate ProjectTrend(
        double percentPerHour,
        TimeSpan observedSpan,
        QuotaTrendConfidence confidence,
        QuotaRateSample latest,
        QuotaWindow window,
        QuotaCyclePace? cycle,
        DateTimeOffset validUntil)
    {
        if (!double.IsFinite(percentPerHour) || percentPerHour <= 0)
        {
            return new QuotaPaceEstimate(
                QuotaPaceStatus.NoMeaningfulConsumption,
                cycle,
                ObservedSpan: observedSpan,
                ValidUntil: validUntil);
        }

        var remaining = Math.Clamp(100 - latest.UsedPercent, 0, 100);
        DateTimeOffset projectedAt;
        try
        {
            projectedAt = latest.CapturedAt.AddHours(remaining / percentPerHour);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new QuotaPaceEstimate(
                QuotaPaceStatus.NoMeaningfulConsumption,
                cycle,
                ObservedSpan: observedSpan,
                ValidUntil: validUntil);
        }

        var resetsFirst = window.ResetsAt is { } reset
            && reset > latest.CapturedAt
            && projectedAt >= reset;
        var recent = new QuotaRecentTrend(
            observedSpan,
            percentPerHour,
            projectedAt,
            resetsFirst,
            confidence);
        return new QuotaPaceEstimate(
            resetsFirst
                ? QuotaPaceStatus.ResetsBeforeExhaustion
                : QuotaPaceStatus.ProjectedExhaustion,
            cycle,
            recent,
            observedSpan,
            validUntil);
    }

    private bool Trim(DateTimeOffset now)
    {
        var before = _samples.Count;
        var cutoff = now - HistoryRetention;
        var retained = ApplySampleLimits(_samples
            .Where(sample =>
                sample.CapturedAt >= cutoff
                && sample.CapturedAt <= now.AddMinutes(1)));
        var changed = before != retained.Length || !_samples.SequenceEqual(retained);
        _samples.Clear();
        _samples.AddRange(retained);
        return changed;
    }

    private static QuotaRateSample[] ApplySampleLimits(IEnumerable<QuotaRateSample> samples) =>
        Deduplicate(samples)
            .Where(IsStructurallyValid)
            .GroupBy(SeriesIdentity)
            .SelectMany(group => CompactImported(group)
                .OrderByDescending(sample => sample.CapturedAt)
                .Take(MaximumSamplesPerSeries))
            .OrderByDescending(sample => sample.CapturedAt)
            .Take(MaximumSamples)
            .OrderBy(sample => sample.CapturedAt)
            .ToArray();

    private static IEnumerable<QuotaRateSample> CompactImported(
        IEnumerable<QuotaRateSample> samples)
    {
        var source = samples.ToArray();
        var live = source.Where(sample => sample.Source == QuotaRateSampleSource.Live);
        var imported = source
            .Where(sample => sample.Source == QuotaRateSampleSource.CodexRollout)
            .GroupBy(sample => sample.CapturedAt.UtcDateTime.Ticks / ImportedSampleInterval.Ticks)
            .SelectMany(CompactImportedBucket)
            .ToArray();
        return live.Concat(imported);
    }

    private static IEnumerable<QuotaRateSample> CompactImportedBucket(
        IEnumerable<QuotaRateSample> samples)
    {
        var source = samples.ToArray();
        var withoutReset = source.Where(sample => sample.ResetsAt is null).ToArray();
        if (withoutReset.Length > 0)
        {
            yield return withoutReset.MaxBy(sample => sample.CapturedAt)!;
        }

        var cluster = new List<QuotaRateSample>();
        QuotaRateSample? previous = null;
        foreach (var sample in source
                     .Where(sample => sample.ResetsAt is not null)
                     .OrderBy(sample => sample.ResetsAt))
        {
            if (previous is not null
                && sample.ResetsAt!.Value - previous.ResetsAt!.Value > ResetTolerance)
            {
                yield return cluster.MaxBy(candidate => candidate.CapturedAt)!;
                cluster.Clear();
            }

            cluster.Add(sample);
            previous = sample;
        }

        if (cluster.Count > 0)
        {
            yield return cluster.MaxBy(sample => sample.CapturedAt)!;
        }
    }

    private static bool StartsNewCycle(QuotaRateSample previous, QuotaRateSample current)
    {
        if (current.UsedPercent < previous.UsedPercent - ResetDropThreshold) return true;
        if ((previous.ResetsAt is null) != (current.ResetsAt is null)) return true;
        return previous.ResetsAt is { } previousReset
            && current.ResetsAt is { } currentReset
            && (currentReset - previousReset).Duration() > ResetTolerance;
    }

    private static bool IsStructurallyValid(QuotaRateSample sample) =>
        !string.IsNullOrWhiteSpace(sample.CardKey)
        && !string.IsNullOrWhiteSpace(sample.WindowLabel)
        && sample.DurationTicks > 0
        && double.IsFinite(sample.UsedPercent)
        && sample.UsedPercent is >= 0 and <= 100
        && Enum.IsDefined(sample.Source);

    private static IEnumerable<QuotaRateSample> Deduplicate(IEnumerable<QuotaRateSample> samples) =>
        samples
            .GroupBy(sample => (
                Series: SeriesIdentity(sample),
                CapturedTicks: sample.CapturedAt.Ticks,
                OffsetTicks: sample.CapturedAt.Offset.Ticks))
            .Select(group => group
                .OrderBy(sample => sample.Source == QuotaRateSampleSource.Live ? 0 : 1)
                .First());

    private static QuotaSeriesKey SeriesIdentity(QuotaRateSample sample) =>
        new(sample.CardKey, sample.WindowLabel, sample.DurationTicks);

    private static QuotaSeriesKey SeriesIdentity(QuotaCard card, QuotaWindow window) =>
        new(card.Key, NormalizeWindowLabel(window), window.Duration.Ticks);

    private readonly record struct QuotaSeriesKey(
        string CardKey,
        string WindowLabel,
        long DurationTicks);

    private sealed record TrendWindow(
        TimeSpan TargetSpan,
        TimeSpan MinimumSpan,
        TimeSpan MaximumSpan,
        int MinimumPoints,
        double MinimumDelta,
        QuotaTrendConfidence Confidence,
        bool UseEndpointAverage = false);

    private sealed record TrendCalculation(
        TimeSpan ObservedSpan,
        double Delta,
        double PercentPerHour,
        double MinimumDelta,
        QuotaTrendConfidence Confidence)
    {
        public double PositiveRate => Math.Max(0, PercentPerHour);
        public bool Meaningful => Delta >= MinimumDelta && PositiveRate > 0;
    }
}
