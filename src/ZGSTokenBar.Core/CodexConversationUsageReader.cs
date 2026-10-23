using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record CodexConversationTurn(
    string SessionId, string TurnId, string? Model, string? Effort,
    DateTimeOffset StartedAt, DateTimeOffset ObservedAt, long OutputTokens,
    double ElapsedSeconds, bool Completed, bool UsageComplete)
{
    public string? ProjectName { get; init; }
    public bool Interrupted { get; init; }
    public bool Stale { get; init; }
    public double? FirstTokenSeconds { get; init; }
    public bool IsTerminal => Completed || Interrupted;
    public string State => Interrupted ? "interrupted" : Completed ? "completed" : Stale ? "stale" : "running";
    // Whole-turn wall time includes reasoning, tool execution and waiting; this is not decode speed.
    public double? OutputTokensPerSecond => UsageComplete && !Stale && ElapsedSeconds > 0
        ? OutputTokens / ElapsedSeconds : null;
}

public sealed class CodexConversationUsageReader
{
    internal const int MaximumSessions = 12;
    internal const int ScanBudgetBytes = 8 * 1024 * 1024;
    private readonly Dictionary<string, FileState> _cache = new(StringComparer.OrdinalIgnoreCase);
    internal long ParsedFiles { get; private set; }
    internal long ScannedBytes { get; private set; }
    public int PendingSessions { get; private set; }

    public IReadOnlyList<CodexConversationTurn> Refresh(
        IReadOnlyList<string> paths, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var turns = new List<CodexConversationTurn>();
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PendingSessions = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumSessions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            retained.Add(path);
            _cache.TryGetValue(path, out var state);
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                var changed = state is null || length != state.Length || file.LastWriteTimeUtc.Ticks != state.WriteTicks;
                if (state is null || length < state.Length || length < state.Offset
                    || changed && (length == state.Length || !state.MatchesCheckpoint(stream)))
                {
                    state = new FileState(SessionId(path));
                    _cache[path] = state;
                }
                if (changed || state.Offset < length)
                {
                    state.CaughtUp = false;
                    var consumed = Scan(stream, length, state, now, cancellationToken);
                    ScannedBytes += consumed;
                    ParsedFiles++;
                    state.Length = length;
                    state.WriteTicks = file.LastWriteTimeUtc.Ticks;
                    state.SaveCheckpoint(stream);
                }
                state.CaughtUp = state.Offset >= length;
                if (state.Offset < length)
                {
                    PendingSessions++;
                    continue;
                }
                if (state.ToTurn(now) is { } turn) turns.Add(turn);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (state is { CaughtUp: false }) PendingSessions++;
                else if (state?.ToTurn(now) is { } cached) turns.Add(cached with { Stale = true });
            }
        }
        foreach (var key in _cache.Keys.Where(key => !retained.Contains(key)).ToArray()) _cache.Remove(key);
        return turns.GroupBy(turn => turn.SessionId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(turn => turn.ObservedAt).First())
            .OrderByDescending(turn => turn.ObservedAt).Take(MaximumSessions).ToArray();
    }

    private static int Scan(FileStream stream, long length, FileState state, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        stream.Position = state.Offset;
        var buffer = new byte[64 * 1024];
        var remaining = (int)Math.Min(ScanBudgetBytes, length - state.Offset);
        var consumed = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == '\n')
                {
                    if (!state.LineTooLong && state.Line.Count > 0)
                        state.ProcessLine(Encoding.UTF8.GetString(state.Line.ToArray()), now);
                    state.Line.Clear();
                    state.LineTooLong = false;
                }
                else if (!state.LineTooLong)
                {
                    if (state.Line.Count < CodexTokenUsageReader.MaximumLineBytes) state.Line.Add(value);
                    else
                    {
                        // Oversized tool output is skipped without allocating an unbounded string.
                        state.LineTooLong = true;
                        state.Line.Clear();
                    }
                }
            }
            state.Offset += read;
            consumed += read;
            remaining -= read;
        }
        return consumed;
    }

    private static string SessionId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length >= 36 && Guid.TryParse(name[^36..], out _) ? name[^36..]
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..16].ToLowerInvariant();
    }

    private static DateTimeOffset? Date(JsonElement value, string name) =>
        DateTimeOffset.TryParse(value.StringProperty(name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private sealed class FileState(string sessionId)
    {
        public long Offset;
        public long Length;
        public long WriteTicks;
        public List<byte> Line = [];
        public bool LineTooLong;
        public bool CaughtUp;
        public TurnState? Active;
        private long? _previousOutput;
        private string? _projectName;
        private byte[] _prefix = [];
        private byte[] _checkpoint = [];
        private int _signatureLength;

        public bool MatchesCheckpoint(FileStream stream) => _signatureLength == 0
            || Signature(stream, 0, _signatureLength).SequenceEqual(_prefix)
                && Signature(stream, Offset - _signatureLength, _signatureLength).SequenceEqual(_checkpoint);

        public void SaveCheckpoint(FileStream stream)
        {
            _signatureLength = (int)Math.Min(256, Offset);
            _prefix = Signature(stream, 0, _signatureLength);
            _checkpoint = Signature(stream, Offset - _signatureLength, _signatureLength);
        }

        private static byte[] Signature(FileStream stream, long offset, int count)
        {
            stream.Position = offset;
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            return SHA256.HashData(bytes);
        }

        public void ProcessLine(string line, DateTimeOffset now)
        {
            if (!line.Contains("\"event_msg\"", StringComparison.Ordinal)
                && !line.Contains("\"turn_context\"", StringComparison.Ordinal)
                && !line.Contains("\"session_meta\"", StringComparison.Ordinal)) return;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (Date(root, "timestamp") is not { } at || at > now.AddMinutes(5)
                    || root.ObjectProperty("payload") is not { } payload) return;
                var type = root.StringProperty("type");
                if (type is "session_meta" or "turn_context")
                {
                    if (payload.StringProperty("cwd") is { } cwd)
                    {
                        var name = cwd.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
                        if (!string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl))
                            _projectName = name[..Math.Min(64, name.Length)];
                    }
                    if (type == "turn_context" && Active is { } active && payload.StringProperty("turn_id") == active.Id)
                    {
                        var model = payload.StringProperty("model");
                        if (active.Model is not null && model != active.Model) active.MixedModel = true;
                        active.Model = model;
                        active.Effort = payload.StringProperty("effort");
                    }
                    return;
                }
                if (type != "event_msg") return;
                switch (payload.StringProperty("type"))
                {
                    case "task_started":
                        if (payload.StringProperty("turn_id") is { Length: > 0 } id)
                        {
                            var startedAt = Date(payload, "started_at");
                            Active = new(id, startedAt is { } start && start <= at ? start : at);
                        }
                        break;
                    case "token_count":
                        if (!CodexTokenUsageReader.TryParseLine(line, out var usage)
                            || usage?.OutputTokens is not { } output) break;
                        if (Active is { } current)
                        {
                            var delta = _previousOutput is { } previous && output >= previous
                                ? output - previous : usage.LastOutputTokens;
                            if (delta is >= 0 && delta <= long.MaxValue - current.Output)
                                current.Output += delta.Value;
                            else current.UsageComplete = false;
                            current.ObservedAt = at;
                        }
                        _previousOutput = output;
                        break;
                    case "task_complete":
                    case "turn_aborted":
                        if (Active is not { } terminal
                            || payload.StringProperty("turn_id") is { } endedId && endedId != terminal.Id) break;
                        var end = Date(payload, "completed_at") ?? at;
                        if (end < terminal.StartedAt || end > at.AddMinutes(5)) end = at;
                        terminal.EndedAt = end;
                        terminal.ObservedAt = at;
                        terminal.Interrupted = payload.StringProperty("type") == "turn_aborted";
                        if (payload.NumberProperty("time_to_first_token_ms") is { } first
                            && double.IsFinite(first) && first >= 0 && first <= (end - terminal.StartedAt).TotalMilliseconds)
                            terminal.FirstTokenSeconds = first / 1000;
                        break;
                }
            }
            catch (JsonException)
            {
                if (Active is { } active) active.UsageComplete = false;
            }
        }

        public CodexConversationTurn? ToTurn(DateTimeOffset now)
        {
            if (Active is not { } turn || turn.Output == 0 || turn.ObservedAt < turn.StartedAt) return null;
            return new(sessionId, turn.Id, turn.MixedModel ? null : turn.Model, turn.MixedModel ? null : turn.Effort,
                turn.StartedAt, turn.ObservedAt, turn.Output,
                Math.Max(0, ((turn.EndedAt ?? now) - turn.StartedAt).TotalSeconds),
                turn.EndedAt is not null && !turn.Interrupted, turn.UsageComplete)
            {
                ProjectName = _projectName,
                Interrupted = turn.Interrupted,
                Stale = turn.EndedAt is null && now - turn.ObservedAt > TimeSpan.FromMinutes(2),
                FirstTokenSeconds = turn.FirstTokenSeconds,
            };
        }
    }

    private sealed class TurnState(string id, DateTimeOffset startedAt)
    {
        public string Id = id;
        public DateTimeOffset StartedAt = startedAt;
        public DateTimeOffset ObservedAt = startedAt;
        public string? Model;
        public string? Effort;
        public bool MixedModel;
        public long Output;
        public DateTimeOffset? EndedAt;
        public bool Interrupted;
        public bool UsageComplete = true;
        public double? FirstTokenSeconds;
    }
}
