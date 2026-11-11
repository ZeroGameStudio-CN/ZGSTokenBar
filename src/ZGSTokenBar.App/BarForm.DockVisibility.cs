using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>One dock ring the user switched on or off (provider id, or "card:" + Codex/Claude card key).</summary>
internal sealed record DockItemVisibilityChange(string Id, bool Visible);

/// <summary>Per-provider/account show/hide: the eye chip of the expanded overflow and its popover.</summary>
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

    private void ToggleDockItem(string id)
    {
        var visible = _hiddenDockItems.Contains(id);
        var next = new HashSet<string>(_hiddenDockItems, StringComparer.Ordinal);
        if (visible) next.Remove(id); else next.Add(id);
        _hiddenDockItems = next;
        ApplySnapshotLayout();
        Invalidate();
        _dockVisibilityPopover?.UpdateEntries(DockVisibilityEntries());
        DockItemVisibilityChanged?.Invoke(this, new DockItemVisibilityChange(id, visible));
    }

    private void ShowDockVisibilityPopover(Rectangle anchor)
    {
        HidePopovers();
        _dockVisibilityPopover ??= CreateDockVisibilityPopover();
        _dockVisibilityPopover.ShowFor(this, anchor, DockVisibilityEntries(), _text.IsChinese, _scale);
        _pluginPopover = _dockVisibilityPopover;
        _pluginPopoverId = DockVisibilityPopoverForm.PopoverId;
        _pluginPopoverAnchor = anchor;
        _popoverPinned = true;
        _popoverLeaveStarted = null;
        _popoverMouseWasDown = Control.MouseButtons != MouseButtons.None;
        _popoverEscapeWasDown = IsEscapeDown();
        _popoverHoverTimer.Stop();
        _popoverStateTimer.Start();
    }

    private DockVisibilityPopoverForm CreateDockVisibilityPopover()
    {
        var popover = new DockVisibilityPopoverForm();
        popover.EntryToggled += (_, id) => ToggleDockItem(id);
        return popover;
    }

    private void HideDockVisibilityPopover()
    {
        if (_dockVisibilityPopover is not { Visible: true }) return;
        if (_pluginPopoverId == DockVisibilityPopoverForm.PopoverId) HidePluginPopover();
        else _dockVisibilityPopover.Hide();
    }
}
