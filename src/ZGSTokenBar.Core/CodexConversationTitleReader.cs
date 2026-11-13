using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

internal sealed class CodexConversationTitleReader
{
    private const int MaximumIndexBytes = 2 * 1024 * 1024;
    private readonly Dictionary<string, IndexSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal CodexTokenUsageSummary Apply(CodexTokenUsageSummary summary, IReadOnlyList<string> homes,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? sessionPaths = null)
    {
        var sources = new List<IndexSnapshot>(4);
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in homes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedHome = CockpitCodexInstanceActivity.NormalizeDirectory(home) ?? home;
            var path = Path.Combine(home, "session_index.jsonl");
            retained.Add(path);
            _cache.TryGetValue(path, out var cached);
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                if (cached is null || file.Length != cached.Length || file.LastWriteTimeUtc.Ticks != cached.WriteTicks)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var length = stream.Length;
                    var offset = Math.Max(0, length - MaximumIndexBytes);
                    stream.Position = Math.Max(0, offset - 1);
                    var skipFirst = offset > 0 && stream.ReadByte() != '\n';
                    stream.Position = offset;
                    var bytes = new byte[(int)(length - offset)];
                    stream.ReadExactly(bytes);
                    var lines = Encoding.UTF8.GetString(bytes).Split('\n');
                    var entries = new Dictionary<string, TitleEntry>(StringComparer.Ordinal);
                    // Only complete metadata lines are published; a partial append retries next refresh.
                    for (var index = skipFirst ? 1 : 0; index < lines.Length - 1; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var document = JsonDocument.Parse(lines[index]);
                            var root = document.RootElement;
                            var id = root.StringProperty("id");
                            var title = root.StringProperty("thread_name");
                            if (string.IsNullOrWhiteSpace(id) || id.Length > 128
                                || string.IsNullOrWhiteSpace(title) || title.Length > 4096
                                || title.Any(value => char.IsControl(value) && !char.IsWhiteSpace(value))) continue;
                            title = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                            DateTimeOffset.TryParse(root.StringProperty("updated_at"), out var updatedAt);
                            var entry = new TitleEntry(title, updatedAt, normalizedHome);
                            if (!entries.TryGetValue(id, out var prior) || entry.UpdatedAt >= prior.UpdatedAt)
                                entries[id] = entry;
                        }
                        catch (JsonException) { }
                    }
                    cached = new(length, file.LastWriteTimeUtc.Ticks, entries);
                    _cache[path] = cached;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            if (cached is not null) sources.Add(cached);
        }
        // The merged title map only changes when one of the per-home snapshots does.
        var titles = _mergedTitles;
        if (titles is null || !sources.SequenceEqual(_mergedSources, ReferenceEqualityComparer.Instance))
        {
            titles = new Dictionary<string, TitleEntry>(StringComparer.Ordinal);
            foreach (var source in sources)
                foreach (var (id, entry) in source.Titles)
                    if (!titles.TryGetValue(id, out var prior) || entry.UpdatedAt >= prior.UpdatedAt)
                        titles[id] = entry;
            _mergedTitles = titles;
            _mergedSources = sources.ToArray();
        }
        var pathHomes = _pathHomes;
        if (sessionPaths is { Count: > 0 } && !ReferenceEquals(sessionPaths, _lastSessionPaths))
        {
            pathHomes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sessionPath in sessionPaths)
            {
                if (CodexConversationUsageReader.SessionCodexHome(sessionPath) is { } sessionHome)
                    pathHomes.TryAdd(CodexConversationUsageReader.SessionId(sessionPath), sessionHome);
            }
            _pathHomes = pathHomes;
            _lastSessionPaths = sessionPaths;
        }
        foreach (var path in _cache.Keys.Where(path => !retained.Contains(path)).ToArray()) _cache.Remove(path);
        CodexConversationTurn WithTitle(CodexConversationTurn turn)
        {
            var entry = titles.GetValueOrDefault(turn.SessionId);
            var title = entry?.Title;
            var home = entry?.Home ?? pathHomes?.GetValueOrDefault(turn.SessionId) ?? turn.CodexHome;
            return string.Equals(turn.Title, title, StringComparison.Ordinal)
                && string.Equals(turn.CodexHome, home, StringComparison.OrdinalIgnoreCase)
                ? turn
                : turn with { Title = title, CodexHome = home };
        }
        return summary with
        {
            RecentTurns = WithTitles(summary.RecentTurns, WithTitle, 0),
            ConversationHistory = WithTitles(summary.ConversationHistory, WithTitle, 1),
        };
    }

    /// <summary>
    /// Runs every second over up to 500 history turns, so an already-titled list is returned
    /// as-is (or reused from the previous call) instead of being copied.
    /// </summary>
    private IReadOnlyList<CodexConversationTurn> WithTitles(
        IReadOnlyList<CodexConversationTurn> turns,
        Func<CodexConversationTurn, CodexConversationTurn> withTitle,
        int slot)
    {
        CodexConversationTurn[]? copy = null;
        for (var index = 0; index < turns.Count; index++)
        {
            var titled = withTitle(turns[index]);
            if (copy is null)
            {
                if (ReferenceEquals(titled, turns[index])) continue;
                copy = new CodexConversationTurn[turns.Count];
                for (var prior = 0; prior < index; prior++) copy[prior] = turns[prior];
            }
            copy[index] = titled;
        }
        if (copy is null) return turns;
        if (_lastTitled[slot] is { } last && last.Length == copy.Length && last.AsSpan().SequenceEqual(copy)) return last;
        return _lastTitled[slot] = copy;
    }

    private readonly CodexConversationTurn[]?[] _lastTitled = new CodexConversationTurn[]?[2];

    private Dictionary<string, TitleEntry>? _mergedTitles;
    private IndexSnapshot[] _mergedSources = [];
    private Dictionary<string, string>? _pathHomes;
    private IReadOnlyList<string>? _lastSessionPaths;

    private sealed record TitleEntry(string Title, DateTimeOffset UpdatedAt, string Home);
    private sealed record IndexSnapshot(long Length, long WriteTicks, IReadOnlyDictionary<string, TitleEntry> Titles);
}
