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
    private readonly VScrollBar _scrollbar = new() { Visible = false, TabStop = false };
    private readonly ToolTip _detailsTip = new() { InitialDelay = 450, ReshowDelay = 150, AutoPopDelay = 12000, ShowAlways = true };
    private string? _hoverDetails;
    private readonly Font _headingFont = new("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _bodyFont = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _strongFont = new("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _nameFont = new("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _rankFont = new("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly List<DisplayRow> _rows = [];
    private QuotaBackgroundTheme _theme = QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette);
    private static readonly Color Accent = Color.FromArgb(147, 197, 253);
    private static readonly Color Muted = Color.FromArgb(161, 174, 193);
    private static readonly Color DeadlineColor = Color.FromArgb(251, 191, 108);
    private static readonly Color CriticalColor = Color.FromArgb(251, 113, 133);
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
    internal string VisibleTextForAcceptance => string.Join(Environment.NewLine, _rows.Select(RowText));

    internal CodexStrategyPopoverForm()
    {
        _entranceTimer.Tick += (_, _) => AdvanceEntrance();
        _exitTimer.Tick += (_, _) => AdvanceExit();
        _scrollbar.ValueChanged += (_, _) => Invalidate();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Text = TitleFor(_text);
        Controls.Add(_scrollbar);
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
        UpdateScrollBar();
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
            this.ShowPopover(BarForm.PopoverOwnerFor(owner));
            TaskbarPlacement.ShowAt(Handle, _entranceLocation, placement.WindowSize);
            _entranceTimer.Start();
            return;
        }
        Opacity = 1;
        if (!wasVisible) this.ShowPopover(BarForm.PopoverOwnerFor(owner));
        if (!TaskbarPlacement.ShowAt(Handle, _restingLocation, placement.WindowSize))
            Bounds = new Rectangle(_restingLocation, placement.WindowSize);
        UpdateRowTip(PointToClient(Cursor.Position));
    }

    internal Bitmap RenderForTest(CodexUsageStrategy strategy, NativeText text, QuotaBackgroundTheme theme,
        int dpi = 96, PopoverTailSide tailSide = PopoverTailSide.Bottom)
    {
        SetContent(strategy, text, theme, Math.Max(1, dpi / 96f));
        _tailSide = tailSide;
        _tailOffset = VerticalTail ? LogicalWidth / 2f : ContentHeight / 2f;
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

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollbar.Value = Math.Clamp(_scrollbar.Value - e.Delta * Scale(LogicalRowHeight) / 40,
            0, Math.Max(0, _scrollbar.Maximum - _scrollbar.LargeChange + 1));
        Invalidate();
        if (IsOpen) UpdateRowTip(PointToClient(Cursor.Position));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateRowTip(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateRowTip(null);
    }

    private void UpdateRowTip(Point? point)
    {
        var details = point is { } position ? DetailAt(position) : null;
        if (details == _hoverDetails) return;
        _hoverDetails = details;
        _detailsTip.SetToolTip(this, details);
    }

    internal string? DetailAt(Point point)
    {
        var body = BodyBounds();
        var logical = new PointF(point.X / _scale, point.Y / _scale);
        if (!body.Contains(logical) || logical.X > body.Right - 20) return null;
        var contentY = logical.Y - body.Top + _scrollbar.Value / _scale;
        var top = 8;
        foreach (var row in _rows)
        {
            if (contentY >= top && contentY < top + row.Height)
                return row.Details is { } details ? $"{RowTitle(row)}{Environment.NewLine}{details}" : null;
            top += row.Height;
        }
        return null;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollBar();
        UpdateWindowRegion();
    }

    private void UpdateScrollBar()
    {
        var body = BodyBounds();
        var viewportHeight = Math.Max(1, Scale((int)body.Height) - Scale(14));
        var contentHeight = Scale(ContentHeight - 14);
        _scrollbar.Maximum = Math.Max(0, contentHeight - 1);
        _scrollbar.LargeChange = viewportHeight;
        _scrollbar.SmallChange = Scale(LogicalRowHeight);
        _scrollbar.Value = Math.Clamp(_scrollbar.Value, 0, Math.Max(0, contentHeight - viewportHeight));
        _scrollbar.Bounds = new Rectangle(
            Scale((int)body.Right) - Scale(20), Scale((int)body.Top) + Scale(8),
            Scale(12), Math.Max(1, Scale((int)body.Height) - Scale(16)));
        _scrollbar.Visible = contentHeight > viewportHeight;
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
        graphics.TranslateTransform(body.Left, body.Top - _scrollbar.Value / _scale);
        var contentWidth = Math.Max(1, body.Width - 32 - (_scrollbar.Maximum >= _scrollbar.LargeChange ? 16 : 0));
        DrawAccountSurfaces(graphics, contentWidth);
        var y = 8f;
        foreach (var row in _rows)
        {
            if (y > _scrollbar.Value / _scale + body.Height) break;
            if (y + row.Height >= _scrollbar.Value / _scale)
                DrawRow(graphics, row, new RectangleF(16, y, contentWidth, row.Height));
            y += row.Height;
        }
        graphics.Restore(state);
    }

    private void DrawAccountSurfaces(Graphics graphics, float width)
    {
        var y = 8f;
        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            if (row.Kind == RowKind.Recommendation)
            {
                var height = row.Height;
                for (var next = index + 1; next < _rows.Count && _rows[next].Kind != RowKind.Gap; next++)
                    height += _rows[next].Height;
                var bounds = new RectangleF(16, y + 1, width, height - 2);
                var first = row.Priority == 1;
                using var fill = new SolidBrush(first ? _theme.QuotaGroup : Color.FromArgb(100, _theme.QuotaGroup));
                graphics.FillRoundedRectangle(fill, bounds, 10);
                if (first)
                {
                    using var tint = new SolidBrush(Color.FromArgb(14, Accent));
                    graphics.FillRoundedRectangle(tint, bounds, 10);
                }
                using var border = new Pen(Color.FromArgb(first ? 72 : 24, first ? Accent : Muted), 1);
                using var path = RoundedRectangle(bounds);
                graphics.DrawPath(border, path);
            }
            y += row.Height;
        }
    }

    private void DrawRow(Graphics graphics, DisplayRow row, RectangleF bounds)
    {
        if (row.Kind == RowKind.Gap) return;
        var values = row.Values;
        if (row.Kind == RowKind.Title)
        {
            DrawText(graphics, values[0], _headingFont, ForeColor,
                new RectangleF(bounds.X, bounds.Y, bounds.Width * .7f, bounds.Height));
            DrawText(graphics, values[1], _bodyFont, row.Urgent ? DeadlineColor : Muted,
                new RectangleF(bounds.X + bounds.Width * .7f, bounds.Y, bounds.Width * .3f, bounds.Height), StringAlignment.Far);
            return;
        }
        if (row.Kind == RowKind.Note)
        {
            DrawText(graphics, values[0], _bodyFont, Muted, bounds);
            return;
        }
        if (row.Kind == RowKind.Section)
        {
            using var line = new Pen(Color.FromArgb(52, 148, 163, 184));
            graphics.DrawLine(line, bounds.Left, bounds.Top + 3, bounds.Right, bounds.Top + 3);
            DrawText(graphics, values[0], _strongFont, ForeColor,
                new RectangleF(bounds.X, bounds.Y + 6, bounds.Width * .6f, bounds.Height - 6));
            if (values.Length > 1)
                DrawText(graphics, values[1], _bodyFont, Muted,
                    new RectangleF(bounds.X + bounds.Width * .6f, bounds.Y + 6, bounds.Width * .4f, bounds.Height - 6),
                    StringAlignment.Far);
            return;
        }
        if (row.Kind == RowKind.Event)
        {
            var left = bounds.Left + 50;
            var availableWidth = bounds.Right - 8 - left;
            var split = left + availableWidth * .60f;
            DrawText(graphics, values[0], _bodyFont, Muted,
                new RectangleF(left, bounds.Top, split - left - 5, bounds.Height));
            DrawText(graphics, values[3], _strongFont, row.Critical ? CriticalColor : row.Urgent ? DeadlineColor : Muted,
                new RectangleF(split, bounds.Top, bounds.Right - 8 - split, bounds.Height), StringAlignment.Far);
            return;
        }
        if (row.Kind == RowKind.Progress)
        {
            var left = bounds.Left + 50;
            var availableWidth = bounds.Right - 8 - left;
            var labelWidth = Math.Min(74, availableWidth * .24f);
            var trackWidth = Math.Min(96, availableWidth * .22f);
            var track = new RectangleF(left + labelWidth + 6, bounds.Top + bounds.Height / 2 - 2, trackWidth, 4);
            DrawText(graphics, values[0], _bodyFont, Muted,
                new RectangleF(left, bounds.Top, labelWidth, bounds.Height));
            using var trackBrush = new SolidBrush(_theme.QuotaGroup);
            graphics.FillRoundedRectangle(trackBrush, track, 2);
            if (row.RemainingPercent is { } remaining)
            {
                using var quotaBrush = new SolidBrush(QuotaColorScale.ForRemaining(remaining));
                graphics.FillRoundedRectangle(quotaBrush, new RectangleF(track.X, track.Y,
                    track.Width * (float)(remaining / 100), track.Height), 2);
            }
            var valueLeft = track.Right + 6;
            DrawText(graphics, values[1], _strongFont, ForeColor,
                new RectangleF(valueLeft, bounds.Top, 36, bounds.Height));
            var forecastLeft = valueLeft + 44;
            DrawText(graphics, values[3], _bodyFont, row.Predicted ? Accent : Muted,
                new RectangleF(forecastLeft, bounds.Top, Math.Max(1, bounds.Right - 8 - forecastLeft), bounds.Height),
                StringAlignment.Far);
            return;
        }
        if (row.Kind == RowKind.Compact)
        {
            DrawAccountName(graphics, row, _bodyFont, Muted,
                new RectangleF(bounds.Left + 8, bounds.Top, bounds.Width * .6f - 8, bounds.Height));
            DrawText(graphics, values[3], _strongFont, row.Critical ? CriticalColor : Muted,
                new RectangleF(bounds.Left + bounds.Width * .6f, bounds.Top, bounds.Width * .4f - 8, bounds.Height), StringAlignment.Far);
            return;
        }

        if (row.Kind == RowKind.Renewal)
        {
            using var surface = new SolidBrush(_theme.QuotaGroup);
            graphics.FillRoundedRectangle(surface, RectangleF.Inflate(bounds, 0, -3), 8);
            var left = bounds.Left + 14;
            var right = bounds.Right - 14;
            var split = left + (right - left) * .53f;
            DrawText(graphics, values[0], _nameFont, ForeColor,
                new RectangleF(left, bounds.Top + 8, split - left - 8, 24));
            DrawText(graphics, values[1], _strongFont, Muted,
                new RectangleF(split, bounds.Top + 8, right - split, 24), StringAlignment.Far);
            DrawText(graphics, values[2], _bodyFont, Muted,
                new RectangleF(left, bounds.Top + 37, split - left - 8, 24));
            DrawText(graphics, values[3], _strongFont, Accent,
                new RectangleF(split, bounds.Top + 37, right - split, 24), StringAlignment.Far);
            return;
        }

        var nameX = bounds.Left + 8;
        var contentRight = bounds.Right - 8;
        if (row.Kind == RowKind.Recommendation)
        {
            var first = row.Priority == 1;
            var rankBounds = new RectangleF(bounds.Left + 10, bounds.Top + 13, 28, 28);
            using var rankFill = new SolidBrush(Color.FromArgb(first ? 45 : 20, first ? Accent : Muted));
            graphics.FillRoundedRectangle(rankFill, rankBounds, 7);
            DrawText(graphics, row.Priority!.Value.ToString(CultureInfo.InvariantCulture), _rankFont,
                first && !row.LowQuota ? Accent : Muted, rankBounds, StringAlignment.Center);
            nameX = bounds.Left + 50;
        }
        else
        {
            using var line = new Pen(Color.FromArgb(28, 148, 163, 184));
            graphics.DrawLine(line, bounds.Left + 8, bounds.Bottom - 1, bounds.Right - 8, bounds.Bottom - 1);
        }
        var detailY = bounds.Top + (row.Kind == RowKind.Recommendation ? 28 : 23);
        var nameWidth = Math.Max(1, (contentRight - nameX) * .60f);
        var rightX = nameX + nameWidth + 8;
        var rightWidth = Math.Max(1, contentRight - rightX);
        DrawAccountName(graphics, row, row.Kind == RowKind.Recommendation ? _nameFont : _strongFont,
            row.LowQuota ? Muted : ForeColor, new RectangleF(nameX, bounds.Top + 3, nameWidth, 24));
        var primaryRight = row.Kind == RowKind.Recommendation ? values[3] : values[1];
        var primaryColor = row.Kind == RowKind.Recommendation
            ? row.Critical ? CriticalColor : row.Urgent ? DeadlineColor : Muted
            : row.LowQuota ? Muted : ForeColor;
        DrawText(graphics, primaryRight, _strongFont, primaryColor,
            new RectangleF(rightX, bounds.Top + 3, rightWidth, 24), StringAlignment.Far);
        DrawText(graphics, values[2], _bodyFont,
            row.Kind == RowKind.Recommendation && row.Priority == 1 ? Accent : Muted,
            new RectangleF(nameX, detailY, row.Kind == RowKind.Recommendation ? contentRight - nameX : nameWidth, 20));
        if (row.Kind == RowKind.Recommendation)
            DrawText(graphics, values[1], _bodyFont, Muted,
                new RectangleF(nameX, detailY + 21, contentRight - nameX, 20));
        if (row.Kind != RowKind.Recommendation)
            DrawText(graphics, values[3], _strongFont, row.Critical ? CriticalColor : row.Urgent ? DeadlineColor : Muted,
                new RectangleF(rightX, detailY, rightWidth, 20), StringAlignment.Far);
    }

    private void DrawAccountName(Graphics graphics, DisplayRow row, Font font, Color color, RectangleF bounds)
    {
        if (row.Status is not { } status)
        {
            DrawText(graphics, row.Values[0], font, color, bounds);
            return;
        }
        var badgeWidth = Math.Min(bounds.Width, graphics.MeasureString(status, _bodyFont).Width + 12);
        var nameWidth = Math.Max(1, bounds.Width - badgeWidth - 6);
        var name = new RectangleF(bounds.Left, bounds.Top, nameWidth, bounds.Height);
        DrawText(graphics, row.Values[0], font, color, name);
        var badgeLeft = bounds.Left + Math.Min(graphics.MeasureString(row.Values[0], font).Width + 6, nameWidth + 6);
        var badge = new RectangleF(badgeLeft, bounds.Top + (bounds.Height - 20) / 2, badgeWidth, 20);
        using var fill = new SolidBrush(Color.FromArgb(20, Accent));
        graphics.FillRoundedRectangle(fill, badge, 5);
        DrawText(graphics, status, _bodyFont, Accent, badge, StringAlignment.Center);
    }

    private static void DrawText(Graphics graphics, string value, Font font, Color color,
        RectangleF bounds, StringAlignment alignment = StringAlignment.Near)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        graphics.DrawString(value, font, brush, bounds, format);
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

    private static GraphicsPath RoundedRectangle(RectangleF bounds) => UiPaint.RoundedPath(bounds, UiTokens.RadiusPanel);

    internal void HidePopover()
    {
        UpdateRowTip(null);
        _detailsTip.Hide(this);
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
        _theme = theme;
        ForeColor = Color.FromArgb(241, 245, 249);
        _rows.Clear();
        var now = strategy.EvaluatedAt;

        var due = strategy.Urgent.Count > 0 ? T($"24 小时内 {strategy.Urgent.Count} 项临期", $"{strategy.Urgent.Count} due within 24h") : "";
        _rows.Add(new([TitleFor(text), due], RowKind.Title, 36, Urgent: strategy.Urgent.Count > 0));
        var immediateCards = strategy.Accounts.SelectMany(account => account.UpcomingEvents
            .Where(item => item.Kind == CodexStrategyDeadline.ResetCredit && item.At - now <= TimeSpan.FromMinutes(30))
            .Select(item => (Account: account, Event: item))).OrderBy(item => item.Event.At).ToArray();
        if (immediateCards.Length > 0) Section(T("先处理", "Act now"));
        foreach (var (account, item) in immediateCards) AddCreditRow(account, item, now, immediate: true);
        var urgentAccounts = strategy.Urgent.Select(item => item.Account.Account.AccountId).ToHashSet(StringComparer.Ordinal);
        var ready = strategy.Accounts.Where(row => row.State == CodexStrategyState.Ready).ToArray();
        var showPlans = ready.Select(row => CodexAccountFormatting.PlanLabel(row.Account.Plan)).Distinct().Count() > 1;
        if (ready.Length > 0) Section(T("优先顺序", "Use in this order"), T("截止 · 余量 · 重置卡", "Deadline · Quota · Credits"));
        foreach (var account in ready)
        {
            var urgent = urgentAccounts.Contains(account.Account.AccountId);
            var action = account.Priority == 1 ? T("优先使用", "Use first") : T("随后使用", "Use next");
            if (account.HasLittleQuota) action += T(" · 适合短任务", " · Short tasks");
            var primary = PrimaryEvent(account);
            var deadline = primary?.At ?? account.PriorityDeadline;
            var promoted = primary?.Kind == CodexStrategyDeadline.ResetCredit && primary.At - now <= TimeSpan.FromMinutes(30);
            var context = primary?.Kind switch
            {
                CodexStrategyDeadline.Subscription => T("订阅", "Subscription"),
                CodexStrategyDeadline.ResetCredit => CreditCount(primary),
                CodexStrategyDeadline.Reset when account.Windows.Count > 1 => WindowNames(primary.WindowLabel),
                _ => "",
            };
            var explanation = T("缺少可靠速度时，按余量、已知整轮重置卡和截止比较压力",
                "Without reliable pace, compare remaining quota, confirmed full resets and deadlines");
            if (!string.IsNullOrEmpty(context)) action += $" · {context}";
            if (account.SuggestedStartAt is { } start)
            {
                action += start <= now ? T(" · 建议现在开始（估算）", " · Start now (estimate)")
                    : T($" · 建议 {CountdownDuration(start - now, text)}内开始", $" · Start within {CountdownDuration(start - now, text)}");
                explanation = start <= now ? T("按近期节奏，建议现在使用", "Observed pace suggests starting now")
                    : T($"按近期节奏，建议 {Time(start)} 前开始", $"Observed pace suggests starting by {Time(start)}");
            }
            if (promoted) action = T("先核对上方重置卡", "Review the reset credit above");
            var plan = CodexAccountFormatting.PlanLabel(account.Account.Plan).ToUpperInvariant();
            var name = CodexAccountFormatting.MaskEmail(account.Account.Email) + (showPlans ? $" · {plan}" : "");
            var reason = PriorityExplanation(account);
            var details = $"{plan}\n{(primary is null ? T("暂无已知截止", "No known deadline") : EventDetails(account, primary, now))}\n{reason}\n{explanation}";
            if (account.PressureEvent is { } driver && account.ConsumptionHours is { } hours)
                details += "\n" + T($"按近期速度需约 {hours:0.#} 小时，截止前剩 {(driver.At - now).TotalHours:0.#} 小时（估算）",
                    $"About {hours:0.#}h of usage needed, {(driver.At - now).TotalHours:0.#}h until the deadline (estimate)");
            _rows.Add(new([name, reason, action,
                promoted ? "" : Countdown(deadline, now, primary?.Kind ?? account.PriorityReason ?? CodexStrategyDeadline.Reset, text)],
                RowKind.Recommendation, 74, account.Priority, urgent, account.HasLittleQuota,
                Critical: !promoted && IsCritical(deadline, now), Details: details,
                Status: account.Account.Active ? T("在用", "Active") : null));
            AddProgress(account, now);
            AddEvents(account, now, displayed: primary);
            _rows.Add(new([""], RowKind.Gap, 8));
        }
        if (strategy.Accounts.Count == 0 && strategy.Renewals.Count == 0)
            _rows.Add(new([T("暂无付费账号额度", "No paid-account quota available")], RowKind.Note, 30));
        var renewalIds = strategy.Renewals.Select(advice => advice.Account.AccountId).ToHashSet(StringComparer.Ordinal);
        var waiting = strategy.Accounts.Where(row => row.State != CodexStrategyState.Ready
            && !renewalIds.Contains(row.Account.AccountId)).ToArray();
        if (waiting.Length > 0) Section(T("暂不可用", "Unavailable for now"));
        foreach (var account in waiting)
        {
            var action = account.State switch
            {
                CodexStrategyState.Waiting when account.CanRedeem => T("可核对兑卡 · 手动确认", "Review redemption · Confirm manually"),
                CodexStrategyState.Waiting => T("等待额度恢复", "Waiting for quota"),
                CodexStrategyState.Expired => T("订阅有效期结束", "Subscription ended"),
                _ => T("额度待刷新", "Refresh quota"),
            };
            var countdown = account.CanRedeem ? action
                : account.Recovery is not null ? Countdown(account.Recovery, now, CodexStrategyDeadline.Reset, text) : action;
            var name = CodexAccountFormatting.MaskEmail(account.Account.Email);
            _rows.Add(new([name, "", "", countdown], RowKind.Compact, 30,
                Details: $"{Quotas(account.Windows)}\n{action}{(account.Recovery is null ? "" : $" · {Time(account.Recovery)}")}",
                Status: account.Account.Active ? T("在用", "Active") : null));
            AddEvents(account, now, skipRecovery: true);
        }
        if (strategy.Renewals.Count > 0)
        {
            Section(T("续开时机", "When to renew"));
            foreach (var advice in strategy.Renewals)
            {
                var future = advice.RecordedResetAt > now;
                var history = T($"历史最低剩余 {100 - advice.HighestUsedPercent:0.#}%",
                    $"Recorded low: {100 - advice.HighestUsedPercent:0.#}% left");
                var details = T($"最后记录 {Time(advice.LastObservedAt)}\n续开后的额度与周期，以刷新结果为准",
                    $"Last seen {Time(advice.LastObservedAt)}\nConfirm allowance and cycle after renewing");
                _rows.Add(new([CodexAccountFormatting.MaskEmail(advice.Account.Email), history,
                    T($"原定重置 {Time(advice.RecordedResetAt)}", $"Prior reset {Time(advice.RecordedResetAt)}"),
                    future ? T($"{CountdownDuration(advice.RecordedResetAt - now, text)}后复查",
                        $"Recheck in {CountdownDuration(advice.RecordedResetAt - now, text)}") : T("原定时间已到 · 待复查", "Prior reset due · Recheck")],
                    RowKind.Renewal, 74, Details: details));
            }
        }
        AccessibleDescription = string.Join(Environment.NewLine, _rows.Select(row =>
            RowText(row) + (row.Details is { } details ? $"\n{details}" : "")));
        Invalidate();
    }

    private void AddProgress(CodexStrategyAccount account, DateTimeOffset now)
    {
        foreach (var progress in account.Progress)
        {
            var window = progress.Window;
            var pace = progress.Pace?.ForDisplay(now);
            var (trend, projection) = _text.QuotaPace(pace, now, window.ResetsAt);
            var predicted = !string.IsNullOrEmpty(projection);
            var summary = predicted ? projection : pace?.Status switch
            {
                QuotaPaceStatus.NoMeaningfulConsumption => T("用量平稳", "Usage steady"),
                QuotaPaceStatus.WaitingForFreshData => T("预测待刷新", "Refresh forecast"),
                _ => T("预测待采样", "Collecting samples"),
            };
            if (predicted && pace!.ValidUntil < now) summary = T($"近期估计 · {summary}", $"Recent estimate · {summary}");
            else if (predicted && pace!.Recent?.Confidence == QuotaTrendConfidence.Provisional)
                summary = T($"初步 · {summary}", $"Early · {summary}");
            var label = T($"{WindowLabel(window)}余量", $"{WindowLabel(window)} left");
            var reset = window.ResetsAt is { } resetAt ? _text.ResetAt(resetAt, now) : T("重置时间待刷新", "Reset time unavailable");
            _rows.Add(new([label, Remaining(window.UsedPercent), "", summary], RowKind.Progress, 24,
                Details: $"{(pace is null ? T("趋势样本不足", "Insufficient trend samples") : trend)}\n{reset}",
                RemainingPercent: window.UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null,
                Predicted: predicted));
        }
    }

    private void AddEvents(CodexStrategyAccount account, DateTimeOffset now,
        CodexStrategyEvent? displayed = null, bool skipRecovery = false)
    {
        if (account.State != CodexStrategyState.Expired && account.Cards is { } count && count > 0)
        {
            var total = T($"共 {count} 张", $"{count} total");
            var applicable = account.State != CodexStrategyState.Refresh && account.ApplicableCards is { } available
                ? T($"当前可兑 {available} 张", $"{available} redeemable now") : T("兑换条件待核对", "Check eligibility");
            var unresolved = account.UnresolvedCards > 0
                ? T($"{account.UnresolvedCards} 张有效期待核对", $"Check expiry for {account.UnresolvedCards}") : "";
            _rows.Add(new([$"{T("重置卡", "Reset credits")} · {total} · {applicable}", "", "", unresolved], RowKind.Event, 28,
                Details: T("仅有总数的卡不推定有效期或恢复范围；在 Cockpit Tools 核对卡详情",
                    "A balance alone establishes neither expiry nor reset scope; check credit details in Cockpit Tools")));
        }
        var nextReset = account.Events.FirstOrDefault(item => item.Kind == CodexStrategyDeadline.Reset);
        foreach (var item in account.Events.Where(item => item.Kind == CodexStrategyDeadline.ResetCredit
                     || item.At - now <= TimeSpan.FromHours(24) || item == nextReset))
        {
            if (item == displayed) continue;
            if (skipRecovery && item.Recovery && item.At == account.Recovery) continue;
            if (item.Kind == CodexStrategyDeadline.ResetCredit && item.At - now <= TimeSpan.FromMinutes(30)) continue;
            _rows.Add(new([EventLabel(item), "", "", Countdown(item.At, now, item.Kind, _text)],
                RowKind.Event, 28, Urgent: !item.Recovery && item.At - now <= TimeSpan.FromHours(24),
                Critical: !item.Recovery && IsCritical(item.At, now),
                Details: EventDetails(account, item, now)));
        }
    }

    private void AddCreditRow(CodexStrategyAccount account, CodexStrategyEvent item, DateTimeOffset now, bool immediate = false)
    {
        var name = CodexAccountFormatting.MaskEmail(account.Account.Email);
        _rows.Add(immediate
            ? new([name, CreditCount(item), CreditAction(account, item), Countdown(item.At, now, item.Kind, _text)], RowKind.Detail, 48,
                Critical: true, Details: EventDetails(account, item, now))
            : new([$"{name} · {CreditCount(item)}", "", "", Countdown(item.At, now, item.Kind, _text)], RowKind.Compact, 30,
                Details: EventDetails(account, item, now)));
    }

    private string EventLabel(CodexStrategyEvent item) => item.Kind switch
    {
        CodexStrategyDeadline.Reset => T($"{WindowNames(item.WindowLabel)}额度", $"{item.WindowLabel} quota"),
        CodexStrategyDeadline.Subscription => T("订阅", "Subscription"),
        _ => item.CoversMainQuota ? CreditCount(item)
            : CreditCount(item) + T(" · 范围待核对", " · Check scope"),
    };

    private static CodexStrategyEvent? PrimaryEvent(CodexStrategyAccount account) =>
        account.State == CodexStrategyState.Ready
            ? account.PressureEvent ?? account.Events.FirstOrDefault(item => item.Kind == account.PriorityReason && item.At == account.PriorityDeadline)
            : null;

    private string CreditCount(CodexStrategyEvent item) => item.CardCount is { } count
        ? T($"{count} 张重置卡", $"{count} reset {(count == 1 ? "credit" : "credits")}")
        : T("重置卡（张数待刷新）", "Reset credits (count unavailable)");

    private string WindowNames(string? labels) => string.Join(" / ", (labels ?? "").Split(" / ").Select(WindowLabel));

    private string EventDetails(CodexStrategyAccount account, CodexStrategyEvent item, DateTimeOffset now)
    {
        var details = $"{DeadlineText(item.Kind, _text)} · {Time(item.At)}";
        if (item.Kind == CodexStrategyDeadline.ResetCredit)
            details += $"\n{CreditAction(account, item)}\n{ReviewText(item, now)}\n"
                + (item.CoversMainQuota ? T("恢复完整额度", "Resets the full allowance")
                    : T("恢复范围待核对，未计入完整额度预测", "Scope unconfirmed; excluded from full-refill forecasts"));
        return details;
    }

    private string PriorityExplanation(CodexStrategyAccount account)
    {
        var driver = account.PressureEvent ?? PrimaryEvent(account);
        var hasCredit = account.Events.Any(item => item.Kind == CodexStrategyDeadline.ResetCredit);
        var hasReset = account.UpcomingEvents.Any(item => item.Kind == CodexStrategyDeadline.Reset && !item.Recovery);
        if (account.PressureIncludesReset && account.PressureIncludesCredit
            && driver?.Kind != CodexStrategyDeadline.Subscription)
            return T("自然重置与卡到期叠加，需要预留消耗时间", "Reset and credit expiry overlap; allow time to use both");
        if (driver?.Kind == CodexStrategyDeadline.Subscription)
            return hasCredit ? T("订阅结束前使用余量，并处理重置卡", "Use quota and review credits before the plan ends")
                : T("订阅到期前使用剩余额度", "Use remaining quota before the subscription ends");
        if (driver?.Kind == CodexStrategyDeadline.ResetCredit)
            return account.Events.Where(item => item.At <= driver.At && item.CoversMainQuota)
                    .Sum(item => (long)(item.CardCount ?? 0)) > 1
                ? T("多张卡需逐次使用，预留消耗间隔", "Allow usage time between multiple credit resets")
                : T("卡失效前，先消耗余量再核对兑换", "Use quota and review redemption before credit expiry");
        if (hasReset && account.UpcomingEvents.Any(item => item.Kind == CodexStrategyDeadline.ResetCredit))
            return T("自然重置与重置卡均临期，优先消耗当前额度", "Quota reset and credit expiry are both approaching");
        if (driver?.Kind == CodexStrategyDeadline.Reset)
            return account.ConsumptionPressure > 1
                ? T("按近期速度，重置前预计用不完", "At the recent pace, quota may remain at reset")
                : T("自然重置将覆盖当前余量", "Scheduled reset replaces the remaining allowance");
        return T("截止时间或消耗速度待刷新", "Refresh deadlines or usage pace");
    }

    private string CreditAction(CodexStrategyAccount account, CodexStrategyEvent item)
    {
        if (account.ApplicableCards == 0) return T("当前不可兑 · 消耗后复查", "Not redeemable now · Recheck after usage");
        if (item.CardExpiresAt != item.At) return T("订阅结束前使用", "Use before the subscription ends");
        if (account.Events.Any(other => other.Kind == CodexStrategyDeadline.Reset && other.At < item.At.AddMinutes(-30)))
            return T("先消耗，结合自然重置核对兑卡时机", "Use quota; consider the scheduled reset before redeeming");
        return account.CanRedeem
                ? T("有可兑卡 · 核对本批条件", "Credits apply now · Check this batch")
                : T("核对兑换条件", "Check eligibility");
    }

    private string ReviewText(CodexStrategyEvent item, DateTimeOffset now)
    {
        var reviewAt = item.At.AddMinutes(-30);
        var review = reviewAt > now
            ? T($"建议 {Time(reviewAt)} 前复查", $"Review by {Time(reviewAt)}")
            : T("现在复查兑换条件", "Review eligibility now");
        return item.CardExpiresAt != item.At
            ? $"{review} · {T("卡原到期", "Credit expires")} {Time(item.CardExpiresAt)}"
            : review;
    }

    private void Section(string value, string? detail = null) =>
        _rows.Add(new(detail is null ? [value] : [value, detail], RowKind.Section, 28));

    private static string RowTitle(DisplayRow row) => row.Values[0] + (row.Status is { } status ? $" · {status}" : "");

    private static string RowText(DisplayRow row) => (row.Priority is { } priority ? $"{priority}. " : "")
        + string.Join(" · ", row.Values.Select((value, index) => index == 0 ? RowTitle(row) : value)
            .Where(value => !string.IsNullOrEmpty(value)));

    private enum RowKind { Title, Note, Section, Recommendation, Detail, Event, Compact, Progress, Renewal, Gap }
    private sealed record DisplayRow(string[] Values, RowKind Kind, int Height,
        int? Priority = null, bool Urgent = false, bool LowQuota = false, bool Critical = false, string? Details = null,
        double? RemainingPercent = null, bool Predicted = false, string? Status = null);

    internal static string Countdown(DateTimeOffset? deadline, DateTimeOffset now,
        CodexStrategyDeadline kind, NativeText text)
    {
        var zh = text.IsChinese;
        if (deadline is null) return zh ? "时间待刷新" : "Time unavailable";
        if (deadline <= now)
        {
            if (kind == CodexStrategyDeadline.Reset)
                return zh ? "已到点，等待刷新" : "Due now · Refresh pending";
            return zh ? "已到期" : "Expired";
        }
        var duration = CountdownDuration(deadline.Value - now, text);
        return kind switch
        {
            CodexStrategyDeadline.Reset => zh ? $"{duration}后重置" : $"Resets in {duration}",
            CodexStrategyDeadline.Subscription => zh ? $"{duration}后到期" : $"Expires in {duration}",
            _ => zh ? $"{duration}内兑卡" : $"Redeem within {duration}",
        };
    }

    private static string CountdownDuration(TimeSpan remaining, NativeText text)
    {
        var seconds = Math.Max(1, (long)Math.Ceiling(remaining.TotalSeconds));
        var days = seconds / 86_400;
        var hours = seconds / 3_600;
        var minutes = seconds % 3_600 / 60;
        var zh = text.IsChinese;
        if (days > 0)
        {
            var dayText = zh ? $"{days}天" : $"{days}d";
            return hours % 24 == 0 ? dayText
                : dayText + (zh ? $"{hours % 24}小时" : $" {hours % 24}h");
        }
        if (hours > 0)
        {
            var hourText = zh ? $"{hours}小时" : $"{hours}h";
            return minutes == 0 ? hourText : hourText + (zh ? $"{minutes}分" : $" {minutes}m");
        }
        if (minutes > 0)
            return zh ? $"{minutes}分{seconds % 60}秒" : $"{minutes}m {seconds % 60}s";
        return zh ? $"{seconds}秒" : $"{seconds}s";
    }

    private static bool IsCritical(DateTimeOffset? deadline, DateTimeOffset now) =>
        deadline is { } target && target - now <= TimeSpan.FromHours(1);

    private string Quotas(IReadOnlyList<QuotaWindow> windows) =>
        windows.Count == 0 ? "—" : string.Join(" · ", windows.Select(window =>
            $"{WindowLabel(window)} {Remaining(window.UsedPercent)}"));

    private string WindowLabel(QuotaWindow window) => WindowLabel(QuotaDisplayFormatting.FormatWindowShort(window));

    private string WindowLabel(string label) => label switch
    {
        "7d" => T("周", "7d"),
        "5h" => T("5小时", "5h"),
        "30d" => T("30天", "30d"),
        _ => label,
    };

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
            _nameFont.Dispose();
            _rankFont.Dispose();
            _detailsTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
