using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>One dock ring for the Settings show/hide list (provider id, or "card:" + Codex/Claude card key).</summary>
internal sealed record DockVisibilityEntry(string Id, string Title, string? Detail, Image? Icon, bool Visible);

/// <summary>Per-provider/account show/hide (edited in Settings only).</summary>
internal sealed partial class BarForm
{
    internal IReadOnlyCollection<string> HiddenDockItems => _hiddenDockItems;

    internal void SetHiddenDockItems(IEnumerable<string>? hidden)
    {
        var next = new HashSet<string>(AppSettings.CopyHiddenDockItems(hidden), StringComparer.Ordinal);
        if (next.SetEquals(_hiddenDockItems)) return;
        _hiddenDockItems = next;
        if (IsHandleCreated) ApplySnapshotLayout();
        Invalidate();
    }

    /// <summary>Every dock ring (hidden ones included), in dock order, for the show/hide list.</summary>
    internal IReadOnlyList<DockVisibilityEntry> DockVisibilityEntries() => BuildGlassItems()
        .Select(item => new DockVisibilityEntry(
            item.Id,
            item.Name,
            string.IsNullOrWhiteSpace(item.Ring.AccountLabel) || item.Ring.AccountLabel == item.Name ? null : item.Ring.AccountLabel,
            item.Logo ?? item.Icon,
            !_hiddenDockItems.Contains(item.Id)))
        .GroupBy(entry => entry.Id, StringComparer.Ordinal)
        .Select(group => group.First())
        .ToArray();
}
