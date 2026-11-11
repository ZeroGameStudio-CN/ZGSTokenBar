using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>
/// The Router hover: the same UiKit hover card as the quota rings (graphite surface, hairline, tail,
/// header with the mark, a state pill, metric rows, footer hint). It paints only the Router summary the
/// bar already holds, so hovering never builds the full monitor panel or touches the network/disk.
/// Clicking the Router still opens (pins) the full panel.
/// </summary>
internal sealed class RouterHoverCardForm : UiCardPopoverForm
{
    private const float Width = 248;
    private const float Pad = UiTokens.Space4;
    private const float HeaderHeight = 44;
    private const float RowHeight = 20;
    private const float FooterHeight = 22;

    private readonly Font _titleFont = new("Segoe UI", UiTokens.FontTitle, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _bodyFont = new("Segoe UI", UiTokens.FontBody, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _strongFont = new("Segoe UI Semibold", UiTokens.FontBody, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _captionFont = new("Segoe UI", UiTokens.FontCaption, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _pillFont = new("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
    private RouterHoverModel _model = RouterHoverModel.Empty(true);
    private bool _chinese = true;

    public RouterHoverCardForm() : base("Router") { }

    protected override SizeF LogicalBodySize => new(Width, HeaderHeight + _model.Rows.Count * RowHeight + UiTokens.Space3 + FooterHeight);

    internal void ShowFor(BarForm owner, Rectangle anchorScreen, MiniCardContribution? card, bool chinese, float scale)
    {
        _chinese = chinese;
        var model = RouterHoverModel.From(card, chinese);
        var resized = model.Rows.Count != _model.Rows.Count;
        _model = model;
        if (Visible && !resized) { Invalidate(); return; }
        ShowAt(owner, anchorScreen, scale);
    }

    internal Bitmap RenderForTest(MiniCardContribution? card, bool chinese, int dpi)
    {
        _chinese = chinese;
        _model = RouterHoverModel.From(card, chinese);
        return RenderCard(dpi);
    }

    protected override void DrawBody(Graphics graphics, RectangleF body)
    {
        var left = body.X + Pad;
        var right = body.Right - Pad;
        using var strong = new SolidBrush(UiTokens.TextStrong);
        using var text = new SolidBrush(UiTokens.Text);
        using var muted = new SolidBrush(UiTokens.Muted);

        // Header: mark, title + subtitle, state pill.
        var markBounds = new RectangleF(left, body.Y + 10, 24, 24);
        using (var tile = new SolidBrush(Color.White.WithAlpha(10)))
            UiPaint.FillRounded(graphics, tile, RectangleF.Inflate(markBounds, 2, 2), UiTokens.RadiusControl);
        RouterCardRenderer.DrawRouterGlyph(graphics, RectangleF.Inflate(markBounds, -3, -3), _model.State, _model.StateColor);
        var pillWidth = graphics.MeasureString(_model.StateText, _pillFont, PointF.Empty, StringFormat.GenericTypographic).Width + 22;
        var pill = new RectangleF(right - pillWidth, body.Y + 13, pillWidth, 18);
        DrawPill(graphics, pill, _model.StateText, _model.StateColor);
        var titleLeft = markBounds.Right + UiTokens.Space3 + 2;
        UiPaint.Text(graphics, "Router", _titleFont, strong, new RectangleF(titleLeft, body.Y + 9, pill.X - titleLeft - 4, 16), StringAlignment.Near);
        UiPaint.Text(graphics, _model.Subtitle, _captionFont, muted, new RectangleF(titleLeft, body.Y + 25, pill.X - titleLeft - 4, 13), StringAlignment.Near);
        UiPaint.Divider(graphics, left, right, body.Y + HeaderHeight - .5f, UiTokens.Hairline);

        var y = body.Y + HeaderHeight + UiTokens.Space2;
        foreach (var row in _model.Rows)
        {
            var rowBounds = new RectangleF(left, y, right - left, RowHeight);
            UiPaint.Text(graphics, row.Label, _bodyFont, muted, new RectangleF(rowBounds.X, rowBounds.Y, 72, RowHeight), StringAlignment.Near);
            using var valueBrush = new SolidBrush(row.Tone ?? UiTokens.Text);
            UiPaint.Text(graphics, row.Value, _strongFont, valueBrush,
                new RectangleF(rowBounds.X + 74, rowBounds.Y, rowBounds.Width - 74, RowHeight), StringAlignment.Far);
            y += RowHeight;
        }

        var footerTop = body.Bottom - FooterHeight;
        UiPaint.Divider(graphics, left, right, footerTop + .5f, UiTokens.Hairline);
        UiPaint.Text(graphics, _chinese ? "点击打开 Router 面板" : "Click to open the Router panel", _captionFont, muted,
            new RectangleF(left, footerTop + 4, right - left, 14), StringAlignment.Near);
    }

    /// <summary>UiKit pill (same tint/dot as <see cref="UiPaint.Pill"/>) drawn in logical units.</summary>
    private void DrawPill(Graphics graphics, RectangleF bounds, string label, Color accent)
    {
        using (var fill = new SolidBrush(UiTokens.Mix(UiTokens.CardSurface, accent, .16f)))
            UiPaint.FillRounded(graphics, fill, bounds, bounds.Height / 2);
        using var brush = new SolidBrush(accent);
        graphics.FillEllipse(brush, bounds.X + 8, bounds.Y + bounds.Height / 2 - 2.2f, 4.4f, 4.4f);
        UiPaint.Text(graphics, label, _pillFont, brush, new RectangleF(bounds.X + 16, bounds.Y, bounds.Width - 19, bounds.Height), StringAlignment.Near);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _strongFont.Dispose();
            _captionFont.Dispose();
            _pillFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed record RouterHoverRow(string Label, string Value, Color? Tone = null);

/// <summary>The hover's view of the Router summary contribution (pure; no IO).</summary>
internal sealed record RouterHoverModel(string State, Color StateColor, string StateText, string Subtitle, IReadOnlyList<RouterHoverRow> Rows)
{
    internal static RouterHoverModel Empty(bool chinese) => new("unknown", UiTokens.State("idle"),
        chinese ? "未连接" : "Offline", chinese ? "任务路由" : "Task router",
        [new(chinese ? "状态" : "State", chinese ? "等待首次读数" : "Waiting for first reading")]);

    internal static RouterHoverModel From(MiniCardContribution? card, bool chinese)
    {
        if (card is null) return Empty(chinese);
        string? Text(string key) => card.Summary.FirstOrDefault(item => item.LabelKey == key)?.Value.Text;
        var stateItem = card.Summary.FirstOrDefault(item => item.LabelKey == "router.state");
        var state = stateItem?.Status ?? "unknown";
        var color = UiTokens.State(state is "cleanup" ? "warning" : state is "attention" ? "failed"
            : state is "idle" or "offline" or "unknown" ? "idle" : state);
        var queued = card.Summary.FirstOrDefault(item => item.LabelKey == "router.queued")?.Value.Integer ?? 0;
        var project = Text("router.project");
        var phase = Text("router.phase");
        var elapsed = Text("router.elapsed");
        var task = Text("router.task");
        var stateText = phase is { Length: > 0 } ? phase : (stateItem?.Value.Text ?? (chinese ? "未知" : "Unknown"));
        static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        var rows = new List<RouterHoverRow>
        {
            new(chinese ? "项目" : "Project", Dash(project)),
            new(chinese ? "状态" : "State", Dash(stateItem?.Value.Text), state is "attention" ? color : null),
            new(chinese ? "已运行" : "Elapsed", Dash(elapsed)),
            new(chinese ? "任务" : "Task", Dash(task)),
            new(chinese ? "排队" : "Queued", queued > 0
                ? (chinese ? $"{queued} 个（全部项目）" : $"{queued} (all projects)")
                : (chinese ? "无" : "None"), queued > 0 ? UiTokens.Accent : null),
        };
        return new(state, color, stateText, chinese ? "任务路由 · 编辑器作业" : "Task router · editor jobs", rows);
    }
}
