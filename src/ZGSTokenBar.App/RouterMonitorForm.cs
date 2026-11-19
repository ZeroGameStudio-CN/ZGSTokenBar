using System.Text;
using System.Text.Json;
using System.Drawing.Drawing2D;
using System.Globalization;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

/// <summary>Native presentation only. All execution, fairness and release decisions belong to Router.</summary>
internal sealed class RouterMonitorForm : MonitorPanelForm
{
    private const string HistoryReadTimeout = "历史记录读取超时，稍后可刷新。";
    private readonly RouterMonitorClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly FlowLayoutPanel _projects = new() { Dock = DockStyle.Fill, Height = 28, Margin = Padding.Empty, WrapContents = false, AutoScroll = false };
    private readonly Dictionary<string, RouterButton> _projectButtons = new(StringComparer.Ordinal);
    private readonly ContextMenuStrip _projectMenu = new() { ShowImageMargin = false };
    private string _projectMenuRevision = "";
    private readonly Label _freshness = Label("连接中", 350);
    private readonly Label _overviewStats = Label("", 340);
    private readonly MonitorBrandMark _brand = new() { Width = 110, Height = 30, Margin = Padding.Empty };


    private readonly RouterStatusBadge _headline = new() { Text = "读取中", Margin = Padding.Empty };
    private readonly Label _metrics = Label("", 420);
    private readonly Label _task = Label("", 760);
    private readonly Label _detail = Label("", 760);
    private readonly Label _source = Label("", 760);
    private readonly Label _notice = Label("", 760);
    private readonly Label _queuePage = Label("", 180);
    private readonly Label _historyPage = Label("", 180);
    private string _statistics = "";
    private readonly RouterGrid _active = Grid();
    private readonly RouterGrid _queue = Grid();
    private readonly RouterGrid _history = Grid();
    private readonly ComboBox _kind = Combo(110), _outcome = Combo(110), _day = Combo(135);
    private readonly TextBox _query = new() { Width = 190, MaxLength = 160, PlaceholderText = "搜索任务或对话" };
    private readonly ToolStripMenuItem _cancel, _release, _nativeStop;
    private readonly Button _queueCancel, _top, _up, _down, _currentMenuButton, _filterToggle, _queuePrevious, _queueNext, _historyPrevious, _historyNext;
    private readonly ContextMenuStrip _currentMenu = new() { ShowImageMargin = false };
    private readonly TableLayoutPanel _root;
    private readonly TableLayoutPanel _singleTask;
    private readonly FlowLayoutPanel _filters;
    private readonly Label _currentHeading = Label("当前", 52), _queueHeading = Label("排队", 120), _historyHeading = Label("历史", 70), _historySummary = Label("", 210);
    private readonly TableLayoutPanel _historyBody;
    private readonly Button _pin;
    private readonly ToolTip _tips = new() { InitialDelay = 450, ReshowDelay = 150, AutoPopDelay = 12000 };
    private RouterVisualPalette _palette = new(Color.FromArgb(9, 11, 15));







    private RouterSnapshot? _snapshot;
    private bool _refreshing, _controlling, _available, _disposed;
    private int _queueOffset, _historyOffset, _generation, _historyGeneration, _queuePageSize = 3, _historyPageSize = 5, _queueTotal, _historyTotal;
    private bool _modal, _filtersVisible;
    private DateTimeOffset _historyReadAt;
    private readonly RouterHistoryBuffer _historyBuffer = new();
    private CancellationTokenSource? _historyRequest, _queueRequest;
    private Task<JsonElement>? _historyLoad;
    private string _historyLoadKey = "";
    private int _historyLoadOffset = -1, _queueGeneration;
    private bool _queueLoading, _applyingQueue, _hasHistoryCounts;
    private int _completedCount, _failedCount;



    private string _historySignature = "";
    private string? _project = RouterMonitorClient.AllProjects;
    internal RouterSnapshot? Snapshot => _snapshot;
    internal string FreshnessText => _freshness.Text;

    public override bool InteractionOpen => _modal || _controlling || _currentMenu.Visible || _projectMenu.Visible || _query.Focused || _kind.DroppedDown || _outcome.DroppedDown || _day.DroppedDown;



    internal RouterMonitorForm(RouterMonitorClient? client = null, bool observe = true, string? initialProject = null)
    {
        _project = initialProject ?? RouterMonitorClient.AllProjects;
        _client = client ?? new();
        SuspendLayout();
        Text = "Router";
        AutoScaleMode = AutoScaleMode.None;
        SetLogicalFont(this, "Microsoft YaHei UI", RouterVisualGeometry.LogicalFontPixels);
        BackColor = UiTokens.CardSurface;
        ForeColor = Color.FromArgb(229, 234, 243);
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = PreferredBodySize;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        DoubleBuffered = true;

        _root = CreatePanelLayout();
        Controls.Add(_root);
        _projects.AccessibleName = "工程切换"; _freshness.Width = 140;
        _kind.Width = _outcome.Width = 92; _day.Width = 112; _query.Width = 160;
        _kind.AccessibleName = "任务类型"; _outcome.AccessibleName = "结果"; _day.AccessibleName = "日期"; _query.AccessibleName = "搜索任务或对话";
        _pin = IconButton(RouterButtonIcon.Pin, "固定弹层", () => { RequestPinChange(!_pinned); return Task.CompletedTask; });
        var brand = _brand;
        _overviewStats.Margin = new Padding(8, 0, 0, 0);
        var header = Header(Flow(brand, _overviewStats), Flow(_freshness, IconButton(RouterButtonIcon.Refresh, "刷新", async () => { await RefreshViewAsync(); await HistoryAsync(forceRefresh: true); }), _pin,
            IconButton(RouterButtonIcon.Close, "收起弹层", () => { RequestClose(); return Task.CompletedTask; })), 240);
        var navigation = CreateNavigation(header, _projects, "项目");
        _root.Controls.Add(navigation, 0, 0);
        SetLogicalFont(_headline, Font.FontFamily.Name, 10.5f, FontStyle.Bold);
        SetLogicalFont(_task, Font.FontFamily.Name, 12, FontStyle.Bold);
        SetLogicalFont(_metrics, Font.FontFamily.Name, 12, FontStyle.Bold);
        foreach (var label in new[] { _detail, _source, _freshness, _overviewStats, _notice, _historySummary })
            SetLogicalFont(label, Font.FontFamily.Name, 10.5f);
        foreach (var label in new[] { _currentHeading, _queueHeading, _historyHeading })
            SetLogicalFont(label, "Segoe UI", UiTokens.FontBody, FontStyle.Bold);
        _currentHeading.Height = 25; _queueHeading.Height = _historyHeading.Height = _historySummary.Height = 30;
        _currentHeading.Margin = _queueHeading.Margin = _historyHeading.Margin = _historySummary.Margin = Padding.Empty;
        _metrics.TextAlign = ContentAlignment.MiddleRight;
        _task.Dock = _detail.Dock = _source.Dock = _notice.Dock = _metrics.Dock = DockStyle.Fill;
        _task.Margin = _detail.Margin = _source.Margin = _metrics.Margin = Padding.Empty;
        var summary = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 2 };
        summary.ColumnStyles.Add(new(SizeType.Percent, 100));
        summary.RowStyles.Add(new(SizeType.Absolute, 27)); summary.RowStyles.Add(new(SizeType.Percent, 100));
        _currentMenuButton = IconButton(RouterButtonIcon.More, "当前任务操作", () => { _currentMenu.Show(_currentMenuButton!, new Point(0, _currentMenuButton!.Height)); return Task.CompletedTask; });
        _currentMenuButton.Visible = _project != RouterMonitorClient.AllProjects;
        _nativeStop = MenuItem("停止并收尾", () => CurrentControlAsync("stop"));
        _cancel = MenuItem("请求取消", () => CurrentControlAsync("cancel"));
        _release = MenuItem("复检清理", () => CurrentControlAsync("release"));
        _currentMenu.Items.AddRange([_nativeStop, _cancel, _release]);
        _active.AccessibleName = "当前项目任务";
        _active.Visible = _active.ColumnHeadersVisible = false;
        _active.Columns.AddRange(Column("project", "项目", 105), Column("task", "任务", 330),
            Column("state", "状态", 176), Column("elapsed", "用时", 98));
        _active.Columns["project"]!.AutoSizeMode = _active.Columns["elapsed"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _active.Columns["project"]!.DefaultCellStyle.ForeColor = UiTokens.Accent;
        _active.Columns["elapsed"]!.DefaultCellStyle.ForeColor = UiTokens.Info;
        Align(_active.Columns["elapsed"]!, DataGridViewContentAlignment.MiddleRight);
        _active.CellPainting += PaintActiveStatus;
        var activePager = new MonitorTablePager(_active, hideSinglePage: true);
        var currentActions = Flow([_currentMenuButton, .. activePager.Buttons.Reverse()]);
        currentActions.FlowDirection = FlowDirection.RightToLeft;
        summary.Controls.Add(Header(Flow(_currentHeading, _headline), currentActions, 150), 0, 0);
        _singleTask = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 3 };
        _singleTask.ColumnStyles.Add(new(SizeType.Percent, 100));
        _singleTask.RowStyles.Add(new(SizeType.Absolute, 23));
        _singleTask.RowStyles.Add(new(SizeType.Absolute, 18));
        _singleTask.RowStyles.Add(new(SizeType.Percent, 100));
        _singleTask.Controls.Add(Header(_task, _metrics, 138), 0, 0);
        _singleTask.Controls.Add(_detail, 0, 1);
        _singleTask.Controls.Add(_source, 0, 2);
        var currentBody = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        currentBody.Controls.AddRange([_singleTask, _active]);
        summary.Controls.Add(currentBody, 0, 1);
        var currentSection = RouterSurface.Section("当前", featured: true);
        currentSection.Controls.Add(summary); _root.Controls.Add(currentSection, 0, 1);

        _queue.Columns.AddRange(Column("task", "任务", 270), Column("source", "来源对话", 210), Column("wait", "等待", 80));
        Align(_queue.Columns["wait"]!, DataGridViewContentAlignment.MiddleRight);
        _queue.Columns["source"]!.DefaultCellStyle.ForeColor = UiTokens.TextSecondary;
        _queue.Columns["wait"]!.DefaultCellStyle.ForeColor = UiTokens.Accent;
        _queue.SelectionChanged += (_, _) => { if (!_applyingQueue) UpdateControls(); };
        _top = IconButton(RouterButtonIcon.Top, "置顶", () => QueueControlAsync("move", 1));
        _up = IconButton(RouterButtonIcon.Up, "上移", () => QueueControlAsync("move", -1));
        _down = IconButton(RouterButtonIcon.Down, "下移", () => QueueControlAsync("move", 0));
        _queueCancel = IconButton(RouterButtonIcon.Close, "取消排队任务", () => QueueControlAsync("cancel", null));
        _queuePrevious = IconButton(RouterButtonIcon.Previous, "队列上一页", () => PageQueueAsync(-1));
        _queueNext = IconButton(RouterButtonIcon.Next, "队列下一页", () => PageQueueAsync(1));
        _queue.PageRequested += async (_, pages) => await PageQueueAsync(pages);
        _queuePage.Width = 64; _queuePage.TextAlign = ContentAlignment.MiddleCenter;
        var queueBody = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 2 };
        queueBody.ColumnStyles.Add(new(SizeType.Percent, 100)); queueBody.RowStyles.Add(new(SizeType.Absolute, 30)); queueBody.RowStyles.Add(new(SizeType.Percent, 100));
        queueBody.Controls.Add(Header(Flow(_queueHeading), Flow(_top, _up, _down, _queueCancel, _queuePrevious, _queuePage, _queueNext), 244), 0, 0);
        _queue.Margin = Padding.Empty; queueBody.Controls.Add(_queue, 0, 1);
        var queueSection = Section("排队"); queueSection.Controls.Add(queueBody); _root.Controls.Add(queueSection, 0, 2);

        _historyBody = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 3 };
        _historyBody.ColumnStyles.Add(new(SizeType.Percent, 100)); _historyBody.RowStyles.Add(new(SizeType.Absolute, 30));
        _historyBody.RowStyles.Add(new(SizeType.Absolute, 0)); _historyBody.RowStyles.Add(new(SizeType.Percent, 100));

        _historyPrevious = IconButton(RouterButtonIcon.Previous, "历史上一页", () => PageHistoryAsync(-1));
        _historyNext = IconButton(RouterButtonIcon.Next, "历史下一页", () => PageHistoryAsync(1));
        _history.PageRequested += async (_, pages) => await PageHistoryAsync(pages);
        _filterToggle = IconButton(RouterButtonIcon.Filter, "筛选记录", async () =>
        {
            _filtersVisible = !_filtersVisible; _filters!.Visible = _filtersVisible; _historyBody.RowStyles[1].Height = _filtersVisible ? 34 * _popoverScale : 0;
            ((RouterButton)_filterToggle!).Selected = _filtersVisible; _root.PerformLayout(); await RefreshViewAsync(); await HistoryAsync();
        });
        _historyPage.Width = 64; _historyPage.TextAlign = ContentAlignment.MiddleCenter;
        _historyBody.Controls.Add(Header(Flow(_historyHeading, _historySummary), Flow(_filterToggle, IconButton(RouterButtonIcon.Export, "导出 CSV", ExportAsync), _historyPrevious, _historyPage, _historyNext), 184), 0, 0);
        Options(_kind, [("all", "全部类型"), ("compile", "编译"), ("editmode", "EditMode"), ("playmode", "PlayMode"), ("script", "脚本")]);
        Options(_outcome, [("finished", "全部结果"), ("completed", "成功"), ("failed", "失败"), ("cancelled", "取消")]);
        Options(_day, [("all", "全部日期")]);
        var search = new RouterSurface { Input = true, Width = 160, Height = 30, Padding = new Padding(8, 6, 8, 6) };
        _query.Dock = DockStyle.Fill; _query.BorderStyle = BorderStyle.None; search.Controls.Add(_query);
        _filters = Flow(_kind, _outcome, _day, search, Button("筛选", async () => { _historyOffset = 0; await HistoryAsync(); }, RouterButtonKind.Primary));
        _filters.Visible = false; _historyBody.Controls.Add(_filters, 0, 1);
        _history.EmptyTitle = "没有匹配的记录"; _history.EmptyDetail = "调整筛选条件后重试";
        _history.Columns.AddRange(Column("createdLocal", "提交时间", 115), Column("task", "任务", 240), Column("source", "来源对话", 150),
            Column("state", "结果", 90), Column("executionSeconds", "占用", 70), Column("cleanupVerified", "释放", 70));
        // Headers sit over their values: centred status pill and check, right-aligned durations.
        Align(_history.Columns["state"]!, DataGridViewContentAlignment.MiddleCenter);
        Align(_history.Columns["executionSeconds"]!, DataGridViewContentAlignment.MiddleRight);
        Align(_history.Columns["cleanupVerified"]!, DataGridViewContentAlignment.MiddleCenter);
        _history.Columns[0].DefaultCellStyle.ForeColor = UiTokens.Muted;
        _history.Columns["source"]!.DefaultCellStyle.ForeColor = UiTokens.TextSecondary;
        _history.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowRecord(_history.Rows[e.RowIndex]); };
        _history.Margin = Padding.Empty; _historyBody.Controls.Add(_history, 0, 2);
        var historySection = Section("历史"); historySection.Controls.Add(_historyBody); _root.Controls.Add(historySection, 0, 3);
        _root.Controls.Add(_notice, 0, 4);
        _timer.Tick += async (_, _) => { if (Visible) await RefreshViewAsync(); };
        _notice.TextChanged += (_, _) => _tips.SetToolTip(_notice, _notice.Text);
        _history.CellPainting += PaintHistoryStatus;
        // Show the (cached) shell first; the data refresh starts after the window has painted.
        if (observe) VisibleChanged += (_, _) =>
        {
            if (!Visible) { _timer.Stop(); return; }
            _timer.Start();
            BeginInvoke(new Action(async () => { if (Visible && !IsDisposed) await RefreshViewAsync(); }));
        };
        UpdateControls();
        ApplyPalette(QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        ResumeLayout(true);
    }

    public override void SetPinned(bool pinned)
    {
        base.SetPinned(pinned);
        ((RouterButton)_pin).Selected = pinned;
        _pin.AccessibleName = pinned ? "取消固定" : "固定弹层";
        _tips.SetToolTip(_pin, pinned ? "取消固定" : "固定后可操作队列与历史");
        if (string.IsNullOrEmpty(_notice.Text) || _notice.Text is "悬停预览 · 点击模块固定后可操作" or "已固定 · Esc 或点击外部收起")
            _notice.Text = pinned ? "已固定 · Esc 或点击外部收起" : "悬停预览 · 点击模块固定后可操作";
        UpdateControls();
        if (pinned && Visible) Activate();
    }
    /// <summary>Click time of the pending open, for the click-to-paint / click-to-data trace.</summary>
    private long _openStamp;
    private bool _openDataPending;

    private long _openDataStamp;
    internal void MarkOpenRequested(long stamp) { _openStamp = stamp; _openDataStamp = stamp; _openDataPending = true; }

    /// <summary>
    /// Idle-time warm-up: scale, fonts, palette, every native child handle and one layout pass,
    /// so a click only positions and shows the already-built window.
    /// </summary>
    internal Task WarmInBackgroundAsync()
    {
        if (Visible || _refreshing || _controlling || IsDisposed) return Task.CompletedTask;
        if (_snapshot is not null && (DateTimeOffset.UtcNow - _snapshot.ReceivedAt).TotalSeconds < 6)
            return Task.CompletedTask;
        return RefreshViewAsync();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_openStamp != 0)
        {
            var stamp = _openStamp; _openStamp = 0;
            PerfTrace.Note($"router.panel.click-to-paint {System.Diagnostics.Stopwatch.GetElapsedTime(stamp).TotalMilliseconds:0.00}ms");
            // Children paint after the frame; the first idle after that is "fully drawn".
            void Idle(object? s, EventArgs a)
            {
                Application.Idle -= Idle;
                PerfTrace.Note($"router.panel.click-to-idle {System.Diagnostics.Stopwatch.GetElapsedTime(stamp).TotalMilliseconds:0.00}ms");
            }
            if (PerfTrace.Enabled) Application.Idle += Idle;
        }
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var point = PointToScreen(e.Location);
        var grid = new[] { _active, _queue, _history }.FirstOrDefault(table => table.Visible && table.RectangleToScreen(table.ClientRectangle).Contains(point));
        if (grid is null) { base.OnMouseWheel(e); return; }
        grid.PageFromWheel(e.Delta);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }


    protected override void ApplyPalette(QuotaBackgroundTheme theme)
    {
        _palette = new(UiTokens.CardSurface);
        ApplyMonitorPalette(_palette, _task);
        _metrics.ForeColor = UiTokens.Info;
        _active.Columns["project"]!.Width = (int)Math.Round(92 * _popoverScale);
        _active.Columns["task"]!.DefaultCellStyle.Font = _task.Font;
        _active.Columns["elapsed"]!.Width = (int)Math.Round(110 * _popoverScale);
        _active.Columns["elapsed"]!.DefaultCellStyle.Font = _metrics.Font;
        _currentMenu.Renderer = new DarkContextMenuRenderer(_palette.Surface, _palette.Input, _palette.Border, _palette.Text, _palette.Muted);
        _currentMenu.Font = Font;
        _projectMenu.Renderer = new DarkContextMenuRenderer(_palette.Surface, _palette.Input, _palette.Border, _palette.Text, _palette.Muted);
        _projectMenu.Font = Font;
        if (!_available && _snapshot is not null) _freshness.ForeColor = RouterVisualPalette.State("warning");
    }

    private void PaintActiveStatus(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != _active.Columns["state"]!.Index
            || _active.Rows[e.RowIndex].Tag is not Color accent) return;
        _active.PaintPill(e, accent);
    }

    private void PaintHistoryStatus(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.Graphics is not { } graphics || e.RowIndex < 0 || e.ColumnIndex < 0 || _history.Rows[e.RowIndex].Tag is not JsonElement row) return;
        var column = _history.Columns[e.ColumnIndex].Name;
        if (column is not ("state" or "cleanupVerified")) return;
        if (column == "state")
        {
            _history.PaintPill(e, RouterVisualPalette.State(RouterJson.Text(row, "outcome")));
            return;
        }
        e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border);
        var color = RouterJson.Boolean(row, "cleanupVerified") ? UiTokens.Positive : UiTokens.Warning;
        TextRenderer.DrawText(graphics, e.FormattedValue?.ToString() ?? "", _history.Font, e.CellBounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        e.Handled = true;
    }

    internal void ApplySnapshot(RouterSnapshot snapshot)
    {
        if (_snapshot is { } previous && previous.InstanceId != snapshot.InstanceId) ResetHistory();
        _snapshot = snapshot; _project = snapshot.ProjectKey;
        _available = true;
        ApplyProjectTabs(snapshot);
        var fresh = snapshot.IsFresh(DateTimeOffset.UtcNow);
        var tone = fresh ? RouterJson.Text(snapshot.View, "tone") : "warning";
        _headline.Text = fresh ? RouterJson.Text(snapshot.View, "headline", "状态未确认") : "状态未确认";
        _headline.Accent = RouterVisualPalette.State(tone); _headline.Invalidate();
        _brand.State = tone switch
        {
            "running" => "running", "warning" => "cleanup", "negative" or "failed" => "attention", "waiting" => "waiting", _ => "idle",
        };
        _brand.Invalidate();
        ApplyCurrentTasks(snapshot, fresh);
        _queueHeading.Text = $"排队  ·  {RouterJson.Integer(snapshot.View, "queuedCount"):N0}";
        _freshness.Text = fresh ? "更新于 " + snapshot.ReceivedAt.ToLocalTime().ToString("HH:mm:ss") : "数据过期 · 状态未确认";
        _freshness.ForeColor = fresh ? UiPalette.Slate400 : Color.Salmon;
        UpdateOverviewStats();
        UpdateControls();
    }

    private void ApplyCurrentTasks(RouterSnapshot snapshot, bool fresh)
    {
        var all = snapshot.ProjectKey == RouterMonitorClient.AllProjects;
        var projects = snapshot.ActiveProjects;
        _active.Visible = projects.Count > 0;
        _singleTask.Visible = projects.Count == 0;
        _currentMenuButton.Visible = !all;
        var hadSelection = _active.CurrentCell is not null;
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var view = project.View;
            var heading = RouterTaskPresentation.Heading(RouterJson.Text(view, "task"));
            var state = fresh ? RouterJson.Text(view, "headline", "状态未确认") : "状态未确认";
            var elapsed = ElapsedText(view, fresh);
            var row = _active.Rows[index < _active.RowCount ? index : _active.Rows.Add()];
            row.Tag = RouterVisualPalette.State(fresh ? RouterJson.Text(view, "tone") : "warning");
            row.SetValues(ProjectCaption(project.Name), heading.Title, state, elapsed);
            row.Cells[0].ToolTipText = project.Name;
            row.Cells[1].ToolTipText = JoinDetails(heading.Original, RouterJson.Text(RouterJson.Field(view, "owner"), "label"));
            row.Cells[2].ToolTipText = JoinDetails(state, Observation(view));
            row.Cells[3].ToolTipText = JoinDetails(elapsed, fresh ? RouterJson.Text(view, "progressClock") : "");
            row.Cells[3].Style.ForeColor = elapsed == "用时 —" ? UiTokens.Muted : UiTokens.Info;
        }
        while (_active.RowCount > projects.Count) _active.Rows.RemoveAt(_active.RowCount - 1);
        if (!hadSelection) { _active.ClearSelection(); _active.CurrentCell = null; }
        _active.Invalidate();

        var taskHeading = RouterTaskPresentation.Heading(RouterJson.Text(snapshot.View, "task"));
        var active = RouterJson.IsHex(RouterJson.Text(snapshot.View, "requestId"), 32);
        _task.Text = fresh || active ? taskHeading.Title : "";
        _tips.SetToolTip(_task, taskHeading.Original); _task.AccessibleDescription = taskHeading.Original;
        var owner = RouterJson.Text(RouterJson.Field(snapshot.View, "owner"), "label");
        _metrics.Text = active ? ElapsedText(snapshot.View, fresh) : "";
        _source.Text = string.IsNullOrWhiteSpace(owner) ? "" : "来源   " + RouterTaskPresentation.Source(owner);
        _tips.SetToolTip(_source, owner); _source.AccessibleDescription = owner;
        var observation = Observation(snapshot.View);
        var progress = fresh ? RouterJson.Text(snapshot.View, "progressClock") : "";
        _detail.Text = string.Join("  ·  ", new[] { progress, observation.Length == 0 ? taskHeading.Context : observation }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        _tips.SetToolTip(_detail, JoinDetails(taskHeading.Context, progress, observation));
        _tips.SetToolTip(_headline, observation);
        _tips.SetToolTip(_metrics, progress);
    }

    private static string ElapsedText(JsonElement view, bool fresh)
    {
        var elapsed = fresh ? RouterJson.Text(view, "elapsed").Trim() : "";
        return "用时 " + (elapsed.Length == 0 ? "—" : elapsed);
    }
    private static string JoinDetails(params string[] lines) => string.Join(Environment.NewLine, lines.Where(text => !string.IsNullOrWhiteSpace(text)));
    private static string Observation(JsonElement view) => string.Join("  ·  ",
        new[] { RouterJson.Text(view, "detail"), RouterJson.Text(view, "error") }.Where(text => !string.IsNullOrWhiteSpace(text)));

    private void UpdateOverviewStats()
    {
        if (!_available || _snapshot is null)
        {
            _overviewStats.Text = "";
            return;
        }
        var active = _snapshot.ProjectKey == RouterMonitorClient.AllProjects ? RouterJson.Integer(_snapshot.View, "activeCount")
            : RouterJson.IsHex(RouterJson.Text(_snapshot.View, "requestId"), 32) ? 1 : 0;
        var queued = RouterJson.Integer(_snapshot.View, "queuedCount");
        _overviewStats.Text = _hasHistoryCounts
            ? $"当前 {active:N0}  ·  排队 {queued:N0}  ·  成功 {_completedCount:N0}  ·  失败 {_failedCount:N0}"
            : $"当前 {active:N0}  ·  排队 {queued:N0}";
    }

    internal static JsonElement[] OrderedProjects(JsonElement projects) => projects.EnumerateArray()
        .OrderByDescending(row => RouterJson.Integer(row, "recorded"))
        .ThenByDescending(row => RouterJson.Integer(row, "pending"))
        .ThenBy(row => RouterJson.Text(row, "name"), StringComparer.OrdinalIgnoreCase)
        .ThenBy(row => RouterJson.Text(row, "key"), StringComparer.Ordinal).ToArray();

    private static string ProjectCaption(string name) => name switch { "ZGSProject_5" => "P5", "POB_Manual" => "Manual", _ => name };

    private void ApplyProjectTabs(RouterSnapshot snapshot)
    {
        var catalog = new[] { JsonSerializer.SerializeToElement(new { key = RouterMonitorClient.AllProjects, name = "全部项目",
                recorded = snapshot.Projects.EnumerateArray().Sum(row => RouterJson.Integer(row, "recorded")),
                pending = snapshot.Projects.EnumerateArray().Sum(row => RouterJson.Integer(row, "pending")) }) }
            .Concat(OrderedProjects(snapshot.Projects)).ToArray();
        var shown = catalog.Take(3).ToList();
        if (shown.All(row => RouterJson.Text(row, "key") != snapshot.ProjectKey))
        {
            var selected = catalog.First(row => RouterJson.Text(row, "key") == snapshot.ProjectKey);
            if (shown.Count > 0) shown.RemoveAt(shown.Count - 1);
            shown.Add(selected);
        }
        var keys = shown.Select(row => RouterJson.Text(row, "key")).ToArray();
        if (!_projectButtons.Keys.SequenceEqual(keys))
        {
            foreach (var child in _projects.Controls.Cast<Control>().ToArray()) child.Dispose();
            _projects.Controls.Clear(); _projectButtons.Clear();
            foreach (var row in shown)
            {
                var key = RouterJson.Text(row, "key");
                var button = Button("", () => SelectProjectAsync(key));
                button.AutoSize = false; button.Palette = _palette;
                _projectButtons[key] = button; _projects.Controls.Add(button);
            }
            if (catalog.Length > 3)
            {
                var more = Button("更多", () => { _projectMenu.Show(_projects, new Point(_projects.Controls.Cast<Control>().Last().Left, _projects.Height)); return Task.CompletedTask; });
                more.Palette = _palette; more.AccessibleName = "更多工程"; _projects.Controls.Add(more);
            }
        }
        var scale = RouterVisualGeometry.Scale(_projects);
        var maximum = Math.Max(40, (_projects.Width - (catalog.Length > 3 ? (int)(54 * scale) : 0)) / Math.Max(1, shown.Count) - (int)(4 * scale));
        foreach (var row in catalog)
        {
            var key = RouterJson.Text(row, "key"); var name = RouterJson.Text(row, "name"); var pending = RouterJson.Integer(row, "pending");
            if (_projectButtons.TryGetValue(key, out var button))
            {
                var caption = ProjectCaption(name);
                button.Text = caption + (pending > 0 ? "  " + pending : ""); button.Selected = key == snapshot.ProjectKey;
                button.AccessibleName = "切换工程 " + name;
                button.Width = Math.Min(maximum, button.GetPreferredSize(Size.Empty).Width); button.Height = (int)(26 * scale);
                _tips.SetToolTip(button, name + $" · 累计 {RouterJson.Integer(row, "recorded")} 项" + (pending > 0 ? $" · {pending} 项尚未结束" : ""));
            }
        }
        var hidden = catalog.Where(row => !_projectButtons.ContainsKey(RouterJson.Text(row, "key"))).ToArray();
        var revision = string.Join("|", hidden.Select(row => RouterJson.Text(row, "key") + RouterJson.Text(row, "name") + RouterJson.Integer(row, "pending")));
        if (revision != _projectMenuRevision && !_projectMenu.Visible)
        {
            foreach (var item in _projectMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
            _projectMenu.Items.Clear();
            foreach (var row in hidden)
            {
                var key = RouterJson.Text(row, "key"); var name = RouterJson.Text(row, "name"); var pending = RouterJson.Integer(row, "pending");
                _projectMenu.Items.Add(MenuItem(name + (pending > 0 ? "  " + pending : ""), () => SelectProjectAsync(key)));
            }
            _projectMenuRevision = revision;
        }
    }

    internal async Task SelectProjectAsync(string key)
    {
        if (_controlling || key == _project) return;
        if (!_pinned) RequestPinChange(true);
        _project = key; _generation++; _queueOffset = _historyOffset = 0; ResetHistory(); _queueRequest?.Cancel();
        _snapshot = null; _available = false; _hasHistoryCounts = false; _completedCount = 0; _failedCount = 0; _headline.Text = "读取中"; _task.Text = ""; _detail.Text = ""; _source.Text = ""; _metrics.Text = "";
        _headline.Accent = UiTokens.Muted; _headline.Invalidate();
        _active.Rows.Clear(); _active.Hide(); _singleTask.Show(); _currentMenuButton.Visible = key != RouterMonitorClient.AllProjects;
        UpdateOverviewStats();
        _queue.Rows.Clear(); _history.Rows.Clear();
        foreach (var item in _projectButtons) item.Value.Selected = item.Key == key;
        UpdateControls(); await RefreshViewAsync(); await HistoryAsync();
    }

    private void ResetHistory()
    {
        _historyGeneration++; _historyBuffer.Clear(); _historyReadAt = default; _historySignature = "";
        _historyRequest?.Cancel(); _historyLoad = null; _historyLoadKey = "";
    }

    internal void ApplyUnavailable(string message)
    {
        _available = false;
        var localQueueUnavailable = message.StartsWith("Router 监视进程在线", StringComparison.Ordinal);
        _freshness.Text = localQueueUnavailable ? "队列暂不可用 · 状态未确认" : "连接中断 · 状态未确认";
        _freshness.ForeColor = localQueueUnavailable ? Color.Goldenrod : Color.Salmon;
        _headline.Text = "状态未确认"; _headline.Accent = RouterVisualPalette.State("warning"); _headline.Invalidate();
        if (_snapshot is { } snapshot) ApplyCurrentTasks(snapshot, fresh: false);
        _notice.Text = message;
        UpdateOverviewStats();
        UpdateControls();
    }

    private static string ComputeHistorySignature(RouterSnapshot snapshot)
    {
        var recorded = 0;
        var pending = 0;
        foreach (var row in snapshot.Projects.EnumerateArray())
        {
            if (RouterJson.Text(row, "key") != snapshot.ProjectKey) continue;
            recorded = RouterJson.Integer(row, "recorded");
            pending = RouterJson.Integer(row, "pending");
            break;
        }
        return $"{snapshot.InstanceId}|{snapshot.ProjectKey}|{recorded}|{pending}|{RouterJson.Text(snapshot.View, "requestId")}";
    }

    internal async Task RefreshViewAsync()
    {
        if (_refreshing || _controlling || IsDisposed) return;
        _refreshing = true;
        var generation = _generation;
        try
        {
            var snapshot = await _client.SnapshotAsync(_project, _stop.Token);
            if (generation != _generation || IsDisposed) return;
            ApplySnapshot(snapshot);
            var signature = ComputeHistorySignature(snapshot);
            var historyDue = !InteractionOpen
                && _historyOffset == 0
                && (_historyReadAt == default
                    || !string.Equals(_historySignature, signature, StringComparison.Ordinal)
                    || (DateTimeOffset.UtcNow - _historyReadAt).TotalSeconds >= 60);
            if (historyDue)
                await Task.WhenAll(RefreshQueueAsync(), HistoryAsync(forceRefresh: true));
            else
                await RefreshQueueAsync();
            if (_openDataPending)
            {
                _openDataPending = false;
                PerfTrace.Note($"router.panel.open-to-data {System.Diagnostics.Stopwatch.GetElapsedTime(_openDataStamp).TotalMilliseconds:0.00}ms");
            }
        }
        catch (Exception e) when (Expected(e))
        { if (!IsDisposed && !_stop.IsCancellationRequested && generation == _generation) ApplyUnavailable(e is RouterMonitorHttpException monitor ? monitor.UserMessage : e.Message); }
        finally
        {
            _refreshing = false;
            if (!IsDisposed && !_stop.IsCancellationRequested && generation != _generation) await RefreshViewAsync();
        }
    }

    private async Task PageQueueAsync(int pages)
    {
        if (!_available || _controlling) return;
        var offset = PageOffset(_queueOffset, pages, _queuePageSize, _queueTotal);
        if (offset == _queueOffset) return;
        _queueOffset = offset;
        await RefreshQueueAsync(userInitiated: true);
    }

    private async Task PageHistoryAsync(int pages)
    {
        if (!_available || _controlling) return;
        var offset = PageOffset(_historyOffset, pages, _historyPageSize, _historyTotal);
        if (offset == _historyOffset) return;
        _historyOffset = offset;
        await HistoryAsync();
    }

    private static int PageOffset(int offset, int pages, int size, int total) =>
        (int)Math.Clamp(offset + (long)pages * size, 0, Math.Max(0, (total - 1) / size * size));

    internal async Task RefreshQueueAsync(bool userInitiated = false)
    {
        if (_snapshot is null || _controlling || IsDisposed || (_queueLoading && !userInitiated)) return;
        var project = _snapshot.ProjectKey;
        var generation = _generation; var queueGeneration = ++_queueGeneration;
        _queueRequest?.Cancel(); _queueRequest?.Dispose();
        _queueRequest = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var token = _queueRequest.Token;
        _queueLoading = true; UpdateControls();
        try
        {
            _queuePageSize = PageRows(_queue);
            var page = await _client.QueueAsync(project, _queueOffset, token, _queuePageSize);
            if (generation != _generation || queueGeneration != _queueGeneration || IsDisposed) return;
            var items = RouterJson.Rows(page, "items").ToArray();
            if (items.Length > _queuePageSize) throw new InvalidDataException("Queue page exceeds the visible row bound.");
            var selected = Selected(_queue);
            _applyingQueue = true; _queue.SuspendLayout();
            try
            {
                _queue.Rows.Clear();
                foreach (var row in items)
                {
                    var n = _queue.Rows.Add(RouterJson.Text(row, "task"), RouterJson.Text(row, "source"), RouterJson.Text(row, "wait"));
                    _queue.Rows[n].Tag = row;
                    _queue.Rows[n].Selected = RouterJson.Text(row, "id") == selected;
                }
            }
            finally { _queue.ResumeLayout(); _applyingQueue = false; }
            _queueOffset = RouterJson.Integer(page, "offset"); _queueTotal = RouterJson.Integer(page, "total");
            _queuePage.Text = PageLabel(_queueOffset, _queueTotal, _queuePageSize);
            UpdateControls();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (Expected(e))
        { if (!IsDisposed && generation == _generation && queueGeneration == _queueGeneration) ApplyUnavailable(e is RouterMonitorHttpException monitor ? monitor.UserMessage : e.Message); }
        finally { if (queueGeneration == _queueGeneration) { _queueLoading = false; if (!IsDisposed) UpdateControls(); } }
    }

    internal async Task HistoryAsync(bool forceRefresh = false)
    {
        if (_snapshot is null || _controlling || IsDisposed) return;
        var project = _snapshot.ProjectKey;
        var generation = _generation;
        var historyGeneration = ++_historyGeneration;
        var kind = Key(_kind); var outcome = Key(_outcome); var day = Key(_day); var query = _query.Text;
        var key = JsonSerializer.Serialize(new[] { _snapshot.InstanceId, project, kind, outcome, day, query });
        var offset = _historyOffset;
        try
        {
            _historyPageSize = PageRows(_history);
            if (forceRefresh || !_historyBuffer.TryRead(key, offset, _historyPageSize, out _))
            {
                // Share an outstanding window that covers this page; supersede only obsolete reads.
                if (forceRefresh || _historyLoad is null || _historyLoad.IsCompleted || _historyLoadKey != key
                    || offset < _historyLoadOffset || offset + _historyPageSize > _historyLoadOffset + RouterHistoryBuffer.Capacity)
                {
                    _historyRequest?.Cancel(); _historyRequest?.Dispose();
                    _historyRequest = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    _historyLoadKey = key; _historyLoadOffset = offset;
                    _historyLoad = _client.HistoryAsync(project, kind, outcome, day, query, offset, _historyRequest.Token, RouterHistoryBuffer.Capacity);
                }
                var page = await _historyLoad;
                if (generation != _generation || historyGeneration != _historyGeneration || IsDisposed) return;
                _historyBuffer.Store(key, page);
                // The server may clamp the offset after a filter or history retention change.
                offset = Math.Max(RouterJson.Integer(page, "offset"), Math.Min(offset, RouterJson.Integer(page, "total")));
            }
            if (generation != _generation || historyGeneration != _historyGeneration || IsDisposed) return;
            if (!_historyBuffer.TryRead(key, offset, _historyPageSize, out var slice) || slice is null)
                throw new InvalidDataException("History window does not cover the requested page.");
            var selectedHistory = Selected(_history);
            _history.SuspendLayout();
            try
            {
                _history.Rows.Clear();
                foreach (var row in slice.Items)
                {
                    var n = _history.Rows.Add(SubmittedTime(RouterJson.Text(row, "createdLocal")), RouterJson.Text(row, "task"),
                        RouterJson.Text(row, "source", RouterJson.Text(row, "threadId")), ResultName(row),
                        Seconds(row, "executionSeconds"), RouterJson.Boolean(row, "cleanupVerified") ? "✓" : "待确认");
                    _history.Rows[n].Tag = row;
                    _history.Rows[n].Cells[0].ToolTipText = RouterJson.Text(row, "createdLocal");
                    _history.Rows[n].Cells[4].ToolTipText = TimingDetails(row);
                    _history.Rows[n].Selected = selectedHistory.Length > 0 && RouterJson.Text(row, "id") == selectedHistory;
                }
                // A refresh keeps the user's pick; otherwise no row is pre-highlighted.
                if (selectedHistory.Length == 0) { _history.ClearSelection(); _history.CurrentCell = null; }
            }
            finally { _history.ResumeLayout(); }
            var metadata = slice.Metadata; var total = slice.Total; _historyOffset = slice.Offset; _history.Tag = total;
            _historyTotal = total; _historyPage.Text = PageLabel(_historyOffset, total, _historyPageSize);
            var selectedDay = Key(_day);
            Options(_day, new[] { ("all", "全部日期") }.Concat(RouterJson.Rows(metadata, "days").Select(d => (RouterJson.Text(d, "date"), RouterJson.Text(d, "date")))).ToArray());
            _day.SelectedItem = _day.Items.Cast<Choice>().FirstOrDefault(c => c.Key == selectedDay) ?? _day.Items[0];
            var counts = RouterJson.Field(metadata, "counts");
            if (kind == "all" && outcome == "finished" && day == "all" && string.IsNullOrWhiteSpace(query) || !_hasHistoryCounts)
            {
                _completedCount = RouterJson.Integer(counts, "completed");
                _failedCount = RouterJson.Integer(counts, "failed");
                _hasHistoryCounts = true;
                if (kind == "all" && outcome == "finished" && day == "all" && string.IsNullOrWhiteSpace(query) && _snapshot is { } currentSnapshot)
                    _historySignature = ComputeHistorySignature(currentSnapshot);
                UpdateOverviewStats();
            }
            _statistics = $"成功 {RouterJson.Integer(counts, "completed")} · 失败 {RouterJson.Integer(counts, "failed")} · 取消 {RouterJson.Integer(counts, "cancelled")}" + Environment.NewLine
                + string.Join("   ", RouterJson.Rows(metadata, "groups").Where(g => RouterJson.Integer(g, "count") > 0)
                    .Select(g => $"{RouterJson.Text(g, "category")} P95 排队/占用/清理 {Seconds(g, "queueSecondsP95")}/{Seconds(g, "executionSecondsP95")}/{Seconds(g, "cleanupSecondsP95")}"));
            _historySummary.Text = $"{total:N0} 条  ·  {RouterJson.Integer(counts, "failed"):N0} 失败";
            _historyReadAt = slice.CapturedAt;
            _statistics = "记录快照 " + _historyReadAt.ToLocalTime().ToString("HH:mm:ss") + Environment.NewLine + _statistics;
            _tips.SetToolTip(_historySummary, _statistics); _historySummary.AccessibleDescription = _statistics;
            if (_notice.Text == HistoryReadTimeout) _notice.Text = "";
            UpdateControls();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || generation != _generation || historyGeneration != _historyGeneration) { }
        catch (Exception e) when (Expected(e))
        {
            if (!IsDisposed && generation == _generation && historyGeneration == _historyGeneration)
                _notice.Text = e is OperationCanceledException ? HistoryReadTimeout : e.Message;
        }
    }

    private async Task CurrentControlAsync(string action)
    {
        if (_snapshot is null) return;
        var primary = RouterJson.Field(RouterJson.Field(_snapshot.View, "resourceRecovery"), "primary");
        await ControlAsync(RouterJson.Text(_snapshot.View, "requestId"),
            action == "stop" ? RouterJson.Text(primary, "action") : action,
            nativeStop: action == "stop" ? RouterJson.Text(primary, "nativeStop", "") : null);
    }
    private Task QueueControlAsync(string action, int? direction)
    {
        if (_queue.SelectedRows.Count == 0 || _queue.SelectedRows[0].Tag is not JsonElement row) return Task.CompletedTask;
        var rank = RouterJson.Integer(row, "rank");
        return ControlAsync(RouterJson.Text(row, "id"), action, RouterJson.Text(row, "version"),
            direction == 1 ? 1 : direction == -1 ? rank - 1 : direction == 0 ? rank + 1 : null, queuedOnly: action == "cancel");
    }
    private async Task ControlAsync(string requestId, string action, string? version = null, int? position = null, string? nativeStop = null, bool queuedOnly = false)
    {
        if (_snapshot is null || _controlling || !_snapshot.IsFresh(DateTimeOffset.UtcNow)) return;
        if (action != "move" && action != "release")
        {
            _modal = true;
            try
            {
                if (MessageBox.Show(this, "只处理所选原请求；停止或取消回执不等于资源已释放。\n" + requestId,
                    "确认操作", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            }
            finally { _modal = false; }
        }
        _controlling = true; UpdateControls();
        try
        {
            var result = await _client.ControlAsync(_snapshot, requestId, action, version, position,
                string.IsNullOrEmpty(nativeStop) ? null : nativeStop, queuedOnly, _stop.Token);
            if (!IsDisposed) _notice.Text = RouterJson.Text(result, "notice");
        }
        catch (Exception e) when (Expected(e)) { if (!IsDisposed) _notice.Text = e.Message + " 请刷新原请求，不会自动重试。"; }
        finally { _controlling = false; if (!IsDisposed) { UpdateControls(); await RefreshViewAsync(); } }
    }
    private async Task ExportAsync()
    {
        if (_snapshot is null) return;
        if (_snapshot.ProjectKey == RouterMonitorClient.AllProjects) { _notice.Text = "请选择单个项目后导出原始 CSV。"; return; }
        using var save = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "router-history-" + DateTime.Now.ToString("yyyyMMdd") + ".csv" };
        _modal = true;
        try { if (save.ShowDialog(this) != DialogResult.OK) return; }
        finally { _modal = false; }
        try { await File.WriteAllTextAsync(save.FileName, await _client.ExportAsync(_snapshot.ProjectKey, _stop.Token), new UTF8Encoding(true), _stop.Token); _notice.Text = save.FileName; }
        catch (Exception e) when (Expected(e)) { if (!IsDisposed) _notice.Text = e.Message; }
    }
    private void UpdateControls()
    {
        var fresh = _pinned && _available && !_controlling && _snapshot?.ProjectKey != RouterMonitorClient.AllProjects
            && _snapshot?.IsFresh(DateTimeOffset.UtcNow) == true;
        var current = fresh && RouterJson.IsHex(RouterJson.Text(_snapshot!.View, "requestId"), 32);
        _currentMenuButton.Enabled = current;
        _cancel.Enabled = _release.Enabled = current;
        var primary = _snapshot is null ? default : RouterJson.Field(RouterJson.Field(_snapshot.View, "resourceRecovery"), "primary");
        _nativeStop.Enabled = current && primary.ValueKind == JsonValueKind.Object;
        _nativeStop.Text = RouterJson.Text(primary, "label", "停止并收尾");
        var row = _queue.SelectedRows.Count == 0 ? default : _queue.SelectedRows[0].Tag is JsonElement element ? element : default;
        _queueCancel.Enabled = fresh && !_queueLoading && row.ValueKind == JsonValueKind.Object;
        _top.Enabled = _up.Enabled = _queueCancel.Enabled && RouterJson.Integer(row, "rank") > 1;
        _down.Enabled = _queueCancel.Enabled && !RouterJson.Boolean(row, "last");
        _queuePrevious.Enabled = _available && _queueOffset > 0;
        _queueNext.Enabled = _available && _queueOffset + _queuePageSize < _queueTotal;
        _historyPrevious.Enabled = _available && _historyOffset > 0;
        _historyNext.Enabled = _available && _historyOffset + _historyPageSize < _historyTotal;
        _filterToggle.Enabled = _pinned && !_controlling;
        _projects.Enabled = !_controlling;
        _query.Enabled = _kind.Enabled = _outcome.Enabled = _day.Enabled = _pinned;
    }
    private void ShowRecord(DataGridViewRow row)
    {
        if (row.Tag is not JsonElement value) return;
        _modal = true;
        try { MessageBox.Show(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), "原请求记录", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { _modal = false; }
    }
    private static string ResultName(JsonElement row) => RouterJson.Text(row, "failureClass") switch
    {
        "dispatch_preflight_failed" => "派发前拒绝",
        "input_changed_before_dispatch" => "输入变化 · 未执行", "compile_failed" => "编译失败", "validation_failed" => "测试失败",
        "queue_timeout" => "排队超时", "execution_timeout" => "执行超时", "cleanup_unconfirmed" => "清理待确认",
        _ => RouterJson.Text(row, "outcome") switch { "completed" => "成功", "failed" => "失败", "cancelled" => "取消", _ => "未结束" },
    };
    private static string TimingDetails(JsonElement row)
    {
        var lines = new List<string> { $"排队 {Seconds(row, "queueSeconds")}", $"资源占用 {Seconds(row, "executionSeconds")}" };
        if (RouterJson.Text(row, "failureClass") == "dispatch_preflight_failed") lines.Add("原生操作未执行");
        foreach (var (field, label) in new[] { ("preflightSeconds", "派发前检查"), ("dispatchRecoveryWaitSeconds", "派发恢复等待"),
                     ("nativeTestSeconds", "原生测试"), ("recoverySeconds", "恢复执行"), ("recoveryWaitSeconds", "恢复等待") })
            if (RouterJson.Number(row, field) is not null) lines.Add(label + " " + Seconds(row, field));
        lines.Add("清理 " + Seconds(row, "cleanupSeconds"));
        return string.Join(Environment.NewLine, lines);
    }
    private static string Seconds(JsonElement row, string name) => RouterJson.Number(row, name) is { } n ? $"{n:0.##}s" : "—";
    private static string SubmittedTime(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        ? time.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) : value;
    private static bool Expected(Exception e) => e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException or InvalidOperationException or RouterCompatibilityException;
    private static string Selected(DataGridView grid) => grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is JsonElement row ? RouterJson.Text(row, "id") : "";
    private static string Key(ComboBox combo) => (combo.SelectedItem as Choice)?.Key ?? "all";
    private static void Options(ComboBox combo, (string Key, string Text)[] choices) { combo.Items.Clear(); combo.Items.AddRange(choices.Select(c => new Choice(c.Key, c.Text)).ToArray()); combo.SelectedIndex = 0; }
    private static ComboBox Combo(int width) => new RouterCombo { Width = width };
    private static Label Label(string text, int width) => new() { Text = text, Width = width, Height = 31, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private static FlowLayoutPanel Flow(params Control[] children) { var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false, AutoScroll = false }; flow.Controls.AddRange(children); return flow; }
    private static RouterSurface Section(string title) => RouterSurface.Section(title);
    private static TableLayoutPanel Header(Control left, Control right, int rightWidth) => CreateHeader(left, right, rightWidth);
    private static ToolStripMenuItem MenuItem(string text, Func<Task> action)
    { var item = new ToolStripMenuItem(text); item.Click += async (_, _) => await action(); return item; }
    private int PageRows(RouterGrid grid) => Math.Clamp((grid.ClientSize.Height - grid.ColumnHeadersHeight - 2) /
        Math.Max(1, (int)Math.Round(26 * _popoverScale)), 1, 20);
    private static string PageLabel(int offset, int total, int size) => total == 0 ? "0/0" : $"{offset / size + 1}/{(total + size - 1) / size}";
    private static RouterButton Button(string text, Func<Task> action, RouterButtonKind kind = RouterButtonKind.Quiet)
    { var button = new RouterButton { Text = text, AccessibleName = text, Kind = kind }; button.Click += async (_, _) => await action(); return button; }
    private RouterButton IconButton(RouterButtonIcon icon, string label, Func<Task> action)
    { var button = Button(label, action); button.Icon = icon; button.AutoSize = true; _tips.SetToolTip(button, label); return button; }
    private static void Align(DataGridViewColumn column, DataGridViewContentAlignment alignment)
    {
        column.DefaultCellStyle.Alignment = alignment;
        column.HeaderCell.Style.Alignment = alignment;
    }
    private static DataGridViewTextBoxColumn Column(string name, string title, int width) => new() { Name = name, HeaderText = title, FillWeight = width, MinimumWidth = 50, SortMode = DataGridViewColumnSortMode.NotSortable };
    private static RouterGrid Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        EnableHeadersVisualStyles = false, ColumnHeadersDefaultCellStyle = new() { BackColor = UiPalette.Slate800, ForeColor = UiPalette.Slate300, Padding = new Padding(4) },
        DefaultCellStyle = new() { BackColor = Color.FromArgb(22, 30, 42), ForeColor = Color.FromArgb(229, 234, 243), SelectionBackColor = Color.FromArgb(42, 62, 89), SelectionForeColor = Color.White, Padding = new Padding(4) },
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None, ScrollBars = ScrollBars.None,
    };
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true; _timer.Stop(); _timer.Dispose(); _stop.Cancel();
            _historyRequest?.Dispose(); _queueRequest?.Dispose(); _stop.Dispose(); _client.Dispose();
            _tips.Dispose();
            _currentMenu.Dispose();
            _projectMenu.Dispose();

        }
        base.Dispose(disposing);
    }
    private sealed record Choice(string Key, string Text) { public override string ToString() => Text; }
}
