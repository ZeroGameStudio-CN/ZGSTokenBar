using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class ConversationPresentationCache
{
    private CodexConversationTurn[] _source = [];
    private (string SessionId, string TurnId)[] _visibleKeys = [];
    private DateOnly _date;
    private string? _timeZone;
    private DateTimeOffset _nextEligibilityChange = DateTimeOffset.MaxValue;
    private DateTimeOffset _lastObservedAt;

    public CodexConversationView? View { get; private set; }
    public IReadOnlyList<CodexModelSpeed> ModelSpeeds { get; private set; } = [];
    public IReadOnlyList<CodexModelSpeedGroup> ModelGroups { get; private set; } = [];
    public int RebuildCount { get; private set; }

    public void Update(CodexTokenUsageSummary? usage, CodexConversationQuery query,
        DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var source = usage?.ConversationTurns ?? [];
        var displayed = CodexConversationDisplay.Select(source, usage?.LiveOutputRate, now);
        var byKey = new Dictionary<(string SessionId, string TurnId), CodexConversationTurn>();
        var unique = true;
        foreach (var turn in displayed) unique &= byKey.TryAdd((turn.SessionId, turn.TurnId), turn);

        var rebuild = View is null || View.Query != query || date != _date || _timeZone != timeZone.Id
            || now < _lastObservedAt || now >= _nextEligibilityChange || query.SortColumn is 8 or 10 || !unique
            || !SameSource(source) || !_visibleKeys.SequenceEqual(displayed.Select(turn => (turn.SessionId, turn.TurnId)));
        if (rebuild)
        {
            View = CodexConversationView.CreateFromRows(displayed, query, now, timeZone);
            ModelSpeeds = CodexConversationSpeeds.BuildRows(View.Rows, now);
            ModelGroups = CodexConversationSpeeds.GroupModels(ModelSpeeds);
            _visibleKeys = displayed.Select(turn => (turn.SessionId, turn.TurnId)).ToArray();
            _nextEligibilityChange = source.Where(turn => turn.ObservedAt > now.AddMinutes(5))
                .Select(turn => turn.ObservedAt.AddMinutes(-5)).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
            RebuildCount++;
        }
        else
        {
            // Refresh live cells and running durations without sorting or regrouping unchanged history.
            var previous = View!;
            View = previous with { Rows = previous.Rows.Select(turn => byKey[(turn.SessionId, turn.TurnId)]).ToArray() };
        }
        _source = source.ToArray();
        _date = date;
        _timeZone = timeZone.Id;
        _lastObservedAt = now;
    }

    private bool SameSource(IReadOnlyList<CodexConversationTurn> source)
    {
        if (_source.Length != source.Count) return false;
        for (var index = 0; index < source.Count; index++)
        {
            var previous = _source[index];
            var current = source[index];
            if (ReferenceEquals(previous, current) || previous == current) continue;
            if (previous != (current with
                {
                    LiveRate = previous.LiveRate,
                    ElapsedSeconds = !previous.IsTerminal && !current.IsTerminal
                        ? previous.ElapsedSeconds : current.ElapsedSeconds,
                })) return false;
        }
        return true;
    }
}
