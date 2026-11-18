namespace ZGSTokenBar.App;

internal readonly record struct RuntimeHealthReading(string Check, bool? Healthy, long Version = 0);

/// <summary>Debounces local observations and reports one warning per continuing incident.</summary>
internal sealed class RuntimeHealthCheck
{
    internal const long IntervalMs = 60_000;
    private const long NotificationGapMs = 10 * IntervalMs;
    private readonly Dictionary<string, Incident> _incidents = new(StringComparer.Ordinal);
    private long? _lastSample;
    private long? _lastNotification;

    internal IReadOnlyList<string> Issues { get; private set; } = [];

    internal IReadOnlyList<string> Observe(long now, IReadOnlyList<RuntimeHealthReading> readings, bool notify = true)
    {
        if (_lastSample is { } previous && (now < previous || now - previous > 2 * IntervalMs)) ResetPending();
        _lastSample = now;

        var confirmed = new List<string>();
        foreach (var (check, healthy, version) in readings)
        {
            if (healthy is not false)
            {
                if (healthy is true || !_incidents.TryGetValue(check, out var skipped) || !skipped.Notified)
                    _incidents.Remove(check);
                continue;
            }
            if (!_incidents.TryGetValue(check, out var issue))
                _incidents[check] = issue = new Incident(now, version);
            else if (version == 0 || issue.Version != version)
            {
                issue.Failures++;
                issue.Version = version;
            }
            if (issue.Failures >= 3 && now - issue.Since >= 2 * IntervalMs) confirmed.Add(check);
        }
        Issues = confirmed;
        if (!notify || _lastNotification is { } last && now - last < NotificationGapMs) return [];
        var warnings = confirmed.Where(check => !_incidents[check].Notified).ToArray();
        if (warnings.Length == 0) return [];
        foreach (var check in warnings) _incidents[check].Notified = true;
        _lastNotification = now;
        return warnings;
    }

    internal void ResetPending()
    {
        foreach (var check in _incidents.Where(pair => !pair.Value.Notified).Select(pair => pair.Key).ToArray())
            _incidents.Remove(check);
        Issues = [];
        _lastSample = null;
    }

    private sealed class Incident(long since, long version)
    {
        internal long Since { get; } = since;
        internal long Version { get; set; } = version;
        internal int Failures { get; set; } = 1;
        internal bool Notified { get; set; }
    }
}
