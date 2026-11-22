using System.Text.Json;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

internal sealed record RouterHistorySlice(JsonElement Metadata, IReadOnlyList<JsonElement> Items, int Offset, int Total, DateTimeOffset CapturedAt);

/// <summary>Bounded LRU read-only history cache across recent filter keys. Never an execution or release authority.</summary>
internal sealed class RouterHistoryBuffer
{
    internal const int Capacity = 100;
    internal const int MaxCachedKeys = 8;

    private sealed record Entry(string Key, JsonElement Metadata, JsonElement[] Items, int Offset, int Total, DateTimeOffset CapturedAt);

    private readonly List<Entry> _entries = new(MaxCachedKeys);

    internal void Store(string key, JsonElement metadata)
    {
        var items = RouterJson.Rows(metadata, "items").ToArray();
        var offset = RouterJson.Integer(metadata, "offset"); var total = RouterJson.Integer(metadata, "total");
        if (items.Length > Capacity || offset < 0 || total < 0 || items.Length != Math.Min(Capacity, Math.Max(0, total - offset)))
            throw new InvalidDataException("History window exceeds its bound or is incomplete.");
        for (var i = _entries.Count - 1; i >= 0; i--)
            if (_entries[i].Key == key) _entries.RemoveAt(i);
        _entries.Add(new Entry(key, metadata, items, offset, total, DateTimeOffset.UtcNow));
        while (_entries.Count > MaxCachedKeys) _entries.RemoveAt(0);
    }

    internal bool TryRead(string key, int offset, int size, out RouterHistorySlice? slice)
    {
        slice = null;
        if (size < 1 || size > Capacity || offset < 0) return false;
        var index = -1;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Key == key) { index = i; break; }
        }
        if (index < 0) return false;
        var entry = _entries[index];
        if (offset < entry.Offset || offset > Math.Max(entry.Offset, entry.Total)
            || Math.Min((long)entry.Total, (long)offset + size) > (long)entry.Offset + entry.Items.Length)
            return false;
        if (index != _entries.Count - 1)
        {
            _entries.RemoveAt(index);
            _entries.Add(entry);
        }
        slice = new(entry.Metadata, entry.Items.Skip(offset - entry.Offset).Take(size).ToArray(), offset, entry.Total, entry.CapturedAt);
        return true;
    }

    internal void Clear() => _entries.Clear();
}
