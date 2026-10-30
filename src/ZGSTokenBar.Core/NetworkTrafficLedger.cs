using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZGSTokenBar.Core;

public sealed record NetworkTrafficTotals(DateTimeOffset Since, long ReceivedBytes, long SentBytes,
    DateTimeOffset SavedAt, bool SaveFailed)
{
    public long TotalBytes => ReceivedBytes + SentBytes;
}

internal sealed record NetworkTrafficState(int SchemaVersion, DateTimeOffset Since, long ReceivedBytes,
    long SentBytes, DateTimeOffset SavedAt, string Writer, long WriterReceivedBytes, long WriterSentBytes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NetworkTrafficState))]
internal partial class NetworkTrafficJsonContext : JsonSerializerContext;

/// <summary>Independent, checkpointed totals of observed adapter deltas; never derives traffic from tokens.</summary>
public sealed class NetworkTrafficLedger
{
    public const string FileName = "network-traffic-history.json";
    private readonly string _path;
    private readonly string _writer = Guid.NewGuid().ToString("N");
    private readonly object _sync = new();
    private NetworkTrafficState? _history;
    private DateTimeOffset? _since;
    private DateTimeOffset _nextSave;
    private long _received, _sent, _savedReceived, _savedSent;
    private bool _savedOnce, _saveFailed;

    public NetworkTrafficLedger(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, FileName);
        try { _history = Parse(File.ReadAllText(_path)); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { _saveFailed = true; }
    }

    public bool SaveFailed { get { lock (_sync) return _saveFailed; } }

    public NetworkTrafficTotals? Observe(long? received, long? sent, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (received is >= 0 && sent is >= 0)
            {
                var nextReceived = checked(_received + received.Value);
                var nextSent = checked(_sent + sent.Value);
                _ = checked(nextReceived + nextSent);
                _received = nextReceived;
                _sent = nextSent;
                _since ??= now;
            }
            if (now >= _nextSave && _since is not null && (!_savedOnce || _received != _savedReceived || _sent != _savedSent))
                FlushCore(now);
            return Snapshot();
        }
    }

    public void Flush(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_since is not null && (!_savedOnce || _received != _savedReceived || _sent != _savedSent))
                FlushCore(now);
        }
    }

    private NetworkTrafficTotals? Snapshot() => _history is null ? null : new(_history.Since,
        checked(_history.ReceivedBytes + _received - _savedReceived),
        checked(_history.SentBytes + _sent - _savedSent), _history.SavedAt, _saveFailed);

    private void FlushCore(DateTimeOffset now)
    {
        _nextSave = now.AddSeconds(30);
        try
        {
            var saved = CredentialSupport.AtomicUpdate(_path, contents =>
            {
                var current = contents is null ? null : Parse(contents);
                if (_savedOnce && current?.Writer != _writer)
                    throw new IOException("Network history changed in another writer.");
                var sameWriter = current?.Writer == _writer;
                var received = checked((current?.ReceivedBytes ?? 0) + _received - (sameWriter ? current!.WriterReceivedBytes : 0));
                var sent = checked((current?.SentBytes ?? 0) + _sent - (sameWriter ? current!.WriterSentBytes : 0));
                _ = checked(received + sent);
                var next = new NetworkTrafficState(1, current?.Since ?? _since!.Value, received, sent, now,
                    _writer, _received, _sent);
                return (JsonSerializer.Serialize(next, NetworkTrafficJsonContext.Default.NetworkTrafficState), next);
            });
            _history = saved;
            _savedReceived = _received;
            _savedSent = _sent;
            _savedOnce = true;
            _saveFailed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or OverflowException)
        { _saveFailed = true; }
    }

    private static NetworkTrafficState Parse(string contents)
    {
        var state = JsonSerializer.Deserialize(contents, NetworkTrafficJsonContext.Default.NetworkTrafficState);
        if (state is null || state.SchemaVersion != 1 || state.Since == default || state.SavedAt < state.Since
            || string.IsNullOrWhiteSpace(state.Writer) || state.ReceivedBytes < 0 || state.SentBytes < 0
            || state.ReceivedBytes > long.MaxValue - state.SentBytes || state.WriterReceivedBytes < 0
            || state.WriterSentBytes < 0 || state.WriterReceivedBytes > state.ReceivedBytes || state.WriterSentBytes > state.SentBytes)
            throw new JsonException("Network history requires recovery before writing.");
        return state;
    }
}
