namespace ZGSTokenBar.App;

/// <summary>Bounds the life of superseded geometry and tracks when a clear position is stable.</summary>
internal sealed class TaskbarRowStability
{
    internal const int ReturnDelayMs = 2000;
    private const int RefreshGraceMs = 500;
    private long? _clearSince;
    private Rectangle _candidate;
    private long _layoutRevision;
    private long? _awaitingLayoutSince;

    internal long LayoutRevision => _layoutRevision;
    internal bool AwaitingLayout => _awaitingLayoutSince.HasValue;

    internal void OnLayoutChanged(long now)
    {
        _awaitingLayoutSince ??= now;
        _layoutRevision++;
        ResetReturn();
    }

    internal bool ConfirmLayout(long revision)
    {
        if (revision != _layoutRevision) return false;
        _awaitingLayoutSince = null;
        return true;
    }

    internal bool CanKeepCurrent(bool sameSize, long now) =>
        _awaitingLayoutSince is not { } since || (sameSize && now - since < RefreshGraceMs);

    internal void ForgetLayout()
    {
        _layoutRevision++;
        _awaitingLayoutSince = null;
        ResetReturn();
    }

    internal void ResetReturn()
    {
        _clearSince = null;
    }

    internal bool CanReturn(Rectangle bounds, long observedAt)
    {
        if (_clearSince is null || bounds != _candidate)
        {
            _candidate = bounds;
            _clearSince = observedAt;
            return false;
        }
        // Only a newer snapshot can prove that the same gap survived the return delay.
        return observedAt - _clearSince.Value >= ReturnDelayMs;
    }
}
