using System.Diagnostics;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class IntakeObserverForm : MonitorPanelForm
{
    private readonly IntakeObserverClient _client;
    private readonly bool _zh;
    private readonly FlowLayoutPanel _projects = Flow();
    private readonly ContextMenuStrip _projectMenu = new() { ShowImageMargin = false };
    private IReadOnlyList<IntakeProject> _projectCatalog = [];
    private string? _projectKey;
    private string _projectSignature = "";
    private readonly TextBox _reference = new() { Width = 180, MaxLength = 36, BorderStyle = BorderStyle.None };
    private readonly RouterButton _search = new() { Kind = RouterButtonKind.Primary };
    private readonly RouterButton _refresh = new() { Icon = RouterButtonIcon.Refresh };
    private readonly RouterButton _pin = new() { Icon = RouterButtonIcon.Pin };
    private readonly RouterButton _close = new() { Icon = RouterButtonIcon.Close };
    private readonly RouterButton _open = new(), _copy = new(), _thread = new(), _diagnose = new();
    private readonly CheckBox _blocked = new() { AutoSize = true, Margin = new(8, 4, 5, 0), UseCompatibleTextRendering = true };
    private readonly Label _status = Label(), _detail = Label(), _freshness = Label(), _projectSummary = Label();
    private readonly RouterGrid _recent = Grid(), _timeline = Grid();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10_000 };
    private readonly ToolTip _tips = new();
    private CancellationTokenSource? _request;
    private IntakeAccess? _access;
    private IntakeItem? _item;
    private DateTimeOffset? _observed;
    private bool _fresh, _detailFresh;
    private readonly bool _observeOnShow;
    private readonly TableLayoutPanel _root;
    internal (int Projects, int Recent, int Timeline, bool ItemLoaded, string Status) Observation =>
        (_projectCatalog.Count, _recent.RowCount, _timeline.RowCount, _detailFresh, _status.Text);
    public override bool InteractionOpen => _projectMenu.Visible || _reference.Focused;
    internal IntakeObserverForm(bool chinese, IntakeObserverClient? client = null, bool observeOnShow = true)
    {
        _zh = chinese;
        _client = client ?? new();
        _observeOnShow = observeOnShow;
        Text = T("自动分拣", "Work intake");
        SuspendLayout();
        _root = CreatePanelLayout();
        Controls.Add(_root);
        var brand = new MonitorBrandMark(Text, intake: true) { Width = 140, Height = 30, Margin = Padding.Empty };
        _projectSummary.Text = T("只读", "Read only"); _projectSummary.Width = 200;
        _freshness.Width = 140;
        var header = CreateHeader(Flow(brand, _projectSummary), Flow(_freshness, _refresh, _pin, _close), 240);
        _root.Controls.Add(CreateNavigation(header, _projects, T("项目", "Project")), 0, 0);

        _reference.AccessibleName = T("工单号", "Work item number");
        _reference.PlaceholderText = T("输入工单号", "Work item number");
        _refresh.AccessibleName = T("刷新", "Refresh");
        _pin.AccessibleName = T("固定弹层", "Pin");
        _close.AccessibleName = T("收起弹层", "Close");
        _search.Text = T("查工单", "Find");
        _blocked.Text = T("仅阻塞", "Blocked only");
        _open.Text = T("打开 ZOS", "Open in ZOS");
        _copy.Text = T("复制卡号", "Copy number");
        _thread.Text = T("打开原对话", "Open conversation");
        _diagnose.Text = T("诊断", "Diagnostics");
        foreach (var button in new[] { _refresh, _pin, _close })
            _tips.SetToolTip(button, button.AccessibleName);

        var search = new RouterSurface { Input = true, Width = 180, Height = 26, Padding = new(8, 5, 8, 3) };
        _reference.Dock = DockStyle.Fill; search.Controls.Add(_reference);
        var heading = Label(T("当前工单", "Current item")); heading.Width = 110;
        SetLogicalFont(heading, "Segoe UI", UiTokens.FontBody, FontStyle.Bold);
        var current = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 3 };
        current.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 28, 28, 32 }) current.RowStyles.Add(new(SizeType.Absolute, height));
        current.Controls.Add(CreateHeader(heading, Flow(search, _search), 268), 0, 0);
        current.Controls.Add(_detail, 0, 1);
        current.Controls.Add(Flow(_open, _copy, _thread, _diagnose), 0, 2);
        var currentSection = RouterSurface.Section(T("当前工单", "Current item")); currentSection.Controls.Add(current);
        _root.Controls.Add(currentSection, 0, 1);

        AddColumn(_recent, T("工单", "Item"), 104); AddColumn(_recent, T("标题", "Title"), 0);
        AddColumn(_recent, T("当前状态", "State"), 106); AddColumn(_recent, T("更新", "Updated"), 110);
        AddColumn(_timeline, T("时间", "Time"), 112); AddColumn(_timeline, T("记录", "Event"), 0);
        AddColumn(_timeline, T("结果", "Result"), 114);
        _recent.AccessibleName = T("近期工单", "Recent work items");
        _timeline.AccessibleName = T("工单时间线和诊断", "Timeline and diagnostics");
        _root.Controls.Add(TableSection(_recent.AccessibleName, _recent, Flow(_blocked)), 0, 2);
        _root.Controls.Add(TableSection(T("历史", "History"), _timeline, Flow()), 0, 3);
        _root.Controls.Add(_status, 0, 4);

        _detail.Text = T("选择一张工单后查看时间线", "Select a work item to view its timeline");
        _status.Text = T("连接 ZOS…", "Connecting to ZOS…");
        _refresh.Click += async (_, _) => await RefreshVisibleAsync();
        _pin.Click += (_, _) => RequestPinChange(!_pinned);
        _close.Click += (_, _) => RequestClose();
        _blocked.CheckedChanged += async (_, _) => await LoadRecentAsync();
        _reference.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true; await FindAsync();
        };
        _search.Click += async (_, _) => await FindAsync();
        _recent.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && _fresh && _recent.Rows[e.RowIndex].Tag is IntakeItem row)
            {
                if (!_pinned) RequestPinChange(true);
                await LoadItemAsync(row.Id.ToString("D"));
            }
        };
        _recent.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || !_fresh || _recent.CurrentRow?.Tag is not IntakeItem row) return;
            e.SuppressKeyPress = true; await LoadItemAsync(row.Id.ToString("D"));
        };
        _open.Click += (_, _) => OpenItem();
        _copy.Click += (_, _) =>
        {
            try { if (_detailFresh && _item is { Number.Length: > 0 } value) Clipboard.SetText(value.Number); }
            catch (System.Runtime.InteropServices.ExternalException) { _status.Text = T("剪贴板暂不可用", "Clipboard unavailable"); }
        };
        _thread.Click += (_, _) =>
        {
            if (_detailFresh && _item?.Thread is { } id) OpenLink(new Uri("codex://threads/" + id.ToString("D")));
        };
        _diagnose.Click += async (_, _) => await LoadDiagnosticsAsync();
        _timer.Tick += async (_, _) =>
        {
            if (!Visible || _request is not null || InteractionOpen) return;
            await RefreshVisibleAsync();
        };
        VisibleChanged += (_, _) =>
        {
            if (!Visible) { _timer.Stop(); _request?.Cancel(); return; }
            if (!_observeOnShow) return;
            _timer.Start();
            BeginInvoke(new Action(async () => { if (Visible && !IsDisposed) await RefreshVisibleAsync(); }));
        };
        UpdateButtons();
        ApplyPalette(QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        ResumeLayout(true);
    }

    private Control TableSection(string title, RouterGrid grid, Control actions)
    {
        var heading = Label(title); heading.Width = 220;
        SetLogicalFont(heading, "Segoe UI", UiTokens.FontBody, FontStyle.Bold);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        body.ColumnStyles.Add(new(SizeType.Percent, 100));
        body.RowStyles.Add(new(SizeType.Absolute, 30)); body.RowStyles.Add(new(SizeType.Percent, 100));
        var pager = new MonitorTablePager(grid);
        actions.Dock = DockStyle.None; actions.AutoSize = true;
        body.Controls.Add(CreateHeader(heading, Flow(new[] { actions }.Concat(pager.Buttons).ToArray()), 300), 0, 0);
        body.Controls.Add(grid, 0, 1);
        var section = RouterSurface.Section(title); section.Controls.Add(body); return section;
    }
    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false, AutoScroll = false };
        panel.Controls.AddRange(controls); return panel;
    }
    private static Label Label(string text = "") => new() { Text = text, Dock = DockStyle.Fill, AutoEllipsis = true,
        UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, Height = 26 };
    public override void SetPinned(bool pinned)
    {
        base.SetPinned(pinned);
        _pin.Selected = pinned;
        _pin.AccessibleName = pinned ? T("取消固定", "Unpin") : T("固定弹层", "Pin");
        UpdateButtons();
    }
    private void ApplyProjects()
    {
        if (_projectMenu.Visible) return;
        var signature = _projectKey + "\n" + string.Join("\n", _projectCatalog.Select(project => project.Key + "\0" + project.Title));
        if (_projectSignature == signature) return;
        _projectSignature = signature;
        foreach (Control child in _projects.Controls.Cast<Control>().ToArray()) child.Dispose();
        _projects.Controls.Clear();
        var shown = _projectCatalog.Take(3).ToList();
        if (_projectCatalog.FirstOrDefault(project => project.Key == _projectKey) is { } selected && !shown.Contains(selected))
        { shown.RemoveAt(shown.Count - 1); shown.Add(selected); }
        foreach (var project in shown)
        {
            var button = new RouterButton { Text = project.Title, Selected = project.Key == _projectKey,
                AccessibleName = T("切换项目 ", "Select project ") + project.Title, Font = (Font)Font.Clone(),
                AutoSize = false, Height = (int)(26 * _popoverScale), Width = Math.Min((int)(200 * _popoverScale),
                    TextRenderer.MeasureText(project.Title, Font).Width + (int)(20 * _popoverScale)),
                Palette = new(UiTokens.CardSurface) };
            button.Click += async (_, _) =>
            {
                if (!_pinned) RequestPinChange(true);
                if (_projectKey == project.Key) return;
                _projectKey = project.Key; ClearItem(); await LoadRecentAsync();
            };
            _projects.Controls.Add(button);
        }
        foreach (var item in _projectMenu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _projectMenu.Items.Clear();
        foreach (var project in _projectCatalog.Except(shown))
        {
            var item = new ToolStripMenuItem(project.Title);
            item.Click += async (_, _) => { if (!_pinned) RequestPinChange(true); _projectKey = project.Key; ClearItem(); await LoadRecentAsync(); };
            _projectMenu.Items.Add(item);
        }
        if (_projectMenu.Items.Count > 0)
        {
            var more = new RouterButton { Text = T("更多", "More"), Font = (Font)Font.Clone() };
            more.Click += (_, _) => _projectMenu.Show(more, new Point(0, more.Height));
            _projects.Controls.Add(more);
        }
        _projectSummary.Text = T("只读", "Read only") + " · " + _projectCatalog.Count;
    }

    protected override void ApplyPalette(QuotaBackgroundTheme theme)
    {
        var palette = new RouterVisualPalette(UiTokens.CardSurface);
        foreach (var control in Children(this))
        {
            var parentSurface = control.Parent;
            while (parentSurface is not null && parentSurface is not RouterSurface) parentSurface = parentSurface.Parent;
            var background = parentSurface is RouterSurface { Input: true } ? palette.Input
                : parentSurface is RouterSurface ? palette.Surface : palette.Background;
            switch (control)
            {
                case RouterSurface surface: surface.Palette = palette; surface.BackColor = surface.Input ? palette.Input : palette.Surface; break;
                case RouterButton button: button.Palette = palette; break;
                case DataGridView grid:
                    grid.BackgroundColor = palette.Background; grid.GridColor = palette.Border;
                    grid.ColumnHeadersDefaultCellStyle.BackColor = grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = palette.Surface;
                    grid.ColumnHeadersDefaultCellStyle.ForeColor = grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = palette.Muted;
                    grid.ColumnHeadersDefaultCellStyle.Padding = new((int)(4 * _popoverScale), (int)(6 * _popoverScale),
                        (int)(4 * _popoverScale), (int)(6 * _popoverScale));
                    grid.ColumnHeadersHeight = (int)(30 * _popoverScale); grid.RowTemplate.Height = (int)(26 * _popoverScale);
                    foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
                    grid.DefaultCellStyle.BackColor = palette.Background; grid.DefaultCellStyle.SelectionBackColor = UiTokens.Mix(palette.Background, palette.Accent, .11f);
                    grid.DefaultCellStyle.Padding = new((int)(8 * _popoverScale), (int)(4 * _popoverScale), (int)(8 * _popoverScale), (int)(4 * _popoverScale));
                    break;
                case Label label: label.BackColor = background; label.ForeColor = label == _detail ? palette.Text : palette.Muted; break;
                case CheckBox check: check.BackColor = background; check.ForeColor = palette.Text; break;
                case Panel panel: panel.BackColor = background; break;
                case TextBox text: text.BackColor = palette.Input; text.ForeColor = palette.Text; break;
            }
        }
        _projectMenu.Renderer = new DarkContextMenuRenderer(palette.Surface, palette.Input, palette.Border, palette.Text, palette.Muted);
        _projectMenu.Font = Font;
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
        var preferred = _projectKey;
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
        _projectCatalog = access.Projects;
        if (!_projectCatalog.Any(project => project.Key == _projectKey)) _projectKey = _projectCatalog.FirstOrDefault()?.Key;
        ApplyProjects();
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
        _freshness.Text = T("更新于 ", "Updated ") + _observed.Value.ToString("HH:mm:ss");
        _status.Text = message + "  ·  " + At(_observed);
    }

    private void ClearSensitive()
    {
        _recent.Rows.Clear();
        _projectMenu.Close();
        _projectCatalog = []; _projectKey = null; ApplyProjects();
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
        _reference.Enabled = _search.Enabled = _blocked.Enabled = _pinned && _request is null;
        _copy.Enabled = _pinned && _request is null && _detailFresh && _item is { Number.Length: > 0 };
        _open.Enabled = _pinned && _request is null && _detailFresh && _item is { } value && _client.WorkItemLink(value.Id) is not null;
        _thread.Enabled = _pinned && _request is null && _detailFresh && _item?.Thread is not null;
        _diagnose.Enabled = _pinned && _request is null && _detailFresh && _item is not null;
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

    private static RouterGrid Grid()
    {
        var palette = new RouterVisualPalette(UiTokens.CardSurface);
        var view = new RouterGrid
        {
            Dock = DockStyle.Fill, BackgroundColor = palette.Surface, GridColor = palette.Border,
            BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ReadOnly = true, MultiSelect = false, RowHeadersVisible = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, EnableHeadersVisualStyles = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ScrollBars = ScrollBars.None,
        };
        view.ColumnHeadersDefaultCellStyle.BackColor = view.ColumnHeadersDefaultCellStyle.SelectionBackColor = palette.Surface;
        view.ColumnHeadersDefaultCellStyle.ForeColor = view.ColumnHeadersDefaultCellStyle.SelectionForeColor = palette.Muted;
        view.ColumnHeadersDefaultCellStyle.Padding = view.DefaultCellStyle.Padding = new Padding(4);
        view.DefaultCellStyle.BackColor = palette.Surface;
        view.DefaultCellStyle.ForeColor = UiTokens.Text;
        view.DefaultCellStyle.SelectionBackColor = UiTokens.Mix(palette.Surface, palette.Accent, .20f);
        view.DefaultCellStyle.SelectionForeColor = UiTokens.TextStrong;
        return view;
    }

    private static void AddColumn(DataGridView grid, string title, int width) => grid.Columns.Add(new DataGridViewTextBoxColumn
    {
        HeaderText = title, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
        MinimumWidth = 40, FillWeight = width == 0 ? 340 : width,
        AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
    });

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _request?.Cancel(); _timer.Dispose(); _tips.Dispose(); _projectMenu.Dispose(); }
        base.Dispose(disposing);
    }
}
