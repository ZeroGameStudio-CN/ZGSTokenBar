using System.Drawing.Drawing2D;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed record SystemUsagePopoverContent(SystemUsageSnapshot Snapshot, bool Pinned, bool LiveRates = false)
{
    /// <summary>
    /// Per-conversation rows for the rate surface (the former Codex usage card's speed table):
    /// null hides the section, an empty list shows that no conversation is active.
    /// </summary>
    public IReadOnlyList<CodexConversationTurn>? Conversations { get; init; }
    /// <summary>Codex tokens counted today (local day), from the token ledger.</summary>
    public long? TodayTokens { get; init; }
    /// <summary>Tokens per hour for the last 24 hours (oldest first, current hour last).</summary>
    public IReadOnlyList<long>? HourlyTokens { get; init; }
    /// <summary>Prompt-cache figures: today's read/write tokens and hit rates (today, all retained).</summary>
    public TokenCacheStats? Cache { get; init; }
    /// <summary>Compact 12-week token activity (Codex-style grid) plus week / average / total stats.</summary>
    public TokenActivityView? Activity { get; init; }
}

internal sealed record TokenCacheStats(long? TodayRead, long? TodayWrite, double? TodayHitPercent, double? TotalHitPercent);

internal sealed class SystemUsagePopoverForm : Form
{
    internal const int LogicalBodyWidth = 300;
    internal const int LiveRateBodyWidth = 640;
    internal const int LogicalBodyHeight = 352;
    private const int LiveRateBodyHeight = 296;
    private const int LogicalTailSize = 8;
    private const int LogicalGap = 3;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MouseActivateNoActivate = 3;
    private const int ExitDurationMs = 90;

    private readonly Font _titleFont = new("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _subtitleFont = new("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _metricLabelFont = new("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _detailFont = new("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _metricValueFont = new("Segoe UI", 11.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _liveValueFont = new("Segoe UI", 25f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _tableValueFont = new("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _totalValueFont = new("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Color RecordedAccent = Color.FromArgb(110, 231, 183);
    private static readonly Color EstimateAccent = UiPalette.Amber400;
    private static readonly Color ReceiveAccent = Color.FromArgb(125, 211, 252);
    private static readonly Color SendAccent = Color.FromArgb(196, 181, 253);
    private static readonly Image OpenAiLogo = BarForm.LoadEmbeddedImage("ZGSTokenBar.App.Assets.openai-official-ios-icon.png");
    private readonly AlignedStringFormats _textFormats = new();
    private readonly System.Windows.Forms.Timer _entranceTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _exitTimer = new() { Interval = 16 };
    private SystemUsagePopoverContent? _content;
    private MetricRow[] _metrics = [];
    private ProcessRow[] _processes = [];
    private string _capturedAt = string.Empty;
    private int _logicalBodyWidth = LogicalBodyWidth;
    private int _logicalBodyHeight = LogicalBodyHeight;
    private CodexConversationTurn[] _sessions = [];
    private int _activeSessionCount;
    private bool _sessionsVisible;
    private bool _sessionsRecent;
    private TokenActivityGrain? _grainOverride;
    private RectangleF _grainToggleBounds;
    private RectangleF _heatmapBounds;
    private PointF? _hoverLogicalPoint;
    private string? _hoverHeatmapLabel;
    private const int MaximumSessionRows = 10;
    private const int SessionRowHeight = 34;
    private const int SessionHeaderHeight = 30;
    private NativeText _text = NativeText.For("zh-CN");
    private PopoverTailSide _tailSide = PopoverTailSide.Bottom;
    private float _tailOffset = LogicalBodyWidth / 2f;
    private float _scale = 1;
    private DateTime _entranceStarted;
    private Point _entranceLocation;
    private Point _restingLocation;
    private DateTime _exitStarted;
    private Point _exitStartLocation;
    private Point _exitLocation;
    private double _exitStartOpacity = 1;
    private bool _animateMotion;
    private QuotaBackgroundTheme _backgroundTheme = QuotaBackgroundPalette.Resolve(
        AppSettings.DefaultBackgroundPalette);

    public SystemUsagePopoverForm()
    {
        _entranceTimer.Tick += (_, _) => AdvanceEntrance();
        _exitTimer.Tick += (_, _) => AdvanceExit();
        AutoScaleMode = AutoScaleMode.None;
        BackColor = UiTokens.CardSurface;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = _text.SystemUsageTitle;
        TopMost = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= ToolWindowStyle | NoActivateStyle;
            return parameters;
        }
    }

    internal void ApplyTheme(QuotaBackgroundTheme theme)
    {
        _backgroundTheme = theme;
        BackColor = UiTokens.CardSurface;
        Invalidate();
    }

    public void UpdateContent(SystemUsagePopoverContent content, NativeText text)
    {
        if (_content == content && ReferenceEquals(_text, text)) return;
        _content = content;
        _text = text;
        Text = content.LiveRates ? (_text.IsChinese ? "实时速率" : "Live rates") : _text.SystemUsageTitle;
        var snapshot = content.Snapshot;
        if (content.LiveRates)
        {
            var live = snapshot.CurrentOutputRate is not null ? snapshot.LiveRate : null;
            var zh = _text.IsChinese;
            _metrics =
            [
                new(zh ? "合计输出" : "Output total", null, snapshot.CurrentOutputRate is { } rate ? $"{rate:0.0} tok/s" : "—"),
                new(zh ? "已记录" : "Recorded", null, live is null ? "—" : $"{live.Measured:0.#} tok/s"),
                new(zh ? "生成中估算" : "In-flight estimate", null, live is null ? "—" : $"{live.Estimated:0.#} tok/s"),
                new(zh ? "网络接收" : "Network receive", null, NetworkUsageSampler.Compact(snapshot.CurrentNetworkReceive) + "/s"),
                new(zh ? "网络发送" : "Network send", null, NetworkUsageSampler.Compact(snapshot.CurrentNetworkSend) + "/s")
            ];
            _processes = [];
        }
        else
        {
            _metrics =
            [
                Metric(_text.SystemUsageCpu, snapshot.CpuPercent,
                    _text.SystemUsageCpuDetail(snapshot.LogicalProcessorCount)),
                Metric(_text.SystemUsageMemory, snapshot.MemoryPercent,
                    _text.SystemUsageMemoryDetail(snapshot.MemoryUsedBytes, snapshot.MemoryTotalBytes, snapshot.MemoryAvailableBytes)),
                Metric(_text.SystemUsageDisk, snapshot.DiskActivePercent,
                    _text.SystemUsageDiskDetail(snapshot.DiskActivePercent, snapshot.DiskReadBytesPerSecond, snapshot.DiskWriteBytesPerSecond)),
                Metric(_text.SystemUsageGpu, snapshot.GpuPercent,
                    _text.SystemUsageGpuDetail(snapshot.GpuPercent, snapshot.GpuEngine, snapshot.GpuProcessCount)),
            ];
            _processes = snapshot.TopProcesses.Take(5).Select(process => new ProcessRow(
                process.Name,
                process.ProcessCount > 1 ? $"×{process.ProcessCount}" : string.Empty,
                FormatUsage(process.CpuPercent),
                FormatProcessMemory(process.PrivateWorkingSetBytes),
                FormatUsage(process.GpuPercent))).ToArray();
        }
        _sessionsVisible = content.LiveRates && content.Conversations is not null;
        var conversations = content.Conversations ?? [];
        var active = conversations
            .Where(turn => turn.LiveRate is { Total: > 0 } || turn.State == "running")
            .OrderByDescending(turn => turn.State == "running")
            .ThenByDescending(turn => turn.LiveRate?.Total ?? 0)
            .ThenByDescending(turn => turn.ObservedAt)
            .Take(MaximumSessionRows)
            .ToArray();
        _activeSessionCount = active.Length;
        _sessionsRecent = active.Length == 0;
        if (!_sessionsVisible)
        {
            _sessions = [];
        }
        else if (active.Length >= MaximumSessionRows)
        {
            _sessions = active;
        }
        else
        {
            var activeSet = active.ToHashSet();
            var recent = conversations
                .Where(turn => !activeSet.Contains(turn))
                .OrderByDescending(turn => turn.ObservedAt)
                .Take(MaximumSessionRows - active.Length);
            _sessions = [.. active, .. recent];
        }
        if (!content.LiveRates)
        {
            _grainToggleBounds = RectangleF.Empty;
            _heatmapBounds = RectangleF.Empty;
            _hoverHeatmapLabel = null;
        }
        _logicalBodyWidth = content.LiveRates ? LiveRateBodyWidth : LogicalBodyWidth;
        _logicalBodyHeight = content.LiveRates
            ? LiveRateBodyHeight + TokenSectionHeight
            : Math.Clamp(282 + Math.Max(0, _processes.Length - 1) * 18, 282, LogicalBodyHeight);
        _capturedAt = _text.SystemUsageCapturedAt(snapshot.CapturedAt);
        Invalidate();
    }

    internal bool LiveRatesContentForAcceptance => _content?.LiveRates == true;

    internal int SessionRowCountForAcceptance => _sessions.Length;

    internal TokenActivityGrain EffectiveActivityGrain =>
        _grainOverride ?? _content?.Activity?.Grain ?? TokenActivityGrain.Daily;

    internal string? HoverHeatmapLabelForAcceptance => _hoverHeatmapLabel;

    private const int TokenSectionHeight = 164;

    private int SessionSectionHeight => !_sessionsVisible ? 0
        : SessionHeaderHeight + Math.Max(1, _sessions.Length) * SessionRowHeight + 8;

    internal int CurrentLogicalBodyWidth => _logicalBodyWidth;

    internal int CurrentLogicalBodyHeight => _logicalBodyHeight;

    internal IReadOnlyList<string> MetricLabelsForAcceptance =>
        _metrics.Select(metric => metric.Label).ToArray();

    internal int ProcessRowCountForAcceptance => _processes.Length;

    private static MetricRow Metric(string label, double? value, string detail) =>
        new(label, value, FormatUsage(value), detail);

    private sealed record MetricRow(string Label, double? Percent, string Value, string Detail = "");
    private sealed record ProcessRow(string Name, string Count, string Cpu, string Memory, string Gpu);

    public void ShowFor(
        BarForm owner,
        Rectangle anchorScreen,
        SystemUsagePopoverContent content,
        NativeText text,
        float scale,
        bool animateEntrance)
    {
        var wasVisible = Visible;
        _exitTimer.Stop();
        _animateMotion = animateEntrance;
        UpdateContent(content, text);
        _scale = Math.Max(1, scale);

        var bodySize = new Size(Scale(_logicalBodyWidth), Scale(_logicalBodyHeight));
        var placement = TaskbarMiniPopoverMath.Place(
            anchorScreen,
            bodySize,
            Scale(LogicalTailSize),
            Scale(LogicalGap),
            Screen.FromRectangle(anchorScreen).WorkingArea);
        _tailSide = placement.TailSide;
        _tailOffset = placement.TailOffset / _scale;
        ClientSize = placement.WindowSize;
        _restingLocation = placement.Location;
        UpdateWindowRegion();
        Invalidate();

        _entranceTimer.Stop();
        if (!wasVisible && animateEntrance)
        {
            _entranceLocation = TaskbarPopoverMath.OffsetFromAnchor(
                _restingLocation,
                _tailSide,
                Math.Max(1, Scale(3)));
            _entranceStarted = DateTime.UtcNow;
            Opacity = .01;
            Location = _entranceLocation;
            this.ShowPopover(BarForm.PopoverOwnerFor(owner));
            if (!TaskbarPlacement.ShowAt(Handle, _entranceLocation, placement.WindowSize)) Location = _entranceLocation;
            _entranceTimer.Start();
            return;
        }

        Opacity = 1;
        if (!wasVisible) this.ShowPopover(BarForm.PopoverOwnerFor(owner));
        if (!TaskbarPlacement.ShowAt(Handle, _restingLocation, placement.WindowSize)) Location = _restingLocation;
    }

    internal Bitmap RenderForTest(
        SystemUsagePopoverContent content,
        NativeText text,
        QuotaBackgroundTheme theme,
        int dpi)
    {
        UpdateContent(content, text);
        _backgroundTheme = theme;
        BackColor = UiTokens.CardSurface;
        _scale = Math.Max(1, dpi / 96f);
        _tailSide = PopoverTailSide.Bottom;
        _tailOffset = _logicalBodyWidth / 2f;
        var size = new Size(Scale(_logicalBodyWidth), Scale(_logicalBodyHeight + LogicalTailSize));
        ClientSize = size;
        CreateControl();
        UpdateWindowRegion();

        var bitmap = new Bitmap(
            size.Width,
            size.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(BackColor);
            OnPaint(new PaintEventArgs(graphics, new Rectangle(Point.Empty, size)));
        }
        return bitmap;
    }

    public void HidePopover()
    {
        _entranceTimer.Stop();
        if (!Visible) return;
        if (!_animateMotion)
        {
            Opacity = 1;
            Hide();
            return;
        }

        _exitStartLocation = Location;
        _exitLocation = TaskbarPopoverMath.OffsetFromAnchor(
            Location,
            _tailSide,
            Math.Max(1, Scale(2)));
        _exitStartOpacity = Opacity;
        _exitStarted = DateTime.UtcNow;
        _exitTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        UiPaint.PrepareQuality(graphics);
        graphics.ScaleTransform(_scale, _scale);

        var body = BodyBounds();
        // The shared hover-card surface: graphite card, white hairline, pointer tail.
        UiPaint.TailedSurface(graphics, body, TailPoints(body), UiTokens.CardSurface, UiTokens.Hairline, UiTokens.RadiusPanel);
        DrawContent(graphics, body);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmMouseActivate)
        {
            message.Result = MouseActivateNoActivate;
            return;
        }
        base.WndProc(ref message);
    }

    internal bool SelectActivityGrainAtLogicalPoint(PointF logicalPoint)
    {
        if (_content?.LiveRates != true || !_grainToggleBounds.Contains(logicalPoint)) return false;
        var next = logicalPoint.X < _grainToggleBounds.Left + _grainToggleBounds.Width / 2f
            ? TokenActivityGrain.Daily
            : TokenActivityGrain.Weekly;
        if (EffectiveActivityGrain == next && _grainOverride == next) return true;
        _grainOverride = next;
        UpdateHoverAtLogicalPoint(logicalPoint);
        Invalidate();
        return true;
    }

    internal void UpdateHoverAtLogicalPoint(PointF? logicalPoint)
    {
        _hoverLogicalPoint = logicalPoint;
        var cursor = logicalPoint is { } pt && _grainToggleBounds.Contains(pt)
            ? Cursors.Hand
            : Cursors.Default;
        if (Cursor != cursor) Cursor = cursor;

        string? nextLabel = null;
        if (_content?.LiveRates == true
            && _content.Activity is { } activity
            && logicalPoint is { } point)
        {
            var effective = activity with { Grain = EffectiveActivityGrain };
            if (_heatmapBounds.Width > 0
                && UiPaint.TryHitActivityHeatmap(_heatmapBounds, effective, point, out var label, out _, out _))
            {
                nextLabel = label;
            }
        }
        if (!string.Equals(_hoverHeatmapLabel, nextLabel, StringComparison.Ordinal))
        {
            _hoverHeatmapLabel = nextLabel;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
            SelectActivityGrainAtLogicalPoint(new PointF(e.X / _scale, e.Y / _scale));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHoverAtLogicalPoint(new PointF(e.X / _scale, e.Y / _scale));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateHoverAtLogicalPoint(null);
    }

    private void DrawContent(Graphics graphics, RectangleF body)
    {
        if (_content is not { } content) return;
        using var primary = new SolidBrush(UiPalette.Slate100);
        using var muted = new SolidBrush(UiPalette.Slate400);
        using var divider = new Pen(UiPalette.Slate500.WithAlpha(36), 1);

        if (content.LiveRates)
        {
            using var stateBrush = new SolidBrush(content.Snapshot.CurrentOutputRate is null ? muted.Color
                : content.Snapshot.LiveRate?.HasEstimate == true ? EstimateAccent : RecordedAccent);
            graphics.FillEllipse(stateBrush, body.X + 16, body.Y + 18, 5, 5);
            DrawString(graphics, Text, _titleFont, primary,
                new RectangleF(body.X + 26, body.Y + 12, 110, 18), StringAlignment.Near);
        }
        else
        {
            DrawString(graphics, Text, _titleFont, primary,
                new RectangleF(body.X + 16, body.Y + 12, 120, 18), StringAlignment.Near);
        }
        DrawString(graphics, _text.SystemUsagePopoverSubtitle(content.Pinned), _subtitleFont, muted,
            new RectangleF(body.X + 142, body.Y + 15, body.Width - 158, 14), StringAlignment.Far);

        if (content.LiveRates)
        {
            DrawLiveRates(graphics, body, content.Snapshot, primary, muted);
            return;
        }

        for (var index = 0; index < _metrics.Length; index++)
        {
            DrawMetricRow(graphics,
                new RectangleF(body.X + 16, body.Y + 40 + index * 40, body.Width - 32, 36),
                _metrics[index], primary, muted);
        }

        graphics.DrawLine(divider, body.X + 16, body.Y + 204, body.Right - 16, body.Y + 204);
        DrawString(graphics, _text.SystemUsageTopProcesses, _subtitleFont, muted,
            new RectangleF(body.X + 16, body.Y + 216, 124, 14), StringAlignment.Near);
        DrawProcessValues(graphics, body, body.Y + 216, _subtitleFont, muted,
            _text.SystemUsageCpu, _text.SystemUsageMemory, _text.SystemUsageGpu);

        if (_processes.Length == 0)
        {
            var emptyBounds = new RectangleF(
                body.X + 16,
                body.Y + 236,
                body.Width - 32,
                Math.Max(28, body.Bottom - body.Y - 274));
            using var emptyPath = RoundedRectangle(emptyBounds, 7);
            using var emptyFill = new SolidBrush(UiPalette.Slate500.WithAlpha(18));
            using var emptyBorder = new Pen(UiPalette.Slate500.WithAlpha(28), 1);
            graphics.FillPath(emptyFill, emptyPath);
            graphics.DrawPath(emptyBorder, emptyPath);
            DrawString(graphics, _text.SystemUsageTopProcessesUnavailable, _detailFont, muted,
                emptyBounds, StringAlignment.Center);
        }
        else
        {
            for (var index = 0; index < _processes.Length; index++)
            {
                DrawProcessRow(graphics, body, body.Y + 236 + index * 18, _processes[index], primary, muted);
            }
        }

        DrawString(graphics, _capturedAt, _subtitleFont, muted,
            new RectangleF(body.X + 16, body.Bottom - 18, body.Width - 32, 12), StringAlignment.Near);
    }

    private void DrawLiveRates(
        Graphics graphics,
        RectangleF body,
        SystemUsageSnapshot snapshot,
        Brush primary,
        Brush muted)
    {
        var zh = _text.IsChinese;
        var live = snapshot.CurrentOutputRate is not null ? snapshot.LiveRate : null;
        using var recorded = new SolidBrush(RecordedAccent);
        using var estimated = new SolidBrush(EstimateAccent);
        using var receive = new SolidBrush(ReceiveAccent);
        using var send = new SolidBrush(SendAccent);
        using var divider = new Pen(UiPalette.Slate500.WithAlpha(42), 1);
        var dy = 0f;
        RectangleF At(float x, float y, float width, float height) => new(body.X + x, body.Y + y + dy, width, height);
        void TextAt(string value, Font font, Brush brush, float x, float y, float width, float height,
            StringAlignment align = StringAlignment.Near) => DrawString(graphics, value, font, brush, At(x, y, width, height), align);
        const float leftWidth = 300f;
        const float rightX = 320f;
        var rightWidth = Math.Max(120f, body.Width - rightX - 12f);
        void Panel(float y, float height)
        {
            using var path = RoundedRectangle(At(12, y, leftWidth - 24, height), 8);
            using var fill = new SolidBrush(_backgroundTheme.ProviderGroup);
            graphics.FillPath(fill, path);
            graphics.DrawPath(divider, path);
        }

        Panel(38, 112);
        TextAt(_metrics[0].Label, _metricLabelFont, muted, 24, 49, 136, 13);
        TextAt(zh ? "3 秒窗口" : "3s window", _subtitleFont, muted, 176, 49, 100, 13, StringAlignment.Far);
        TextAt(_metrics[0].Value, _liveValueFont, primary, 24, 67, 252, 29);
        var rail = At(24, 105, 252, 3);
        using var railBrush = new SolidBrush(UiPalette.Slate500.WithAlpha(48));
        graphics.FillRoundedRectangle(railBrush, rail, 1.5f);
        if (live?.Total is > 0)
        {
            var estimatedWidth = rail.Width * (float)Math.Clamp(live.EstimatedShare, 0, 1);
            var recordedWidth = rail.Width - estimatedWidth;
            if (recordedWidth > 0) graphics.FillRectangle(recorded, rail.X, rail.Y, recordedWidth, rail.Height);
            if (estimatedWidth > 0) graphics.FillRectangle(estimated, rail.X + recordedWidth, rail.Y, estimatedWidth, rail.Height);
        }
        TextAt(_metrics[1].Label, _subtitleFont, muted, 24, 115, 118, 12);
        TextAt(live is null ? (zh ? "估算" : "Estimated")
                : zh ? $"估算 · {live.EstimatedShare:P0}" : $"Estimated · {live.EstimatedShare:P0}",
            _subtitleFont, muted, 158, 115, 118, 12);
        TextAt(_metrics[1].Value, _metricValueFont, live is null ? muted : recorded, 24, 131, 118, 14);
        TextAt(_metrics[2].Value, _metricValueFont, live is null ? muted : estimated, 158, 131, 118, 14);

        Panel(158, 40);
        graphics.FillEllipse(recorded, body.X + 24, body.Y + 174, 5, 5);
        graphics.FillEllipse(send, body.X + 158, body.Y + 174, 5, 5);
        TextAt(zh ? "生成中" : "Generating", _detailFont, primary, 35, 170, 80, 14);
        TextAt(zh ? "工具中" : "Tools", _detailFont, primary, 169, 170, 76, 14);
        TextAt(live?.Generating.ToString() ?? "—", _metricValueFont, primary, 114, 170, 28, 14, StringAlignment.Far);
        TextAt(live?.Tools.ToString() ?? "—", _metricValueFont, primary, 248, 170, 28, 14, StringAlignment.Far);

        // Compact 12-week activity grid (Daily / Weekly) plus 本周 / 周均 / 本日 / 日均 / 总量.
        Panel(206, TokenSectionHeight - 8);
        var activity = _content?.Activity;
        var grain = _grainOverride ?? activity?.Grain ?? TokenActivityGrain.Daily;
        var effectiveActivity = activity is null ? null : activity with { Grain = grain };
        _grainToggleBounds = At(168, 212, 108, 18);
        _heatmapBounds = effectiveActivity is null ? RectangleF.Empty : At(16, 232, 268, 90);
        TextAt(zh ? "用量活动" : "Token activity", _metricLabelFont, muted, 24, 215, 70, 13);
        if (effectiveActivity is not null
            && _hoverLogicalPoint is { } hoverPt
            && UiPaint.TryHitActivityHeatmap(_heatmapBounds, effectiveActivity, hoverPt, out var hoverLabel, out _, out _))
        {
            _hoverHeatmapLabel = hoverLabel;
            TextAt(hoverLabel, _subtitleFont, recorded, 78, 215, 86, 13, StringAlignment.Far);
        }
        else
        {
            _hoverHeatmapLabel = null;
        }
        UiPaint.Segmented(graphics, _grainToggleBounds,
            zh ? new[] { "每日", "每周" } : new[] { "Daily", "Weekly" },
            grain == TokenActivityGrain.Weekly ? 1 : 0, _subtitleFont);
        if (effectiveActivity is not null)
        {
            UiPaint.ActivityHeatmap(graphics, _heatmapBounds, effectiveActivity, _subtitleFont, zh, _hoverLogicalPoint);
            void Stat(float x, string caption, long value)
            {
                TextAt(caption, _subtitleFont, muted, x, 324, 52, 11);
                TextAt(NativeText.FormatTokenCount(value), _tableValueFont, primary, x, 336, 52, 14);
            }
            var labels = zh
                ? new[] { "本周", "周均", "本日", "日均", "总量" }
                : new[] { "Week", "Wk avg", "Today", "Day avg", "Total" };
            var values = new[] { effectiveActivity.ThisWeekTokens, effectiveActivity.WeekAverage, effectiveActivity.TodayTokens, effectiveActivity.DayAverage, effectiveActivity.AllTimeTotal };
            for (var i = 0; i < 5; i++)
                Stat(24 + i * 51.6f, labels[i], values[i]);
        }
        else
        {
            TextAt(zh ? "暂无按日用量" : "No daily totals yet", _detailFont, muted, 24, 270, 252, 14);
        }
        dy = TokenSectionHeight;

        // Live rates plus cumulative traffic (token totals now live on the activity card).
        Panel(206, 78);
        var history = snapshot.NetworkHistory;
        TextAt(zh ? "本机网络" : "System network", _metricLabelFont, muted, 24, 215, 110, 13);
        var cache = _content?.Cache;
        TextAt(cache?.TodayHitPercent is { } hit
                ? (zh ? $"缓存 {NativeText.FormatCacheHitPercent(hit)}" : $"cache {NativeText.FormatCacheHitPercent(hit)}")
                : (zh ? $"{snapshot.NetworkInterfaces} 个接口" : $"{snapshot.NetworkInterfaces} NIC"),
            _subtitleFont, muted, 150, 215, 126, 13, StringAlignment.Far);
        UiIcons.ArrowDown(graphics, At(24, 236, 9, 11), ReceiveAccent);
        TextAt(_metrics[3].Value, _metricValueFont, receive, 37, 234, 100, 15);
        UiIcons.ArrowUp(graphics, At(158, 236, 9, 11), SendAccent);
        TextAt(_metrics[4].Value, _metricValueFont, send, 171, 234, 105, 15);
        if (history is null)
        {
            TextAt(snapshot.NetworkHistorySaveFailed
                    ? (zh ? "流量记录待保存" : "Traffic checkpoint pending")
                    : (zh ? "等待流量计数" : "Waiting for traffic"),
                _subtitleFont, muted, 24, 257, 252, 13);
        }
        else
        {
            UiIcons.Traffic(graphics, At(24, 258, 10, 10), UiTokens.Muted);
            TextAt(NetworkUsageSampler.Total(history.TotalBytes), _subtitleFont, muted, 36, 257, 52, 13);
            UiIcons.ArrowDown(graphics, At(92, 259, 8, 9), ReceiveAccent);
            TextAt(NetworkUsageSampler.Total(history.ReceivedBytes), _subtitleFont, muted, 102, 257, 52, 13);
            UiIcons.ArrowUp(graphics, At(158, 259, 8, 9), SendAccent);
            TextAt(NetworkUsageSampler.Total(history.SentBytes), _subtitleFont, muted, 168, 257, 52, 13);
            TextAt(TotalsSince(history.Since, snapshot.CapturedAt), _subtitleFont, muted, 210, 257, 66, 13, StringAlignment.Far);
        }

        if (_sessionsVisible)
        {
            var sessionTop = 38f;
            var sessionHeight = (LiveRateBodyHeight + TokenSectionHeight) - sessionTop - 12;
            using (var path = RoundedRectangle(new RectangleF(body.X + rightX, body.Y + sessionTop, rightWidth, sessionHeight), 8))
            using (var fill = new SolidBrush(_backgroundTheme.ProviderGroup))
            {
                graphics.FillPath(fill, path);
                graphics.DrawPath(divider, path);
            }
            DrawSessions(graphics, new RectangleF(body.X + rightX, body.Y + sessionTop, rightWidth, sessionHeight), primary, muted);
        }
    }

    /// <summary>"Since" stamp of the totals: month/day and time, with the year only when it differs.</summary>
    internal static string TotalsSince(DateTimeOffset since, DateTimeOffset now)
    {
        var local = since.ToLocalTime();
        return local.ToString(local.Year == now.ToLocalTime().Year ? "M/d HH:mm" : "yyyy/M/d HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Area curve of hourly tokens: shared accent, faint baseline, the current hour marked.</summary>
    internal static void DrawHourlyCurve(Graphics graphics, RectangleF bounds, IReadOnlyList<long>? hourly)
    {
        using (var baseline = new Pen(UiPalette.Slate500.WithAlpha(60), 1))
            graphics.DrawLine(baseline, bounds.Left, bounds.Bottom - .5f, bounds.Right, bounds.Bottom - .5f);
        if (hourly is not { Count: > 1 }) return;
        var peak = Math.Max(1, hourly.Max());
        var step = bounds.Width / (hourly.Count - 1);
        var points = hourly.Select((value, index) => new PointF(
            bounds.Left + index * step,
            bounds.Bottom - 1 - (float)(value / (double)peak) * (bounds.Height - 3))).ToArray();
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var area = new System.Drawing.Drawing2D.GraphicsPath())
        {
            area.AddLine(bounds.Left, bounds.Bottom, points[0].X, points[0].Y);
            area.AddCurve(points, .35f);
            area.AddLine(points[^1].X, points[^1].Y, bounds.Right, bounds.Bottom);
            area.CloseFigure();
            using var fill = new System.Drawing.Drawing2D.LinearGradientBrush(bounds, UiTokens.Accent.WithAlpha(90), UiTokens.Accent.WithAlpha(8),
                System.Drawing.Drawing2D.LinearGradientMode.Vertical);
            graphics.FillPath(fill, area);
        }
        using (var line = new Pen(UiTokens.Accent, 1.5f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            graphics.DrawCurve(line, points, .35f);
        using (var dot = new SolidBrush(UiTokens.TextStrong))
            graphics.FillEllipse(dot, points[^1].X - 2.5f, points[^1].Y - 2.5f, 5, 5);
        graphics.SmoothingMode = smoothing;
    }

    /// <summary>
    /// Active Codex conversations with their own live tok/s, model, state, output and average
    /// speed: the per-conversation view that used to live on the Codex usage card.
    /// </summary>
    private void DrawSessions(Graphics graphics, RectangleF panel, Brush primary, Brush muted)
    {
        var zh = _text.IsChinese;
        var title = _sessionsRecent
            ? (zh ? $"最近会话 · {_sessions.Length}" : $"Recent chats · {_sessions.Length}")
            : (zh ? $"活跃会话 · {_activeSessionCount}" : $"Active chats · {_activeSessionCount}");
        const float pad = 12f;
        var hasNumberedInstance = _sessions.Any(s => s.InstanceNumber > 0 || (s.InstanceLabel is { Length: > 5 }));
        var chipWidth = hasNumberedInstance ? 62f : 50f;
        const float speedWidth = 56f;
        const float totalWidth = 46f;
        const float cacheWidth = 48f;
        const float firstWidth = 38f;
        var speedLeft = panel.Right - pad - speedWidth;
        var totalLeft = speedLeft - totalWidth;
        var cacheLeft = totalLeft - cacheWidth;
        var firstLeft = cacheLeft - firstWidth;
        var modelLeft = panel.X + pad;
        var modelWidth = Math.Max(48f, firstLeft - 4 - modelLeft);
        var chipLeft = panel.X + pad;
        var titleLeft = chipLeft + chipWidth + 6;
        var titleWidth = Math.Max(48f, panel.Right - pad - titleLeft);

        DrawString(graphics, title, _metricLabelFont, muted,
            new RectangleF(panel.X + pad, panel.Y + 9, panel.Width - pad * 2, 13), StringAlignment.Near);
        if (_sessions.Length == 0)
        {
            DrawString(graphics, zh ? "暂无 Codex 会话读数" : "No Codex chat data yet", _detailFont, muted,
                new RectangleF(panel.X + pad, panel.Y + SessionHeaderHeight + 6, panel.Width - 24, 15), StringAlignment.Near);
            return;
        }

        using var recorded = new SolidBrush(RecordedAccent);
        using var estimated = new SolidBrush(EstimateAccent);
        using var toolBrush = new SolidBrush(SendAccent);
        using var idle = new SolidBrush(UiPalette.Slate500);
        using var rowDivider = new Pen(UiPalette.Slate400.WithAlpha(28), 1);
        using var trimmed = new StringFormat(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
        };
        using var chipLabel = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        for (var index = 0; index < _sessions.Length; index++)
        {
            var turn = _sessions[index];
            var top = panel.Y + SessionHeaderHeight + index * SessionRowHeight;
            if (index > 0) graphics.DrawLine(rowDivider, panel.X + pad, top - 1, panel.Right - pad, top - 1);
            var live = turn.LiveRate is { Total: > 0 } rate ? rate : null;
            var accent = live is null ? (turn.State == "running" ? recorded : idle)
                : live.Estimated > 0 ? estimated : recorded;

            // Line 1: taskbar-ring-linked Codex instance chip · full-width title.
            var instanceLabel = string.IsNullOrWhiteSpace(turn.InstanceLabel)
                ? (turn.InstanceNumber > 0 ? $"Codex · {turn.InstanceNumber}" : "Codex")
                : turn.InstanceLabel;
            var ringColor = InstanceRingColor(turn.InstanceRemainingPercent);
            var chip = new RectangleF(chipLeft, top + 2, chipWidth, 15);
            using (var path = RoundedRectangle(chip, 7.5f))
            using (var chipFill = new SolidBrush(Color.FromArgb(36, ringColor)))
            using (var chipBorder = new Pen(Color.FromArgb(88, ringColor), 1f))
            {
                graphics.FillPath(chipFill, path);
                graphics.DrawPath(chipBorder, path);
            }
            DrawInstanceRing(graphics, new RectangleF(chip.X + 2.5f, chip.Y + 1.5f, 12f, 12f), turn.InstanceRemainingPercent, ringColor);
            using (var chipText = new SolidBrush(ringColor))
            {
                graphics.DrawString(instanceLabel, _subtitleFont, chipText,
                    new RectangleF(chip.X + 15.5f, chip.Y, chip.Width - 17.5f, chip.Height), chipLabel);
            }

            graphics.DrawString(_text.CodexTurnName(turn), _detailFont, primary,
                new RectangleF(titleLeft, top + 2, titleWidth, 15), trimmed);

            // Line 2: model · effort, first-token latency, cache hit rate, this-turn tokens, and live tok/s.
            var metricsTop = top + 17;
            void Metric(float x, float width, Action<Graphics, RectangleF, Color> icon, string value)
            {
                icon(graphics, new RectangleF(x, metricsTop + 1.5f, 10, 10), UiTokens.Muted);
                DrawString(graphics, value, _subtitleFont, muted,
                    new RectangleF(x + 12, metricsTop, width - 13, 13), StringAlignment.Near);
            }
            var modelEffort = string.IsNullOrWhiteSpace(turn.Model) && string.IsNullOrWhiteSpace(turn.Effort)
                ? "—"
                : string.IsNullOrWhiteSpace(turn.Effort) ? turn.Model ?? "—"
                : string.IsNullOrWhiteSpace(turn.Model) ? turn.Effort
                : $"{turn.Model} · {turn.Effort}";
            graphics.DrawString(modelEffort, _subtitleFont, muted,
                new RectangleF(modelLeft, metricsTop, modelWidth, 13), trimmed);
            var first = turn.FirstTokenSeconds is { } seconds && double.IsFinite(seconds) && seconds >= 0
                ? (seconds < 10
                    ? seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    : seconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)) + "s"
                : "—";
            var total = Math.Max(0, turn.OutputTokens) + Math.Max(0, turn.InputTokens ?? 0);
            var totalText = turn.InputTokens is null && turn.OutputTokens <= 0
                ? "—"
                : NativeText.FormatTokenCount(total);
            Metric(firstLeft, firstWidth, UiIcons.Clock, first);
            Metric(cacheLeft, cacheWidth, UiIcons.Cache, NativeText.FormatCacheHitPercent(turn.CacheHitPercent));
            Metric(totalLeft, totalWidth, UiIcons.Tokens, totalText);
            string speed;
            Brush speedBrush;
            var speedFont = _tableValueFont;
            if (live is not null)
            {
                speed = $"{live.Total.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} tok/s";
                speedBrush = accent;
            }
            else if (turn.State == "running")
            {
                speed = zh ? "工具中" : "Tools";
                speedBrush = toolBrush;
                speedFont = _subtitleFont;
            }
            else if (turn.OutputTokensPerSecond is { } avg && double.IsFinite(avg) && avg > 0)
            {
                speed = $"{avg.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} tok/s";
                speedBrush = muted;
                speedFont = _subtitleFont;
            }
            else
            {
                speed = "—";
                speedBrush = muted;
            }
            DrawString(graphics, speed, speedFont,
                speedBrush, new RectangleF(speedLeft, metricsTop - .5f, speedWidth, 14), StringAlignment.Far);
        }
    }

    private static Color InstanceRingColor(double? remaining) => GlassDockLayout.Band(remaining) switch
    {
        "red" => Color.FromArgb(255, 99, 99),
        "amber" => Color.FromArgb(252, 196, 64),
        "green" => Color.FromArgb(52, 220, 150),
        _ => RecordedAccent,
    };

    private void DrawInstanceRing(Graphics graphics, RectangleF bounds, double? remaining, Color ringColor)
    {
        const float stroke = 1.4f;
        var core = RectangleF.Inflate(bounds, -stroke - .2f, -stroke - .2f);
        var pixels = Math.Max(8, (int)MathF.Round(core.Width * _scale));
        var baked = GlassDockPainter.BakedIcon(OpenAiLogo, pixels, .3f * _scale, .92f);
        var interpolation = graphics.InterpolationMode;
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.DrawImage(baked, core);
        graphics.InterpolationMode = interpolation;

        var ellipse = RectangleF.Inflate(bounds, -stroke / 2, -stroke / 2);
        using (var track = new Pen(Color.FromArgb(52, 255, 255, 255), stroke))
            graphics.DrawEllipse(track, ellipse);
        var sweep = remaining is null ? 360f : GlassDockLayout.RingSweep(remaining);
        if (sweep > 0)
        {
            using var arc = new Pen(ringColor, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (sweep >= 360f) graphics.DrawEllipse(arc, ellipse);
            else graphics.DrawArc(arc, ellipse, GlassDockLayout.RingStart(remaining), sweep);
        }
    }

    private void DrawMetricRow(
        Graphics graphics,
        RectangleF bounds,
        MetricRow metric,
        Brush primary,
        Brush muted)
    {
        var color = metric.Percent is { } percent ? UsageColor(percent) : UiPalette.Slate500;
        using var valueBrush = new SolidBrush(color);
        DrawString(graphics, metric.Label, _metricLabelFont, primary,
            new RectangleF(bounds.X, bounds.Y, 44, 14), StringAlignment.Near);
        DrawString(graphics, metric.Value, _metricValueFont, valueBrush,
            new RectangleF(bounds.Right - 44, bounds.Y - 1, 44, 16), StringAlignment.Far);

        UiPaint.ProgressBar(graphics, new RectangleF(bounds.X + 44, bounds.Y + 6, bounds.Width - 96, 3),
            metric.Percent / 100, color, UiPalette.Slate500.WithAlpha(48));
        DrawString(graphics, metric.Detail, _subtitleFont, muted,
            new RectangleF(bounds.X + 44, bounds.Y + 18, bounds.Width - 44, 14), StringAlignment.Near);
    }

    private void DrawProcessRow(
        Graphics graphics,
        RectangleF body,
        float y,
        ProcessRow process,
        Brush primary,
        Brush muted)
    {
        var countWidth = process.Count.Length == 0 ? 0 : graphics.MeasureString(process.Count, _subtitleFont).Width;
        var nameWidth = 124 - (countWidth > 0 ? countWidth + 4 : 0);
        DrawString(graphics, process.Name, _detailFont, primary,
            new RectangleF(body.X + 16, y, nameWidth, 15), StringAlignment.Near);
        if (countWidth > 0)
        {
            var nameEnd = Math.Min(nameWidth, graphics.MeasureString(process.Name, _detailFont).Width);
            DrawString(graphics, process.Count, _subtitleFont, muted,
                new RectangleF(body.X + 16 + nameEnd + 4, y + 1, countWidth, 14), StringAlignment.Near);
        }
        DrawProcessValues(graphics, body, y, _tableValueFont, primary, process.Cpu, process.Memory, process.Gpu);
    }

    private void DrawProcessValues(
        Graphics graphics,
        RectangleF body,
        float y,
        Font font,
        Brush brush,
        string cpu,
        string memory,
        string gpu)
    {
        DrawString(graphics, cpu, font, brush,
            new RectangleF(body.X + 146, y, 34, 15), StringAlignment.Far);
        DrawString(graphics, memory, font, brush,
            new RectangleF(body.X + 184, y, 62, 15), StringAlignment.Far);
        DrawString(graphics, gpu, font, brush,
            new RectangleF(body.X + 250, y, 34, 15), StringAlignment.Far);
    }

    private void AdvanceEntrance()
    {
        var progress = Math.Clamp((DateTime.UtcNow - _entranceStarted).TotalMilliseconds / 130, 0, 1);
        var eased = TaskbarPopoverMath.EntranceEase(progress);
        var location = TaskbarPopoverMath.Interpolate(_entranceLocation, _restingLocation, eased);
        Opacity = TaskbarPopoverMath.FadeIn(.01, eased);
        if (!TaskbarPlacement.ShowAt(Handle, location, ClientSize)) Location = location;
        if (progress < 1) return;
        _entranceTimer.Stop();
        Opacity = 1;
        if (!TaskbarPlacement.ShowAt(Handle, _restingLocation, ClientSize)) Location = _restingLocation;
    }

    private void AdvanceExit()
    {
        var progress = Math.Clamp((DateTime.UtcNow - _exitStarted).TotalMilliseconds / ExitDurationMs, 0, 1);
        var eased = TaskbarPopoverMath.ExitEase(progress);
        var location = TaskbarPopoverMath.Interpolate(_exitStartLocation, _exitLocation, eased);
        Opacity = TaskbarPopoverMath.FadeOut(_exitStartOpacity, eased);
        if (!TaskbarPlacement.ShowAt(Handle, location, ClientSize)) Location = location;
        if (progress < 1) return;
        _exitTimer.Stop();
        Opacity = 1;
        Hide();
    }

    private RectangleF BodyBounds() => TaskbarPopoverMath.BodyBounds(
        _tailSide,
        _logicalBodyWidth,
        _logicalBodyHeight,
        LogicalTailSize);

    private PointF[] TailPoints(RectangleF body) => TaskbarPopoverMath.TailPoints(
        _tailSide,
        body,
        _tailOffset,
        LogicalTailSize);

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var body = BodyBounds();
        using var bodyPath = RoundedRectangle(body, 10);
        using var tailPath = new GraphicsPath();
        tailPath.AddPolygon(TailPoints(body));
        using var matrix = new Matrix();
        matrix.Scale(_scale, _scale);
        bodyPath.Transform(matrix);
        tailPath.Transform(matrix);
        var next = new Region(bodyPath);
        next.Union(tailPath);
        Region?.Dispose();
        Region = next;
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));

    private static string FormatUsage(double? value)
    {
        if (value is null) return "—";
        if (value <= 0) return "0%";
        if (value < 1) return "<1%";
        return $"{Math.Round(value.Value):0}%";
    }

    internal static string FormatProcessMemory(ulong bytes)
    {
        const double bytesPerMegabyte = 1024d * 1024;
        const double bytesPerGigabyte = 1024d * 1024 * 1024;
        if (bytes >= bytesPerGigabyte) return $"{bytes / bytesPerGigabyte:0.#} GB";
        var megabytes = bytes / bytesPerMegabyte;
        if (megabytes < 1) return "<1 MB";
        return $"{Math.Round(megabytes):0} MB";
    }

    private static Color UsageColor(double usage) => usage switch
    {
        >= 90 => UiPalette.Rose400,
        >= 70 => UiPalette.Amber400,
        _ => UiPalette.Emerald400,
    };

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius) => UiPaint.RoundedPath(bounds, radius);

    private void DrawString(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        RectangleF bounds,
        StringAlignment alignment) =>
        UiPaint.Text(graphics, text, font, brush, bounds, alignment);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _entranceTimer.Stop();
            _entranceTimer.Dispose();
            _exitTimer.Stop();
            _exitTimer.Dispose();
            _titleFont.Dispose();
            _subtitleFont.Dispose();
            _metricLabelFont.Dispose();
            _detailFont.Dispose();
            _metricValueFont.Dispose();
            _liveValueFont.Dispose();
            _tableValueFont.Dispose();
            _totalValueFont.Dispose();
            _textFormats.Dispose();
        }
        base.Dispose(disposing);
    }
}
