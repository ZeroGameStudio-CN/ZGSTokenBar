using System.Drawing.Drawing2D;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed record SystemUsagePopoverContent(SystemUsageSnapshot Snapshot, bool Pinned);

internal sealed class SystemUsagePopoverForm : Form
{
    internal const int LogicalBodyWidth = 276;
    internal const int LogicalBodyHeight = 324;
    private const int LogicalTailSize = 8;
    private const int LogicalGap = 3;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MouseActivateNoActivate = 3;
    private const int ExitDurationMs = 90;

    private readonly Font _titleFont = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _subtitleFont = new("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _metricLabelFont = new("Segoe UI Semibold", 8.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _detailFont = new("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _tableValueFont = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly AlignedStringFormats _textFormats = new();
    private readonly System.Windows.Forms.Timer _entranceTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _exitTimer = new() { Interval = 16 };
    private SystemUsagePopoverContent? _content;
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
        BackColor = _backgroundTheme.Popover;
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
        BackColor = theme.Popover;
        Invalidate();
    }

    public void UpdateContent(SystemUsagePopoverContent content, NativeText text)
    {
        _content = content;
        _text = text;
        Text = _text.SystemUsageTitle;
        Invalidate();
    }

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
        _content = content;
        _text = text;
        Text = _text.SystemUsageTitle;
        _scale = Math.Max(1, scale);

        var bodySize = new Size(Scale(LogicalBodyWidth), Scale(LogicalBodyHeight));
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
        SystemUsagePopoverContent content,
        NativeText text,
        QuotaBackgroundTheme theme,
        int dpi)
    {
        _content = content;
        _text = text;
        _backgroundTheme = theme;
        BackColor = theme.Popover;
        _scale = Math.Max(1, dpi / 96f);
        _tailSide = PopoverTailSide.Bottom;
        _tailOffset = LogicalBodyWidth / 2f;
        ClientSize = new Size(Scale(LogicalBodyWidth), Scale(LogicalBodyHeight + LogicalTailSize));
        CreateControl();
        UpdateWindowRegion();

        var bitmap = new Bitmap(
            ClientSize.Width,
            ClientSize.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
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
        if (_content is not { } content) return;
        var snapshot = content.Snapshot;
        using var primary = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var muted = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var divider = new Pen(Color.FromArgb(36, 100, 116, 139), 1);

        DrawString(
            graphics,
            _text.SystemUsageTitle,
            _titleFont,
            primary,
            new RectangleF(body.X + 12, body.Y + 8, 128, 16),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.SystemUsagePopoverSubtitle(content.Pinned),
            _subtitleFont,
            muted,
            new RectangleF(body.Right - 148, body.Y + 10, 136, 12),
            StringAlignment.Far);

        var metrics = new[]
        {
            (_text.SystemUsageCpu, snapshot.CpuPercent),
            (_text.SystemUsageMemory, snapshot.MemoryPercent),
            (_text.SystemUsageDisk, snapshot.DiskActivePercent),
            (_text.SystemUsageGpu, snapshot.GpuPercent),
        };
        const float metricGap = 3;
        const float metricHeight = 18;
        for (var index = 0; index < metrics.Length; index++)
        {
            var row = new RectangleF(
                body.X + 12,
                body.Y + 34 + index * (metricHeight + metricGap),
                body.Width - 24,
                metricHeight);
            DrawMetricRow(graphics, row, metrics[index].Item1, metrics[index].Item2);
        }

        graphics.DrawLine(divider, body.X + 12, body.Y + 123, body.Right - 12, body.Y + 123);
        DrawDetailRow(
            graphics,
            body,
            body.Y + 128,
            _text.SystemUsageCpu,
            _text.SystemUsageCpuDetail(snapshot.LogicalProcessorCount),
            primary,
            muted);
        DrawDetailRow(
            graphics,
            body,
            body.Y + 144,
            _text.SystemUsageMemory,
            _text.SystemUsageMemoryDetail(
                snapshot.MemoryUsedBytes,
                snapshot.MemoryTotalBytes,
                snapshot.MemoryAvailableBytes),
            primary,
            muted);
        DrawDetailRow(
            graphics,
            body,
            body.Y + 160,
            _text.SystemUsageDisk,
            _text.SystemUsageDiskDetail(
                snapshot.DiskActivePercent,
                snapshot.DiskReadBytesPerSecond,
                snapshot.DiskWriteBytesPerSecond),
            primary,
            muted);
        DrawDetailRow(
            graphics,
            body,
            body.Y + 176,
            _text.SystemUsageGpu,
            _text.SystemUsageGpuDetail(snapshot.GpuPercent, snapshot.GpuEngine, snapshot.GpuProcessCount),
            primary,
            muted);

        graphics.DrawLine(divider, body.X + 12, body.Y + 197, body.Right - 12, body.Y + 197);
        DrawString(
            graphics,
            _text.SystemUsageTopProcesses,
            _subtitleFont,
            muted,
            new RectangleF(body.X + 12, body.Y + 203, 112, 12),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.SystemUsageCpu,
            _subtitleFont,
            muted,
            new RectangleF(body.X + 126, body.Y + 203, 34, 12),
            StringAlignment.Far);
        DrawString(
            graphics,
            _text.SystemUsageMemory,
            _subtitleFont,
            muted,
            new RectangleF(body.X + 162, body.Y + 203, 56, 12),
            StringAlignment.Far);
        DrawString(
            graphics,
            _text.SystemUsageGpu,
            _subtitleFont,
            muted,
            new RectangleF(body.X + 220, body.Y + 203, 44, 12),
            StringAlignment.Far);

        if (snapshot.TopProcesses.Count == 0)
        {
            DrawString(
                graphics,
                _text.SystemUsageTopProcessesUnavailable,
                _detailFont,
                muted,
                new RectangleF(body.X + 12, body.Y + 222, body.Width - 24, 16),
                StringAlignment.Near);
        }
        else
        {
            for (var index = 0; index < Math.Min(5, snapshot.TopProcesses.Count); index++)
            {
                DrawProcessRow(
                    graphics,
                    body,
                    body.Y + 220 + index * 15,
                    index + 1,
                    snapshot.TopProcesses[index],
                    primary);
            }
        }

        DrawString(
            graphics,
            _text.SystemUsageCapturedAt(snapshot.CapturedAt),
            _subtitleFont,
            muted,
            new RectangleF(body.X + 12, body.Bottom - 14, 120, 10),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            muted,
            new RectangleF(body.Right - 120, body.Bottom - 14, 108, 10),
            StringAlignment.Far);
    }

    private void DrawMetricRow(Graphics graphics, RectangleF bounds, string label, double? value)
    {
        using var cardFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var cardBorder = new Pen(Color.FromArgb(46, 100, 116, 139), 1);
        using var path = RoundedRectangle(bounds, 7);
        graphics.FillPath(cardFill, path);
        graphics.DrawPath(cardBorder, path);

        var color = value is { } percent ? UsageColor(percent) : Color.FromArgb(100, 116, 139);
        using var labelBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(color);
        DrawString(
            graphics,
            label,
            _metricLabelFont,
            labelBrush,
            new RectangleF(bounds.X + 8, bounds.Y + 3, 45, 12),
            StringAlignment.Near);

        var rail = new RectangleF(bounds.X + 58, bounds.Y + 7, bounds.Width - 116, 4);
        using var railBrush = new SolidBrush(Color.FromArgb(48, 100, 116, 139));
        graphics.FillRoundedRectangle(railBrush, rail, 2);
        if (value is { } usage && usage > 0)
        {
            using var usageBrush = new SolidBrush(color);
            graphics.FillRoundedRectangle(
                usageBrush,
                new RectangleF(rail.X, rail.Y, rail.Width * (float)Math.Clamp(usage / 100, 0, 1), rail.Height),
                2);
        }

        DrawString(
            graphics,
            FormatUsage(value),
            _tableValueFont,
            valueBrush,
            new RectangleF(bounds.Right - 50, bounds.Y + 2, 42, 14),
            StringAlignment.Far);
    }

    private void DrawDetailRow(
        Graphics graphics,
        RectangleF body,
        float y,
        string label,
        string value,
        Brush primary,
        Brush muted)
    {
        DrawString(
            graphics,
            label,
            _detailFont,
            muted,
            new RectangleF(body.X + 12, y, 42, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            value,
            _detailFont,
            primary,
            new RectangleF(body.X + 58, y, body.Width - 70, 14),
            StringAlignment.Near);
    }

    private void DrawProcessRow(
        Graphics graphics,
        RectangleF body,
        float y,
        int rank,
        SystemProcessUsage process,
        Brush primary)
    {
        DrawString(
            graphics,
            $"{rank}. {_text.SystemUsageProcessName(process.Name, process.ProcessCount)}",
            _detailFont,
            primary,
            new RectangleF(body.X + 12, y, 112, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            FormatUsage(process.CpuPercent),
            _tableValueFont,
            primary,
            new RectangleF(body.X + 126, y, 34, 14),
            StringAlignment.Far);
        DrawString(
            graphics,
            FormatProcessMemory(process.PrivateWorkingSetBytes),
            _tableValueFont,
            primary,
            new RectangleF(body.X + 162, y, 56, 14),
            StringAlignment.Far);
        DrawString(
            graphics,
            FormatUsage(process.GpuPercent),
            _tableValueFont,
            primary,
            new RectangleF(body.X + 220, y, 44, 14),
            StringAlignment.Far);
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
        LogicalBodyWidth,
        LogicalBodyHeight,
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
        if (value is null) return "--";
        if (value <= 0) return "0%";
        if (value < 1) return "<1%";
        return $"{Math.Round(value.Value):0}%";
    }

    private static string FormatProcessMemory(ulong bytes)
    {
        const double bytesPerMegabyte = 1024d * 1024;
        const double bytesPerGigabyte = 1024d * 1024 * 1024;
        if (bytes >= bytesPerGigabyte) return $"{bytes / bytesPerGigabyte:0.#}G";
        var megabytes = bytes / bytesPerMegabyte;
        if (megabytes < 1) return "<1M";
        return $"{Math.Round(megabytes):0}M";
    }

    private static Color UsageColor(double usage) => usage switch
    {
        >= 90 => Color.FromArgb(251, 113, 133),
        >= 70 => Color.FromArgb(251, 191, 36),
        _ => Color.FromArgb(52, 211, 153),
    };

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
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

    private void DrawString(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        RectangleF bounds,
        StringAlignment alignment)
    {
        graphics.DrawString(text, font, brush, bounds, _textFormats.For(alignment));
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
            _subtitleFont.Dispose();
            _metricLabelFont.Dispose();
            _detailFont.Dispose();
            _tableValueFont.Dispose();
            _textFormats.Dispose();
        }
        base.Dispose(disposing);
    }
}
