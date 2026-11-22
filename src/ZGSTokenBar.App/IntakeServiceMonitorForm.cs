using System.Text.Json;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed record IntakeServiceObservation(int Projects, int History, int InFlight, string Status,
    int? PendingReview, int? ProtectedArchive, int? Unconfirmed, int ClosedHistory, DateTimeOffset? ObservedAt)
{
    internal int Recent => History;
}

/// <summary>The same native monitor host and table controls as Router; no workflow actions.</summary>
internal sealed class IntakeServiceMonitorForm : MonitorPanelForm
{
    private readonly bool _zh;
    private readonly IntakeServiceMonitorClient _client;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly RouterButton _refresh = new() { Icon = RouterButtonIcon.Refresh }, _pin = new() { Icon = RouterButtonIcon.Pin },
        _close = new() { Icon = RouterButtonIcon.Close }, _workItems = new();
    private readonly RouterStatusBadge _headline = new() { Margin = Padding.Empty };
    private readonly IntakeOverviewStrip _overview = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _freshness = Label(), _serviceScope = Label(), _progressHeading = Label(), _progressSub = Label(), _historyHeading = Label(), _historySummary = Label();
    private readonly TableLayoutPanel _root;
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
    private bool _recordOpen, _lastProgressUnavailable, _lastProgressLoading = true;
    private int _lastProgressCount = -1;
    internal event EventHandler? WorkItemsRequested;
    internal event Action<int?>? ProcessingObserved;
    internal event Action<IReadOnlyList<IntakeServiceResult>>? EvidenceObserved;
    internal IntakeServiceObservation Observation { get; private set; } = new(0, 0, 0, "", null, null, null, 0, null);
    public override bool InteractionOpen => _recordOpen || _projectMenu.Visible;

    internal IntakeServiceMonitorForm(bool chinese, IntakeServiceMonitorClient client, bool observe = true)
    {
        _zh = chinese; _client = client; _observe = observe;
        Text = T("工单与分拣", "Work intake");
        SuspendLayout();
        _root = CreatePanelLayout(currentHeight: 78, footerHeight: 0);
        Controls.Add(_root);
        var brand = new MonitorBrandMark(T("自动分拣", "Intake"), intake: true) { Width = 140, Height = 30 };
        _freshness.Width = 140;
        _root.Controls.Add(CreateNavigation(CreateHeader(Flow(brand), Flow(_freshness, _refresh, _pin, _close), 240),
            _projects, T("项目", "Project")), 0, 0);
        _workItems.Text = T("工单详情", "Work items");
        _workItems.Kind = RouterButtonKind.Primary;
        _workItems.Click += (_, _) => { if (_pinned) WorkItemsRequested?.Invoke(this, EventArgs.Empty); };
        _refresh.AccessibleName = T("刷新", "Refresh"); _pin.AccessibleName = T("固定弹层", "Pin"); _close.AccessibleName = T("收起弹层", "Close");
        foreach (var button in new[] { _refresh, _pin, _close }) _tips.SetToolTip(button, button.AccessibleName);
        var actions = Flow(_workItems);
        actions.FlowDirection = FlowDirection.RightToLeft;
        var statusTopLeft = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        statusTopLeft.ColumnStyles.Add(new(SizeType.AutoSize));
        statusTopLeft.ColumnStyles.Add(new(SizeType.Percent, 100));
        statusTopLeft.RowStyles.Add(new(SizeType.Percent, 100));
        _serviceScope.Margin = new Padding(8, 0, 0, 0);
        statusTopLeft.Controls.Add(_headline, 0, 0);
        statusTopLeft.Controls.Add(_serviceScope, 1, 0);
        var statusBody = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 2 };
        statusBody.ColumnStyles.Add(new(SizeType.Percent, 100));
        statusBody.RowStyles.Add(new(SizeType.Absolute, 28));
        statusBody.RowStyles.Add(new(SizeType.Percent, 100));
        statusBody.Controls.Add(CreateHeader(statusTopLeft, actions, 122), 0, 0);
        statusBody.Controls.Add(_overview, 0, 1);
        var surface = RouterSurface.Section(T("服务状态", "Service status"), featured: true);
        surface.Padding = new Padding(10, 6, 10, 6);
        surface.Margin = new Padding(0, 0, 0, 8);
        surface.Controls.Add(statusBody);
        _root.Controls.Add(surface, 0, 1);
        IntakeObserverForm.AddColumn(_progress, T("工单与摘要", "Work item"), 360);
        IntakeObserverForm.AddColumn(_progress, T("项目 · 操作", "Project · Action"), 155);
        IntakeObserverForm.AddColumn(_progress, T("当前状态", "Status"), 165);
        IntakeObserverForm.AddColumn(_history, T("观测时间", "Observed"), 108);
        IntakeObserverForm.AddColumn(_history, T("工单与摘要", "Work item"), 330);
        IntakeObserverForm.AddColumn(_history, T("操作", "Action"), 105);
        IntakeObserverForm.AddColumn(_history, T("状态", "Status"), 165);
        _progress.Columns[1].DefaultCellStyle.ForeColor = UiTokens.TextSecondary;
        _history.Columns[0].DefaultCellStyle.ForeColor = UiTokens.Muted;
        _history.Columns[2].DefaultCellStyle.ForeColor = UiTokens.TextSecondary;
        _progress.Columns[2].HeaderCell.Style.Alignment = _history.Columns[3].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _progress.CellPainting += PaintStage;
        _history.CellPainting += PaintStage;
        foreach (var grid in new[] { _progress, _history })
        {
            grid.CellDoubleClick += (_, e) =>
            {
                if (_pinned && e.RowIndex >= 0 && grid.Rows[e.RowIndex].Tag is IntakeServiceRecord record) ShowRecord(record);
            };
        }
        _tips.SetToolTip(_history, T("观测时间来自服务日记；缺少时间戳的旧回执显示 —", "Times come from service observations; older receipts without timestamps show —"));
        _root.Controls.Add(Table(T("当前回执", "Current receipts"), _progress, _progressHeading, _progressSub), 0, 2);
        var historySection = Table(T("运行历史", "Service history"), _history, _historyHeading, _historySummary);
        historySection.Margin = Padding.Empty;
        _root.Controls.Add(historySection, 0, 3);
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
        SetLogicalFont(_freshness, "Segoe UI", 10.5f);
        SetLogicalFont(_serviceScope, "Segoe UI", 10.5f);
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
            EvidenceObserved?.Invoke(readings);
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
        var progress = _last.Values.SelectMany(r => r.Progress).Where(r => Match(r) && !r.IsHistoryOnly).ToArray();
        var history = _last.Values.SelectMany(r => r.History).Where(Match)
            .OrderByDescending(r => r.IsHistoryOnly).ThenByDescending(r => r.ObservedAt).Take(160)
            .OrderByDescending(r => r.ObservedAt).ToArray();
        Rows(_progress, progress, r => [ItemTitle(r), r.Project.Length > 0 ? r.Project + " · " + Kind(r.Kind) : Kind(r.Kind), Stage(r)]);
        Rows(_history, history, r => [r.ObservedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—", ItemTitle(r), Kind(r.Kind), Stage(r)]);
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
            : _last.Count > 0 ? T("服务在线", "Service online")
            : T("正在连接分拣服务…", "Connecting to intake service…");
        var observedAt = _last.Count > 0 ? _last.Values.Max(r => r.ObservedAt) : (DateTimeOffset?)null;
        if (observedAt is { } seen)
            _freshness.Text = (unavailable ? T("上次读取 ", "Last read ") : T("更新于 ", "Updated "))
                + seen.ToLocalTime().ToString("HH:mm:ss");
        var tone = unavailable ? "warning"
            : current.Any(r => !r.Active) ? "offline" : loading ? "idle" : "running";
        var accent = UiTokens.State(tone);
        if (_headline.Accent != accent) { _headline.Accent = accent; _headline.Invalidate(); }
        _tips.SetToolTip(_headline, configurationUnavailable
            ? T("检查本机分拣服务配置 intake-services.json", "Check the local intake-services.json configuration") : "");
        var pending = progress.Count(r => r.Review == "attention");
        var protectedArchive = progress.Count(r => r.Review == "archive_protected");
        var unconfirmed = progress.Count(r => r.Review == "unconfirmed");
        var countsKnown = !loading && !unavailable && current.All(r => r.Active);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var hasTimedHistory = history.Any(r => r.ObservedAt is not null);
        var todayHistory = history.Count(r => r.ObservedAt is { } at && DateOnly.FromDateTime(at.ToLocalTime().DateTime) == today);
        var closedHistory = history.Count(r => r.Review == "source_closed");

        _serviceScope.Text = _project is not null
            ? T($"筛选项目：{_project}", $"Project: {_project}")
            : countsKnown && projects.Length > 0
                ? T($"已接入 {projects.Length:N0} 个项目", $"{projects.Length:N0} projects")
                : "";
        _serviceScope.Visible = _serviceScope.Text.Length > 0;

        var overviewSummary = BuildOverviewSummary(countsKnown, active, pending, protectedArchive, unconfirmed, todayHistory, hasTimedHistory, closedHistory);
        var overviewFallback = loading
            ? T("正在汇总当前回执与历史指标…", "Loading service metrics…")
            : unavailable
                ? T("当前服务观测不完整或暂不可读，暂不汇总总量。", "Observations are incomplete or unavailable; totals are withheld.")
                : T("分拣服务未运行，当前统计暂不可用。", "Intake service is stopped; live metrics are withheld.");
        var overviewTip = countsKnown
            ? T("归档保护：原对话仍在活动或有新增内容，已跳过归档；待核对/待确认：需要人工介入或补充证据。",
                "Archive protected: conversation is still active or changed; Needs review/Unconfirmed: requires follow-up or confirmation.")
            : overviewFallback;
        _overview.Update(
            new(countsKnown, active, pending, protectedArchive, unconfirmed, todayHistory, hasTimedHistory, closedHistory, overviewFallback, _zh),
            overviewSummary,
            overviewTip);
        _tips.SetToolTip(_overview, overviewTip);

        _progressHeading.Text = T("当前回执", "Current receipts");
        _progressSub.Text = progress.Length > 0
            ? T($"{progress.Length:N0} 条", $"{progress.Length:N0} items")
            : countsKnown ? T("全部清空", "All clear") : "";
        Observation = new(projects.Length, history.Length, _last.Values.SelectMany(r => r.Progress).Count(r => r.InFlight), _headline.Text,
            countsKnown ? pending : null, countsKnown ? protectedArchive : null, countsKnown ? unconfirmed : null,
            closedHistory, observedAt);
        _historyHeading.Text = T("运行历史", "Service history");
        if (history.Length == 0 || unavailable)
        {
            _historySummary.Text = "";
        }
        else
        {
            var historyParts = new List<string>(3) { T($"{history.Length:N0} 条", $"{history.Length:N0}") };
            if (hasTimedHistory) historyParts.Add(T($"今日 {todayHistory:N0}", $"Today {todayHistory:N0}"));
            if (closedHistory > 0) historyParts.Add(T($"已结案 {closedHistory:N0}", $"Closed {closedHistory:N0}"));
            _historySummary.Text = string.Join("  ·  ", historyParts);
        }

        UpdateProgressRowHeight(progress.Length, unavailable, loading);
        SetEmptyState(_progress, unavailable ? T("在途状态暂不可确认", "In-flight state is unconfirmed")
            : loading ? T("正在读取分拣进度…", "Reading intake progress…") : T("暂无当前回执", "No current receipts"));
        SetEmptyState(_history, unavailable ? T("历史记录暂不可用", "Service history is unavailable")
            : loading ? T("正在读取运行历史…", "Reading service history…") : T("暂无服务历史记录", "No retained service history"));
    }

    private string BuildOverviewSummary(bool countsKnown, int active, int pending, int protectedArchive, int unconfirmed,
        int todayHistory, bool hasTimedHistory, int closedHistory)
    {
        if (!countsKnown) return "";
        var parts = new List<string>(6)
        {
            T($"在途 {active:N0}", $"In flight {active:N0}"),
            unconfirmed > 0 ? T($"已知待核 {pending:N0}", $"Known review {pending:N0}") : T($"待核对 {pending:N0}", $"Needs review {pending:N0}"),
        };
        if (unconfirmed > 0) parts.Add(T($"待确认 {unconfirmed:N0}", $"Unconfirmed {unconfirmed:N0}"));
        parts.Add(T($"归档保护 {protectedArchive:N0}", $"Archive protected {protectedArchive:N0}"));
        if (hasTimedHistory) parts.Add(T($"今日记录 {todayHistory:N0}", $"Today {todayHistory:N0}"));
        if (closedHistory > 0) parts.Add(T($"已结案 {closedHistory:N0}", $"Closed {closedHistory:N0}"));
        return string.Join(" · ", parts);
    }

    private void UpdateProgressRowHeight(int progressCount, bool unavailable, bool loading)
    {
        _lastProgressCount = progressCount;
        _lastProgressUnavailable = unavailable;
        _lastProgressLoading = loading;
        var row = _root.RowStyles[2];
        SizeType targetType;
        float targetHeight;
        bool headersVisible;
        if (!unavailable && !loading && progressCount == 0)
        {
            headersVisible = false;
            targetType = SizeType.Absolute;
            targetHeight = (int)Math.Round(82 * _popoverScale);
        }
        else if (progressCount is >= 1 and <= 4)
        {
            headersVisible = true;
            targetType = SizeType.Absolute;
            targetHeight = (int)Math.Round((86 + 26 * progressCount) * _popoverScale);
        }
        else
        {
            headersVisible = true;
            targetType = SizeType.Percent;
            targetHeight = 50;
        }
        var changed = _progress.ColumnHeadersVisible != headersVisible
            || row.SizeType != targetType
            || Math.Abs(row.Height - targetHeight) >= 0.5f;
        if (!changed) return;
        _progress.ColumnHeadersVisible = headersVisible;
        row.SizeType = targetType;
        row.Height = targetHeight;
        _root.PerformLayout();
    }

    private static void SetEmptyState(RouterGrid grid, string title)
    {
        if (grid.EmptyTitle == title && grid.EmptyDetail.Length == 0) return;
        grid.EmptyTitle = title;
        grid.EmptyDetail = "";
        if (grid.RowCount == 0) grid.Invalidate();
    }

    private Control Table(string title, RouterGrid grid, Label heading, Label summary)
    {
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 2 };
        body.ColumnStyles.Add(new(SizeType.Percent, 100)); body.RowStyles.Add(new(SizeType.Absolute, 30)); body.RowStyles.Add(new(SizeType.Percent, 100));
        heading.Text = title;
        heading.Dock = DockStyle.None;
        heading.AutoSize = true;
        heading.Anchor = AnchorStyles.Left;
        heading.Margin = Padding.Empty;
        SetLogicalFont(heading, "Segoe UI", UiTokens.FontBody, FontStyle.Bold);
        summary.Margin = new Padding(8, 0, 0, 0);
        SetLogicalFont(summary, "Segoe UI", 10.5f);
        var titleRow = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        titleRow.ColumnStyles.Add(new(SizeType.AutoSize));
        titleRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        titleRow.RowStyles.Add(new(SizeType.Percent, 100));
        titleRow.Controls.Add(heading, 0, 0);
        titleRow.Controls.Add(summary, 1, 0);
        var pager = new MonitorTablePager(grid, hideSinglePage: true);
        body.Controls.Add(CreateHeader(titleRow, Flow(pager.Buttons), 114), 0, 0); body.Controls.Add(grid, 0, 1);
        var section = RouterSurface.Section(title); section.Controls.Add(body); return section;
    }
    private void PaintStage(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (sender is not RouterGrid grid || e.RowIndex < 0
            || e.ColumnIndex != grid.ColumnCount - 1 || grid.Rows[e.RowIndex].Tag is not IntakeServiceRecord row) return;
        grid.PaintPill(e, row.Review switch
        {
            "archive_protected" or "archive_retained" or "source_advanced" => UiTokens.Muted,
            "archive_waiting" => UiTokens.Info,
            "source_closed" => UiTokens.Positive,
            "attention" or "unconfirmed" => UiTokens.Warning,
            _ => StageColor(row.Phase),
        });
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
                var tooltip = item.ReviewQuestion.Length > 0 ? item.Title + Environment.NewLine + item.ReviewQuestion : item.Title;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = tooltip;
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
        ApplyMonitorPalette(palette);
        _headline.ForeColor = palette.Text;
        _overview.ApplyPalette(palette);
        if (_lastProgressCount >= 0) UpdateProgressRowHeight(_lastProgressCount, _lastProgressUnavailable, _lastProgressLoading);
        _projectMenu.Renderer = new DarkContextMenuRenderer(palette.Surface, palette.Input, palette.Border, palette.Text, palette.Muted);
        _projectMenu.Font = Font;
    }
    private string T(string zh, string en) => _zh ? zh : en;
    private static string Item(IntakeServiceRecord r) => r.Number.Length > 0 ? r.Number : r.ItemId.Length > 8 ? r.ItemId[..8] : "—";
    private static string ItemTitle(IntakeServiceRecord r)
    {
        var item = Item(r);
        return r.Title.Length == 0 ? item : item == "—" ? r.Title : item + " · " + r.Title;
    }
    private string Kind(string kind) => kind.Replace("receipt:", "", StringComparison.Ordinal) switch
    { "result" or "precheck" => T("分拣", "Triage"), "rework" => T("返工续办", "Rework"), "archive" => T("验收后归档", "Archive"),
      "related" => T("关联续办", "Related"), "original_continuation" => T("原对话补交", "Original delivery"),
      "ready" => T("服务启动", "Service start"), "error" => T("服务异常", "Service error"), _ => kind };

    private string Stage(IntakeServiceRecord record) => record.Review switch
    {
        "archive_protected" => T("归档保护", "Archive protected"),
        "archive_waiting" => T("等待对话空闲", "Waiting for conversation to be idle"),
        "archive_retained" => T("续聊已保留", "Conversation retained"),
        "source_closed" => T("原卡已结案", "Source item closed"),
        "source_advanced" => T("原卡已进入后续阶段", "Source item advanced"),
        "unconfirmed" => T("待确认", "Unconfirmed"),
        "attention" when record.ReviewQuestion.Length > 0 => T("待补证据", "Evidence requested"),
        "attention" => T("待核对", "Needs review"),
        _ => Phase(record.Phase),
    };

    private void ShowRecord(IntakeServiceRecord record)
    {
        var lines = new List<string> { T("── 回执概况", "── Receipt overview") };
        if (record.Project.Length > 0) lines.Add(T("项目  ", "Project  ") + record.Project);
        lines.Add(T("工单  ", "Work item  ") + Item(record));
        lines.Add(T("操作  ", "Action  ") + Kind(record.Kind));
        lines.Add(T("状态  ", "Status  ") + Stage(record));
        if (record.ObservedAt is { } at) lines.Add(T("观测时间  ", "Observed  ") + at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        if (record.SourceStatus.Length > 0) lines.Add(T("原卡状态  ", "Source status  ") + record.SourceStatus);

        var notes = new List<string>();
        if (record.ReviewQuestion.Length > 0) notes.Add(T("待补证据  ", "Question  ") + record.ReviewQuestion);
        if (record.Review == "archive_protected")
            notes.Add(T("原对话有变化或仍在活动，已保留。", "The original conversation changed or is still active and was kept."));
        else if (record.Review == "archive_retained")
            notes.Add(T("已确认原对话有后续内容，保留该对话。", "Follow-up activity was confirmed and the conversation was kept."));
        else if (record.IsHistoryOnly && record.ReasonCode == "need_evidence")
            notes.Add(T("这条旧回执曾要求补充证据。", "This earlier receipt requested more evidence."));
        if (notes.Count > 0)
        {
            lines.Add("");
            lines.Add(T("── 补充说明", "── Details"));
            lines.AddRange(notes);
        }
        using var dialog = new MonitorRecordDialog(T("分拣回执", "Intake receipt"), ItemTitle(record),
            string.Join(Environment.NewLine, lines), JsonSerializer.Serialize(record), _zh);
        _recordOpen = true;
        try { dialog.ShowDialog(this); }
        finally { _recordOpen = false; }
    }

    private string Phase(string phase) => phase switch
    { "model_started" => T("分类请求已发出", "Classification requested"), "sending" => T("准备提交", "Preparing submission"),
      "waiting_upstream" => T("等待上游恢复", "Waiting for upstream"), "waiting_original" => T("等待原任务", "Waiting for original"),
      "submitting" => T("正在提交续办", "Submitting continuation"), "submitted" => T("已提交待回执", "Submitted; awaiting receipt"),
      "retry" or "archive_retry" => T("等待重查", "Waiting for reconciliation"), "classified" => T("分类已记录", "Classification recorded"),
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
