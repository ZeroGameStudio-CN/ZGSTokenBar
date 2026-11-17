using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>The same native monitor host and table controls as Router; no workflow actions.</summary>
internal sealed class IntakeServiceMonitorForm : MonitorPanelForm
{
    private readonly bool _zh;
    private readonly IntakeServiceMonitorClient _client;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly RouterButton _refresh = new() { Icon = RouterButtonIcon.Refresh }, _pin = new() { Icon = RouterButtonIcon.Pin },
        _close = new() { Icon = RouterButtonIcon.Close }, _workItems = new();
    private readonly Label _headline = Label(), _task = Label(), _detail = Label(), _summary = Label(), _freshness = Label(), _status = Label();
    private readonly FlowLayoutPanel _projects = Flow();
    private readonly ContextMenuStrip _projectMenu = new() { ShowImageMargin = false };
    private readonly RouterGrid _progress = IntakeObserverForm.Grid(), _history = IntakeObserverForm.Grid();
    private readonly Dictionary<string, IntakeServiceReading> _last = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unavailable = new(StringComparer.Ordinal);
    private CancellationTokenSource? _request;
    private bool _refreshAgain;
    private string? _project;
    private string _projectSignature = "";
    private readonly bool _observe;
    internal event EventHandler? WorkItemsRequested;
    internal (int Projects, int Recent, int History, int InFlight, string Status) Observation =>
        (_last.Values.SelectMany(r => r.Projects).Distinct().Count(), _history.RowCount, _history.RowCount,
            _last.Values.SelectMany(r => r.Progress).Count(r => r.InFlight), _headline.Text);
    public override bool InteractionOpen => _projectMenu.Visible;

    internal IntakeServiceMonitorForm(bool chinese, IntakeServiceMonitorClient client, bool observe = true)
    {
        _zh = chinese; _client = client; _observe = observe;
        Text = T("工单与分拣", "Work intake");
        SuspendLayout();
        var root = CreatePanelLayout(); Controls.Add(root);
        var brand = new MonitorBrandMark(T("自动分拣", "Intake"), intake: true) { Width = 140, Height = 30 };
        _summary.Width = 180; _freshness.Width = 140;
        _summary.Text = T("服务观测 · 只读", "Service observations · Read only");
        root.Controls.Add(CreateNavigation(CreateHeader(Flow(brand, _summary), Flow(_freshness, _refresh, _pin, _close), 240),
            _projects, T("项目", "Project")), 0, 0);
        _workItems.Text = T("工单详情", "Work items");
        _workItems.Click += (_, _) => { if (_pinned) WorkItemsRequested?.Invoke(this, EventArgs.Empty); };
        _refresh.AccessibleName = T("刷新", "Refresh"); _pin.AccessibleName = T("固定弹层", "Pin"); _close.AccessibleName = T("收起弹层", "Close");
        var current = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 3 };
        current.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 30, 28, 30 }) current.RowStyles.Add(new(SizeType.Absolute, height));
        current.Controls.Add(CreateHeader(_headline, _workItems, 100), 0, 0);
        current.Controls.Add(_task, 0, 1); current.Controls.Add(_detail, 0, 2);
        var surface = RouterSurface.Section(T("当前", "Current")); surface.Controls.Add(current); root.Controls.Add(surface, 0, 1);
        IntakeObserverForm.AddColumn(_progress, T("工单／内容", "Item / title"), 0);
        IntakeObserverForm.AddColumn(_progress, T("项目／操作", "Project / operation"), 160);
        IntakeObserverForm.AddColumn(_progress, T("当前阶段", "Stage"), 180);
        IntakeObserverForm.AddColumn(_history, T("记录时间", "Observed"), 110);
        IntakeObserverForm.AddColumn(_history, T("工单／内容", "Item / title"), 0);
        IntakeObserverForm.AddColumn(_history, T("操作", "Operation"), 100);
        IntakeObserverForm.AddColumn(_history, T("结果／阶段", "Result / stage"), 180);
        _progress.EmptyTitle = T("暂无在途回执", "No in-flight receipts");
        _progress.EmptyDetail = T("有新反馈或返工时自动更新", "New feedback and rework appear here");
        _history.EmptyTitle = T("正在连接分拣服务…", "Connecting to intake service…");
        _history.EmptyDetail = T("历史直接显示，无需先选工单", "History appears without selecting an item");
        root.Controls.Add(Table(T("在途与待核对", "In flight and held records"), _progress), 0, 2);
        root.Controls.Add(Table(T("整体运行历史", "Service history"), _history), 0, 3);
        root.Controls.Add(_status, 0, 4);
        _headline.Text = T("正在连接分拣服务…", "Connecting to intake service…");
        _status.Text = T("只读 · 时间缺失的旧回执显示 —", "Read only · Older receipts without timestamps show —");
        _refresh.Click += async (_, _) => await RefreshAsync();
        _pin.Click += (_, _) => RequestPinChange(!_pinned);
        _close.Click += (_, _) => RequestClose();
        _timer.Tick += async (_, _) => { if (Visible && _request is null) await RefreshAsync(); };
        VisibleChanged += (_, _) =>
        {
            if (!Visible) { _timer.Stop(); _request?.Cancel(); return; }
            if (!_observe) return;
            _timer.Start(); BeginInvoke(new Action(async () => { if (Visible && !IsDisposed) await RefreshAsync(); }));
        };
        SetLogicalFont(_headline, "Segoe UI", UiTokens.FontBody, FontStyle.Bold);
        ApplyPalette(QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        ApplyRows(); ResumeLayout(true);
    }

    internal async Task RefreshAsync()
    {
        if (_request is not null) { _refreshAgain = true; return; }
        _refreshAgain = false;
        using var operation = new CancellationTokenSource(); _request = operation;
        _refresh.Enabled = false;
        _status.Text = _last.Count > 0 ? T("正在更新 · 保留已读记录", "Updating · Keeping previous records") : T("读取服务记录…", "Reading service records…");
        try
        {
            var readings = await _client.ReadAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            foreach (var result in readings)
            {
                if (result.Reading is { } reading)
                {
                    if (reading.Gaps.Length > 0 && _last.TryGetValue(result.Source.Id, out var previous))
                        reading = reading with
                        {
                            Projects = reading.Projects.Concat(previous.Projects).Distinct(StringComparer.Ordinal).ToArray(),
                            Progress = reading.Gaps.Contains("receipts_unavailable", StringComparer.Ordinal) ? previous.Progress : reading.Progress,
                            History = reading.History.Concat(previous.History).DistinctBy(row => row.Id)
                                .OrderByDescending(row => row.ObservedAt).Take(160).ToArray(),
                        };
                    _last[result.Source.Id] = reading; _unavailable.Remove(result.Source.Id);
                }
                else _unavailable.Add(result.Source.Id);
            }
            ApplyRows();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_request, operation)) _request = null;
            if (!IsDisposed)
            {
                _refresh.Enabled = true;
                if (_refreshAgain && Visible) { _refreshAgain = false; BeginInvoke(new Action(async () => await RefreshAsync())); }
            }
        }
    }

    private void ApplyRows()
    {
        var projects = _last.Values.SelectMany(r => r.Projects).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var signature = string.Join('\n', projects);
        if (_projects.Controls.Count == 0 || signature != _projectSignature)
        {
            _projectSignature = signature;
            foreach (Control old in _projects.Controls.Cast<Control>().ToArray()) old.Dispose();
            if (_project is not null && !projects.Contains(_project)) _project = null;
            foreach (ToolStripItem old in _projectMenu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
            var shown = projects.Take(2).ToList();
            if (_project is not null && !shown.Contains(_project)) { if (shown.Count == 2) shown.RemoveAt(1); shown.Add(_project); }
            foreach (var project in new[] { "" }.Concat(shown))
            {
                var button = new RouterButton { Text = project.Length == 0 ? T("全部项目", "All projects") : project,
                    Selected = project == (_project ?? ""), AutoSize = true };
                button.Click += (_, _) => SelectProject(project.Length == 0 ? null : project);
                _projects.Controls.Add(button);
            }
            foreach (var project in projects.Except(shown))
            {
                var item = new ToolStripMenuItem(project);
                item.Click += (_, _) => SelectProject(project); _projectMenu.Items.Add(item);
            }
            if (_projectMenu.Items.Count > 0)
            {
                var more = new RouterButton { Text = T("更多", "More") };
                more.Click += (_, _) => { if (!_pinned) RequestPinChange(true); _projectMenu.Show(more, new Point(0, more.Height)); };
                _projects.Controls.Add(more);
            }
        }
        bool Match(IntakeServiceRecord row) => _project is null || row.Project == _project;
        var progress = _last.Values.SelectMany(r => r.Progress).Where(Match).ToArray();
        var history = _last.Values.SelectMany(r => r.History).Where(Match).OrderByDescending(r => r.ObservedAt).Take(160).ToArray();
        Rows(_progress, progress.Select(r => (new object?[] { ItemTitle(r), r.Project + " · " + Kind(r.Kind), Phase(r.Phase) }, r)).ToArray());
        Rows(_history, history.Select(r => (new object?[] { r.ObservedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—",
            ItemTitle(r), Kind(r.Kind), Phase(r.Phase) }, r)).ToArray());
        var active = progress.Count(r => r.InFlight);
        var current = _last.Where(pair => !_unavailable.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        _headline.Text = _client.ConfigurationError is not null ? T("服务配置不可读", "Service configuration unavailable")
            : _unavailable.Count > 0 ? T("服务读取暂不可用 · 保留上次记录", "Service read unavailable · Keeping previous records")
            : current.Any(r => !r.Active) ? T("分拣服务未运行", "Intake service is not running")
            : current.Any(r => r.Gaps.Length > 0) ? T("服务在线 · 观测记录不完整", "Service online · Observations incomplete")
            : active > 0 ? T($"有 {active} 条在途回执", $"{active} in-flight receipts")
            : _last.Count > 0 ? T("服务在线 · 暂无在途回执", "Service online · No in-flight receipts")
            : T("正在连接分拣服务…", "Connecting to intake service…");
        _detail.Text = T($"服务 {_client.Sources.Count} · 已读在途 {active} · 保留待核对 {progress.Length - active} · 历史 {history.Length}",
            $"Services {_client.Sources.Count} · Observed in flight {active} · Held {progress.Length - active} · History {history.Length}");
        var task = progress.FirstOrDefault(r => r.InFlight) ?? history.FirstOrDefault(r => r.Title.Length > 0);
        _task.Text = task is null ? T("等待新的反馈或返工记录", "Waiting for new feedback or rework")
            : (active > 0 ? "" : T("最近记录 · ", "Latest record · ")) + ItemTitle(task);
        if (_last.Count > 0) _freshness.Text = T("更新于 ", "Updated ") + _last.Values.Max(r => r.ObservedAt).ToLocalTime().ToString("HH:mm:ss");
        var partial = _unavailable.Count > 0 || current.Any(r => r.Gaps.Length > 0);
        _status.Text = _client.ConfigurationError is not null ? T("检查本机 intake-services.json；未请求登录或更改服务", "Check local intake-services.json; no sign-in or service changes requested")
            : partial ? T("部分服务记录不可用；不能据此判断没有任务", "Partial service observations; this does not establish an empty queue")
            : T("只读 · 记录时间不等于业务验收时间 · 缺失时间显示 —", "Read only · Observation time is not business acceptance time · Missing timestamps show —");
        _progress.EmptyTitle = partial ? T("在途状态暂不可确认", "In-flight state is unconfirmed") : T("暂无在途回执", "No in-flight receipts");
        _progress.EmptyDetail = partial ? T("部分记录未返回，不能据此判断没有任务", "Missing observations do not establish an empty queue") : T("有新反馈或返工时自动更新", "New feedback and rework appear here");
        _history.EmptyTitle = partial ? T("历史记录暂不可用", "Service history is unavailable")
            : _last.Count > 0 ? T("暂无服务历史记录", "No retained service history") : T("尚未读到服务记录", "No service observation received");
    }

    private Control Table(string title, RouterGrid grid)
    {
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 2 };
        body.ColumnStyles.Add(new(SizeType.Percent, 100)); body.RowStyles.Add(new(SizeType.Absolute, 30)); body.RowStyles.Add(new(SizeType.Percent, 100));
        var heading = Label(); heading.Text = title; heading.Width = 240;
        var pager = new MonitorTablePager(grid);
        body.Controls.Add(CreateHeader(heading, Flow(pager.Buttons), 200), 0, 0); body.Controls.Add(grid, 0, 1);
        var section = RouterSurface.Section(title); section.Controls.Add(body); return section;
    }
    private static void Rows(RouterGrid grid, (object?[] Values, IntakeServiceRecord Record)[] rows)
    {
        grid.SuspendLayout();
        try
        {
            for (var i = 0; i < rows.Length; i++)
            {
                if (i >= grid.RowCount) grid.Rows.Add();
                for (var c = 0; c < rows[i].Values.Length; c++)
                    if (!Equals(grid.Rows[i].Cells[c].Value, rows[i].Values[c])) grid.Rows[i].Cells[c].Value = rows[i].Values[c];
                foreach (DataGridViewCell cell in grid.Rows[i].Cells) cell.ToolTipText = rows[i].Record.Title;
                grid.Rows[i].Tag = rows[i].Record;
            }
            while (grid.RowCount > rows.Length) grid.Rows.RemoveAt(grid.RowCount - 1);
        }
        finally { grid.ResumeLayout(true); }
    }
    public override void SetPinned(bool pinned) { base.SetPinned(pinned); _pin.Selected = pinned; _workItems.Enabled = pinned; }
    private void SelectProject(string? project)
    {
        if (!_pinned) RequestPinChange(true);
        _project = project; _projectSignature = "changed"; ApplyRows();
    }
    protected override void ApplyPalette(QuotaBackgroundTheme theme)
    {
        IntakeObserverForm.ApplyObservationPalette(this, _popoverScale, _task);
        var palette = new RouterVisualPalette(UiTokens.CardSurface);
        _headline.ForeColor = palette.Text;
        _projectMenu.Renderer = new DarkContextMenuRenderer(palette.Surface, palette.Input, palette.Border, palette.Text, palette.Muted);
        _projectMenu.Font = Font;
    }
    private string T(string zh, string en) => _zh ? zh : en;
    private static string Item(IntakeServiceRecord r) => r.Number.Length > 0 ? r.Number : r.ItemId.Length > 8 ? r.ItemId[..8] : "—";
    private static string ItemTitle(IntakeServiceRecord r) => Item(r) + (r.Title.Length > 0 ? " · " + r.Title : "");
    private string Kind(string kind) => kind.Replace("receipt:", "", StringComparison.Ordinal) switch
    { "result" or "precheck" => T("分拣", "Triage"), "rework" => T("返工续办", "Rework"), "archive" => T("验收后归档", "Archive"),
      "related" => T("关联续办", "Related"), "original_continuation" => T("原对话补交", "Original delivery"),
      "ready" => T("服务启动", "Service start"), "error" => T("服务异常", "Service error"), _ => kind };
    private string Phase(string phase) => phase switch
    { "model_started" => T("分类请求已发出", "Classification requested"), "sending" => T("准备提交", "Preparing submission"),
      "waiting_upstream" => T("等待上游恢复", "Waiting for upstream"), "waiting_original" => T("等待原任务", "Waiting for original"),
      "submitting" => T("正在提交续办", "Submitting continuation"), "submitted" => T("已提交待回执", "Submitted; awaiting receipt"),
      "retry" => T("等待重查", "Waiting for reconciliation"), "classified" => T("分类已记录", "Classification recorded"),
      "archive_pending" => T("等待归档回执", "Awaiting archive receipt"),
      "archive_submitted" => T("归档请求已提交", "Archive request submitted"),
      "archive_observation_deferred" => T("等待归档观测", "Deferred archive check"),
      "confirmed" => T("回执已确认", "Receipt confirmed"), "archived" => T("已归档", "Archived"), "needs_attention" => T("保留待核对", "Held for review"),
      "needs_review" => T("待核对", "Needs review"), "indeterminate" => T("结果待核对", "Outcome unknown"), "related" => T("已关联原任务", "Linked to original"),
      "source_changed" => T("原卡已有变化", "Source changed"), "reconciled_existing" => T("已有记录已核对", "Existing record reconciled"),
      "error" => T("服务异常", "Service error"), "ready" => T("已启动", "Ready"), _ => T("其他已记录阶段", "Other recorded stage") };
    private static Label Label(string text = "") => new() { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true, Margin = Padding.Empty,
        UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleLeft, Height = 26 };
    private static FlowLayoutPanel Flow(params Control[] controls)
    { var row = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false }; row.Controls.AddRange(controls); return row; }
    protected override void Dispose(bool disposing)
    { if (disposing) { _request?.Cancel(); _timer.Dispose(); _projectMenu.Dispose(); } base.Dispose(disposing); }
}
