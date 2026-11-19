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
    private readonly RouterStatusBadge _headline = new() { Margin = Padding.Empty };
    private readonly Label _task = Label(), _detail = Label(), _summary = Label(), _freshness = Label(), _status = Label();
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100 };
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
    internal event Action<int?>? ProcessingObserved;
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
        _workItems.Kind = RouterButtonKind.Primary;
        _workItems.Click += (_, _) => { if (_pinned) WorkItemsRequested?.Invoke(this, EventArgs.Empty); };
        _refresh.AccessibleName = T("刷新", "Refresh"); _pin.AccessibleName = T("固定弹层", "Pin"); _close.AccessibleName = T("收起弹层", "Close");
        foreach (var button in new[] { _refresh, _pin, _close }) _tips.SetToolTip(button, button.AccessibleName);
        var current = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 3 };
        current.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 30, 28, 30 }) current.RowStyles.Add(new(SizeType.Absolute, height));
        current.Controls.Add(CreateHeader(_headline, _workItems, 100), 0, 0);
        current.Controls.Add(_task, 0, 1); current.Controls.Add(_detail, 0, 2);
        var surface = RouterSurface.Section(T("当前", "Current"), featured: true); surface.Controls.Add(current); root.Controls.Add(surface, 0, 1);
        IntakeObserverForm.AddColumn(_progress, T("工单／内容", "Item / title"), 0);
        IntakeObserverForm.AddColumn(_progress, T("项目／操作", "Project / operation"), 160);
        IntakeObserverForm.AddColumn(_progress, T("当前阶段", "Stage"), 180);
        IntakeObserverForm.AddColumn(_history, T("记录时间", "Observed"), 110);
        IntakeObserverForm.AddColumn(_history, T("工单／内容", "Item / title"), 0);
        IntakeObserverForm.AddColumn(_history, T("操作", "Operation"), 100);
        IntakeObserverForm.AddColumn(_history, T("结果／阶段", "Result / stage"), 180);
        _progress.Columns[1].DefaultCellStyle.ForeColor = UiTokens.TextSecondary;
        _history.Columns[0].DefaultCellStyle.ForeColor = UiTokens.Muted;
        _history.Columns[2].DefaultCellStyle.ForeColor = UiTokens.TextSecondary;
        _progress.Columns[2].HeaderCell.Style.Alignment = _history.Columns[3].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _progress.CellPainting += PaintStage;
        _history.CellPainting += PaintStage;
        _tips.SetToolTip(_history, T("记录时间来自服务观测；旧回执缺少时间时显示 —", "Times come from service observations; older receipts without timestamps show —"));
        root.Controls.Add(Table(T("在途与待核对", "In flight and held records"), _progress), 0, 2);
        root.Controls.Add(Table(T("整体运行历史", "Service history"), _history), 0, 3);
        root.Controls.Add(_status, 0, 4);
        _tips.SetToolTip(_status, T("缺失时间的旧回执显示 —", "Older receipts without timestamps show —"));
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
        SetLogicalFont(_headline, "Segoe UI", 10.5f, FontStyle.Bold);
        SetLogicalFont(_task, "Microsoft YaHei UI", UiTokens.FontTitle, FontStyle.Bold);
        foreach (var label in new[] { _detail, _freshness, _summary, _status }) SetLogicalFont(label, "Segoe UI", 10.5f);
        ApplyPalette(QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        ApplyRows(); ResumeLayout(true);
    }

    internal async Task RefreshAsync()
    {
        if (_request is not null) { _refreshAgain = true; return; }
        _refreshAgain = false;
        using var operation = new CancellationTokenSource(); _request = operation;
        _refresh.Enabled = false;
        try
        {
            var readings = await _client.ReadAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (IsDisposed) return;
            ProcessingObserved?.Invoke(_client.ProcessingCount(readings));
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
        Rows(_progress, progress, r => [ItemTitle(r), r.Project + " · " + Kind(r.Kind), Phase(r.Phase)]);
        Rows(_history, history, r => [r.ObservedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—", ItemTitle(r), Kind(r.Kind), Phase(r.Phase)]);
        var active = progress.Count(r => r.InFlight);
        var current = _last.Where(pair => !_unavailable.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        var configurationUnavailable = _client.ConfigurationError is not null;
        var partial = _unavailable.Count > 0 || current.Any(r => r.Gaps.Length > 0);
        var loading = !configurationUnavailable && !partial && _last.Count == 0;
        var unavailable = configurationUnavailable || partial;
        var retained = _unavailable.Any(_last.ContainsKey);
        _headline.Text = configurationUnavailable ? T("服务配置不可读", "Service configuration unavailable")
            : _unavailable.Count > 0 ? (retained ? T("服务读取暂不可用 · 保留上次记录", "Service read unavailable · Keeping previous records")
                : T("服务读取暂不可用", "Service read unavailable"))
            : current.Any(r => !r.Active) ? T("分拣服务未运行", "Intake service is not running")
            : partial ? T("服务在线 · 观测记录不完整", "Service online · Observations incomplete")
            : active > 0 ? T($"有 {active} 条在途回执", $"{active} in-flight receipts")
            : _last.Count > 0 ? T("服务在线 · 暂无在途回执", "Service online · No in-flight receipts")
            : T("正在连接分拣服务…", "Connecting to intake service…");
        _detail.Text = _last.Count == 0 ? "" : T($"服务 {_client.Sources.Count} · 已读在途 {active} · 保留待核对 {progress.Length - active} · 历史 {history.Length}",
            $"Services {_client.Sources.Count} · Observed in flight {active} · Held {progress.Length - active} · History {history.Length}");
        var task = progress.FirstOrDefault(r => r.InFlight) ?? history.FirstOrDefault(r => r.Title.Length > 0);
        _task.Text = task is null ? (loading || unavailable ? "" : T("等待新的反馈或返工记录", "Waiting for new feedback or rework"))
            : (active > 0 ? "" : T("最近记录 · ", "Latest record · ")) + ItemTitle(task);
        if (_last.Count > 0) _freshness.Text = T("更新于 ", "Updated ") + _last.Values.Max(r => r.ObservedAt).ToLocalTime().ToString("HH:mm:ss");
        var tone = unavailable ? "warning"
            : current.Any(r => !r.Active) ? "offline" : active > 0 ? "running" : "idle";
        var accent = UiTokens.State(tone);
        if (_headline.Accent != accent) { _headline.Accent = accent; _headline.Invalidate(); }
        var fullTitle = task?.Title ?? _task.Text;
        if (_task.AccessibleDescription != fullTitle)
        {
            _tips.SetToolTip(_task, fullTitle);
            _task.AccessibleDescription = fullTitle;
        }
        _status.Text = configurationUnavailable ? T("请检查本机分拣服务配置 intake-services.json", "Check the local intake-services.json configuration")
            : partial ? T("部分服务记录不可用；不能据此判断没有任务", "Partial service observations; this does not establish an empty queue")
            : loading ? T("等待服务观测结果", "Waiting for the service snapshot")
            : T("时间以服务观测为准", "Times reflect service observations");
        var emptyDetail = configurationUnavailable ? T("检查本机分拣服务配置", "Check the local intake configuration")
            : partial ? T("服务恢复后自动更新", "Updates when the service is available")
            : loading ? "" : T("有新记录时自动更新", "New observations appear here");
        SetEmptyState(_progress, unavailable ? T("在途状态暂不可确认", "In-flight state is unconfirmed")
            : loading ? T("正在读取分拣进度…", "Reading intake progress…") : T("暂无在途回执", "No in-flight receipts"), emptyDetail);
        SetEmptyState(_history, unavailable ? T("历史记录暂不可用", "Service history is unavailable")
            : loading ? T("正在读取运行历史…", "Reading service history…") : T("暂无服务历史记录", "No retained service history"), emptyDetail);
    }

    private static void SetEmptyState(RouterGrid grid, string title, string detail)
    {
        if (grid.EmptyTitle == title && grid.EmptyDetail == detail) return;
        grid.EmptyTitle = title;
        grid.EmptyDetail = detail;
        if (grid.RowCount == 0) grid.Invalidate();
    }

    private Control Table(string title, RouterGrid grid)
    {
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 2 };
        body.ColumnStyles.Add(new(SizeType.Percent, 100)); body.RowStyles.Add(new(SizeType.Absolute, 30)); body.RowStyles.Add(new(SizeType.Percent, 100));
        var heading = Label(); heading.Text = title; heading.Width = 240;
        SetLogicalFont(heading, "Segoe UI", UiTokens.FontBody, FontStyle.Bold);
        var pager = new MonitorTablePager(grid);
        body.Controls.Add(CreateHeader(heading, Flow(pager.Buttons), 114), 0, 0); body.Controls.Add(grid, 0, 1);
        var section = RouterSurface.Section(title); section.Controls.Add(body); return section;
    }
    private void PaintStage(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (sender is not RouterGrid grid || e.RowIndex < 0
            || e.ColumnIndex != grid.ColumnCount - 1 || grid.Rows[e.RowIndex].Tag is not IntakeServiceRecord row) return;
        grid.PaintPill(e, StageColor(row.Phase));
    }
    private static Color StageColor(string phase) => phase switch
    {
        "confirmed" or "archived" or "related" or "reconciled_existing" => UiTokens.Positive,
        "needs_attention" or "needs_review" or "indeterminate" or "waiting_upstream" => UiTokens.Warning,
        "error" => UiTokens.Negative,
        "sending" or "model_started" or "classified" or "waiting_original" or "submitting" or "submitted"
            or "retry" or "archive_pending" or "archive_submitted" or "archive_observation_deferred" => UiTokens.Info,
        _ => UiTokens.Muted,
    };
    private static void Rows(RouterGrid grid, IntakeServiceRecord[] rows, Func<IntakeServiceRecord, object[]> format)
    {
        var hadSelection = grid.CurrentCell is not null;
        grid.SuspendLayout();
        try
        {
            for (var i = 0; i < rows.Length; i++)
            {
                if (i >= grid.RowCount) grid.Rows.Add();
                var row = grid.Rows[i];
                var item = rows[i];
                if (row.Tag is IntakeServiceRecord previous && previous == item) continue;
                row.Tag = item;
                row.SetValues(format(item));
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = item.Title;
            }
            while (grid.RowCount > rows.Length) grid.Rows.RemoveAt(grid.RowCount - 1);
            if (!hadSelection) { grid.ClearSelection(); grid.CurrentCell = null; }
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
        var palette = new RouterVisualPalette(UiTokens.CardSurface);
        ApplyMonitorPalette(palette, _task);
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
    { if (disposing) { _request?.Cancel(); _timer.Dispose(); _projectMenu.Dispose(); _tips.Dispose(); } base.Dispose(disposing); }
}
