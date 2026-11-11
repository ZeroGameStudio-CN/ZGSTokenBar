using System.Drawing.Drawing2D;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>
/// The shared hover-card window: graphite <see cref="UiTokens.CardSurface"/> body with a hairline and a
/// pointer tail, placed against a bar anchor, never activated. Subclasses only size and draw the body.
/// </summary>
internal abstract class UiCardPopoverForm : Form
{
    protected const int LogicalTailSize = 8;
    private const int LogicalGap = 3;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MouseActivateNoActivate = 3;

    private PopoverTailSide _tailSide = PopoverTailSide.Bottom;
    private float _tailOffset;
    protected float UiScale { get; private set; } = 1;

    protected UiCardPopoverForm(string title)
    {
        AutoScaleMode = AutoScaleMode.None;
        BackColor = UiTokens.CardSurface;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = title;
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

    /// <summary>Logical (96-dpi) body size for the current content.</summary>
    protected abstract SizeF LogicalBodySize { get; }

    protected abstract void DrawBody(Graphics graphics, RectangleF body);

    protected RectangleF BodyBounds()
    {
        var size = LogicalBodySize;
        return TaskbarPopoverMath.BodyBounds(_tailSide, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), LogicalTailSize);
    }

    /// <summary>Place (or re-place) the card against <paramref name="anchorScreen"/> and show it.</summary>
    protected void ShowAt(BarForm owner, Rectangle anchorScreen, float scale)
    {
        UiScale = Math.Max(1, scale);
        var size = LogicalBodySize;
        var placement = TaskbarMiniPopoverMath.Place(
            anchorScreen,
            new Size(Scale(size.Width), Scale(size.Height)),
            Scale(LogicalTailSize),
            Scale(LogicalGap),
            Screen.FromRectangle(anchorScreen).WorkingArea);
        _tailSide = placement.TailSide;
        _tailOffset = placement.TailOffset / UiScale;
        if (ClientSize != placement.WindowSize) ClientSize = placement.WindowSize;
        UpdateWindowRegion();
        Invalidate();
        if (!Visible) this.ShowPopover(BarForm.PopoverOwnerFor(owner));
        if (!TaskbarPlacement.ShowAt(Handle, placement.Location, placement.WindowSize)) Location = placement.Location;
    }

    /// <summary>Re-measure after the content changed while the card stays where it is.</summary>
    protected void Relayout()
    {
        var size = LogicalBodySize;
        var body = TaskbarPopoverMath.BodyBounds(_tailSide, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), LogicalTailSize);
        var window = new Size(
            Scale(_tailSide is PopoverTailSide.Left or PopoverTailSide.Right ? body.Width + LogicalTailSize : body.Width),
            Scale(_tailSide is PopoverTailSide.Top or PopoverTailSide.Bottom ? body.Height + LogicalTailSize : body.Height));
        if (_tailSide == PopoverTailSide.Bottom && Visible && window.Height != ClientSize.Height)
        {
            // Grow upwards so the tail keeps touching the anchor.
            Top -= window.Height - ClientSize.Height;
        }
        if (ClientSize != window) ClientSize = window;
        UpdateWindowRegion();
        Invalidate();
    }

    /// <summary>Offscreen render of the card exactly as it paints (bottom tail, centred).</summary>
    internal Bitmap RenderCard(int dpi)
    {
        UiScale = Math.Max(1, dpi / 96f);
        _tailSide = PopoverTailSide.Bottom;
        var size = LogicalBodySize;
        _tailOffset = size.Width / 2f;
        ClientSize = new Size(Scale(size.Width), Scale(size.Height + LogicalTailSize));
        CreateControl();
        UpdateWindowRegion();
        var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    protected PointF ToLogical(Point client) => new(client.X / UiScale, client.Y / UiScale);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        UiPaint.PrepareQuality(graphics);
        graphics.ScaleTransform(UiScale, UiScale);
        var body = BodyBounds();
        UiPaint.TailedSurface(graphics, body, TailPoints(body), UiTokens.CardSurface, UiTokens.Hairline, UiTokens.RadiusPanel);
        DrawBody(graphics, body);
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

    private PointF[] TailPoints(RectangleF body) => TaskbarPopoverMath.TailPoints(_tailSide, body, _tailOffset, LogicalTailSize);

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var body = BodyBounds();
        using var bodyPath = UiPaint.RoundedPath(body, UiTokens.RadiusPanel);
        using var tailPath = new GraphicsPath();
        tailPath.AddPolygon(TailPoints(body));
        using var matrix = new Matrix();
        matrix.Scale(UiScale, UiScale);
        bodyPath.Transform(matrix);
        tailPath.Transform(matrix);
        var next = new Region(bodyPath);
        next.Union(tailPath);
        Region?.Dispose();
        Region = next;
    }

    protected int Scale(float value) => Math.Max(1, (int)Math.Round(value * UiScale));
}
