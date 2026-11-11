namespace ZGSTokenBar.App;

/// <summary>One row of the show/hide list: a dock ring (provider or account) and whether it is shown.</summary>
internal sealed record DockVisibilityEntry(string Id, string Title, string? Detail, Image? Icon, bool Visible);

/// <summary>
/// The "show/hide" card opened from the eye chip of the expanded overflow (or the dock menu): one UiKit
/// row per provider/account with a switch. Toggling is applied immediately and persisted by the app.
/// </summary>
internal sealed class DockVisibilityPopoverForm : UiCardPopoverForm
{
    internal const string PopoverId = "dock.visibility";
    private const float Width = 248;
    private const float Pad = UiTokens.Space4;
    private const float HeaderHeight = UiTokens.HeaderHeight;
    private const float RowHeight = 30;
    private const float FooterHeight = 22;

    private readonly Font _titleFont = new("Segoe UI", UiTokens.FontTitle, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _bodyFont = new("Segoe UI", UiTokens.FontBody, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _captionFont = new("Segoe UI", UiTokens.FontCaption, FontStyle.Regular, GraphicsUnit.Pixel);
    private IReadOnlyList<DockVisibilityEntry> _entries = [];
    private bool _chinese = true;
    private int _hoverRow = -1;

    public event EventHandler<string>? EntryToggled;

    public DockVisibilityPopoverForm() : base("Dock items") { }

    protected override SizeF LogicalBodySize => new(Width, HeaderHeight + Pad / 2 + Math.Max(1, _entries.Count) * RowHeight + FooterHeight + Pad / 2);

    internal void ShowFor(BarForm owner, Rectangle anchorScreen, IReadOnlyList<DockVisibilityEntry> entries, bool chinese, float scale)
    {
        _entries = entries;
        _chinese = chinese;
        _hoverRow = -1;
        ShowAt(owner, anchorScreen, scale);
    }

    internal void UpdateEntries(IReadOnlyList<DockVisibilityEntry> entries)
    {
        var resized = entries.Count != _entries.Count;
        _entries = entries;
        if (resized) Relayout(); else Invalidate();
    }

    internal Bitmap RenderForTest(IReadOnlyList<DockVisibilityEntry> entries, bool chinese, int dpi)
    {
        _entries = entries;
        _chinese = chinese;
        return RenderCard(dpi);
    }

    protected override void DrawBody(Graphics graphics, RectangleF body)
    {
        using var strong = new SolidBrush(UiTokens.TextStrong);
        using var text = new SolidBrush(UiTokens.Text);
        using var muted = new SolidBrush(UiTokens.Muted);
        var header = new RectangleF(body.X + Pad, body.Y + 6, body.Width - Pad * 2, HeaderHeight - 8);
        UiPaint.Text(graphics, _chinese ? "显示 / 隐藏" : "Show / hide", _titleFont, strong, header, StringAlignment.Near);
        var shown = _entries.Count(entry => entry.Visible);
        UiPaint.Text(graphics, $"{shown}/{_entries.Count}", _captionFont, muted, header, StringAlignment.Far);
        UiPaint.Divider(graphics, body.X + Pad, body.Right - Pad, body.Y + HeaderHeight, UiTokens.Hairline);

        for (var index = 0; index < _entries.Count; index++)
        {
            var entry = _entries[index];
            var row = RowBounds(body, index);
            if (index == _hoverRow)
            {
                using var hover = new SolidBrush(Color.White.WithAlpha(14));
                UiPaint.FillRounded(graphics, hover, RectangleF.Inflate(row, Pad / 2, -1), UiTokens.RadiusControl);
            }
            var iconSize = 16f;
            var iconBounds = new RectangleF(row.X, row.Y + (row.Height - iconSize) / 2, iconSize, iconSize);
            if (entry.Icon is { } icon)
            {
                var state = graphics.Save();
                if (!entry.Visible) graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.DrawImage(icon, iconBounds);
                graphics.Restore(state);
                if (!entry.Visible)
                {
                    using var veil = new SolidBrush(UiTokens.CardSurface.WithAlpha(150));
                    graphics.FillRectangle(veil, RectangleF.Inflate(iconBounds, 1, 1));
                }
            }
            var switchBounds = new RectangleF(row.Right - 30, row.Y + (row.Height - 16) / 2, 30, 16);
            var textLeft = iconBounds.Right + UiTokens.Space3;
            var textBounds = new RectangleF(textLeft, row.Y, switchBounds.X - UiTokens.Space3 - textLeft, row.Height);
            var titleBrush = entry.Visible ? text : muted;
            if (entry.Detail is { Length: > 0 } detail)
            {
                UiPaint.Text(graphics, entry.Title, _bodyFont, titleBrush, new RectangleF(textBounds.X, row.Y + 2, textBounds.Width, 15), StringAlignment.Near);
                UiPaint.Text(graphics, detail, _captionFont, muted, new RectangleF(textBounds.X, row.Y + 16, textBounds.Width, 13), StringAlignment.Near);
            }
            else
            {
                UiPaint.Text(graphics, entry.Title, _bodyFont, titleBrush, textBounds, StringAlignment.Near);
            }
            UiPaint.Switch(graphics, switchBounds, entry.Visible, index == _hoverRow);
        }

        var footer = new RectangleF(body.X + Pad, body.Bottom - Pad / 2 - FooterHeight, body.Width - Pad * 2, FooterHeight);
        UiPaint.Text(graphics, _chinese ? "也可在 设置 › 提供方 中开关数据源" : "Providers can also be switched off in Settings", _captionFont, muted, footer, StringAlignment.Near);
    }

    private RectangleF RowBounds(RectangleF body, int index) =>
        new(body.X + Pad, body.Y + HeaderHeight + Pad / 2 + index * RowHeight, body.Width - Pad * 2, RowHeight);

    private int RowAt(Point client)
    {
        var point = ToLogical(client);
        var body = BodyBounds();
        for (var index = 0; index < _entries.Count; index++)
        {
            if (RowBounds(body, index).Contains(point)) return index;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var row = RowAt(e.Location);
        if (row == _hoverRow) return;
        _hoverRow = row;
        Cursor = row >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverRow < 0) return;
        _hoverRow = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var row = RowAt(e.Location);
        if (row >= 0 && row < _entries.Count) EntryToggled?.Invoke(this, _entries[row].Id);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _captionFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
