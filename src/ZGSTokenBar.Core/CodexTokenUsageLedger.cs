namespace ZGSTokenBar.Core;

public sealed class CodexTokenUsageLedger
{
    private readonly AppSettingsStore _store;
    private readonly Func<IReadOnlyList<string>> _homes;
    private readonly object _sync = new();
    private CodexTokenUsageIndex? _pending;
    private CodexTokenUsageSummary? _summary;
    private readonly CodexConversationUsageReader _conversations = new();
    private readonly CodexConversationTitleReader _titles = new();
    private IReadOnlyList<string> _recentSessionPaths = [];

    public CodexTokenUsageLedger(AppSettingsStore store)
        : this(store, () => [CodexQuotaService.CodexHome(), .. CockpitCodexInstanceActivity.ReadTokenUsageHomes()])
    {
    }

    internal CodexTokenUsageLedger(AppSettingsStore store, Func<IReadOnlyList<string>> homes)
    {
        _store = store;
        _homes = homes;
    }

    public CodexTokenUsageSummary? Snapshot(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_pending is null && _store.TryLoadCodexTokenUsageIndex(out var index))
                _summary = new CodexTokenUsageReader(index).Snapshot(now);
            return _summary is null ? null : _titles.Apply(_summary, _homes());
        }
    }

    public CodexTokenUsageSummary? Refresh(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            CodexTokenUsageReadResult? observed = null;
            try
            {
                // Reload and scan while holding the ledger's cross-process write lock.
                // An older desktop/CLI reader must never replace a newer durable index.
                var committed = _store.UpdateCodexTokenUsageIndex(current =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var retrying = _pending is not null;
                    var reader = new CodexTokenUsageReader(MergePending(current, _pending));
                    observed = reader.Refresh(_homes(), now, cancellationToken);
                    if (retrying) observed = observed with { Changed = true };
                    if (observed.Changed) _pending = observed.Index;
                    return observed;
                });
                _pending = null;
                _summary = committed.Summary is null
                    ? null
                    : committed.Summary with { ConversationHistory = committed.Index.ConversationHistory };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep both the useful observation and its uncommitted sessions. A later
                // refresh retries the save even if no source file changes or still exists.
                if (observed is not null) _summary = observed.Summary;
            }
            if (_summary is not null && observed is not null)
            {
                _recentSessionPaths = ConversationPaths(observed.RecentSessionPaths, cancellationToken);
                _summary = _summary with
                {
                    RecentTurns = _conversations.Refresh(_recentSessionPaths, now, cancellationToken),
                    PendingConversationCount = _conversations.PendingSessions,
                    LiveRateFeed = _conversations.LiveRateFeed,
                };
                _summary = PersistConversationHistory(_summary, now);
            }
            return _summary is null ? null : _titles.Apply(_summary, _homes(), cancellationToken);
        }
    }

    public CodexTokenUsageSummary? RefreshConversations(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_summary is null) return null;
            _recentSessionPaths = ConversationPaths(_recentSessionPaths, cancellationToken);
            _summary = _summary with
            {
                RecentTurns = _conversations.Refresh(_recentSessionPaths, now, cancellationToken),
                PendingConversationCount = _conversations.PendingSessions,
                LiveRateFeed = _conversations.LiveRateFeed,
            };
            _summary = PersistConversationHistory(_summary, now);
            return _titles.Apply(_summary, _homes(), cancellationToken);
        }
    }

    private string[] ConversationPaths(IReadOnlyList<string> known, CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        // Resumed turns keep their original date folder. Live discovery has no session-count cap
        // and is independent of the token ledger's bounded historical accounting discovery.
        foreach (var codexHome in _homes().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = Path.Combine(codexHome, "sessions");
            try
            {
                if (Directory.Exists(directory))
                    foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        paths.Add(path);
                    }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return paths.Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .GroupBy(file => file.Name, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(file => file.Length).ThenByDescending(file => file.LastWriteTimeUtc).First())
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName).ToArray();
    }

    private static CodexTokenUsageIndex MergePending(
        CodexTokenUsageIndex current,
        CodexTokenUsageIndex? pending)
    {
        if (pending is null) return current;
        if (current.HistoricalBaselineTokens != pending.HistoricalBaselineTokens
            || current.HistoricalBaselineAt != pending.HistoricalBaselineAt)
            throw new IOException("The Token baseline changed while a save was pending.");

        var files = current.Files.GroupBy(file => file.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var file in pending.Files)
        {
            if (!files.TryGetValue(file.Key, out var saved) || IsNewer(file, saved))
                files[file.Key] = file;
        }
        return new CodexTokenUsageIndex
        {
            HistoricalBaselineTokens = current.HistoricalBaselineTokens,
            HistoricalBaselineAt = current.HistoricalBaselineAt,
            ConversationHistory = current.ConversationHistory,
            Files = files.Values.OrderBy(file => file.Key, StringComparer.Ordinal).ToList(),
        };
    }

    private CodexTokenUsageSummary PersistConversationHistory(
        CodexTokenUsageSummary summary,
        DateTimeOffset now)
    {
        var completed = summary.RecentTurns.Where(turn => turn.IsTerminal && turn.HasRecordedUsage).ToArray();
        if (completed.Length == 0) return summary;

        // Unchanged local history needs no full-ledger transaction. New turns,
        // pending saves and due retention still merge the latest index under lock.
        if (HistoryEquals(summary.ConversationHistory,
                MergeConversationHistory(summary.ConversationHistory, completed, now)))
            return summary;

        try
        {
            var persisted = _store.UpdateCodexTokenUsageIndex(current =>
            {
                var history = MergeConversationHistory(current.ConversationHistory, completed, now);
                if (HistoryEquals(current.ConversationHistory, history))
                    return new CodexTokenUsageReadResult(summary, current, false);

                current.ConversationHistory = history;
                return new CodexTokenUsageReadResult(
                    summary with { ConversationHistory = history },
                    current,
                    true);
            });
            return summary with { ConversationHistory = persisted.Index.ConversationHistory };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return summary;
        }
    }

    private static List<CodexConversationTurn> MergeConversationHistory(
        IReadOnlyList<CodexConversationTurn>? existing,
        IReadOnlyList<CodexConversationTurn> updates,
        DateTimeOffset now)
    {
        var turns = (existing ?? [])
            .Where(turn => turn.IsTerminal)
            .ToDictionary(TurnKey, StringComparer.Ordinal);
        foreach (var turn in updates)
            turns[TurnKey(turn)] = turn with { Stale = false };

        var cutoff = now.AddDays(-CodexTokenUsageIndex.ConversationHistoryRetentionDays);
        return turns.Values
            .Where(turn => turn.ObservedAt >= cutoff)
            .OrderByDescending(turn => turn.ObservedAt)
            .Take(CodexTokenUsageIndex.ConversationHistoryLimit)
            .ToList();
    }

    private static bool HistoryEquals(
        IReadOnlyList<CodexConversationTurn>? left,
        IReadOnlyList<CodexConversationTurn> right) =>
        (left ?? []).SequenceEqual(right);

    private static string TurnKey(CodexConversationTurn turn) =>
        $"{turn.SessionId}\u001f{turn.TurnId}";

    private static bool IsNewer(CodexTokenUsageFileIndex pending, CodexTokenUsageFileIndex saved)
    {
        if (pending.AccountingVersion != saved.AccountingVersion)
            return pending.AccountingVersion > saved.AccountingVersion;
        if (pending.Length != saved.Length) return pending.Length > saved.Length;
        if (pending.LastWriteTimeUtcTicks != saved.LastWriteTimeUtcTicks)
            return pending.LastWriteTimeUtcTicks > saved.LastWriteTimeUtcTicks;
        if (pending.SpendAccountingVersion != saved.SpendAccountingVersion)
            return pending.SpendAccountingVersion > saved.SpendAccountingVersion;
        if (pending.SpendScannedLength != saved.SpendScannedLength)
            return pending.SpendScannedLength.GetValueOrDefault() > saved.SpendScannedLength.GetValueOrDefault();
        return pending.BaselineTokens is not null && saved.BaselineTokens is null;
    }
}
