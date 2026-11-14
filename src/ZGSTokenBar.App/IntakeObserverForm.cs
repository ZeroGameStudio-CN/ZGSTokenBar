using System.Diagnostics;

namespace ZGSTokenBar.App;

internal sealed class IntakeObserverForm : Form
{
    private readonly IntakeObserverClient _client;
    private readonly bool _zh;
    private readonly RouterCombo _projects = new() { Width = 195 };
    private readonly TextBox _reference = new() { Width = 140, MaxLength = 36, BorderStyle = BorderStyle.FixedSingle };
    private readonly RouterButton _search = new() { Kind = RouterButtonKind.Primary };
    private readonly RouterButton _refresh = new();
    private readonly RouterButton _open = new();
    private readonly RouterButton _copy = new();
    private readonly RouterButton _thread = new();
    private readonly RouterButton _diagnose = new();
    private readonly CheckBox _blocked = new() { AutoSize = true, Margin = new Padding(8, 6, 5, 0) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label _detail = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly DataGridView _recent = Grid();
    private readonly DataGridView _timeline = Grid();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30_000 };
    private CancellationTokenSource? _request;
    private IntakeAccess? _access;
    private IntakeItem? _item;
    private DateTimeOffset? _observed;
    private bool _filling;
    private bool _fresh;
    private bool _detailFresh;
    private readonly bool _observeOnShow;
    private Font? _panelFont;
    internal (int Projects, int Recent, int Timeline, bool ItemLoaded, string Status) Observation =>
        (_projects.Items.Count, _recent.RowCount, _timeline.RowCount, _detailFresh, _status.Text);

    internal IntakeObserverForm(bool chinese, IntakeObserverClient? client = null, bool observeOnShow = true)
    {
        _zh = chinese;
        _client = client ?? new();
        _observeOnShow = observeOnShow;
        Text = T("自动分拣 · 只读", "Work intake · Read only");
        StartPosition = FormStartPosition.CenterScreen;
        // Keep the logical dimensions paired with Router's pixel fonts and
        // rescale the in-client layout explicitly on this window's DPI.
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new(755, 575);
        MinimumSize = new(640, 500);
        ShowInTaskbar = false;
        BackColor = UiTokens.WindowContent;
        ForeColor = UiTokens.Text;

        _projects.AccessibleName = T("项目", "Project");
        _reference.AccessibleName = T("工单号", "Work item number");
        _reference.PlaceholderText = T("输入工单号", "Work item number");
        _reference.BackColor = UiTokens.WindowSurface;
        _reference.ForeColor = UiTokens.Text;
        _refresh.Text = T("刷新", "Refresh");
        _search.Text = T("查工单", "Find");
        _blocked.Text = T("仅阻塞", "Blocked only");
        _open.Text = T("打开 ZOS", "Open in ZOS");
        _copy.Text = T("复制卡号", "Copy number");
        _thread.Text = T("打开原对话", "Open conversation");
        _diagnose.Text = T("诊断", "Diagnostics");
        _status.ForeColor = UiTokens.Muted;
        _detail.ForeColor = UiTokens.Text;

        var head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
        head.ColumnStyles.Add(new(SizeType.Percent, 55));
        head.ColumnStyles.Add(new(SizeType.AutoSize));
        head.ColumnStyles.Add(new(SizeType.AutoSize));
        head.ColumnStyles.Add(new(SizeType.Percent, 45));
        head.ColumnStyles.Add(new(SizeType.AutoSize));
        _projects.Dock = DockStyle.Fill;
        _reference.Dock = DockStyle.Fill;
        head.Controls.Add(_projects, 0, 0);
        head.Controls.Add(_blocked, 1, 0);
        head.Controls.Add(_refresh, 2, 0);
        head.Controls.Add(_reference, 3, 0);
        head.Controls.Add(_search, 4, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.AddRange([_open, _copy, _thread, _diagnose]);
        AddColumn(_recent, T("工单", "Item"), 104);
        AddColumn(_recent, T("标题", "Title"), 0);
        AddColumn(_recent, T("当前状态", "State"), 106);
        AddColumn(_recent, T("更新", "Updated"), 110);
        AddColumn(_timeline, T("时间", "Time"), 112);
        AddColumn(_timeline, T("记录", "Event"), 0);
        AddColumn(_timeline, T("结果", "Result"), 114);
        _recent.AccessibleName = T("近期工单", "Recent work items");
        _timeline.AccessibleName = T("工单时间线和诊断", "Work item timeline and diagnostics");

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new(14) };
        layout.RowStyles.Add(new(SizeType.Absolute, 38));
        layout.RowStyles.Add(new(SizeType.Absolute, 24));
        layout.RowStyles.Add(new(SizeType.Percent, 50));
        layout.RowStyles.Add(new(SizeType.Absolute, 27));
        layout.RowStyles.Add(new(SizeType.Absolute, 37));
        layout.RowStyles.Add(new(SizeType.Percent, 50));
        layout.Controls.Add(head, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_recent, 0, 2);
        layout.Controls.Add(_detail, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        layout.Controls.Add(_timeline, 0, 5);
        Controls.Add(layout);
        void ScaleLayout(int dpi, bool setWindowSize)
        {
            var scale = dpi / 96f;
            int Span(int value) => (int)Math.Round(value * scale);
            SuspendLayout();
            layout.SuspendLayout();
            var old = _panelFont;
            _panelFont = new Font("Segoe UI", Span(13), FontStyle.Regular, GraphicsUnit.Pixel);
            Font = _panelFont;
            old?.Dispose();
            layout.Padding = new(Span(14));
            layout.RowStyles[0].Height = Span(36);
            layout.RowStyles[1].Height = Span(25);
            layout.RowStyles[3].Height = Span(29);
            layout.RowStyles[4].Height = Span(34);
            foreach (var view in new[] { _recent, _timeline })
            {
                view.RowTemplate.Height = Span(26);
                view.ColumnHeadersHeight = Span(30);
                foreach (DataGridViewRow row in view.Rows) row.Height = Span(26);
            }
            foreach (var (view, index, width) in new[] { (_recent, 0, 120), (_recent, 2, 110), (_recent, 3, 118),
                         (_timeline, 0, 118), (_timeline, 2, 125) })
                view.Columns[index].Width = Span(width);
            if (setWindowSize)
            {
                var area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new(Math.Min(Span(638), area.Width), Math.Min(Span(468), area.Height));
                ClientSize = new(Math.Min(Span(755), Math.Max(Span(580), area.Width - Span(18))),
                    Math.Min(Span(575), Math.Max(Span(405), area.Height - Span(55))));
            }
            layout.ResumeLayout();
            ResumeLayout();
        }
        ScaleLayout(DeviceDpi, false);
        Load += (_, _) => ScaleLayout(DeviceDpi, true);
        DpiChanged += (_, e) => ScaleLayout(e.DeviceDpiNew, false);

        _detail.Text = T("选择一张工单后查看时间线", "Select a work item to view its timeline");
        _status.Text = T("连接 ZOS…", "Connecting to ZOS…");
        _refresh.Click += async (_, _) => await RefreshVisibleAsync();
        _projects.SelectedIndexChanged += async (_, _) =>
        {
            if (_filling) return;
            ClearItem();
            await LoadRecentAsync();
        };
        _blocked.CheckedChanged += async (_, _) => await LoadRecentAsync();
        _reference.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await FindAsync();
        };
        _search.Click += async (_, _) => await FindAsync();
        _recent.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && _fresh && _recent.Rows[e.RowIndex].Tag is IntakeItem row)
                await LoadItemAsync(row.Id.ToString("D"));
        };
        _recent.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || !_fresh || _recent.CurrentRow?.Tag is not IntakeItem row) return;
            e.SuppressKeyPress = true;
            await LoadItemAsync(row.Id.ToString("D"));
        };
        _open.Click += (_, _) => OpenItem();
        _copy.Click += (_, _) =>
        {
            try
            {
                if (_detailFresh && _item is { Number.Length: > 0 } value) Clipboard.SetText(value.Number);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            { _status.Text = T("剪贴板暂不可用", "Clipboard unavailable"); }
        };
        _thread.Click += (_, _) =>
        {
            if (_detailFresh && _item?.Thread is { } id)
                OpenLink(new Uri("codex://threads/" + id.ToString("D")));
        };
        _diagnose.Click += async (_, _) => await LoadDiagnosticsAsync();
        _timer.Tick += async (_, _) =>
        {
            if (!Visible || WindowState == FormWindowState.Minimized || _request is not null
                || _projects.DroppedDown || _reference.Focused) return;
            await RefreshVisibleAsync();
        };
        Shown += async (_, _) =>
        {
            if (!_observeOnShow) return;
            _timer.Start();
            await LoadRecentAsync();
        };
        FormClosed += (_, _) => { _timer.Stop(); _request?.Cancel(); };
        UpdateButtons();
    }

    private Task RefreshVisibleAsync() => _item is { } item
        ? LoadItemAsync(item.Id.ToString("D")) : LoadRecentAsync();

    private async Task FindAsync()
    {
        var text = _reference.Text.Trim();
        if (!IntakeObserverClient.IsReference(text))
        {
            _status.Text = T("请输入有效的工单号", "Enter a valid work item number");
            return;
        }
        await LoadItemAsync(text);
    }

    internal async Task LoadRecentAsync()
    {
        var preferred = (_projects.SelectedItem as IntakeProject)?.Key;
        await ReadAsync(async token =>
        {
            var access = await RefreshAccessAsync(token);
            if (access.Projects.Count == 0)
            {
                _recent.Rows.Clear();
                _status.Text = T("当前账号没有可读工单项目", "This account has no readable work item projects");
                return;
            }
            var project = access.Projects.FirstOrDefault(row => row.Key == preferred) ?? access.Projects[0];
            var rows = await _client.RecentAsync(project.Key, _blocked.Checked, token);
            token.ThrowIfCancellationRequested();
            ClearItem();
            _recent.Rows.Clear();
            foreach (var row in rows)
                _recent.Rows[_recent.Rows.Add(row.Number, row.Title, ItemState(row), At(row.UpdatedAt))].Tag = row;
            _recent.ClearSelection();
            _fresh = true;
            Observe(rows.Count == 0 ? T("没有符合条件的工单", "No matching work items")
                : T("近期工单 · 双击查看时间线", "Recent work items · Double-click for timeline"));
        });
    }

    internal Task LoadItemAsync(string reference) => ReadAsync(async token =>
    {
        var referenceChanged = _item is not { } old || (!string.Equals(old.Id.ToString("D"), reference, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(old.Number, reference, StringComparison.OrdinalIgnoreCase));
        if (referenceChanged) ClearItem();
        var access = await RefreshAccessAsync(token);
        var item = await _client.ItemAsync(reference, token);
        if (!access.Projects.Any(project => project.Key == item.Project)) throw new IntakeReadException("denied");
        var history = await _client.HistoryAsync(item.Id, token);
        token.ThrowIfCancellationRequested();
        var keepDiagnostics = _item?.Id == item.Id && _item.UpdatedAt == item.UpdatedAt;
        _item = item;
        _detail.Text = $"{item.Number} · {item.Project} · {item.Title}";
        for (var i = _timeline.RowCount - 1; i >= 0; i--)
            if (!keepDiagnostics || _timeline.Rows[i].Tag is not true) _timeline.Rows.RemoveAt(i);
        _timeline.Rows.Insert(0, At(item.UpdatedAt), T("原卡当前", "Current item"), ItemState(item));
        if (item.EvidenceMode.Length > 0)
            _timeline.Rows.Insert(1, "—", T("原对话补交已记录", "Original conversation delivery recorded"), "");
        for (var i = 0; i < history.Count; i++)
        {
            var row = history[i];
            _timeline.Rows.Insert(i + (item.EvidenceMode.Length > 0 ? 2 : 1),
                At(row.Timestamp), Command(row.Command), State(row.Status));
        }
        _timeline.ClearSelection();
        _detailFresh = true;
        Observe(history.Count == IntakeObserverClient.HistoryLimit
            ? T("显示最近 60 条工单历史", "Showing up to 60 recent history entries")
            : T("工单状态来自 ZOS；派发需另有回执", "Work item state is from ZOS; dispatch requires a receipt"));
    });

    internal Task LoadDiagnosticsAsync()
    {
        if (!_detailFresh || _item is not { } selected) return Task.CompletedTask;
        return ReadAsync(async token =>
        {
            var access = await RefreshAccessAsync(token);
            if (_item?.Id != selected.Id || !access.Projects.Any(project => project.Key == selected.Project))
                throw new IntakeReadException("account_changed");
            var bundle = await _client.DiagnosticsAsync(selected.Id, token);
            token.ThrowIfCancellationRequested();
            // Replace the previous diagnostic rows without duplicating the underlying business history.
            for (var i = _timeline.RowCount - 1; i >= 0; i--)
                if (_timeline.Rows[i].Tag is true) _timeline.Rows.RemoveAt(i);
            foreach (var row in bundle.Events)
                _timeline.Rows[_timeline.Rows.Add(At(row.Timestamp), row.Name + (row.Phase.Length == 0 ? "" : " · " + row.Phase),
                    row.Error.Length == 0 ? row.Outcome : row.Error)].Tag = true;
            var detail = bundle.Status == "partial" || bundle.HasMore
                ? T("诊断有部分记录，不能据此确认派发", "Diagnostics are partial; dispatch is unconfirmed")
                : bundle.Events.Count == 0
                    ? T("此工单未返回诊断事件；派发回执未确认", "No diagnostic events for this item; dispatch is unconfirmed")
                    : T($"诊断事件 {bundle.Events.Count} 条", $"{bundle.Events.Count} diagnostic events");
            _status.Text = detail + "  ·  " + At(DateTimeOffset.Now);
        }, diagnosticOnly: true);
    }

    private async Task<IntakeAccess> RefreshAccessAsync(CancellationToken token)
    {
        var access = await _client.AccessAsync(token);
        token.ThrowIfCancellationRequested();
        if (_access is not null && !_access.SameSession(access)) ClearSensitive();
        _access = access;
        var previous = (_projects.SelectedItem as IntakeProject)?.Key;
        if (_projects.Items.Cast<IntakeProject>().SequenceEqual(access.Projects)) return access;
        _filling = true;
        try
        {
            _projects.Items.Clear();
            _projects.Items.AddRange(access.Projects.Cast<object>().ToArray());
            _projects.SelectedItem = access.Projects.FirstOrDefault(row => row.Key == previous) ?? access.Projects.FirstOrDefault();
        }
        finally { _filling = false; }
        return access;
    }

    private async Task ReadAsync(Func<CancellationToken, Task> work, bool diagnosticOnly = false)
    {
        _request?.Cancel();
        using var operation = new CancellationTokenSource();
        _request = operation;
        _status.Text = T("读取中…", "Reading…");
        if (!diagnosticOnly) { _fresh = false; _detailFresh = false; }
        UpdateButtons();
        try { await work(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (IntakeReadException error)
        {
            if (operation.IsCancellationRequested || IsDisposed) return;
            if (error.Code is "signed_out" or "not_installed" or "install_invalid"
                || error.Code == "denied" && !diagnosticOnly || _access?.ExpiresAt <= DateTimeOffset.UtcNow)
                ClearSensitive();
            else if (diagnosticOnly && error.Code == "denied")
                for (var i = _timeline.RowCount - 1; i >= 0; i--)
                    if (_timeline.Rows[i].Tag is true) _timeline.Rows.RemoveAt(i);
            _status.Text = error.Code switch
            {
                "denied" when diagnosticOnly => T("当前账号无诊断权限；工单记录仍可查看", "Diagnostics are restricted; item history is still available"),
                "denied" => T("当前 ZOS 账号没有此项读取权限", "The current ZOS account does not have access"),
                "signed_out" => T("请先在 ZOS CLI 登录后刷新", "Sign in to the ZOS CLI, then refresh"),
                "account_changed" => T("账号权限已变化，请刷新工单列表", "Account access changed. Refresh the item list"),
                "not_found" => T("找不到工单或当前不可见", "Work item is missing or not visible"),
                "no_work_items" => T("此项目尚未提供工单", "Work items aren't available for this project"),
                "not_installed" => T("请先安装 Zero OS CLI", "Install the Zero OS CLI first"),
                "install_invalid" => T("Zero OS CLI 安装不可读", "The Zero OS CLI installation could not be verified"),
                _ => _observed is { } seen ? T($"暂不可用 · 上次更新 {At(seen)}", $"Unavailable · Last read {At(seen)}")
                    : T("暂不可用，稍后刷新", "Unavailable. Try refreshing later"),
            };
        }
        finally
        {
            if (ReferenceEquals(_request, operation))
            {
                _request = null;
                if (!IsDisposed) UpdateButtons();
            }
        }
    }

    private void Observe(string message)
    {
        _observed = DateTimeOffset.Now;
        _status.Text = message + "  ·  " + At(_observed);
    }

    private void ClearSensitive()
    {
        _recent.Rows.Clear();
        _filling = true;
        try { _projects.Items.Clear(); }
        finally { _filling = false; }
        _access = null;
        _observed = null;
        ClearItem();
    }

    private void ClearItem()
    {
        _item = null;
        _detailFresh = false;
        _timeline.Rows.Clear();
        _detail.Text = T("选择一张工单后查看时间线", "Select a work item to view its timeline");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _copy.Enabled = _request is null && _detailFresh && _item is { Number.Length: > 0 };
        _open.Enabled = _request is null && _detailFresh && _item is { } value && _client.WorkItemLink(value.Id) is not null;
        _thread.Enabled = _request is null && _detailFresh && _item?.Thread is not null;
        _diagnose.Enabled = _request is null && _detailFresh && _item is not null;
    }

    private void OpenItem()
    {
        if (!_detailFresh || _item is not { } item || _client.WorkItemLink(item.Id) is not { } link) return;
        OpenLink(link);
    }

    private void OpenLink(Uri link)
    {
        try { Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { _status.Text = T("无法打开链接", "Could not open link"); }
    }

    private string ItemState(IntakeItem row) => row.Blocked ? T("阻塞", "Blocked") : row.Phase switch
    {
        "queued" => row.Status == "rework" ? T("返工排队", "Rework queued") : T("已排队", "Queued"),
        "starting" => T("准备启动", "Starting"),
        "running" => row.Status == "rework" ? T("返工中", "Rework running") : T("执行中", "Running"),
        _ => State(row.Status),
    };

    private string State(string value) => value switch
    {
        "pending_triage" => T("待分拣", "Needs triage"),
        "pending_execution" => T("待执行", "Needs execution"),
        "pending_acceptance" => T("待验收", "Needs review"),
        "rework" => T("返工", "Rework"),
        "passed" => T("已结案", "Closed"),
        "screened_no_issue" => T("历史归档", "Historically archived"),
        _ => "—",
    };

    private string Command(string value) => value switch
    {
        "create" => T("录入工单", "Item created"),
        "triage" => T("分拣决定", "Triage decision"),
        "request_execution" => T("请求执行", "Execution requested"),
        "rework" => T("人工退回返工", "Rework requested"),
        "mark_blocked" => T("报告阻塞", "Blocker recorded"),
        "resolve_blocker" => T("解除阻塞", "Blocker resolved"),
        "submit_result" => T("提交待验收", "Submitted for review"),
        "accept" => T("人工验收", "Human acceptance"),
        "direct_close" or "close_investigation" => T("人工结案", "Closed by reviewer"),
        _ => value,
    };

    private string T(string zh, string en) => _zh ? zh : en;
    private static string At(DateTimeOffset? value) => value?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";

    private static DataGridView Grid()
    {
        var view = new DataGridView
        {
            Dock = DockStyle.Fill, BackgroundColor = UiTokens.WindowSurface, GridColor = UiTokens.WindowBorder,
            BorderStyle = BorderStyle.FixedSingle, ReadOnly = true, MultiSelect = false, RowHeadersVisible = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        };
        view.ColumnHeadersDefaultCellStyle.BackColor = UiTokens.WindowFooter;
        view.ColumnHeadersDefaultCellStyle.ForeColor = UiTokens.TextSecondary;
        view.DefaultCellStyle.BackColor = UiTokens.WindowSurface;
        view.DefaultCellStyle.ForeColor = UiTokens.Text;
        view.DefaultCellStyle.SelectionBackColor = UiTokens.Mix(UiTokens.WindowSurface, UiTokens.Accent, .20f);
        view.DefaultCellStyle.SelectionForeColor = UiTokens.TextStrong;
        return view;
    }

    private static void AddColumn(DataGridView grid, string title, int width) => grid.Columns.Add(new DataGridViewTextBoxColumn
    {
        HeaderText = title, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
        Width = Math.Max(width, 30), AutoSizeMode = width == 0
            ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
    });

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _request?.Cancel(); _timer.Dispose(); }
        base.Dispose(disposing);
        if (disposing) { _panelFont?.Dispose(); _panelFont = null; }
    }
}
