using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record CodexConversationTurn(
    string SessionId,
    string TurnId,
    string? Model,
    string? Effort,
    DateTimeOffset StartedAt,
    DateTimeOffset ObservedAt,
    long OutputTokens,
    double ElapsedSeconds,
    bool Completed,
    bool UsageComplete)
{
    // Whole-turn wall time includes reasoning, tool execution and waiting; this is not decode speed.
    public double? OutputTokensPerSecond => UsageComplete && ElapsedSeconds > 0
        ? OutputTokens / ElapsedSeconds : null;
}

public sealed class CodexConversationUsageReader
{
    internal const int MaximumSessions = 12;
    private const int TailBytes = 2 * 1024 * 1024;
    private readonly Dictionary<string, CachedFile> _cache = new(StringComparer.OrdinalIgnoreCase);
    internal long ParsedFiles { get; private set; }

    public IReadOnlyList<CodexConversationTurn> Refresh(
        IReadOnlyList<string> paths, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var turns = new List<CodexConversationTurn>();
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Take(MaximumSessions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            retained.Add(path);
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                if (!_cache.TryGetValue(path, out var cached)
                    || cached.Length != file.Length || cached.WriteTicks != file.LastWriteTimeUtc.Ticks)
                {
                    var turn = Read(path, now, cancellationToken);
                    cached = new(file.Length, file.LastWriteTimeUtc.Ticks, turn);
                    _cache[path] = cached;
                    ParsedFiles++;
                }
                if (cached.Turn is { } latest)
                {
                    if (!latest.Completed)
                        latest = latest with { ElapsedSeconds = Math.Max(0, (now - latest.StartedAt).TotalSeconds) };
                    turns.Add(latest);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A busy or truncated session contributes no invented speed and never blocks the ledger.
            }
        }
        foreach (var key in _cache.Keys.Where(key => !retained.Contains(key)).ToArray()) _cache.Remove(key);
        return turns.OrderByDescending(turn => turn.ObservedAt).Take(MaximumSessions).ToArray();
    }

    private static CodexConversationTurn? Read(string path, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var offset = Math.Max(0, length - TailBytes);
        stream.Position = offset;
        var bytes = new byte[(int)(length - offset)];
        var read = 0;
        while (read < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(bytes.AsSpan(read));
            if (count == 0) break;
            read += count;
        }
        var span = bytes.AsSpan(0, read);
        if (offset > 0)
        {
            var newline = span.IndexOf((byte)'\n');
            if (newline < 0) return null;
            span = span[(newline + 1)..];
        }
        var finalNewline = span.LastIndexOf((byte)'\n');
        if (finalNewline < 0) return null;
        var lines = Encoding.UTF8.GetString(span[..(finalNewline + 1)]).Split('\n');
        long? previousOutput = null;
        TurnState? active = null;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0 || line.Length > CodexTokenUsageReader.MaximumLineBytes
                || (!line.Contains("\"event_msg\"", StringComparison.Ordinal)
                    && !line.Contains("\"turn_context\"", StringComparison.Ordinal))) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!DateTimeOffset.TryParse(root.StringProperty("timestamp"),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    || at > now.AddMinutes(5) || root.ObjectProperty("payload") is not { } payload) continue;
                if (root.StringProperty("type") == "turn_context")
                {
                    if (active is not null && payload.StringProperty("turn_id") == active.Id)
                    {
                        active.Model = payload.StringProperty("model");
                        active.Effort = payload.StringProperty("effort");
                    }
                    continue;
                }
                if (root.StringProperty("type") != "event_msg") continue;
                switch (payload.StringProperty("type"))
                {
                    case "task_started":
                        if (payload.StringProperty("turn_id") is { Length: > 0 } id)
                            active = new(id, at);
                        break;
                    case "token_count":
                        if (!CodexTokenUsageReader.TryParseLine(line, out var usage)
                            || usage?.OutputTokens is not { } output) break;
                        if (active is not null)
                        {
                            var delta = previousOutput is { } previous && output >= previous
                                ? output - previous : usage.LastOutputTokens;
                            if (delta is >= 0 && delta <= long.MaxValue - active.Output)
                                active.Output += delta.Value;
                            else active.CompleteUsage = false;
                            active.ObservedAt = at;
                        }
                        previousOutput = output;
                        break;
                    case "task_complete":
                        if (active is not null && payload.StringProperty("turn_id") == active.Id)
                        {
                            active.Completed = true;
                            active.EndedAt = at;
                            active.ObservedAt = at;
                        }
                        break;
                    case "turn_aborted":
                        // No terminal timing contract: omit partial/interrupted turns from measured results.
                        active = null;
                        break;
                }
            }
            catch (JsonException) { }
        }
        if (active is null || active.Output == 0 || active.ObservedAt < active.StartedAt) return null;
        var name = Path.GetFileNameWithoutExtension(path);
        var sessionId = name.Length >= 36 && Guid.TryParse(name[^36..], out _) ? name[^36..] : "session";
        return new(sessionId, active.Id, active.Model, active.Effort,
            active.StartedAt, active.ObservedAt, active.Output,
            Math.Max(0, ((active.EndedAt ?? now) - active.StartedAt).TotalSeconds),
            active.Completed, active.CompleteUsage);
    }

    private sealed record CachedFile(long Length, long WriteTicks, CodexConversationTurn? Turn);
    private sealed class TurnState(string id, DateTimeOffset startedAt)
    {
        public string Id = id;
        public DateTimeOffset StartedAt = startedAt;
        public DateTimeOffset ObservedAt = startedAt;
        public string? Model;
        public string? Effort;
        public long Output;
        public bool Completed;
        public DateTimeOffset? EndedAt;
        public bool CompleteUsage = true;
    }
}
