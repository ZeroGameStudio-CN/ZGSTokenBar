namespace ZGSTokenBar.App;

internal sealed partial class IntakeObserverForm
{
    private IntakeItemsView _itemsView;
    private readonly Dictionary<IntakeItemsView, RouterButton> _viewButtons = [];

    private Control CreateViews()
    {
        var row = Flow(); row.AutoSize = true; row.Dock = DockStyle.None;
        foreach (var view in Enum.GetValues<IntakeItemsView>())
        {
            var button = new RouterButton { Text = ViewTitle(view), Selected = view == _itemsView,
                AccessibleName = T("工单视图 ", "Work item view ") + ViewTitle(view) };
            button.Click += async (_, _) =>
            {
                if (!_pinned) RequestPinChange(true);
                await SetItemsViewAsync(view);
            };
            _viewButtons.Add(view, button); row.Controls.Add(button);
        }
        return row;
    }

    internal Task SetItemsViewAsync(IntakeItemsView view)
    {
        if (!Enum.IsDefined(view)) throw new ArgumentOutOfRangeException(nameof(view));
        if (_itemsView == view) return Task.CompletedTask;
        _itemsView = view;
        foreach (var entry in _viewButtons) entry.Value.Selected = entry.Key == view;
        _recent.Rows.Clear(); ClearItem();
        return LoadRecentAsync();
    }

    private string ViewTitle(IntakeItemsView view) => view switch
    {
        IntakeItemsView.Triage => T("待分拣", "Triage"),
        IntakeItemsView.Pending => T("待执行", "Pending"),
        IntakeItemsView.Running => T("运行中", "Running"),
        _ => T("近期", "Recent"),
    };
    private string ViewEmptyTitle() => _itemsView switch
    {
        IntakeItemsView.Triage => T("当前没有待分拣工单", "No items awaiting triage"),
        IntakeItemsView.Pending => T("当前没有待执行工单", "No items awaiting execution"),
        IntakeItemsView.Running => T("当前没有运行中工单", "No running items"),
        _ => T("没有匹配的近期工单", "No matching recent work items"),
    };

    private void SetRecentEmpty(string title, string detail)
    {
        _recent.EmptyTitle = title; _recent.EmptyDetail = detail; _recent.Invalidate();
    }
    private void SetTimelineEmpty(string? title = null, string? detail = null)
    {
        _timeline.EmptyTitle = title ?? T("尚未选择工单", "No work item selected");
        _timeline.EmptyDetail = detail ?? T("单击上方工单后查看时间线", "Select an item above to view its timeline");
        _timeline.Invalidate();
    }
}
