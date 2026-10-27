using System.Text;
using System.Text.Json;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

/// <summary>Native presentation only. All execution, fairness and release decisions belong to Router.</summary>
internal sealed class RouterMonitorForm : Form
{
    private readonly RouterMonitorClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly ComboBox _projects = Combo(230);
    private readonly Label _freshness = Label("连接中", 350);
    private readonly Label _headline = Label("读取中", 760);
    private readonly Label _task = Label("", 760);
    private readonly Label _detail = Label("", 760);
    private readonly Label _notice = Label("", 760);
    private readonly Label _queuePage = Label("", 180);
    private readonly Label _historyPage = Label("", 180);
    private readonly Label _statistics = Label("", 760);
    private readonly DataGridView _queue = Grid();
    private readonly DataGridView _history = Grid();
    private readonly ComboBox _kind = Combo(110), _outcome = Combo(110), _day = Combo(135);
    private readonly TextBox _query = new() { Width = 190, MaxLength = 160, PlaceholderText = "搜索任务或对话" };
    private readonly Panel _pages = new() { Dock = DockStyle.Fill };
    private readonly List<Button> _pageTabs = [];
    private readonly Button _cancel, _release, _nativeStop, _queueCancel, _top, _up, _down;
    private readonly List<Control> _controls = [];
    private RouterSnapshot? _snapshot;
    private bool _refreshing, _controlling, _binding, _available, _disposed;
    private int _queueOffset, _historyOffset, _generation, _selectedPage;
    private string? _project;
    internal RouterSnapshot? Snapshot => _snapshot;
    internal string FreshnessText => _freshness.Text;

    internal RouterMonitorForm(RouterMonitorClient? client = null, bool observe = true)
    {
        _client = client ?? new();
        SuspendLayout();
        Text = "Router";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        BackColor = Color.FromArgb(17, 21, 29);
        ForeColor = Color.FromArgb(229, 234, 243);
        MinimumSize = new Size(820, 540);
        ClientSize = new Size(970, 680);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7 };
        root.RowStyles.Add(new(SizeType.Absolute, 42));
        root.RowStyles.Add(new(SizeType.Absolute, 40));
        root.RowStyles.Add(new(SizeType.Absolute, 34));
        root.RowStyles.Add(new(SizeType.Absolute, 64));
        root.RowStyles.Add(new(SizeType.Absolute, 40));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 40));
        Controls.Add(root);
        var header = Flow(_projects, _freshness, Button("刷新", async () => await RefreshViewAsync()));
        root.Controls.Add(header, 0, 0);
        _headline.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
        _headline.Height = 38;
        _task.Height = 30;
        _detail.Height = 62;
        _notice.Height = 38;
        _statistics.Dock = DockStyle.Fill;
        root.Controls.Add(_headline, 0, 1);
        root.Controls.Add(_task, 0, 2);
        root.Controls.Add(_detail, 0, 3);
        _pageTabs.Add(Button("当前与队列", async () => { SelectPage(0); await RefreshViewAsync(); }));
        _pageTabs.Add(Button("历史记录", async () => { SelectPage(1); await HistoryAsync(); }));
        root.Controls.Add(Flow(_pageTabs.ToArray()), 0, 4);
        root.Controls.Add(_pages, 0, 5);
        root.Controls.Add(_notice, 0, 6);

        var queueTab = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, ForeColor = ForeColor };
        var historyTab = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, ForeColor = ForeColor };
        _pages.Controls.AddRange([queueTab, historyTab]);
        _queue.Columns.AddRange(Column("task", "任务", 270), Column("source", "来源对话", 210), Column("wait", "等待", 80));
        _queue.SelectionChanged += (_, _) => UpdateControls();
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        body.RowStyles.Add(new(SizeType.Absolute, 44)); body.RowStyles.Add(new(SizeType.Percent, 100)); body.RowStyles.Add(new(SizeType.Absolute, 44));
        _nativeStop = Button("停止并收尾", () => CurrentControlAsync("stop"));
        _cancel = Button("请求取消", () => CurrentControlAsync("cancel"));
        _release = Button("复检清理", () => CurrentControlAsync("release"));
        body.Controls.Add(Flow(_nativeStop, _cancel, _release), 0, 0);
        body.Controls.Add(_queue, 0, 1);
        _top = Button("置顶", () => QueueControlAsync("move", 1));
        _up = Button("上移", () => QueueControlAsync("move", -1));
        _down = Button("下移", () => QueueControlAsync("move", 0));
        _queueCancel = Button("取消排队", () => QueueControlAsync("cancel", null));
        body.Controls.Add(Flow(_top, _up, _down, _queueCancel,
            Button("上一页", async () => { _queueOffset = Math.Max(0, _queueOffset - 20); await RefreshViewAsync(); }),
            _queuePage, Button("下一页", async () => { if (_snapshot is not null && _queueOffset + 20 < RouterJson.Integer(_snapshot.View, "queuedCount")) _queueOffset += 20; await RefreshViewAsync(); })), 0, 2);
        queueTab.Controls.Add(body);
        _controls.AddRange([_nativeStop, _cancel, _release, _top, _up, _down, _queueCancel]);

        var historyBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        historyBody.RowStyles.Add(new(SizeType.Absolute, 44)); historyBody.RowStyles.Add(new(SizeType.Percent, 100));
        historyBody.RowStyles.Add(new(SizeType.Absolute, 44)); historyBody.RowStyles.Add(new(SizeType.Absolute, 50));
        Options(_kind, [("all", "全部类型"), ("compile", "编译"), ("editmode", "EditMode"), ("playmode", "PlayMode"), ("script", "脚本")]);
        Options(_outcome, [("all", "全部结果"), ("completed", "成功"), ("failed", "失败"), ("cancelled", "取消"), ("pending", "未结束")]);
        Options(_day, [("all", "全部日期")]);
        historyBody.Controls.Add(Flow(_kind, _outcome, _day, _query,
            Button("筛选", async () => { _historyOffset = 0; await HistoryAsync(); }), Button("导出 CSV", ExportAsync)), 0, 0);
        _history.Columns.AddRange(Column("createdLocal", "提交时间", 145), Column("task", "任务", 240),
            Column("source", "来源对话", 155), Column("state", "结果", 90), Column("queueSeconds", "等待", 65),
            Column("executionSeconds", "执行", 65), Column("cleanupSeconds", "清理", 65), Column("cleanupVerified", "释放", 55));
        _history.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowRecord(_history.Rows[e.RowIndex]); };
        historyBody.Controls.Add(_history, 0, 1);
        historyBody.Controls.Add(Flow(Button("上一页", async () => { _historyOffset = Math.Max(0, _historyOffset - 25); await HistoryAsync(); }), _historyPage,
            Button("下一页", async () => { if (int.TryParse(_history.Tag?.ToString(), out var total) && _historyOffset + 25 < total) _historyOffset += 25; await HistoryAsync(); })), 0, 2);
        historyBody.Controls.Add(_statistics, 0, 3);
        historyTab.Controls.Add(historyBody);
        _projects.SelectedIndexChanged += async (_, _) =>
        {
            if (_binding) return;
            _project = (_projects.SelectedItem as Choice)?.Key; _generation++; _queueOffset = _historyOffset = 0;
            _snapshot = null; UpdateControls(); await RefreshViewAsync();
            if (_selectedPage == 1) await HistoryAsync();
        };
        _timer.Tick += async (_, _) => await RefreshViewAsync();
        if (observe) Shown += async (_, _) => { _timer.Start(); await RefreshViewAsync(); };
        SelectPage(0);
        UpdateControls();
        ResumeLayout(true);
    }

    internal void SelectPage(int page)
    {
        _selectedPage = Math.Clamp(page, 0, 1);
        for (var i = 0; i < _pages.Controls.Count; i++)
        {
            _pages.Controls[i].Visible = i == _selectedPage;
            _pageTabs[i].BackColor = i == _selectedPage ? Color.FromArgb(37, 74, 106) : Color.FromArgb(30, 41, 59);
        }
    }

    internal void ApplySnapshot(RouterSnapshot snapshot)
    {
        _snapshot = snapshot; _project = snapshot.ProjectKey;
        _available = true;
        _binding = true;
        var choices = snapshot.Projects.EnumerateArray().Select(p => new Choice(RouterJson.Text(p, "key"), RouterJson.Text(p, "name"))).ToArray();
        if (!choices.Select(c => c.Key).SequenceEqual(_projects.Items.Cast<Choice>().Select(c => c.Key)))
        { _projects.Items.Clear(); _projects.Items.AddRange(choices); }
        _projects.SelectedItem = _projects.Items.Cast<Choice>().FirstOrDefault(c => c.Key == snapshot.ProjectKey);
        _binding = false;
        _headline.Text = RouterJson.Text(snapshot.View, "headline", "状态未确认");
        _task.Text = RouterJson.Text(snapshot.View, "task");
        var owner = RouterJson.Text(RouterJson.Field(snapshot.View, "owner"), "label");
        _detail.Text = owner + "  ·  已运行 " + RouterJson.Text(snapshot.View, "elapsed") + "  ·  "
            + RouterJson.Text(snapshot.View, "progressClock") + Environment.NewLine
            + RouterJson.Text(snapshot.View, "detail") + "  " + RouterJson.Text(snapshot.View, "error");
        _freshness.Text = snapshot.IsFresh(DateTimeOffset.UtcNow) ? "更新于 " + snapshot.ReceivedAt.ToLocalTime().ToString("HH:mm:ss") : "数据过期 · 状态未确认";
        _freshness.ForeColor = snapshot.IsFresh(DateTimeOffset.UtcNow) ? Color.FromArgb(148, 163, 184) : Color.Salmon;
        UpdateControls();
    }

    internal void ApplyUnavailable(string message)
    {
        _available = false;
        _freshness.Text = "连接中断 · 状态未确认"; _freshness.ForeColor = Color.Salmon;
        _notice.Text = message;
        foreach (var button in _controls) button.Enabled = false;
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
            if (_selectedPage == 0)
            {
                var page = await _client.QueueAsync(snapshot.ProjectKey, _queueOffset, _stop.Token);
                if (generation != _generation || IsDisposed) return;
                var selected = Selected(_queue);
                _queue.Rows.Clear();
                foreach (var row in RouterJson.Rows(page, "items"))
                {
                    var n = _queue.Rows.Add(RouterJson.Text(row, "task"), RouterJson.Text(row, "source"), RouterJson.Text(row, "wait"));
                    _queue.Rows[n].Tag = row;
                    _queue.Rows[n].Selected = RouterJson.Text(row, "id") == selected;
                }
                _queueOffset = RouterJson.Integer(page, "offset");
                _queuePage.Text = $"{_queueOffset + (_queue.Rows.Count > 0 ? 1 : 0)}–{_queueOffset + _queue.Rows.Count} / {RouterJson.Integer(page, "total")}";
                UpdateControls();
            }
        }
        catch (Exception e) when (Expected(e)) { if (!IsDisposed && !_stop.IsCancellationRequested) ApplyUnavailable(e.Message); }
        finally { _refreshing = false; }
    }

    internal async Task HistoryAsync()
    {
        if (_snapshot is null || _controlling || IsDisposed) return;
        var project = _snapshot.ProjectKey;
        var generation = _generation;
        try
        {
            var page = await _client.HistoryAsync(project, Key(_kind), Key(_outcome), Key(_day), _query.Text, _historyOffset, _stop.Token);
            if (generation != _generation || IsDisposed) return;
            _history.Rows.Clear();
            foreach (var row in RouterJson.Rows(page, "items"))
            {
                var n = _history.Rows.Add(RouterJson.Text(row, "createdLocal").Replace('T', ' '), RouterJson.Text(row, "task"),
                    RouterJson.Text(row, "source", RouterJson.Text(row, "threadId")), ResultName(row), Seconds(row, "queueSeconds"),
                    Seconds(row, "executionSeconds"), Seconds(row, "cleanupSeconds"), RouterJson.Boolean(row, "cleanupVerified") ? "✓" : "待确认");
                _history.Rows[n].Tag = row;
            }
            var total = RouterJson.Integer(page, "total"); _history.Tag = total;
            _historyPage.Text = $"{_historyOffset + (_history.Rows.Count > 0 ? 1 : 0)}–{_historyOffset + _history.Rows.Count} / {total}";
            var selectedDay = Key(_day);
            Options(_day, new[] { ("all", "全部日期") }.Concat(RouterJson.Rows(page, "days").Select(d => (RouterJson.Text(d, "date"), RouterJson.Text(d, "date")))).ToArray());
            _day.SelectedItem = _day.Items.Cast<Choice>().FirstOrDefault(c => c.Key == selectedDay) ?? _day.Items[0];
            var counts = RouterJson.Field(page, "counts");
            _statistics.Text = $"成功 {RouterJson.Integer(counts, "completed")} · 失败 {RouterJson.Integer(counts, "failed")} · 取消 {RouterJson.Integer(counts, "cancelled")} · 未结束 {RouterJson.Integer(counts, "pending")}" + Environment.NewLine
                + string.Join("   ", RouterJson.Rows(page, "groups").Where(g => RouterJson.Integer(g, "count") > 0)
                    .Select(g => $"{RouterJson.Text(g, "category")} P95 等待/执行/清理 {Seconds(g, "queueSecondsP95")}/{Seconds(g, "executionSecondsP95")}/{Seconds(g, "cleanupSecondsP95")}"));
        }
        catch (Exception e) when (Expected(e)) { if (!IsDisposed) _notice.Text = e.Message; }
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
        if (action != "move" && action != "release" && MessageBox.Show(this,
            "只处理所选原请求；停止或取消回执不等于资源已释放。\n" + requestId,
            "确认操作", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
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
        using var save = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "router-history-" + DateTime.Now.ToString("yyyyMMdd") + ".csv" };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        try { await File.WriteAllTextAsync(save.FileName, await _client.ExportAsync(_snapshot.ProjectKey, _stop.Token), new UTF8Encoding(true), _stop.Token); _notice.Text = save.FileName; }
        catch (Exception e) when (Expected(e)) { if (!IsDisposed) _notice.Text = e.Message; }
    }
    private void UpdateControls()
    {
        var fresh = _available && !_controlling && _snapshot?.IsFresh(DateTimeOffset.UtcNow) == true;
        var current = fresh && RouterJson.IsHex(RouterJson.Text(_snapshot!.View, "requestId"), 32);
        _cancel.Enabled = _release.Enabled = current;
        var primary = _snapshot is null ? default : RouterJson.Field(RouterJson.Field(_snapshot.View, "resourceRecovery"), "primary");
        _nativeStop.Enabled = current && primary.ValueKind == JsonValueKind.Object;
        _nativeStop.Text = RouterJson.Text(primary, "label", "停止并收尾");
        var row = _queue.SelectedRows.Count == 0 ? default : _queue.SelectedRows[0].Tag is JsonElement element ? element : default;
        _queueCancel.Enabled = fresh && row.ValueKind == JsonValueKind.Object;
        _top.Enabled = _up.Enabled = _queueCancel.Enabled && RouterJson.Integer(row, "rank") > 1;
        _down.Enabled = _queueCancel.Enabled && !RouterJson.Boolean(row, "last");
        _projects.Enabled = !_controlling;
    }
    private static void ShowRecord(DataGridViewRow row)
    {
        if (row.Tag is not JsonElement value) return;
        MessageBox.Show(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), "原请求记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private static string ResultName(JsonElement row) => RouterJson.Text(row, "failureClass") switch
    {
        "input_changed_before_dispatch" => "输入变化 · 未执行", "compile_failed" => "编译失败", "validation_failed" => "测试失败",
        "queue_timeout" => "排队超时", "execution_timeout" => "执行超时", "cleanup_unconfirmed" => "清理待确认",
        _ => RouterJson.Text(row, "outcome") switch { "completed" => "成功", "failed" => "失败", "cancelled" => "取消", _ => "未结束" },
    };
    private static string Seconds(JsonElement row, string name) => RouterJson.Number(row, name) is { } n ? $"{n:0.##}s" : "—";
    private static bool Expected(Exception e) => e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or IOException or InvalidOperationException or RouterCompatibilityException;
    private static string Selected(DataGridView grid) => grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Tag is JsonElement row ? RouterJson.Text(row, "id") : "";
    private static string Key(ComboBox combo) => (combo.SelectedItem as Choice)?.Key ?? "all";
    private static void Options(ComboBox combo, (string Key, string Text)[] choices) { combo.Items.Clear(); combo.Items.AddRange(choices.Select(c => new Choice(c.Key, c.Text)).ToArray()); combo.SelectedIndex = 0; }
    private static ComboBox Combo(int width) => new() { Width = width, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(30, 41, 59), ForeColor = Color.White };
    private static Label Label(string text, int width) => new() { Text = text, Width = width, Height = 31, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private static FlowLayoutPanel Flow(params Control[] children) { var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false, AutoScroll = true }; flow.Controls.AddRange(children); return flow; }
    private static Button Button(string text, Func<Task> action) { var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Height = 31, BackColor = Color.FromArgb(30, 41, 59), ForeColor = Color.White }; button.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85); button.Click += async (_, _) => await action(); return button; }
    private static DataGridViewTextBoxColumn Column(string name, string title, int width) => new() { Name = name, HeaderText = title, FillWeight = width, MinimumWidth = 50, SortMode = DataGridViewColumnSortMode.NotSortable };
    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.FromArgb(17, 21, 29), BorderStyle = BorderStyle.None,
        EnableHeadersVisualStyles = false, ColumnHeadersDefaultCellStyle = new() { BackColor = Color.FromArgb(30, 41, 59), ForeColor = Color.FromArgb(203, 213, 225), Padding = new Padding(4) },
        DefaultCellStyle = new() { BackColor = Color.FromArgb(22, 30, 42), ForeColor = Color.FromArgb(229, 234, 243), SelectionBackColor = Color.FromArgb(42, 62, 89), SelectionForeColor = Color.White, Padding = new Padding(4) },
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
    };
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        { _disposed = true; _timer.Stop(); _timer.Dispose(); _stop.Cancel(); _stop.Dispose(); _client.Dispose(); }
        base.Dispose(disposing);
    }
    private sealed record Choice(string Key, string Text) { public override string ToString() => Text; }
}
