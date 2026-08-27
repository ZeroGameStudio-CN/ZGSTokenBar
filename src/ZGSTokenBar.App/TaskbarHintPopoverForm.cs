using System.Drawing.Drawing2D;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class TaskbarHintPopoverForm : Form
{
    // Keep long refresh diagnostics readable while sizing short hints to their content.
    internal const int LogicalBodyWidth = 300;
    internal const int LogicalMinimumBodyWidth = 140;
    private const int LogicalTopPadding = 10;
    private const int LogicalTitleHeight = 16;
    private const int LogicalDetailGap = 3;
    private const int LogicalLineHeight = 15;
    private const int LogicalBottomPadding = 10;
    private const int LogicalTailSize = 8;
    private const int LogicalGap = 3;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MouseActivateNoActivate = 3;
    private const int ExitDurationMs = 90;

    private readonly Font _titleFont = new("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _detailFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly System.Windows.Forms.Timer _entranceTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _exitTimer = new() { Interval = 16 };
    private string _title = string.Empty;
    private string[] _detailLines = [];
    private QuotaBackgroundTheme _backgroundTheme = QuotaBackgroundPalette.Resolve(
        AppSettings.DefaultBackgroundPalette);
    private PopoverTailSide _tailSide = PopoverTailSide.Bottom;
    private float _tailOffset = LogicalBodyWidth / 2f;
    private float _scale = 1;
    private int _logicalBodyWidth = LogicalBodyWidth;
    private int _logicalBodyHeight = BodyHeight("");
    private DateTime _entranceStarted;
    private Point _entranceLocation;
    private Point _restingLocation;
    private DateTime _exitStarted;
    private Point _exitStartLocation;
    private Point _exitLocation;
    private double _exitStartOpacity = 1;
    private bool _animateMotion;

    public TaskbarHintPopoverForm()
    {
        _entranceTimer.Tick += (_, _) => AdvanceEntrance();
        _exitTimer.Tick += (_, _) => AdvanceExit();
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _backgroundTheme.Popover;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Taskbar hint";
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
        BackColor = theme.Popover;
        Invalidate();
    }

    internal int CurrentLogicalBodyWidth => _logicalBodyWidth;

    internal void ShowFor(
        BarForm owner,
        Rectangle anchorScreen,
        string title,
        string detail,
        float scale,
        bool animateMotion)
    {
        var wasVisible = Visible;
        _exitTimer.Stop();
        _animateMotion = animateMotion;
        _title = title;
        _detailLines = Lines(detail);
        _scale = Math.Max(1, scale);
        _logicalBodyWidth = CalculateLogicalBodyWidth();
        _logicalBodyHeight = BodyHeight(detail);

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
        if (!wasVisible && animateMotion)
        {
            _entranceLocation = TaskbarPopoverMath.OffsetFromAnchor(
                _restingLocation,
                _tailSide,
                Math.Max(1, Scale(3)));
            _entranceStarted = DateTime.UtcNow;
            Opacity = .01;
            Location = _entranceLocation;
            Show(owner);
            if (!TaskbarPlacement.ShowAt(Handle, _entranceLocation, placement.WindowSize)) Location = _entranceLocation;
            _entranceTimer.Start();
            return;
        }

        Opacity = 1;
        if (!wasVisible) Show(owner);
        if (!TaskbarPlacement.ShowAt(Handle, _restingLocation, placement.WindowSize)) Location = _restingLocation;
    }

    internal Bitmap RenderForTest(
        string title,
        string detail,
        QuotaBackgroundTheme theme,
        int dpi)
    {
        _title = title;
        _detailLines = Lines(detail);
        _backgroundTheme = theme;
        BackColor = theme.Popover;
        _scale = Math.Max(1, dpi / 96f);
        _logicalBodyWidth = CalculateLogicalBodyWidth();
        _logicalBodyHeight = BodyHeight(detail);
        _tailSide = PopoverTailSide.Bottom;
        _tailOffset = _logicalBodyWidth / 2f;
        ClientSize = new Size(Scale(_logicalBodyWidth), Scale(_logicalBodyHeight + LogicalTailSize));
        CreateControl();
        UpdateWindowRegion();

        var bitmap = new Bitmap(
            ClientSize.Width,
            ClientSize.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    internal static int LogicalBodyHeightFor(string detail) => BodyHeight(detail);

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
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.ScaleTransform(_scale, _scale);

        var body = BodyBounds();
        using var bodyPath = RoundedRectangle(RectangleF.Inflate(body, -.5f, -.5f), 10);
        var tail = TailPoints(body);
        using var surfacePath = new GraphicsPath { FillMode = FillMode.Winding };
        surfacePath.AddPath(bodyPath, false);
        surfacePath.AddPolygon(tail);
        using var fill = new SolidBrush(_backgroundTheme.Popover);
        using var border = new Pen(Color.FromArgb(86, 100, 116, 139), 1);
        graphics.FillPath(fill, surfacePath);
        graphics.DrawPath(border, bodyPath);
        graphics.DrawLines(border, tail);

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

    private void DrawContent(Graphics graphics, RectangleF body)
    {
        var x = body.X + 14;
        var y = body.Y + LogicalTopPadding;
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var detailBrush = new SolidBrush(Color.FromArgb(203, 213, 225));
        DrawString(
            graphics,
            _title,
            _titleFont,
            titleBrush,
            new RectangleF(x, y, body.Width - 28, LogicalTitleHeight),
            StringAlignment.Near);

        var detailY = y + LogicalTitleHeight + LogicalDetailGap;
        for (var index = 0; index < _detailLines.Length; index++)
        {
            DrawString(
                graphics,
                _detailLines[index],
                _detailFont,
                detailBrush,
                new RectangleF(x, detailY + index * LogicalLineHeight, body.Width - 28, LogicalLineHeight),
                StringAlignment.Near);
        }
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

    private static int BodyHeight(string detail) =>
        LogicalTopPadding
        + LogicalTitleHeight
        + LogicalDetailGap
        + Math.Max(1, Lines(detail).Length) * LogicalLineHeight
        + LogicalBottomPadding;

    private static string[] Lines(string detail) =>
        detail.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Where(line => line.Length > 0)
            .Take(4)
            .DefaultIfEmpty("—")
            .ToArray();

    private int CalculateLogicalBodyWidth()
    {
        using var bitmap = new Bitmap(1, 1);
        using var measurement = Graphics.FromImage(bitmap);
        measurement.PageUnit = GraphicsUnit.Pixel;
        var widest = MeasureText(measurement, _title, _titleFont);
        foreach (var line in _detailLines)
        {
            widest = Math.Max(widest, MeasureText(measurement, line, _detailFont));
        }

        return Math.Clamp(widest + 28, LogicalMinimumBodyWidth, LogicalBodyWidth);
    }

    private static int MeasureText(Graphics graphics, string value, Font font) =>
        Math.Max(0, (int)Math.Ceiling(graphics.MeasureString(value, font).Width));

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

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawString(
        Graphics graphics,
        string value,
        Font font,
        Brush brush,
        RectangleF bounds,
        StringAlignment alignment)
    {
        using var format = new StringFormat
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        graphics.DrawString(value, font, brush, bounds, format);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _entranceTimer.Stop();
            _entranceTimer.Dispose();
            _exitTimer.Stop();
            _exitTimer.Dispose();
            _titleFont.Dispose();
            _detailFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
