namespace ZGSTokenBar.App;

/// <summary>
/// One UI-thread timer that drives every periodic refresh source (quota limits, usage-board
/// providers, Router monitor). Each source keeps its own interval and optional failure backoff;
/// the timer is re-armed for the earliest due source, so idle sources cost no wake-ups.
/// Sources keep their own reentrancy and caching (the quota coordinator's throttle/defer logic,
/// provider-side caches such as Antigravity's 10-minute cache); the scheduler only decides when.
/// </summary>
internal sealed class RefreshScheduler : IDisposable
{
    private const int MinimumDelayMs = 50;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly List<Source> _sources = [];
    private readonly Func<DateTime> _now;
    private bool _disposed;

    public RefreshScheduler(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _timer.Tick += (_, _) => RunDue();
    }

    /// <summary>
    /// Adds a source. <paramref name="run"/> returns false on a failure that should back off:
    /// the next attempt waits interval × 2^failures, capped at <paramref name="maxBackoff"/>
    /// (no backoff when null). A run still in flight is never started twice.
    /// </summary>
    public void Add(string name, Func<TimeSpan> interval, Func<Task<bool>> run, TimeSpan? firstDelay = null, TimeSpan? maxBackoff = null)
    {
        if (_sources.Any(source => source.Name == name)) throw new InvalidOperationException($"Refresh source '{name}' already exists.");
        _sources.Add(new Source(name, interval, run, maxBackoff) { Due = _now() + (firstDelay ?? interval()) });
        Arm();
    }

    /// <summary>Stops a source (its owner was disposed).</summary>
    public void Remove(string name)
    {
        _sources.RemoveAll(source => source.Name == name);
        if (_disposed) return;
        if (_sources.Count == 0) _timer.Stop();
        else Arm();
    }

    /// <summary>Restarts a source's countdown from now (e.g. after its interval setting changed).</summary>
    public void Reschedule(string name)
    {
        if (Find(name) is not { } source) return;
        source.Failures = 0;
        source.Due = _now() + source.Interval();
        Arm();
    }

    /// <summary>Next due time per source, for diagnostics and tests.</summary>
    internal IReadOnlyDictionary<string, DateTime> DueTimes => _sources.ToDictionary(source => source.Name, source => source.Due);

    internal int Failures(string name) => Find(name)?.Failures ?? 0;

    /// <summary>Runs every due source; public for tests (the timer calls it on the UI thread).</summary>
    internal void RunDue()
    {
        if (_disposed) return;
        _timer.Stop();
        var now = _now();
        foreach (var source in _sources.Where(source => source.Due <= now).ToArray())
        {
            // Periodic like the old per-source timers: the next slot counts from this start.
            source.Due = now + source.Interval();
            if (source.Running) continue;
            _ = RunAsync(source);
        }
        Arm();
    }

    private async Task RunAsync(Source source)
    {
        source.Running = true;
        var ok = true;
        try { ok = await source.Run().ConfigureAwait(true); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ok = false; }
        finally { source.Running = false; }
        if (_disposed || !_sources.Contains(source)) return;
        if (ok || source.MaxBackoff is not { } cap)
        {
            source.Failures = 0;
            return;
        }
        source.Failures = Math.Min(source.Failures + 1, 16);
        var interval = source.Interval();
        var delay = TimeSpan.FromTicks(Math.Min(cap.Ticks, interval.Ticks * (1L << Math.Min(source.Failures, 20))));
        source.Due = _now() + (delay < interval ? interval : delay);
        Arm();
    }

    private void Arm()
    {
        if (_disposed || _sources.Count == 0) return;
        var next = _sources.Min(source => source.Due);
        var delay = (int)Math.Clamp((next - _now()).TotalMilliseconds, MinimumDelayMs, int.MaxValue);
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private Source? Find(string name) => _sources.FirstOrDefault(source => source.Name == name);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }

    private sealed class Source(string name, Func<TimeSpan> interval, Func<Task<bool>> run, TimeSpan? maxBackoff)
    {
        public string Name { get; } = name;
        public Func<TimeSpan> Interval { get; } = interval;
        public Func<Task<bool>> Run { get; } = run;
        public TimeSpan? MaxBackoff { get; } = maxBackoff;
        public DateTime Due { get; set; }
        public bool Running { get; set; }
        public int Failures { get; set; }
    }
}
