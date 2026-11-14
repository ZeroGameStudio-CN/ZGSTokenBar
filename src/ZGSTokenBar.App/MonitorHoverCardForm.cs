namespace ZGSTokenBar.App;

/// <summary>Shared chrome and spacing for the two taskbar monitor previews.</summary>
internal abstract class MonitorHoverCardForm(string windowTitle) : UiCardPopoverForm(windowTitle)
{
    protected const float CardWidth = 248;
    protected const float Pad = UiTokens.Space4;
    protected const float HeaderHeight = 44;
    protected const float FooterHeight = 22;

    private readonly Font _title = new("Segoe UI", UiTokens.FontTitle, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _caption = new("Segoe UI", UiTokens.FontCaption, FontStyle.Regular, GraphicsUnit.Pixel);

    protected void DrawHeader(Graphics graphics, RectangleF body, string title, string subtitle,
        Action<Graphics, RectangleF> drawGlyph, float? contentRight = null)
    {
        var left = body.Left + Pad;
        var right = contentRight ?? body.Right - Pad;
        var tileBounds = new RectangleF(left, body.Y + 10, 24, 24);
        using var tile = new SolidBrush(Color.White.WithAlpha(10));
        using var strong = new SolidBrush(UiTokens.TextStrong);
        using var muted = new SolidBrush(UiTokens.Muted);
        UiPaint.FillRounded(graphics, tile, RectangleF.Inflate(tileBounds, 2, 2), UiTokens.RadiusControl);
        drawGlyph(graphics, MonitorGlyph.Centered(tileBounds));
        var textLeft = tileBounds.Right + UiTokens.Space3 + 2;
        UiPaint.Text(graphics, title, _title, strong, new(textLeft, body.Y + 9, right - textLeft, 16), StringAlignment.Near);
        UiPaint.Text(graphics, subtitle, _caption, muted, new(textLeft, body.Y + 25, right - textLeft, 13), StringAlignment.Near);
    }

    protected void DrawFooter(Graphics graphics, RectangleF body, string detail)
    {
        var y = body.Bottom - FooterHeight;
        UiPaint.Divider(graphics, body.Left + Pad, body.Right - Pad, y + .5f, UiTokens.Hairline);
        using var muted = new SolidBrush(UiTokens.Muted);
        UiPaint.Text(graphics, detail, _caption, muted,
            new(body.Left + Pad, y + 4, body.Width - 2 * Pad, 14), StringAlignment.Near);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0084 /* WM_NCHITTEST */)
        {
            message.Result = -1; // HTTRANSPARENT; a preview does not receive clicks.
            return;
        }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _title.Dispose();
            _caption.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class IntakeHoverCardForm() : MonitorHoverCardForm("Work intake")
{
    private NativeText _text = NativeText.For("zh-CN");
    protected override SizeF LogicalBodySize => new(CardWidth, HeaderHeight + FooterHeight);

    internal void ShowFor(BarForm owner, Rectangle anchor, NativeText text, float scale)
    {
        _text = text;
        ShowAt(owner, anchor, scale);
    }

    internal Bitmap RenderForTest(NativeText text, int dpi)
    {
        _text = text;
        return RenderCard(dpi);
    }

    protected override void DrawBody(Graphics graphics, RectangleF body)
    {
        DrawHeader(graphics, body, _text.IntakeObserver, _text.IntakePreviewSubtitle,
            static (g, icon) => MonitorGlyph.DrawIntake(g, icon));
        DrawFooter(graphics, body, _text.IntakePreviewAction);
    }
}
