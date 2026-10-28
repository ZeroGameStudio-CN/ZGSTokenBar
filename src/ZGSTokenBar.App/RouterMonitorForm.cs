using System.Text;
using System.Text.Json;
using System.Drawing.Drawing2D;
using System.Globalization;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

/// <summary>Native presentation only. All execution, fairness and release decisions belong to Router.</summary>
internal sealed class RouterMonitorForm : Form, IPluginPopoverContent
{
    private readonly RouterMonitorClient _client;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly FlowLayoutPanel _projects = new() { Width = 300, Height = 28, Margin = Padding.Empty, WrapContents = false, AutoScroll = false };
    private readonly Dictionary<string, RouterButton> _projectButtons = new(StringComparer.Ordinal);
    private readonly ContextMenuStrip _projectMenu = new() { ShowImageMargin = false };
    private string _projectMenuRevision = "";
    private readonly Label _freshness = Label("连接中", 350);
    private readonly RouterStatusBadge _headline = new() { Text = "读取中", Margin = Padding.Empty };
    private readonly Label _metrics = Label("", 420);
    private readonly Label _task = Label("", 760);
    private readonly Label _detail = Label("", 760);
    private readonly Label _source = Label("", 760);
    private readonly Label _notice = Label("", 760);
    private readonly Label _queuePage = Label("", 180);
    private readonly Label _historyPage = Label("", 180);
    private string _statistics = "";
    private readonly RouterGrid _queue = Grid();
    private readonly RouterGrid _history = Grid();
    private readonly ComboBox _kind = Combo(110), _outcome = Combo(110), _day = Combo(135);
    private readonly TextBox _query = new() { Width = 190, MaxLength = 160, PlaceholderText = "搜索任务或对话" };
    private readonly ToolStripMenuItem _cancel, _release, _nativeStop;
    private readonly Button _queueCancel, _top, _up, _down, _currentMenuButton, _filterToggle, _queuePrevious, _queueNext, _historyPrevious, _historyNext;
    private readonly ContextMenuStrip _currentMenu = new() { ShowImageMargin = false };
    private readonly TableLayoutPanel _root;
    private readonly FlowLayoutPanel _filters;
    private readonly Label _currentHeading = Label("当前", 52), _queueHeading = Label("排队", 120), _historyHeading = Label("历史", 70), _historySummary = Label("", 210);
    private readonly TableLayoutPanel _historyBody;
    private readonly Button _pin;
    private readonly ToolTip _tips = new() { InitialDelay = 450, ReshowDelay = 150, AutoPopDelay = 12000 };
    private RouterVisualPalette _palette = new(Color.FromArgb(9, 11, 15));
    private bool _pinned, _modal;
    private PopoverTailSide _tailSide = PopoverTailSide.Bottom;
    private float _tailOffset, _popoverScale = 1;
    private static readonly Size PreferredBodySize = new(760, 760);
    private Size _bodySize = PreferredBodySize;
    private readonly Dictionary<Control, (string Family, float Pixels, FontStyle Style)> _logicalFonts = [];
    private readonly Dictionary<(string Family, float Pixels, FontStyle Style), Font> _scaledFonts = [];
    private RouterSnapshot? _snapshot;
    private bool _refreshing, _controlling, _available, _disposed;
    private int _queueOffset, _historyOffset, _generation, _historyGeneration, _queuePageSize = 3, _historyPageSize = 5, _queueTotal, _historyTotal;
    private bool _filtersVisible;
    private DateTimeOffset _historyReadAt;
    private readonly RouterHistoryBuffer _historyBuffer = new();
    private CancellationTokenSource? _historyRequest, _queueRequest;
    private Task<JsonElement>? _historyLoad;
    private string _historyLoadKey = "";
    private int _historyLoadOffset = -1, _queueGeneration;
    private bool _queueLoading, _applyingQueue;
    private string? _project;
    internal RouterSnapshot? Snapshot => _snapshot;
    internal string FreshnessText => _freshness.Text;
    internal bool Pinned => _pinned;
    public bool InteractionOpen => _modal || _controlling || _currentMenu.Visible || _projectMenu.Visible || _query.Focused || _kind.DroppedDown || _outcome.DroppedDown || _day.DroppedDown;
    public event EventHandler<bool>? PinChanged;
    public event EventHandler? CloseRequested;

    internal RouterMonitorForm(RouterMonitorClient? client = null, bool observe = true)
    {
        _client = client ?? new();
        SuspendLayout();
        Text = "Router";
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Microsoft YaHei UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        BackColor = Color.FromArgb(17, 21, 29);
        ForeColor = Color.FromArgb(229, 234, 243);
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = PreferredBodySize;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        DoubleBuffered = true;

        _root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        _root.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var style in new[] { new RowStyle(SizeType.Absolute, 34), new RowStyle(SizeType.Absolute, 120),
            new RowStyle(SizeType.Percent, 50), new RowStyle(SizeType.Percent, 50), new RowStyle(SizeType.Absolute, 24) }) _root.RowStyles.Add(style);
        Controls.Add(_root);
        _projects.AccessibleName = "工程切换"; _freshness.Width = 140;
        _kind.Width = _outcome.Width = 92; _day.Width = 112; _query.Width = 160;
        _kind.AccessibleName = "任务类型"; _outcome.AccessibleName = "结果"; _day.AccessibleName = "日期"; _query.AccessibleName = "搜索任务或对话";
        _pin = IconButton(RouterButtonIcon.Pin, "固定弹层", () => { PinChanged?.Invoke(this, !_pinned); return Task.CompletedTask; });
        var brand = Label("ROUTER", 70); brand.Font = new Font("Segoe UI Semibold", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        var header = Header(Flow(brand, _projects, _freshness), Flow(IconButton(RouterButtonIcon.Refresh, "刷新", async () => { await RefreshViewAsync(); await HistoryAsync(forceRefresh: true); }), _pin,
            IconButton(RouterButtonIcon.Close, "收起弹层", () => { CloseRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; })), 108);
        _root.Controls.Add(header, 0, 0);
        _headline.Font = new Font(Font.FontFamily, 11.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        _task.Font = new Font(Font.FontFamily, 14, FontStyle.Bold, GraphicsUnit.Pixel);
        _metrics.Font = _detail.Font = _source.Font = _freshness.Font = _notice.Font = _historySummary.Font = new Font(Font.FontFamily, 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _currentHeading.Font = _queueHeading.Font = _historyHeading.Font = new Font(Font.FontFamily, 13, FontStyle.Bold, GraphicsUnit.Pixel);
        _currentHeading.Height = 24; _queueHeading.Height = _historyHeading.Height = _historySummary.Height = 30;
        _currentHeading.Margin = _queueHeading.Margin = _historyHeading.Margin = _historySummary.Margin = Padding.Empty;
        _metrics.TextAlign = ContentAlignment.MiddleRight;
        _task.Dock = _detail.Dock = _source.Dock = _notice.Dock = _metrics.Dock = DockStyle.Fill;
        _task.Margin = _detail.Margin = _source.Margin = _metrics.Margin = Padding.Empty;
        var summary = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 1, RowCount = 4 };
        summary.ColumnStyles.Add(new(SizeType.Percent, 100));
        summary.RowStyles.Add(new(SizeType.Absolute, 24)); summary.RowStyles.Add(new(SizeType.Absolute, 26)); summary.RowStyles.Add(new(SizeType.Absolute, 20)); summary.RowStyles.Add(new(SizeType.Percent, 100));
        var status = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 3, RowCount = 1 };
        status.RowStyles.Add(new(SizeType.Percent, 100));
        status.ColumnStyles.Add(new(SizeType.Absolute, 200)); status.ColumnStyles.Add(new(SizeType.Percent, 100)); status.ColumnStyles.Add(new(SizeType.Absolute, 36));
        _currentMenuButton = IconButton(RouterButtonIcon.More, "当前任务操作", () => { _currentMenu.Show(_currentMenuButton!, new Point(0, _currentMenuButton!.Height)); return Task.CompletedTask; });
        _nativeStop = MenuItem("停止并收尾", () => CurrentControlAsync("stop"));
        _cancel = MenuItem("请求取消", () => CurrentControlAsync("cancel"));
        _release = MenuItem("复检清理", () => CurrentControlAsync("release"));
        _currentMenu.Items.AddRange([_nativeStop, _cancel, _release]);
        status.Controls.Add(Flow(_currentHeading, _headline), 0, 0); status.Controls.Add(_metrics, 1, 0); status.Controls.Add(_currentMenuButton, 2, 0);
        summary.Controls.Add(status, 0, 0); summary.Controls.Add(_task, 0, 1); summary.Controls.Add(_detail, 0, 2); summary.Controls.Add(_source, 0, 3);
        var currentSection = Section("当前"); currentSection.Controls.Add(summary); _root.Controls.Add(currentSection, 0, 1);

        _queue.Columns.AddRange(Column("task", "任务", 270), Column("source", "来源对话", 210), Column("wait", "等待", 80));
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
        queueBody.Controls.Add(Header(Flow(_queueHeading), Flow(_top, _up, _down, _queueCancel, _queuePrevious, _queuePage, _queueNext), 280), 0, 0);
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
        _historyBody.Controls.Add(Header(Flow(_historyHeading, _historySummary), Flow(_filterToggle, IconButton(RouterButtonIcon.Export, "导出 CSV", ExportAsync), _historyPrevious, _historyPage, _historyNext), 208), 0, 0);
        Options(_kind, [("all", "全部类型"), ("compile", "编译"), ("editmode", "EditMode"), ("playmode", "PlayMode"), ("script", "脚本")]);
        Options(_outcome, [("all", "全部结果"), ("completed", "成功"), ("failed", "失败"), ("cancelled", "取消"), ("pending", "未结束")]);
        Options(_day, [("all", "全部日期")]);
        var search = new RouterSurface { Input = true, Width = 160, Height = 30, Padding = new Padding(8, 6, 8, 6) };
        _query.Dock = DockStyle.Fill; _query.BorderStyle = BorderStyle.None; search.Controls.Add(_query);
        _filters = Flow(_kind, _outcome, _day, search, Button("筛选", async () => { _historyOffset = 0; await HistoryAsync(); }, RouterButtonKind.Primary));
        _filters.Visible = false; _historyBody.Controls.Add(_filters, 0, 1);
        _history.EmptyTitle = "没有匹配的记录"; _history.EmptyDetail = "调整筛选条件后重试";
        _history.Columns.AddRange(Column("createdLocal", "提交时间", 115), Column("task", "任务", 255), Column("source", "来源对话", 150),
            Column("state", "结果", 90), Column("executionSeconds", "占用", 70), Column("cleanupVerified", "释放", 55));
        _history.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowRecord(_history.Rows[e.RowIndex]); };
        _history.Margin = Padding.Empty; _historyBody.Controls.Add(_history, 0, 2);
        var historySection = Section("历史"); historySection.Controls.Add(_historyBody); _root.Controls.Add(historySection, 0, 3);
        _root.Controls.Add(_notice, 0, 4);
        _timer.Tick += async (_, _) => { if (Visible) await RefreshViewAsync(); };
        _notice.TextChanged += (_, _) => _tips.SetToolTip(_notice, _notice.Text);
        _history.CellPainting += PaintHistoryStatus;
        if (observe) VisibleChanged += async (_, _) => { if (Visible) { _timer.Start(); await RefreshViewAsync(); } else _timer.Stop(); };
        UpdateControls();
        ApplyPalette(QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        ResumeLayout(true);
        foreach (var control in new[] { this }.Concat(Children(this)))
        {
            var font = control.Font;
            _logicalFonts[control] = (font.FontFamily.Name, font.Unit == GraphicsUnit.Pixel ? font.Size : font.SizeInPoints * 96 / 72, font.Style);
        }
    }

    protected override bool ShowWithoutActivation => !_pinned;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x80; if (!_pinned) value.ExStyle |= 0x08000000; return value; }
    }
    public void SetPinned(bool pinned)
    {
        var changed = _pinned != pinned; _pinned = pinned;
        ((RouterButton)_pin).Selected = pinned;
        _pin.AccessibleName = pinned ? "取消固定" : "固定弹层";
        _tips.SetToolTip(_pin, pinned ? "取消固定" : "固定后可操作队列与历史");
        if (string.IsNullOrEmpty(_notice.Text) || _notice.Text is "悬停预览 · 点击模块固定后可操作" or "已固定 · Esc 或点击外部收起")
            _notice.Text = pinned ? "已固定 · Esc 或点击外部收起" : "悬停预览 · 点击模块固定后可操作";
        if (changed && IsHandleCreated) RecreateHandle();
        UpdateControls();
        if (pinned && Visible) Activate();
    }
    internal void ShowFor(BarForm owner, Rectangle anchor, bool pinned, float scale, QuotaBackgroundTheme theme)
    {
        ConfigureFor(anchor, pinned, scale, theme, Screen.FromRectangle(anchor).WorkingArea);
        if (!Visible) Show(owner);
        if (pinned) Activate();
    }
    internal void ConfigureFor(Rectangle anchor, bool pinned, float scale, QuotaBackgroundTheme theme, Rectangle area)
    {
        SetPinned(pinned);
        var nextScale = Math.Max(1, scale);
        if (Math.Abs(nextScale - _popoverScale) > .001)
        {
            SuspendLayout();
            Scale(new SizeF(nextScale / _popoverScale, nextScale / _popoverScale));
            foreach (var (control, logical) in _logicalFonts)
            {
                var key = (logical.Family, logical.Pixels * nextScale, logical.Style);
                if (!_scaledFonts.TryGetValue(key, out var font))
                { font = new Font(key.Family, key.Item2, key.Style, GraphicsUnit.Pixel); _scaledFonts[key] = font; }
                control.Font = font;
            }
            ResumeLayout(true);
        }
        _popoverScale = Math.Max(1, scale);
        ApplyPalette(theme);
        _bodySize = new(Math.Min((int)(PreferredBodySize.Width * _popoverScale), area.Width - (int)(16 * _popoverScale)),
            Math.Min((int)(PreferredBodySize.Height * _popoverScale), area.Height - (int)(28 * _popoverScale)));
        var tail = (int)(8 * _popoverScale);
        var placement = TaskbarMiniPopoverMath.Place(anchor, _bodySize, tail, (int)(3 * _popoverScale), area);
        _tailSide = placement.TailSide; _tailOffset = placement.TailOffset;
        Padding = new Padding(2 + (_tailSide == PopoverTailSide.Left ? tail : 0), 2 + (_tailSide == PopoverTailSide.Top ? tail : 0),
            2 + (_tailSide == PopoverTailSide.Right ? tail : 0), 2 + (_tailSide == PopoverTailSide.Bottom ? tail : 0));
        ClientSize = placement.WindowSize;
        Location = placement.Location;
        using (var path = SurfacePath()) { var old = Region; Region = new Region(path); old?.Dispose(); }
    }
    private GraphicsPath SurfacePath()
    {
        var tail = 8 * _popoverScale;
        var body = TaskbarPopoverMath.BodyBounds(_tailSide, _bodySize.Width, _bodySize.Height, tail);
        var path = new GraphicsPath { FillMode = FillMode.Winding };
        var d = 14 * _popoverScale;
        path.AddArc(body.Left, body.Top, d, d, 180, 90); path.AddArc(body.Right - d, body.Top, d, d, 270, 90);
        path.AddArc(body.Right - d, body.Bottom - d, d, d, 0, 90); path.AddArc(body.Left, body.Bottom - d, d, d, 90, 90); path.CloseFigure();
        path.AddPolygon(TaskbarPopoverMath.TailPoints(_tailSide, body, _tailOffset, tail));
        return path;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = SurfacePath(); using var border = new Pen(Color.FromArgb(86, 100, 116, 139));
        e.Graphics.DrawPath(border, path);
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        var point = PointToScreen(e.Location);
        var grid = new[] { _queue, _history }.FirstOrDefault(table => table.RectangleToScreen(table.ClientRectangle).Contains(point));
        if (grid is null) { base.OnMouseWheel(e); return; }
        grid.PageFromWheel(e.Delta);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }
    private static IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Children(child)));

    private void ApplyPalette(QuotaBackgroundTheme theme)
    {
        _palette = new(theme.Popover); BackColor = _palette.Background;
        Color SurfaceFor(Control control)
        {
            for (var parent = control.Parent; parent is not null; parent = parent.Parent)
                if (parent is RouterSurface surface) return surface.Input ? _palette.Input : _palette.Surface;
            return _palette.Background;
        }
        foreach (var control in Children(this))
        {
            switch (control)
            {
                case RouterSurface surface:
                    surface.Palette = _palette; surface.BackColor = surface.Input ? _palette.Input : _palette.Surface; surface.Invalidate(); break;
                case RouterButton button:
                    button.Palette = _palette; button.Invalidate(); break;
                case RouterStatusBadge badge:
                    badge.Palette = _palette with { Background = _palette.Surface }; badge.Invalidate(); break;
                case RouterCombo combo:
                    combo.Palette = _palette; combo.BackColor = _palette.Input; combo.ForeColor = _palette.Text; combo.Invalidate(); break;
                case RouterGrid grid:
                    var scale = RouterVisualGeometry.Scale(grid);
                    grid.Palette = _palette; grid.BackgroundColor = SurfaceFor(grid); grid.GridColor = _palette.Border;
                    grid.ColumnHeadersDefaultCellStyle.BackColor = _palette.Surface;
                    grid.ColumnHeadersDefaultCellStyle.ForeColor = _palette.Muted;
                    grid.ColumnHeadersDefaultCellStyle.Padding = new Padding((int)(4 * scale), (int)(6 * scale), (int)(4 * scale), (int)(6 * scale));
                    grid.DefaultCellStyle.BackColor = _palette.Background; grid.DefaultCellStyle.ForeColor = _palette.Text;
                    grid.DefaultCellStyle.SelectionBackColor = RouterVisualPalette.Mix(_palette.Background, _palette.Accent, .11f);
                    grid.DefaultCellStyle.SelectionForeColor = _palette.Text;
                    grid.DefaultCellStyle.Padding = new Padding((int)(8 * scale), (int)(4 * scale), (int)(8 * scale), (int)(4 * scale));
                    grid.RowTemplate.Height = (int)Math.Round(26 * scale);
                    foreach (DataGridViewRow row in grid.Rows) row.Height = grid.RowTemplate.Height;
                    grid.Invalidate(); break;
                case TextBox box: box.BackColor = _palette.Input; box.ForeColor = _palette.Text; break;
                case System.Windows.Forms.Label label:
                    label.BackColor = SurfaceFor(label);
                    label.ForeColor = label == _task ? _palette.Text : label == _queueHeading || label == _historyHeading
                        ? Color.FromArgb(165, 180, 252) : _palette.Muted; break;
                case Panel panel: panel.BackColor = SurfaceFor(panel); break;
            }
        }
        _currentMenu.Renderer = new DarkContextMenuRenderer(_palette.Surface, _palette.Input, _palette.Border, _palette.Text, _palette.Muted);
        _currentMenu.Font = Font;
        _projectMenu.Renderer = new DarkContextMenuRenderer(_palette.Surface, _palette.Input, _palette.Border, _palette.Text, _palette.Muted);
        _projectMenu.Font = Font;
        if (!_available && _snapshot is not null) _freshness.ForeColor = RouterVisualPalette.State("warning");
    }

    private void PaintHistoryStatus(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.Graphics is not { } graphics || e.RowIndex < 0 || e.ColumnIndex < 0 || _history.Rows[e.RowIndex].Tag is not JsonElement row) return;
        var column = _history.Columns[e.ColumnIndex].Name;
        if (column is not ("state" or "cleanupVerified")) return;
        e.PaintBackground(e.ClipBounds, true);
        var s = RouterVisualGeometry.Scale(_history);
        var text = e.FormattedValue?.ToString() ?? "";
        var color = RouterVisualPalette.State(column == "state" ? RouterJson.Text(row, "outcome")
            : RouterJson.Boolean(row, "cleanupVerified") ? "completed" : "warning");
        if (column == "state")
        {
            var w = Math.Min(e.CellBounds.Width - 8 * s, TextRenderer.MeasureText(text, _history.Font).Width + 16 * s);
            var h = Math.Min(e.CellBounds.Height - 6 * s, 23 * s);
            var bounds = new RectangleF(e.CellBounds.X + (e.CellBounds.Width - w) / 2, e.CellBounds.Y + (e.CellBounds.Height - h) / 2, w, h);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RouterVisualGeometry.Rounded(bounds, 5 * s);
            using var fill = new SolidBrush(RouterVisualPalette.Mix(_palette.Background, color, .10f)); graphics.FillPath(fill, path);
            TextRenderer.DrawText(graphics, text, _history.Font, Rectangle.Round(bounds), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        else TextRenderer.DrawText(graphics, text, _history.Font, e.CellBounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        e.Handled = true;
    }

    internal void ApplySnapshot(RouterSnapshot snapshot)
    {
        if (_snapshot is { } previous && previous.InstanceId != snapshot.InstanceId) ResetHistory();
        _snapshot = snapshot; _project = snapshot.ProjectKey;
        _available = true;
        ApplyProjectTabs(snapshot);
        _headline.Text = RouterJson.Text(snapshot.View, "headline", "状态未确认");
        _headline.Accent = RouterVisualPalette.State(RouterJson.Text(snapshot.View, "tone")); _headline.Invalidate();
        var task = RouterJson.Text(snapshot.View, "task");
        var heading = RouterTaskPresentation.Heading(task);
        _task.Text = heading.Title;
        _tips.SetToolTip(_task, heading.Original); _task.AccessibleDescription = heading.Original;
        var owner = RouterJson.Text(RouterJson.Field(snapshot.View, "owner"), "label");
        _metrics.Text = string.IsNullOrEmpty(RouterJson.Text(snapshot.View, "requestId")) ? ""
            : "运行 " + RouterJson.Text(snapshot.View, "elapsed") + "    " + RouterJson.Text(snapshot.View, "progressClock");
        _source.Text = string.IsNullOrWhiteSpace(owner) ? "" : "来源   " + RouterTaskPresentation.Source(owner);
        _tips.SetToolTip(_source, owner); _source.AccessibleDescription = owner;
        var observation = string.Join("  ·  ", new[] { RouterJson.Text(snapshot.View, "detail"), RouterJson.Text(snapshot.View, "error") }.Where(text => !string.IsNullOrWhiteSpace(text)));
        _detail.Text = string.IsNullOrEmpty(observation) ? heading.Context : observation;
        _tips.SetToolTip(_detail, heading.Context + (string.IsNullOrEmpty(observation) ? "" : "\n" + observation));
        _tips.SetToolTip(_headline, observation); _tips.SetToolTip(_metrics, observation);
        _queueHeading.Text = "排队  ·  " + RouterJson.Integer(snapshot.View, "queuedCount");
        _freshness.Text = snapshot.IsFresh(DateTimeOffset.UtcNow) ? "更新于 " + snapshot.ReceivedAt.ToLocalTime().ToString("HH:mm:ss") : "数据过期 · 状态未确认";
        _freshness.ForeColor = snapshot.IsFresh(DateTimeOffset.UtcNow) ? Color.FromArgb(148, 163, 184) : Color.Salmon;
        UpdateControls();
    }

    private void ApplyProjectTabs(RouterSnapshot snapshot)
    {
        var catalog = snapshot.Projects.EnumerateArray().ToArray();
        var shown = catalog.Take(catalog.Length > 3 ? 2 : 3).ToList();
        if (shown.All(row => RouterJson.Text(row, "key") != snapshot.ProjectKey))
        {
            var selected = catalog.First(row => RouterJson.Text(row, "key") == snapshot.ProjectKey);
            if (shown.Count == 2) shown.RemoveAt(1);
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
                var more = Button("…", () => { _projectMenu.Show(_projects, new Point(0, _projects.Height)); return Task.CompletedTask; });
                more.Palette = _palette; more.AccessibleName = "更多工程"; _projects.Controls.Add(more);
            }
        }
        var scale = RouterVisualGeometry.Scale(_projects);
        var maximum = Math.Max(40, (_projects.Width - (catalog.Length > 3 ? (int)(30 * scale) : 0)) / Math.Max(1, shown.Count) - (int)(4 * scale));
        foreach (var row in catalog)
        {
            var key = RouterJson.Text(row, "key"); var name = RouterJson.Text(row, "name"); var pending = RouterJson.Integer(row, "pending");
            if (_projectButtons.TryGetValue(key, out var button))
            {
                var caption = name switch { "ZGSProject_5" => "P5", "POB_Manual" => "Manual", _ => name };
                button.Text = caption + (pending > 0 ? "  " + pending : ""); button.Selected = key == snapshot.ProjectKey;
                button.AccessibleName = "切换工程 " + name;
                button.Width = Math.Min(maximum, button.GetPreferredSize(Size.Empty).Width); button.Height = (int)(26 * scale);
                _tips.SetToolTip(button, name + (pending > 0 ? $" · {pending} 项尚未结束" : ""));
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

    private async Task SelectProjectAsync(string key)
    {
        if (_controlling || key == _project) return;
        if (!_pinned) PinChanged?.Invoke(this, true);
        _project = key; _generation++; _queueOffset = _historyOffset = 0; ResetHistory(); _queueRequest?.Cancel();
        _snapshot = null; _available = false; _headline.Text = "读取中"; _task.Text = ""; _detail.Text = ""; _source.Text = ""; _metrics.Text = "";
        _queue.Rows.Clear(); _history.Rows.Clear();
        foreach (var item in _projectButtons) item.Value.Selected = item.Key == key;
        UpdateControls(); await RefreshViewAsync(); await HistoryAsync();
    }

    private void ResetHistory()
    {
        _historyGeneration++; _historyBuffer.Clear(); _historyReadAt = default;
        _historyRequest?.Cancel(); _historyLoad = null; _historyLoadKey = "";
    }

    internal void ApplyUnavailable(string message)
    {
        _available = false;
        _freshness.Text = "连接中断 · 状态未确认"; _freshness.ForeColor = Color.Salmon;
        _headline.Text = "状态未确认"; _headline.Accent = RouterVisualPalette.State("warning"); _headline.Invalidate();
        _notice.Text = message;
        UpdateControls();
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
            await RefreshQueueAsync();
            if (!InteractionOpen && _historyOffset == 0 && (DateTimeOffset.UtcNow - _historyReadAt).TotalSeconds >= 10)
                await HistoryAsync(forceRefresh: true);
        }
        catch (Exception e) when (Expected(e)) { if (!IsDisposed && !_stop.IsCancellationRequested && generation == _generation) ApplyUnavailable(e.Message); }
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
        { if (!IsDisposed && generation == _generation && queueGeneration == _queueGeneration) ApplyUnavailable(e.Message); }
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
                }
            }
            finally { _history.ResumeLayout(); }
            var metadata = slice.Metadata; var total = slice.Total; _historyOffset = slice.Offset; _history.Tag = total;
            _historyTotal = total; _historyPage.Text = PageLabel(_historyOffset, total, _historyPageSize);
            var selectedDay = Key(_day);
            Options(_day, new[] { ("all", "全部日期") }.Concat(RouterJson.Rows(metadata, "days").Select(d => (RouterJson.Text(d, "date"), RouterJson.Text(d, "date")))).ToArray());
            _day.SelectedItem = _day.Items.Cast<Choice>().FirstOrDefault(c => c.Key == selectedDay) ?? _day.Items[0];
            var counts = RouterJson.Field(metadata, "counts");
            _statistics = $"成功 {RouterJson.Integer(counts, "completed")} · 失败 {RouterJson.Integer(counts, "failed")} · 取消 {RouterJson.Integer(counts, "cancelled")} · 未结束 {RouterJson.Integer(counts, "pending")}" + Environment.NewLine
                + string.Join("   ", RouterJson.Rows(metadata, "groups").Where(g => RouterJson.Integer(g, "count") > 0)
                    .Select(g => $"{RouterJson.Text(g, "category")} P95 排队/占用/清理 {Seconds(g, "queueSecondsP95")}/{Seconds(g, "executionSecondsP95")}/{Seconds(g, "cleanupSecondsP95")}"));
            _historySummary.Text = $"{total} 条  ·  {RouterJson.Integer(counts, "failed")} 失败";
            _historyReadAt = slice.CapturedAt;
            _statistics = "记录快照 " + _historyReadAt.ToLocalTime().ToString("HH:mm:ss") + Environment.NewLine + _statistics;
            _tips.SetToolTip(_historySummary, _statistics); _historySummary.AccessibleDescription = _statistics;
            UpdateControls();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || generation != _generation || historyGeneration != _historyGeneration) { }
        catch (Exception e) when (Expected(e)) { if (!IsDisposed && generation == _generation && historyGeneration == _historyGeneration) _notice.Text = e.Message; }
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
        using var save = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "router-history-" + DateTime.Now.ToString("yyyyMMdd") + ".csv" };
        _modal = true;
        try { if (save.ShowDialog(this) != DialogResult.OK) return; }
        finally { _modal = false; }
        try { await File.WriteAllTextAsync(save.FileName, await _client.ExportAsync(_snapshot.ProjectKey, _stop.Token), new UTF8Encoding(true), _stop.Token); _notice.Text = save.FileName; }
        catch (Exception e) when (Expected(e)) { if (!IsDisposed) _notice.Text = e.Message; }
    }
    private void UpdateControls()
    {
        var fresh = _pinned && _available && !_controlling && _snapshot?.IsFresh(DateTimeOffset.UtcNow) == true;
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
    private static RouterSurface Section(string title) => new()
    { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(10, 6, 10, 8), AccessibleName = title, AccessibleRole = AccessibleRole.Grouping };
    private static TableLayoutPanel Header(Control left, Control right, int rightWidth)
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, rightWidth));
        header.Controls.Add(left, 0, 0); header.Controls.Add(right, 1, 0); return header;
    }
    private static ToolStripMenuItem MenuItem(string text, Func<Task> action)
    { var item = new ToolStripMenuItem(text); item.Click += async (_, _) => await action(); return item; }
    private int PageRows(RouterGrid grid) => Math.Clamp((grid.ClientSize.Height - grid.ColumnHeadersHeight - 2) /
        Math.Max(1, (int)Math.Round(26 * _popoverScale)), 1, 20);
    private static string PageLabel(int offset, int total, int size) => total == 0 ? "0/0" : $"{offset / size + 1}/{(total + size - 1) / size}";
    private static RouterButton Button(string text, Func<Task> action, RouterButtonKind kind = RouterButtonKind.Quiet)
    { var button = new RouterButton { Text = text, AccessibleName = text, Kind = kind }; button.Click += async (_, _) => await action(); return button; }
    private RouterButton IconButton(RouterButtonIcon icon, string label, Func<Task> action)
    { var button = Button(label, action); button.Icon = icon; button.AutoSize = true; _tips.SetToolTip(button, label); return button; }
    private static DataGridViewTextBoxColumn Column(string name, string title, int width) => new() { Name = name, HeaderText = title, FillWeight = width, MinimumWidth = 50, SortMode = DataGridViewColumnSortMode.NotSortable };
    private static RouterGrid Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        EnableHeadersVisualStyles = false, ColumnHeadersDefaultCellStyle = new() { BackColor = Color.FromArgb(30, 41, 59), ForeColor = Color.FromArgb(203, 213, 225), Padding = new Padding(4) },
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
            foreach (var font in _scaledFonts.Values) font.Dispose();
        }
        base.Dispose(disposing);
    }
    private sealed record Choice(string Key, string Text) { public override string ToString() => Text; }
}
