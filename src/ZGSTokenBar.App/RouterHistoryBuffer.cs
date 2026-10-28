using System.Text.Json;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

internal sealed record RouterHistorySlice(JsonElement Metadata, IReadOnlyList<JsonElement> Items, int Offset, int Total, DateTimeOffset CapturedAt);

/// <summary>One bounded read-only history window. Never an execution or release authority.</summary>
internal sealed class RouterHistoryBuffer
{
    internal const int Capacity = 100;
    private string? _key;
    private JsonElement _metadata;
    private JsonElement[] _items = [];
    private int _offset, _total;
    private DateTimeOffset _capturedAt;

    internal void Store(string key, JsonElement metadata)
    {
        var items = RouterJson.Rows(metadata, "items").ToArray();
        var offset = RouterJson.Integer(metadata, "offset"); var total = RouterJson.Integer(metadata, "total");
        if (items.Length > Capacity || offset < 0 || total < 0 || items.Length != Math.Min(Capacity, Math.Max(0, total - offset)))
            throw new InvalidDataException("History window exceeds its bound or is incomplete.");
        _key = key; _metadata = metadata; _items = items; _offset = offset; _total = total; _capturedAt = DateTimeOffset.UtcNow;
    }
    internal bool TryRead(string key, int offset, int size, out RouterHistorySlice? slice)
    {
        slice = null;
        if (size < 1 || size > Capacity || offset < 0 || _key != key || offset < _offset
            || offset > Math.Max(_offset, _total) || Math.Min((long)_total, (long)offset + size) > (long)_offset + _items.Length) return false;
        slice = new(_metadata, _items.Skip(offset - _offset).Take(size).ToArray(), offset, _total, _capturedAt);
        return true;
    }
    internal void Clear() { _key = null; _items = []; _metadata = default; }
}
