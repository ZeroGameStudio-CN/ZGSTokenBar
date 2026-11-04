using System.Globalization;
using System.Drawing.Drawing2D;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class CodexStrategyPopoverForm : Form
{
    private const int LogicalWidth = 640;
    private const int LogicalRowHeight = 24;
    private const int LogicalTailSize = 8;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private readonly System.Windows.Forms.Timer _entranceTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _exitTimer = new() { Interval = 16 };
    private readonly Font _headingFont = new("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _bodyFont = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _strongFont = new("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly List<(string[] Values, Font Font, int Height)> _rows = [];
    private float _scale = 1;
    private int ContentHeight => 14 + _rows.Sum(row => row.Height);
    private NativeText _text = NativeText.For("zh-CN");
    private PopoverTailSide _tailSide = PopoverTailSide.Bottom;
    private float _tailOffset = LogicalWidth / 2f;
    private bool _animateMotion;
    private Point _restingLocation;
    private Point _entranceLocation;
    private DateTime _entranceStarted;
    private Point _exitStartLocation;
    private Point _exitLocation;
    private DateTime _exitStarted;
    private double _exitStartOpacity;
    internal bool IsOpen => Visible && !_exitTimer.Enabled;

    internal CodexStrategyPopoverForm()
    {
        _entranceTimer.Tick += (_, _) => AdvanceEntrance();
        _exitTimer.Tick += (_, _) => AdvanceExit();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        AutoScroll = true;
        DoubleBuffered = true;
        Text = TitleFor(_text);
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

    internal static string TitleFor(NativeText text) =>
        text.Locale == "zh-CN" ? "Codex 使用策略" : "Codex usage strategy";

    internal void ShowFor(
        BarForm owner,
        Rectangle anchor,
        CodexUsageStrategy strategy,
        NativeText text,
        QuotaBackgroundTheme theme,
        float scale,
        bool animateMotion)
    {
        var wasVisible = Visible;
        _exitTimer.Stop();
        _animateMotion = animateMotion;
        SetContent(strategy, text, theme, Math.Max(1, scale));
        var area = Screen.FromRectangle(anchor).WorkingArea;
        var tail = Scale(LogicalTailSize);
        var bodySize = new Size(
            Math.Min(Math.Max(1, area.Width - tail), Scale(LogicalWidth)),
            Math.Min(Math.Max(1, area.Height - tail), Scale(ContentHeight)));
        var placement = TaskbarMiniPopoverMath.Place(anchor, bodySize, tail, Scale(3), area);
        _tailSide = placement.TailSide;
        _tailOffset = placement.TailOffset / _scale;
        ClientSize = placement.WindowSize;
        AutoScrollMinSize = new Size(0, Scale(ContentHeight) + (VerticalTail ? tail : 0));
        _restingLocation = placement.Location;
        UpdateWindowRegion();
        Invalidate();
        _entranceTimer.Stop();
        if (!wasVisible && animateMotion)
        {
            _entranceLocation = TaskbarPopoverMath.OffsetFromAnchor(_restingLocation, _tailSide, Scale(3));
            _entranceStarted = DateTime.UtcNow;
            Opacity = .01;
            Location = _entranceLocation;
            Show(owner);
            TaskbarPlacement.ShowAt(Handle, _entranceLocation, placement.WindowSize);
            _entranceTimer.Start();
            return;
        }
        Opacity = 1;
        if (!wasVisible) Show(owner);
        if (!TaskbarPlacement.ShowAt(Handle, _restingLocation, placement.WindowSize))
            Bounds = new Rectangle(_restingLocation, placement.WindowSize);
    }

    internal Bitmap RenderForTest(CodexUsageStrategy strategy, NativeText text, QuotaBackgroundTheme theme,
        int dpi = 96, PopoverTailSide tailSide = PopoverTailSide.Bottom)
    {
        SetContent(strategy, text, theme, Math.Max(1, dpi / 96f));
        _tailSide = tailSide;
        _tailOffset = VerticalTail ? LogicalWidth / 2f : ContentHeight / 2f;
        AutoScrollMinSize = Size.Empty;
        ClientSize = new Size(Scale(LogicalWidth + (VerticalTail ? 0 : LogicalTailSize)),
            Scale(ContentHeight + (VerticalTail ? LogicalTailSize : 0)));
        UpdateWindowRegion();
        var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        using var graphics = Graphics.FromImage(bitmap);
        DrawContent(graphics);
        return bitmap;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawContent(e.Graphics);
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0021)
        {
            message.Result = 3;
            return;
        }
        base.WndProc(ref message);
    }

    internal bool ContainsScreenPoint(Point point)
    {
        var local = PointToClient(point);
        return ClientRectangle.Contains(local) && (Region?.IsVisible(local) ?? true);
    }

    private void DrawContent(Graphics graphics)
    {
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.ScaleTransform(_scale, _scale);
        var body = BodyBounds();
        var tail = TaskbarPopoverMath.TailPoints(_tailSide, body, _tailOffset, LogicalTailSize);
        using var bodyPath = RoundedRectangle(RectangleF.Inflate(body, -.5f, -.5f));
        using var surface = new GraphicsPath { FillMode = FillMode.Winding };
        surface.AddPath(bodyPath, false);
        surface.AddPolygon(tail);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(Color.FromArgb(86, 100, 116, 139), 1);
        graphics.FillPath(fill, surface);
        graphics.DrawPath(border, bodyPath);
        graphics.DrawLines(border, tail);
        graphics.SetClip(RectangleF.Inflate(body, -1, -1));
        graphics.TranslateTransform(body.Left, body.Top + AutoScrollPosition.Y / _scale);
        using var foreground = new SolidBrush(ForeColor);
        using var muted = new SolidBrush(Color.FromArgb(160, 174, 192));
        using var format = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        var contentWidth = Math.Max(1, body.Width - 24);
        var columnWidths = new[] { .23f, .19f, .29f, .29f };
        var y = 6f;
        foreach (var (values, font, height) in _rows)
        {
            var x = 12f;
            for (var column = 0; column < values.Length; column++)
            {
                var cellWidth = contentWidth * (values.Length == 1 ? 1 : columnWidths[column]);
                graphics.DrawString(values[column], column == 0 ? font : _bodyFont,
                    font == _bodyFont ? muted : foreground,
                    new RectangleF(x, y, Math.Max(1, cellWidth - 5), height), format);
                x += cellWidth;
            }
            y += height;
        }
        graphics.Restore(state);
    }

    private bool VerticalTail => _tailSide is PopoverTailSide.Top or PopoverTailSide.Bottom;

    private RectangleF BodyBounds() => TaskbarPopoverMath.BodyBounds(_tailSide,
        Math.Max(1, ClientSize.Width / _scale - (VerticalTail ? 0 : LogicalTailSize)),
        Math.Max(1, ClientSize.Height / _scale - (VerticalTail ? LogicalTailSize : 0)), LogicalTailSize);

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var body = BodyBounds();
        using var path = RoundedRectangle(body);
        using var tail = new GraphicsPath();
        tail.AddPolygon(TaskbarPopoverMath.TailPoints(_tailSide, body, _tailOffset, LogicalTailSize));
        using var matrix = new Matrix();
        matrix.Scale(_scale, _scale);
        path.Transform(matrix);
        tail.Transform(matrix);
        var region = new Region(path);
        region.Union(tail);
        Region?.Dispose();
        Region = region;
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(20, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) return path;
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

    internal void HidePopover()
    {
        _entranceTimer.Stop();
        if (!IsOpen) return;
        if (!_animateMotion)
        {
            Opacity = 1;
            Hide();
            return;
        }
        _exitStartLocation = Location;
        _exitLocation = TaskbarPopoverMath.OffsetFromAnchor(Location, _tailSide, Scale(2));
        _exitStartOpacity = Opacity;
        _exitStarted = DateTime.UtcNow;
        _exitTimer.Start();
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
    }

    private void AdvanceExit()
    {
        var progress = Math.Clamp((DateTime.UtcNow - _exitStarted).TotalMilliseconds / 90, 0, 1);
        var eased = TaskbarPopoverMath.ExitEase(progress);
        var location = TaskbarPopoverMath.Interpolate(_exitStartLocation, _exitLocation, eased);
        Opacity = TaskbarPopoverMath.FadeOut(_exitStartOpacity, eased);
        if (!TaskbarPlacement.ShowAt(Handle, location, ClientSize)) Location = location;
        if (progress < 1) return;
        _exitTimer.Stop();
        Opacity = 1;
        Hide();
    }

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));

    private void SetContent(
        CodexUsageStrategy strategy,
        NativeText text,
        QuotaBackgroundTheme theme,
        float scale)
    {
        _text = text;
        _scale = scale;
        Text = TitleFor(text);
        BackColor = theme.Popover;
        ForeColor = Color.FromArgb(241, 245, 249);
        _rows.Clear();

        Heading(TitleFor(text), _headingFont, extraHeight: 5);
        Heading(T($"未来 24 小时有 {strategy.Urgent.Count} 项已知截止。按任务所需额度选择账号。",
            $"{strategy.Urgent.Count} known deadlines in 24h. Choose accounts that fit each task."));
        _rows.Add(([T("账号", "Account"), T("余量 / 复查", "Quota / review"),
            T("事项", "Action"), T("截止 / 恢复", "Deadline / recovery")], _bodyFont, LogicalRowHeight));
        var ready = strategy.Accounts.Where(row => row.State == CodexStrategyState.Ready).ToArray();
        ReadyRows(T("现在可用", "Ready now"), ready.Where(row => !row.HasLittleQuota));
        ReadyRows(T("余量较少 · 先核对任务", "Low quota · Check task fit"), ready.Where(row => row.HasLittleQuota));
        Rows(T("等待恢复或刷新", "Waiting or needs refresh"),
            strategy.Accounts.Where(row => row.State != CodexStrategyState.Ready),
            row => row.State switch
            {
                CodexStrategyState.Waiting => T("等待额度恢复", "Waiting for quota"),
                CodexStrategyState.Expired => T("订阅有效期结束", "Subscription ended"),
                _ => T("额度待刷新", "Refresh quota"),
            },
            row => Time(row.Recovery));
        Rows(T("重置卡", "Reset credits"),
            strategy.Accounts.Where(row => row.State != CodexStrategyState.Expired && (row.CardExpiry is not null || row.Cards > 0))
                .OrderBy(row => row.RedeemBy ?? DateTimeOffset.MaxValue),
            row => row.CardExpiry is null ? T("到期待刷新", "Expiry unavailable")
                : row.ResetBeforeCard ? T("自然重置后复查", "Check after scheduled reset")
                : row.CanRedeem ? T("可兑卡 · 手动确认", "Eligible · Confirm manually")
                : T("核对兑换条件", "Check eligibility"),
            row => row.RedeemBy != row.CardExpiry
                ? $"{Time(row.RedeemBy)} {T("订阅结束", "plan ends")}"
                : Time(row.CardExpiry),
            showCredits: true);
        if (strategy.Accounts.Count == 0) Heading(T("暂无 Cockpit Tools 账号额度。", "No Cockpit Tools account quotas."));

        AccessibleDescription = string.Join(Environment.NewLine, _rows.Select(row => string.Join(" · ", row.Values)));
        Invalidate();
    }

    private void ReadyRows(string title, IEnumerable<CodexStrategyAccount> accounts) =>
        Rows(title, accounts,
            row => row.DeadlineKind is { } kind ? DeadlineText(kind, _text) : T("可分配任务", "Available"),
            row => Time(row.Deadline));

    private void Rows(
        string title,
        IEnumerable<CodexStrategyAccount> accounts,
        Func<CodexStrategyAccount, string> action,
        Func<CodexStrategyAccount, string> time,
        bool showCredits = false)
    {
        var rows = accounts.ToArray();
        if (rows.Length == 0) return;
        Heading(title, _strongFont, extraHeight: 4);
        foreach (var row in rows)
        {
            var quota = showCredits
                ? row.ReviewBy is { } review ? T($"{Time(review)} 前复查", $"Check by {Time(review)}")
                    : row.CardExpiry is not null ? T("现在复查", "Check now")
                    : row.Cards is { } count ? T($"{count} 张", $"{count} credits") : "—"
                : Quotas(row.Windows);
            _rows.Add(([CodexAccountFormatting.MaskEmail(row.Account.Email), quota, action(row), time(row)],
                _strongFont, LogicalRowHeight));
        }
    }

    private void Heading(string value, Font? font = null, int extraHeight = 0) =>
        _rows.Add(([value], font ?? _bodyFont, LogicalRowHeight + extraHeight));

    private static string Quotas(IReadOnlyList<QuotaWindow> windows) =>
        windows.Count == 0 ? "—" : string.Join(" · ", windows.Select(window =>
            $"{QuotaDisplayFormatting.FormatWindowShort(window)} {Remaining(window.UsedPercent)}"));

    private static string Remaining(double? used) => used switch
    {
        null => "—",
        > 99 and < 100 => "<1%",
        _ => $"{Math.Floor(100 - used.Value):0}%",
    };

    private static string Time(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";

    private string T(string zh, string en) => _text.Locale == "zh-CN" ? zh : en;

    private static string DeadlineText(CodexStrategyDeadline kind, NativeText text) => kind switch
    {
        CodexStrategyDeadline.Reset => text.Locale == "zh-CN" ? "自然重置" : "Scheduled reset",
        CodexStrategyDeadline.Subscription => text.Locale == "zh-CN" ? "订阅有效期结束" : "Subscription ends",
        _ => text.Locale == "zh-CN" ? "兑卡截止" : "Redeem by",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _entranceTimer.Dispose();
            _exitTimer.Dispose();
            _headingFont.Dispose();
            _bodyFont.Dispose();
            _strongFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
