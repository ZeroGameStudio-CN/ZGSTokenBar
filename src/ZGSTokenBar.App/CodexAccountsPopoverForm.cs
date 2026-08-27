using System.Drawing.Drawing2D;
using System.Globalization;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class CodexAccountsPopoverForm : Form
{
    // The width is content-driven between these bounds so short account lists do not leave empty space.
    internal const int LogicalBodyWidth = 360;
    internal const int LogicalMinimumBodyWidth = 260;
    internal const int LogicalRowHeight = 18;
    private const int LogicalTopPadding = 10;
    private const int LogicalHeadingHeight = 16;
    private const int LogicalRowsGap = 4;
    private const int LogicalBottomPadding = 10;
    private const int LogicalTailSize = 8;
    private const int LogicalGap = 3;
    private const int LogicalContentPadding = 14;
    private const int LogicalMarkerToEmailGap = 18;
    private const int LogicalColumnGap = 16;
    private const int LogicalMinimumEmailWidth = 84;
    private const int LogicalMinimumPlanWidth = 44;
    private const int LogicalMinimumQuotaWidth = 60;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MouseActivateNoActivate = 3;
    private const int ExitDurationMs = 90;

    private readonly Font _headingFont = new("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _rowFont = new("Segoe UI Semibold", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _planFont = new("Cascadia Mono", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _quotaFont = new("Cascadia Mono", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _markerFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly System.Windows.Forms.Timer _entranceTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _exitTimer = new() { Interval = 16 };
    private CodexAccountInfo[] _accounts = [];
    private CodexAccountQuota[] _quotas = [];
    private NativeText _text = NativeText.For("zh-CN");
    private PopoverTailSide _tailSide = PopoverTailSide.Bottom;
    private float _tailOffset = LogicalBodyWidth / 2f;
    private float _scale = 1;
    private int _logicalBodyWidth = LogicalBodyWidth;
    private int _logicalBodyHeight = BodyHeight(0);
    private int _emailColumnWidth = LogicalMinimumEmailWidth;
    private int _planColumnWidth = LogicalMinimumPlanWidth;
    private int _quotaColumnWidth;
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

    public CodexAccountsPopoverForm()
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
        Text = "Codex accounts";
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

    internal static int LogicalBodyHeightFor(int accountCount) => BodyHeight(accountCount);

    internal int CurrentLogicalBodyWidth => _logicalBodyWidth;

    public void ShowFor(
        BarForm owner,
        Rectangle anchorScreen,
        IReadOnlyList<CodexAccountInfo> accounts,
        QuotaCard hoveredCard,
        NativeText text,
        float scale,
        bool animateMotion,
        IReadOnlyList<CodexAccountQuota>? quotas = null)
    {
        var wasVisible = Visible;
        _exitTimer.Stop();
        _animateMotion = animateMotion;
        _accounts = accounts.ToArray();
        _quotas = (quotas ?? []).ToArray();
        _text = text;
        _scale = Math.Max(1, scale);
        _logicalBodyWidth = CalculateLogicalBodyWidth();
        _logicalBodyHeight = BodyHeight(_accounts.Length);

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
        IReadOnlyList<CodexAccountInfo> accounts,
        QuotaCard hoveredCard,
        NativeText text,
        QuotaBackgroundTheme theme,
        int dpi,
        IReadOnlyList<CodexAccountQuota>? quotas = null)
    {
        _accounts = accounts.ToArray();
        _quotas = (quotas ?? []).ToArray();
        _text = text;
        _backgroundTheme = theme;
        BackColor = theme.Popover;
        _scale = Math.Max(1, dpi / 96f);
        _logicalBodyWidth = CalculateLogicalBodyWidth();
        _logicalBodyHeight = BodyHeight(_accounts.Length);
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
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
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
        var x = body.X + LogicalContentPadding;
        var y = body.Y + LogicalTopPadding;
        using var headingBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var rowBrush = new SolidBrush(Color.FromArgb(203, 213, 225));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var currentBrush = new SolidBrush(Color.FromArgb(94, 234, 212));
        using var separatorPen = new Pen(Color.FromArgb(48, 71, 85, 105), 1);

        DrawString(
            graphics,
            _text.CodexAccountsHeading,
            _headingFont,
            headingBrush,
            new RectangleF(x, y, body.Width - 28, LogicalHeadingHeight),
            StringAlignment.Near);

        var rowY = y + LogicalHeadingHeight + LogicalRowsGap;
        for (var index = 0; index < _accounts.Length; index++)
        {
            var account = _accounts[index];
            var rowBounds = new RectangleF(x, rowY, body.Width - 28, LogicalRowHeight);
            var quotaLeft = rowBounds.Right - _quotaColumnWidth;
            var planRight = _quotaColumnWidth > 0
                ? quotaLeft - LogicalColumnGap
                : rowBounds.Right;
            var planLeft = planRight - _planColumnWidth;
            var emailLeft = rowBounds.X + LogicalMarkerToEmailGap;
            var emailRight = planLeft - LogicalColumnGap;
            DrawString(
                graphics,
                account.Active ? "●" : "○",
                _markerFont,
                account.Active ? currentBrush : mutedBrush,
                new RectangleF(rowBounds.X, rowBounds.Y, 14, rowBounds.Height),
                StringAlignment.Near);
            DrawString(
                graphics,
                AccountIdentity(account),
                _rowFont,
                account.Active ? rowBrush : mutedBrush,
                new RectangleF(emailLeft, rowBounds.Y, Math.Max(1, emailRight - emailLeft), rowBounds.Height),
                StringAlignment.Near);
            DrawPlanBadge(
                graphics,
                account,
                new RectangleF(planLeft, rowBounds.Y, _planColumnWidth, rowBounds.Height),
                account.Active ? currentBrush : mutedBrush);
            var quotaText = QuotaText(account);
            if (quotaText is not null)
            {
                var quota = QuotaFor(account);
                DrawString(
                    graphics,
                    quotaText,
                    _quotaFont,
                    quota?.Windows.Any(window => window.UsedPercent is not null) == true
                        ? currentBrush
                        : mutedBrush,
                    new RectangleF(quotaLeft, rowBounds.Y, _quotaColumnWidth, rowBounds.Height),
                    StringAlignment.Far);
            }
            if (index < _accounts.Length - 1)
            {
                graphics.DrawLine(
                    separatorPen,
                    rowBounds.Left,
                    rowBounds.Bottom + 1,
                    rowBounds.Right,
                    rowBounds.Bottom + 1);
            }
            rowY += LogicalRowHeight;
        }
    }

    private int CalculateLogicalBodyWidth()
    {
        using var bitmap = new Bitmap(1, 1);
        using var measurement = Graphics.FromImage(bitmap);
        measurement.PageUnit = GraphicsUnit.Pixel;

        var headingWidth = MeasureText(measurement, _text.CodexAccountsHeading, _headingFont);
        var emailWidth = _accounts
            .Select(account => MeasureText(
                measurement,
                AccountIdentity(account),
                _rowFont))
            .DefaultIfEmpty(0)
            .Max();
        var planWidth = _accounts
            .Select(account => PlanBadgePresentation.Width(PlanBadgePresentation.Label(account.Plan)))
            .DefaultIfEmpty(0)
            .Max();
        var quotaTexts = _accounts
            .Select(QuotaText)
            .Where(text => text is not null)
            .Select(text => text!)
            .ToArray();
        var quotaWidth = quotaTexts
            .Select(text => MeasureText(measurement, text, _quotaFont))
            .DefaultIfEmpty(0)
            .Max();

        _emailColumnWidth = Math.Max(LogicalMinimumEmailWidth, emailWidth + 4);
        _planColumnWidth = Math.Max(
            LogicalMinimumPlanWidth,
            (int)Math.Ceiling(planWidth) + 4);
        _quotaColumnWidth = quotaTexts.Length == 0
            ? 0
            : Math.Max(LogicalMinimumQuotaWidth, quotaWidth + 4);

        var rowWidth = (LogicalContentPadding * 2)
            + LogicalMarkerToEmailGap
            + _emailColumnWidth
            + LogicalColumnGap
            + _planColumnWidth
            + (_quotaColumnWidth > 0 ? LogicalColumnGap + _quotaColumnWidth : 0);
        var textWidth = headingWidth + LogicalContentPadding * 2;
        var width = Math.Clamp(
            Math.Max(rowWidth, textWidth),
            LogicalMinimumBodyWidth,
            LogicalBodyWidth);

        var fixedWidth = (LogicalContentPadding * 2)
            + LogicalMarkerToEmailGap
            + LogicalColumnGap
            + _planColumnWidth
            + (_quotaColumnWidth > 0 ? LogicalColumnGap + _quotaColumnWidth : 0);
        _emailColumnWidth = Math.Max(1, Math.Min(_emailColumnWidth, width - fixedWidth));
        return width;
    }

    private static int MeasureText(Graphics graphics, string value, Font font) =>
        Math.Max(0, (int)Math.Ceiling(graphics.MeasureString(value, font).Width));

    private void DrawPlanBadge(
        Graphics graphics,
        CodexAccountInfo account,
        RectangleF bounds,
        Brush fallbackBrush)
    {
        var label = PlanBadgePresentation.Label(account.Plan);
        if (!PlanBadgePresentation.TryGetStyle(label, out var style))
        {
            DrawString(
                graphics,
                CodexAccountFormatting.PlanLabel(account.Plan),
                _planFont,
                fallbackBrush,
                bounds,
                StringAlignment.Near);
            return;
        }

        var tagBounds = new RectangleF(
            bounds.X,
            bounds.Y + (bounds.Height - 13) / 2,
            PlanBadgePresentation.Width(label),
            13);
        using var tagFill = new SolidBrush(style.Fill);
        using var tagBorder = new Pen(style.Border, 1);
        using var tagPath = RoundedRectangle(tagBounds, 4);
        graphics.FillPath(tagFill, tagPath);
        graphics.DrawPath(tagBorder, tagPath);
        using var tagTextBrush = new SolidBrush(style.Text);
        DrawString(graphics, label, _planFont, tagTextBrush, tagBounds, StringAlignment.Center);
    }

    private static string AccountIdentity(CodexAccountInfo account) =>
        string.Equals(
            CodexAccountFormatting.PlanLabel(account.Plan),
            "API key",
            StringComparison.OrdinalIgnoreCase)
            ? $"API · {Math.Max(1, account.AccountCount)}"
            : CodexAccountFormatting.MaskEmail(account.Email);

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

    private static int BodyHeight(int accountCount) =>
        LogicalTopPadding
        + LogicalHeadingHeight
        + LogicalRowsGap
        + Math.Max(1, accountCount) * LogicalRowHeight
        + LogicalBottomPadding;

    private string? QuotaText(CodexAccountInfo account)
    {
        if (string.Equals(
                CodexAccountFormatting.PlanLabel(account.Plan),
                "API key",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var quota = QuotaFor(account);
        var windows = quota?.Windows
            .Where(window => window.UsedPercent is not null)
            .OrderBy(window => window.Duration)
            .Take(2)
            .Select(window =>
                $"{QuotaDisplayFormatting.FormatWindowShort(window)} {FormatRemaining(window.UsedPercent!.Value)}")
            .ToArray() ?? [];
        return windows.Length == 0 ? "—" : string.Join(" · ", windows);
    }

    private CodexAccountQuota? QuotaFor(CodexAccountInfo account) =>
        _quotas.FirstOrDefault(quota =>
            string.Equals(quota.AccountId, account.AccountId, StringComparison.Ordinal));

    private static string FormatRemaining(double usedPercent)
    {
        var remaining = Math.Clamp(100 - usedPercent, 0, 100);
        if (remaining > 0 && remaining < 1) return "<1%";
        return $"{Math.Round(remaining).ToString("0", CultureInfo.InvariantCulture)}%";
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
            _headingFont.Dispose();
            _rowFont.Dispose();
            _planFont.Dispose();
            _quotaFont.Dispose();
            _markerFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
