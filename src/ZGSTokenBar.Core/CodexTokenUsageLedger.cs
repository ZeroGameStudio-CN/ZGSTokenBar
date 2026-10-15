namespace ZGSTokenBar.Core;

public sealed class CodexTokenUsageLedger
{
    private readonly AppSettingsStore _store;
    private readonly Func<IReadOnlyList<string>> _homes;
    private readonly object _sync = new();
    private CodexTokenUsageIndex? _pending;
    private CodexTokenUsageSummary? _summary;

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
            return _summary;
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
                _summary = committed.Summary;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep both the useful observation and its uncommitted sessions. A later
                // refresh retries the save even if no source file changes or still exists.
                if (observed is not null) _summary = observed.Summary;
            }
            return _summary;
        }
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
            Files = files.Values.OrderBy(file => file.Key, StringComparer.Ordinal).ToList(),
        };
    }

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
