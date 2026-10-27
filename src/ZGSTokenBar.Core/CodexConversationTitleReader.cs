using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

internal sealed class CodexConversationTitleReader
{
    private const int MaximumIndexBytes = 2 * 1024 * 1024;
    private readonly Dictionary<string, IndexSnapshot> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal CodexTokenUsageSummary Apply(CodexTokenUsageSummary summary, IReadOnlyList<string> homes,
        CancellationToken cancellationToken = default)
    {
        var titles = new Dictionary<string, TitleEntry>(StringComparer.Ordinal);
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in homes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                            var entry = new TitleEntry(title, updatedAt);
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
            if (cached is null) continue;
            foreach (var (id, entry) in cached.Titles)
                if (!titles.TryGetValue(id, out var prior) || entry.UpdatedAt >= prior.UpdatedAt)
                    titles[id] = entry;
        }
        foreach (var path in _cache.Keys.Where(path => !retained.Contains(path)).ToArray()) _cache.Remove(path);
        CodexConversationTurn WithTitle(CodexConversationTurn turn) => turn with
        {
            Title = titles.GetValueOrDefault(turn.SessionId)?.Title,
        };
        return summary with
        {
            RecentTurns = summary.RecentTurns.Select(WithTitle).ToArray(),
            ConversationHistory = summary.ConversationHistory.Select(WithTitle).ToArray(),
        };
    }

    private sealed record TitleEntry(string Title, DateTimeOffset UpdatedAt);
    private sealed record IndexSnapshot(long Length, long WriteTicks, IReadOnlyDictionary<string, TitleEntry> Titles);
}
