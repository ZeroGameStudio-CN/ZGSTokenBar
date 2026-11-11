using System.Drawing.Drawing2D;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed record QuotaPopoverContent(
    QuotaCard Card,
    QuotaWindow Window,
    DateTimeOffset? WeeklyBlockResetAt,
    DateTimeOffset CapturedAt,
    QuotaPaceEstimate? Pace,
    bool Pinned,
    AiGatewayUsageSummary? AiGatewayUsage = null,
    CodexQuotaTokenSummary? CodexQuotaTokens = null);

internal sealed class QuotaPopoverForm : Form
{
    internal const int LogicalBodyWidth = 240;
    internal const int LogicalBodyHeight = 144;
    internal const int LogicalQuotaBodyHeight = 124;
    internal const int LogicalCodexTokenBodyHeight = 156;
    /// <summary>One card layout for every provider's quota ring (Codex, Claude, Grok, …).</summary>
    internal const int LogicalUnifiedQuotaBodyHeight = 184;
    internal const int LogicalSub2ApiQuotaBodyHeight = 164;
    internal const int LogicalSub2ApiAccountAvailabilityBodyHeight = 164;
    private const int AccountAvailabilityFirstRowY = 76;
    private const int AccountAvailabilityRowHeight = 18;
    private const int LogicalTailSize = 8;
    private const int LogicalGap = 3;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MouseActivateNoActivate = 3;
    private const int ExitDurationMs = 90;

    private readonly Font _titleFont = new("Segoe UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _subtitleFont = new("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _statusFont = new("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _valueFont = new("Segoe UI", 16.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _metricFont = new("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _detailFont = new("Segoe UI Semibold", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _cardTitleFont = new("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _cardChipFont = new("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _cardHeroFont = new("Segoe UI", 25f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _cardRowFont = new("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _cardRowStrongFont = new("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _cardSmallFont = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly AlignedStringFormats _textFormats = new();
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 30_000 };
    private readonly System.Windows.Forms.Timer _entranceTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _exitTimer = new() { Interval = 16 };
    private QuotaPopoverContent? _content;
    private NativeText _text = NativeText.For("zh-CN");
    private Image? _providerLogo;
    private Image? _resetClockIcon;
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
    private DateTimeOffset? _renderNow;

    private (string Label, string Value, string Detail)? QuotaTokenUsage =>
        _content is { Card: { Provider: ProviderKind.Codex, IsService: false } } content
            ? _text.CodexQuotaTokenUsage(content.CodexQuotaTokens)
            : null;

    private int ContentBodyHeight
    {
        get
        {
            if (_content is not { } content) return LogicalQuotaBodyHeight;
            if (!content.Card.IsService) return LogicalUnifiedQuotaBodyHeight;
            if (Sub2ApiServicePresentation.IsSub2ApiService(content.Card))
            {
                var presentation = Sub2ApiServicePresentation.Resolve(
                    content.Card,
                    _renderNow ?? content.CapturedAt);
                return presentation.Kind switch
                {
                    Sub2ApiServicePresentationKind.CompleteAvailability
                        when presentation.Availability is { Accounts: { } accounts } =>
                        AccountAvailabilityBodyHeight(accounts.Count, includeProgressRail: true),
                    Sub2ApiServicePresentationKind.PartialAvailability
                        or Sub2ApiServicePresentationKind.KnownNoneAvailability
                        when presentation.Availability is { Accounts: { } accounts } =>
                        AccountAvailabilityBodyHeight(accounts.Count),
                    Sub2ApiServicePresentationKind.LegacyAggregateQuota => LogicalSub2ApiQuotaBodyHeight,
                    _ => LogicalBodyHeight,
                };
            }

            return content.Card.Sub2ApiAccountAvailability is { } availability
                && Sub2ApiAccountAvailabilityFormatting.IsRenderable(availability)
                    ? AccountAvailabilityBodyHeight(availability.Accounts!.Count)
                    : content.Card.Sub2ApiQuota is { } quota
                    && Sub2ApiQuotaFormatting.PreferredWindow(quota) is not null
                        ? LogicalSub2ApiQuotaBodyHeight
                        : LogicalBodyHeight;
        }
    }

    internal static int AccountAvailabilityBodyHeight(
        int accountCount,
        bool includeProgressRail = false)
    {
        var count = Math.Clamp(accountCount, 1, 64);
        var columns = AccountAvailabilityColumnCount(count);
        var rows = (count + columns - 1) / columns;
        var progressRailHeight = includeProgressRail ? 14 : 0;
        return Math.Max(
            LogicalSub2ApiAccountAvailabilityBodyHeight,
            128 + rows * AccountAvailabilityRowHeight + progressRailHeight);
    }

    internal static int AccountAvailabilityColumnCount(int accountCount) => accountCount switch
    {
        <= 2 => 1,
        <= 8 => 2,
        <= 18 => 3,
        _ => 4,
    };
    private QuotaBackgroundTheme _backgroundTheme = QuotaBackgroundPalette.Resolve(
        AppSettings.DefaultBackgroundPalette);

    public QuotaPopoverForm()
    {
        _clockTimer.Tick += (_, _) => Invalidate();
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
        Text = _text.QuotaDetailsTitle;
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
        BackColor = CardSurface;
        Invalidate();
    }

    public void ShowFor(
        BarForm owner,
        Rectangle anchorScreen,
        QuotaPopoverContent content,
        NativeText text,
        Image providerLogo,
        Image resetClockIcon,
        float scale,
        bool animateEntrance)
    {
        var wasVisible = Visible;
        _exitTimer.Stop();
        _animateMotion = animateEntrance;
        _content = content;
        _text = text;
        Text = _text.QuotaDetailsTitle;
        _providerLogo = providerLogo;
        _resetClockIcon = resetClockIcon;
        _scale = Math.Max(1, scale);

        var bodySize = new Size(Scale(LogicalBodyWidth), Scale(ContentBodyHeight));
        var tailSize = Scale(LogicalTailSize);
        var gap = Scale(LogicalGap);
        var workingArea = Screen.FromRectangle(anchorScreen).WorkingArea;
        var placement = TaskbarMiniPopoverMath.Place(anchorScreen, bodySize, tailSize, gap, workingArea);
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
            Show(BarForm.PopoverOwnerFor(owner));
            if (!TaskbarPlacement.ShowAt(Handle, _entranceLocation, placement.WindowSize)) Location = _entranceLocation;
            _entranceTimer.Start();
        }
        else
        {
            Opacity = 1;
            if (!wasVisible) Show(BarForm.PopoverOwnerFor(owner));
            if (!TaskbarPlacement.ShowAt(Handle, _restingLocation, placement.WindowSize)) Location = _restingLocation;
        }
        _clockTimer.Start();
    }

    internal Bitmap RenderForTest(
        QuotaPopoverContent content,
        NativeText text,
        Image providerLogo,
        Image resetClockIcon,
        int dpi,
        DateTimeOffset now)
    {
        _content = content;
        _text = text;
        _providerLogo = providerLogo;
        _resetClockIcon = resetClockIcon;
        _scale = Math.Max(1, dpi / 96f);
        _tailSide = PopoverTailSide.Bottom;
        _tailOffset = LogicalBodyWidth / 2f;
        _renderNow = now;
        ClientSize = new Size(
            Scale(LogicalBodyWidth),
            Scale(ContentBodyHeight + LogicalTailSize));
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
        _clockTimer.Stop();
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
        using var surfacePath = new GraphicsPath();
        surfacePath.FillMode = FillMode.Winding;
        surfacePath.AddPath(bodyPath, false);
        surfacePath.AddPolygon(tail);
        // Solid graphite card in every theme, matching the dock and the overview panel.
        using var fill = new SolidBrush(CardSurface);
        // Hairline like the taskbar dock's card.
        using var border = new Pen(Color.FromArgb(40, 255, 255, 255), 1);
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
        if (_content is not { } content || _providerLogo is null || _resetClockIcon is null) return;

        if (content.Card.IsService)
        {
            DrawServiceContent(graphics, body, content);
            return;
        }

        DrawQuotaCard(graphics, body, content);
    }

    internal static readonly Color CardSurface = Color.FromArgb(19, 21, 28);
    private static readonly Color CardTitle = Color.FromArgb(243, 245, 249);
    private static readonly Color CardValue = Color.FromArgb(226, 232, 240);
    private static readonly Color CardMuted = Color.FromArgb(148, 157, 176);
    private static readonly Color CardDim = Color.FromArgb(100, 110, 130);
    private static readonly Color CardTeal = Color.FromArgb(94, 234, 212);
    private static readonly Color CardHairline = Color.FromArgb(22, 255, 255, 255);

    /// <summary>
    /// The single quota card used by every provider ring: header (logo, name, window chip, plan
    /// pill + account), hero remaining % with reset countdown, progress rail, then fixed rows in a
    /// fixed order (pace, tokens + detail) and a footer (freshness, pin hint). Missing data keeps
    /// its row and shows a dash so all providers share one geometry.
    /// </summary>
    private void DrawQuotaCard(Graphics graphics, RectangleF body, QuotaPopoverContent content)
    {
        var used = content.Window.UsedPercent is null
            ? (double?)null
            : Math.Clamp(content.Window.UsedPercent.Value, 0, 100);
        var remaining = used is null ? (double?)null : 100 - used.Value;
        var now = _renderNow ?? DateTimeOffset.UtcNow;
        var blocked = content.WeeklyBlockResetAt is not null;
        var valueColor = blocked || remaining is null ? CardDim : QuotaColorScale.ForRemaining(remaining.Value);
        var left = body.X + 14;
        var right = body.Right - 14;
        var y = body.Y;
        using var titleBrush = new SolidBrush(CardTitle);
        using var valueBrush = new SolidBrush(CardValue);
        using var mutedBrush = new SolidBrush(CardMuted);
        using var dimBrush = new SolidBrush(CardDim);
        using var tealBrush = new SolidBrush(CardTeal);
        using var hairline = new Pen(CardHairline, 1f);

        // Header.
        DrawLogo(graphics, _providerLogo!, new RectangleF(left, y + 13, 28, 28));
        var window = QuotaDisplayFormatting.FormatWindowShort(content.Window);
        var chipWidth = graphics.MeasureString(window, _cardChipFont, PointF.Empty, StringFormat.GenericTypographic).Width + 12;
        var chip = new RectangleF(right - chipWidth, y + 14, chipWidth, 16);
        using (var chipPath = RoundedRectangle(chip, 8))
        using (var chipFill = new SolidBrush(Color.FromArgb(14, 255, 255, 255)))
        using (var chipEdge = new Pen(Color.FromArgb(40, 255, 255, 255), 1f))
        {
            graphics.FillPath(chipFill, chipPath);
            graphics.DrawPath(chipEdge, chipPath);
        }
        DrawString(graphics, window, _cardChipFont, mutedBrush, chip, StringAlignment.Center);
        var titleRight = chip.X - 6;
        if (content.Pinned)
        {
            DrawPinIcon(graphics, new RectangleF(chip.X - 17, y + 16, 12, 12));
            titleRight -= 16;
        }
        DrawString(graphics, content.Card.Label, _cardTitleFont, titleBrush,
            new RectangleF(left + 38, y + 12, Math.Max(1, titleRight - left - 38), 18), StringAlignment.Near);
        DrawPlanAndAccount(graphics, content.Card, new RectangleF(left + 38, y + 31, right - left - 38, 14));

        // Hero: remaining % on the left, reset countdown on the right.
        var percent = remaining is null ? "--" : FormatPercent(remaining.Value);
        using var heroBrush = new SolidBrush(valueColor);
        DrawString(graphics, percent, _cardHeroFont, heroBrush, new RectangleF(left - 1, y + 50, 110, 32), StringAlignment.Near);
        var heroWidth = graphics.MeasureString(percent, _cardHeroFont, PointF.Empty, StringFormat.GenericTypographic).Width;
        DrawString(graphics, _text.IsChinese ? "剩余" : "left", _cardRowFont, mutedBrush,
            new RectangleF(left + heroWidth + 4, y + 65, 40, 14), StringAlignment.Near);
        var countdown = blocked
            ? _text.FormatResetCountdown(content.WeeklyBlockResetAt!.Value, now)
            : content.Window.ResetsAt is { } reset ? _text.FormatResetCountdown(reset, now) : "—";
        DrawString(graphics, countdown, _cardRowStrongFont, blocked || content.Window.ResetsAt is null ? dimBrush : valueBrush,
            new RectangleF(right - 120, y + 54, 120, 15), StringAlignment.Far);
        var resetText = blocked
            ? _text.WeeklyQuotaBlocked
            : content.Window.ResetsAt is { } resetAt ? _text.ResetAt(resetAt, now) : _text.ResetUnavailable;
        DrawString(graphics, resetText, _cardSmallFont, mutedBrush,
            new RectangleF(right - 140, y + 70, 140, 12), StringAlignment.Far);

        // Progress rail.
        var rail = new RectangleF(left, y + 88, right - left, 6);
        using (var railBrush = new SolidBrush(Color.FromArgb(18, 255, 255, 255)))
            graphics.FillRoundedRectangle(railBrush, rail, 3);
        if (remaining is > 0)
        {
            using var activeBrush = new SolidBrush(valueColor);
            graphics.FillRoundedRectangle(activeBrush,
                new RectangleF(rail.X, rail.Y, Math.Max(6, rail.Width * (float)(remaining.Value / 100)), rail.Height), 3);
        }
        graphics.DrawLine(hairline, left, y + 104.5f, right, y + 104.5f);

        // Row 1: pace.
        var pace = _text.QuotaPace(content.Pace?.ForDisplay(now), now, content.Window.ResetsAt);
        var hasPace = pace.Right.Length > 0;
        DrawTrendPaceIcon(graphics, new RectangleF(left, y + 115, 10, 10), hasPace ? CardTeal : CardMuted);
        DrawString(graphics, pace.Left, _cardRowFont, hasPace ? tealBrush : mutedBrush,
            new RectangleF(left + 16, y + 112, hasPace ? 110 : right - left - 16, 16), StringAlignment.Near);
        DrawString(graphics, hasPace ? pace.Right : "—", _cardRowStrongFont, hasPace ? tealBrush : dimBrush,
            new RectangleF(right - 96, y + 112, 96, 16), StringAlignment.Far);

        // Row 2: tokens (label, value) + detail line.
        var tokens = QuotaTokenUsage;
        DrawTokenIcon(graphics, new RectangleF(left, y + 135, 10, 10), tokens is null ? CardMuted : CardValue);
        DrawString(graphics, tokens?.Label ?? (_text.IsChinese ? "Token 用量" : "Tokens"), _cardRowFont, mutedBrush,
            new RectangleF(left + 16, y + 132, 120, 16), StringAlignment.Near);
        DrawString(graphics, tokens?.Value ?? "—", _cardRowStrongFont, tokens is null ? dimBrush : valueBrush,
            new RectangleF(right - 96, y + 132, 96, 16), StringAlignment.Far);
        DrawString(graphics, tokens?.Detail ?? (_text.IsChinese ? "该服务未提供 Token 统计" : "No token stats for this service"),
            _cardSmallFont, dimBrush, new RectangleF(left + 16, y + 149, right - left - 16, 12), StringAlignment.Near);

        // Footer.
        graphics.DrawLine(hairline, left, y + 166.5f, right, y + 166.5f);
        DrawString(graphics, _text.Freshness(content.CapturedAt, now), _cardSmallFont, mutedBrush,
            new RectangleF(left, y + 170, 110, 12), StringAlignment.Near);
        DrawString(graphics, content.Pinned ? _text.ClosePinnedHint : _text.PinHint, _cardSmallFont, mutedBrush,
            new RectangleF(right - 110, y + 170, 110, 12), StringAlignment.Far);
    }

    /// <summary>Plan pill (any provider) followed by the account hint.</summary>
    private void DrawPlanAndAccount(Graphics graphics, QuotaCard card, RectangleF bounds)
    {
        var plan = CardPlanLabel(card);
        var x = bounds.X;
        if (plan is not null)
        {
            var style = PlanBadgePresentation.TryGetStyle(plan, out var known)
                ? known
                : new PlanBadgeStyle(Color.FromArgb(34, 38, 50), Color.FromArgb(70, 78, 98), Color.FromArgb(214, 220, 232));
            var width = graphics.MeasureString(plan, _statusFont, PointF.Empty, StringFormat.GenericTypographic).Width + 10;
            var pill = new RectangleF(x, bounds.Y, width, 13);
            using var path = RoundedRectangle(pill, 4);
            using var fill = new SolidBrush(style.Fill);
            using var edge = new Pen(style.Border, 1);
            using var ink = new SolidBrush(style.Text);
            graphics.FillPath(fill, path);
            graphics.DrawPath(edge, path);
            DrawString(graphics, plan, _statusFont, ink, pill, StringAlignment.Center);
            x = pill.Right + 6;
        }
        var account = HasAccountHint(card) ? card.AccountHint! : plan is null ? _text.LiveQuota : null;
        if (account is null) return;
        using var brush = new SolidBrush(Color.FromArgb(190, 198, 212));
        DrawString(graphics, account, _subtitleFont, brush,
            new RectangleF(x, bounds.Y, Math.Max(1, bounds.Right - x), bounds.Height), StringAlignment.Near);
    }

    /// <summary>Plan label for the header pill: shared Codex labels, otherwise the provider's own plan name.</summary>
    internal static string? CardPlanLabel(QuotaCard card)
    {
        if (string.IsNullOrWhiteSpace(card.Badge)) return null;
        if (card.Provider == ProviderKind.Codex) return PlanBadgePresentation.Label(card.Badge);
        var label = card.Badge.Trim().ToUpperInvariant();
        // A sign-in type is not a plan; older snapshots stored Grok's auth_mode here.
        if (SignInTypes.Contains(label)) return null;
        return label.Length > 18 ? label[..18].TrimEnd() : label;
    }

    private static readonly HashSet<string> SignInTypes = new(StringComparer.Ordinal)
    {
        "OIDC", "OAUTH", "OAUTH2", "API_KEY", "APIKEY", "API KEY", "SSO", "DEVICE", "BROWSER", "TOKEN",
    };

    private static void DrawTokenIcon(Graphics graphics, RectangleF bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, bounds.Left + 1, bounds.Top + 6, 2, 4);
        graphics.FillRectangle(brush, bounds.Left + 4.5f, bounds.Top + 3, 2, 7);
        graphics.FillRectangle(brush, bounds.Left + 8, bounds.Top, 2, 10);
    }

    private void DrawQuotaTokenUsage(
        Graphics graphics,
        RectangleF bounds,
        (string Label, string Value, string Detail) usage,
        Brush mutedBrush,
        Brush valueBrush)
    {
        DrawString(
            graphics,
            usage.Label,
            _detailFont,
            mutedBrush,
            new RectangleF(bounds.X, bounds.Y, bounds.Width - 70, 16),
            StringAlignment.Near);
        DrawString(
            graphics,
            usage.Value,
            _titleFont,
            valueBrush,
            new RectangleF(bounds.Right - 70, bounds.Y, 70, 16),
            StringAlignment.Far);
        DrawString(
            graphics,
            usage.Detail,
            _detailFont,
            mutedBrush,
            new RectangleF(bounds.X, bounds.Y + 16, bounds.Width, 16),
            StringAlignment.Near);
    }

    private void DrawServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content)
    {
        if (Sub2ApiServicePresentation.IsSub2ApiService(content.Card))
        {
            var presentation = Sub2ApiServicePresentation.Resolve(
                content.Card,
                _renderNow ?? content.CapturedAt);
            switch (presentation.Kind)
            {
                case Sub2ApiServicePresentationKind.CompleteAvailability
                    when presentation.Availability is { } completeAvailability:
                    DrawSub2ApiAccountAvailabilityServiceContent(
                        graphics,
                        body,
                        content,
                        completeAvailability,
                        includeProgressRail: true);
                    return;
                case Sub2ApiServicePresentationKind.PartialAvailability
                    or Sub2ApiServicePresentationKind.KnownNoneAvailability
                    when presentation.Availability is { } partialAvailability:
                    DrawSub2ApiAccountAvailabilityServiceContent(
                        graphics,
                        body,
                        content,
                        partialAvailability);
                    return;
                case Sub2ApiServicePresentationKind.LegacyAggregateQuota
                    when presentation.LegacyQuota is { } legacy:
                    DrawSub2ApiLegacyQuotaServiceContent(graphics, body, content, legacy);
                    return;
                case Sub2ApiServicePresentationKind.Usage
                    when presentation.Usage is { } usage:
                    DrawSub2ApiUsageServiceContent(graphics, body, content, usage);
                    return;
                case Sub2ApiServicePresentationKind.Pool
                    when presentation.Pool is { } pool:
                    DrawSub2ApiPoolServiceContent(graphics, body, content, pool);
                    return;
                default:
                    DrawSub2ApiUnavailableServiceContent(graphics, body, content);
                    return;
            }
        }

        if (content.Card.Sub2ApiAccountAvailability is { } availability
            && Sub2ApiAccountAvailabilityFormatting.IsRenderable(availability))
        {
            DrawSub2ApiAccountAvailabilityServiceContent(graphics, body, content, availability);
            return;
        }
        if (content.Card.Sub2ApiQuota is { } quota && Sub2ApiQuotaFormatting.PreferredWindow(quota) is not null)
        {
            DrawSub2ApiQuotaServiceContent(graphics, body, content, quota);
            return;
        }
        if (content.Card.Sub2ApiUsage is not null)
        {
            DrawSub2ApiUsageServiceContent(graphics, body, content, content.Card.Sub2ApiUsage);
            return;
        }
        if (content.Card.Sub2ApiPool is not null)
        {
            DrawSub2ApiPoolServiceContent(graphics, body, content, content.Card.Sub2ApiPool);
            return;
        }
        if (content.Card.Balance is not null)
        {
            DrawBalanceServiceContent(graphics, body, content, content.Card.Balance);
            return;
        }

        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(Color.FromArgb(94, 234, 212));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.ApiServiceConfigured,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            content.Pinned ? _text.Pinned : _text.Preview,
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.ApiServiceConfigured,
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.ApiServiceNoQuota,
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 78, body.Width - 24, 18),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private void DrawBalanceServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        AiGatewayBalance balance)
    {
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(balance.Status == AiGatewayBalanceStatus.Available
            ? Color.FromArgb(94, 234, 212)
            : Color.FromArgb(251, 191, 36));
        DrawString(
            graphics,
            _text.AiGateway,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.AiGatewayModel,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.AiGatewayStatus(balance.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            AiGatewayBalanceFormatting.Amount(balance.TotalBalance),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            $"{_text.AiGatewayToppedUpBalance} {AiGatewayBalanceFormatting.Amount(balance.ToppedUpBalance)} · {_text.AiGatewayGrantedBalance} {AiGatewayBalanceFormatting.Amount(balance.GrantedBalance)}",
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 76, body.Width - 24, 18),
            StringAlignment.Near);
        if (content.AiGatewayUsage is { } usage)
        {
            DrawString(
                graphics,
                _text.AiGatewayTodayUsage(usage),
                _detailFont,
                mutedBrush,
                new RectangleF(x + 12, y + 94, body.Width - 24, 18),
                StringAlignment.Near);
            DrawString(
                graphics,
                _text.AiGatewayUsageDetail(usage),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 112, body.Width - 24, 13),
                StringAlignment.Near);
            DrawString(
                graphics,
                _text.AiGatewayUpdatedShort(balance.ObservedAt),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 129, 108, 10),
                StringAlignment.Near);
        }
        else
        {
            DrawString(
                graphics,
                $"{_text.AiGatewayUpdated(balance.ObservedAt)} · {_text.AiGatewayStatus(balance.Status)}",
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 103, body.Width - 24, 13),
                StringAlignment.Near);
        }
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiPoolServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiPoolAvailability pool)
    {
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(Sub2ApiPoolStatusColor(pool.Status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiPool,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiPoolStatus(pool.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiPoolAvailableAccounts(pool),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiPoolFreeConcurrency(pool),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 76, body.Width - 24, 18),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiPoolIssues(pool),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 94, body.Width - 24, 18),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiPoolUpdatedShort(pool.ObservedAt),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, y + 112, 108, 10),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiUsageServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiUsageSummary usage)
    {
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(Sub2ApiUsageStatusColor(usage.Status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiUsage,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiUsageStatus(usage.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiUsageTodayTokens(usage),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiUsageTotalTokens(usage),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 76, body.Width - 24, 18),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiUsageRequests(usage),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 94, body.Width - 24, 18),
            StringAlignment.Near);
        if (content.Card.Sub2ApiPool is { } pool)
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsagePool(pool),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 112, body.Width - 24, 10),
                StringAlignment.Near);
            DrawString(
                graphics,
                _text.Sub2ApiUsageUpdatedShort(usage.ObservedAt),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 129, 108, 10),
                StringAlignment.Near);
        }
        else
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsageUpdatedShort(usage.ObservedAt),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 112, 108, 10),
                StringAlignment.Near);
        }
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiQuotaServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiQuotaSummary quota)
    {
        var preferred = Sub2ApiQuotaFormatting.PreferredWindow(quota)!;
        var other = Sub2ApiQuotaFormatting.OtherWindow(quota);
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(Sub2ApiQuotaStatusColor(quota.Status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiQuota,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaStatus(quota.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiQuotaHeadline(preferred),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaWindowDetails(preferred),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 76, body.Width - 24, 18),
            StringAlignment.Near);
        if (other is not null)
        {
            DrawString(
                graphics,
                _text.Sub2ApiQuotaWindowDetails(other),
                _detailFont,
                mutedBrush,
                new RectangleF(x + 12, y + 94, body.Width - 24, 18),
                StringAlignment.Near);
        }
        var poolRowY = other is null ? y + 94 : y + 130;
        if (content.Card.Sub2ApiUsage is { } usage)
        {
            DrawString(
                graphics,
                _text.Sub2ApiQuotaProxyTokens(usage),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 112, body.Width - 24, 10),
                StringAlignment.Near);
        }
        if (content.Card.Sub2ApiPool is { } pool)
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsagePool(pool),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, poolRowY, body.Width - 24, 10),
                StringAlignment.Near);
        }
        DrawString(
            graphics,
            _text.Sub2ApiQuotaUpdatedShort(quota.ObservedAt),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, y + 148, 108, 10),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 152, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiAccountAvailabilityServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiAccountAvailabilitySummary availability,
        bool includeProgressRail = false)
    {
        var accounts = availability.Accounts!;
        var columns = AccountAvailabilityColumnCount(accounts.Count);
        var rows = (accounts.Count + columns - 1) / columns;
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(Sub2ApiQuotaStatusColor(availability.Status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiAccountAvailability,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaStatus(availability.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiAccountAvailabilityHeadline(availability),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 20),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiAccountAvailabilityCoverage(availability),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, y + 64, body.Width - 24, 10),
            StringAlignment.Near);

        var columnWidth = (body.Width - 24) / columns;
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            var column = index % columns;
            var row = index / columns;
            var rowX = x + 12 + column * columnWidth;
            var rowY = y + AccountAvailabilityFirstRowY + row * AccountAvailabilityRowHeight;
            var accountColor = account.RemainingPercent is { } remaining
                ? QuotaColorScale.ForRemaining(remaining)
                : Color.FromArgb(148, 163, 184);
            using var accountBrush = new SolidBrush(accountColor);
            DrawString(
                graphics,
                _text.Sub2ApiAccountAvailabilitySlot(account.Slot),
                _subtitleFont,
                mutedBrush,
                new RectangleF(rowX, rowY, 20, 12),
                StringAlignment.Near);
            DrawString(
                graphics,
                _text.Sub2ApiAccountAvailabilityPercent(account),
                _metricFont,
                accountBrush,
                new RectangleF(rowX + 20, rowY - 1, columnWidth - 20, 14),
                StringAlignment.Far);
        }

        var progressRailY = y + AccountAvailabilityFirstRowY + rows * AccountAvailabilityRowHeight + 4;
        if (includeProgressRail
            && Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(availability) is { } aggregate)
        {
            DrawSub2ApiProgressRail(
                graphics,
                new RectangleF(x + 12, progressRailY, body.Width - 24, 5),
                aggregate,
                valueBrush.Color);
        }

        var poolRowY = progressRailY + (includeProgressRail ? 14 : 0);
        if (content.Card.Sub2ApiPool is { } pool)
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsagePool(pool),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, poolRowY, body.Width - 24, 10),
                StringAlignment.Near);
        }
        var footerY = poolRowY + 18;
        DrawString(
            graphics,
            _text.Sub2ApiQuotaUpdatedShort(availability.ObservedAt),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, footerY, 108, 10),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, footerY, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiLegacyQuotaServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiQuotaWindow legacy)
    {
        var status = content.Card.Sub2ApiQuota?.Status ?? Sub2ApiQuotaStatus.Available;
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(Sub2ApiQuotaStatusColor(status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiQuota,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaStatus(status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiLegacyQuotaHeadline(legacy),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawSub2ApiProgressRail(
            graphics,
            new RectangleF(x + 12, y + 68, body.Width - 24, 5),
            legacy.RemainingPercent,
            valueBrush.Color);
        DrawString(
            graphics,
            _text.Sub2ApiLegacyQuotaDetails(legacy),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 78, body.Width - 24, 18),
            StringAlignment.Near);

        var poolRowY = y + 112;
        if (content.Card.Sub2ApiPool is { } pool)
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsagePool(pool),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, poolRowY, body.Width - 24, 10),
                StringAlignment.Near);
        }
        var footerY = content.Card.Sub2ApiPool is null ? y + 112 : y + 130;
        DrawString(
            graphics,
            _text.Sub2ApiQuotaUpdatedShort(content.Card.Sub2ApiQuota?.ObservedAt),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, footerY, 108, 10),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiUnavailableServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content)
    {
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(Color.FromArgb(241, 245, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var valueBrush = new SolidBrush(Sub2ApiQuotaStatusColor(Sub2ApiQuotaStatus.Unavailable));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiAccountAvailability,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(Color.FromArgb(71, 85, 105), 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaStatus(Sub2ApiQuotaStatus.Unavailable),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiUnavailable,
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaUpdatedShort(null),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 78, body.Width - 24, 18),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private static void DrawSub2ApiProgressRail(
        Graphics graphics,
        RectangleF bounds,
        double remaining,
        Color valueColor)
    {
        var trough = new RectangleF(bounds.X, bounds.Y + 1, bounds.Width, 4);
        using var troughBrush = new SolidBrush(Color.FromArgb(30, 41, 59));
        graphics.FillRoundedRectangle(troughBrush, trough, 2);
        var active = Math.Clamp(remaining, 0, 100);
        if (active <= 0) return;
        using var activeBrush = new SolidBrush(valueColor);
        graphics.FillRoundedRectangle(
            activeBrush,
            new RectangleF(bounds.X, bounds.Y + 1, bounds.Width * (float)(active / 100), 4),
            2);
    }

    private static Color Sub2ApiPoolStatusColor(Sub2ApiPoolStatus status) => status switch
    {
        Sub2ApiPoolStatus.Available => Color.FromArgb(94, 234, 212),
        Sub2ApiPoolStatus.Stale => Color.FromArgb(251, 191, 36),
        Sub2ApiPoolStatus.Unavailable => Color.FromArgb(251, 113, 133),
        _ => Color.FromArgb(148, 163, 184),
    };

    private static Color Sub2ApiUsageStatusColor(Sub2ApiUsageStatus status) => status switch
    {
        Sub2ApiUsageStatus.Available => Color.FromArgb(94, 234, 212),
        Sub2ApiUsageStatus.Stale => Color.FromArgb(251, 191, 36),
        Sub2ApiUsageStatus.Unavailable => Color.FromArgb(251, 113, 133),
        _ => Color.FromArgb(148, 163, 184),
    };

    private static Color Sub2ApiQuotaStatusColor(Sub2ApiQuotaStatus status) => status switch
    {
        Sub2ApiQuotaStatus.Available => Color.FromArgb(94, 234, 212),
        Sub2ApiQuotaStatus.Stale => Color.FromArgb(251, 191, 36),
        Sub2ApiQuotaStatus.Unavailable => Color.FromArgb(251, 113, 133),
        _ => Color.FromArgb(148, 163, 184),
    };

    internal static string AccountSubtitle(QuotaCard card, NativeText text)
    {
        var badge = string.IsNullOrWhiteSpace(card.Badge) ? text.LiveQuota : card.Badge!;
        var accountHint = card.AccountHint;
        return !HasAccountHint(card)
            ? badge
            : $"{badge} · {accountHint}";
    }

    internal static string? PlanBadgeLabel(QuotaCard card) =>
        card.Provider == ProviderKind.Codex && !string.IsNullOrWhiteSpace(card.Badge)
            ? PlanBadgePresentation.Label(card.Badge)
            : null;

    private void DrawAccountSubtitle(
        Graphics graphics,
        QuotaCard card,
        NativeText text,
        Brush mutedBrush,
        RectangleF bounds)
    {
        var planLabel = PlanBadgeLabel(card);
        if (planLabel is not { } tagLabel
            || !PlanBadgePresentation.TryGetStyle(tagLabel, out var style))
        {
            DrawString(
                graphics,
                AccountSubtitle(card, text),
                _subtitleFont,
                mutedBrush,
                bounds,
                StringAlignment.Near);
            return;
        }

        var tagWidth = PlanBadgePresentation.Width(tagLabel);
        var tagBounds = new RectangleF(bounds.X, bounds.Y, tagWidth, 13);
        using var tagFill = new SolidBrush(style.Fill);
        using var tagBorder = new Pen(style.Border, 1);
        using var tagPath = RoundedRectangle(tagBounds, 4);
        graphics.FillPath(tagFill, tagPath);
        graphics.DrawPath(tagBorder, tagPath);
        using var tagTextBrush = new SolidBrush(style.Text);
        DrawString(graphics, tagLabel, _statusFont, tagTextBrush, tagBounds, StringAlignment.Center);

        var accountHint = card.AccountHint;
        if (string.IsNullOrWhiteSpace(accountHint)
            || string.Equals(accountHint, "Codex account", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var accountBrush = new SolidBrush(Color.FromArgb(203, 213, 225));
        DrawString(
            graphics,
            accountHint,
            _subtitleFont,
            accountBrush,
            new RectangleF(
                bounds.X + tagWidth + 6,
                bounds.Y,
                Math.Max(1, bounds.Width - tagWidth - 6),
                bounds.Height),
            StringAlignment.Near);
    }

    private static bool HasAccountHint(QuotaCard card) =>
        !string.IsNullOrWhiteSpace(card.AccountHint)
        && !string.Equals(card.AccountHint, "Codex account", StringComparison.OrdinalIgnoreCase);

    /// <summary>Official icon clipped to a circle, as on the dock rings.</summary>
    private static void DrawLogo(Graphics graphics, Image logo, RectangleF bounds)
    {
        var state = graphics.Save();
        using var clip = new GraphicsPath();
        clip.AddEllipse(bounds);
        graphics.SetClip(clip, CombineMode.Intersect);
        graphics.DrawImage(logo, bounds);
        graphics.Restore(state);
    }

    private static void DrawPinIcon(Graphics graphics, RectangleF bounds)
    {
        using var pen = new Pen(Color.FromArgb(148, 163, 184), 1.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        graphics.DrawLines(pen,
        [
            new PointF(bounds.Left + 4, bounds.Top + 1), new PointF(bounds.Right - 1, bounds.Top + 6),
            new PointF(bounds.Right - 3, bounds.Top + 6), new PointF(bounds.Left + 5, bounds.Bottom - 2),
            new PointF(bounds.Left + 2, bounds.Top + 7), new PointF(bounds.Left + 5, bounds.Top + 3),
            new PointF(bounds.Left + 4, bounds.Top + 1),
        ]);
        graphics.DrawLine(pen, bounds.Left + 4, bounds.Top + 8, bounds.Left + 1, bounds.Bottom - 1);
    }

    private static void DrawTrendPaceIcon(Graphics graphics, RectangleF bounds, Color color)
    {
        using var pen = new Pen(color, 1.25f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        var points = new[]
        {
            new PointF(bounds.Left + 1, bounds.Bottom - 2),
            new PointF(bounds.Left + 4, bounds.Top + 5),
            new PointF(bounds.Left + 6, bounds.Top + 7),
            new PointF(bounds.Right - 1, bounds.Top + 2),
        };
        graphics.DrawLines(pen, points);
        using var dot = new SolidBrush(color);
        graphics.FillEllipse(dot, bounds.Right - 2.5f, bounds.Top + .5f, 2.5f, 2.5f);
    }

    private RectangleF BodyBounds() => TaskbarPopoverMath.BodyBounds(
        _tailSide,
        LogicalBodyWidth,
        ContentBodyHeight,
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

    private static string FormatPercent(double percent)
    {
        if (percent <= 0) return "0%";
        if (percent < 1) return "<1%";
        if (percent < 10) return $"{Math.Round(percent, 1):0.#}%";
        return $"{Math.Round(percent):0}%";
    }

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
            _clockTimer.Stop();
            _clockTimer.Dispose();
            _entranceTimer.Stop();
            _entranceTimer.Dispose();
            _exitTimer.Stop();
            _exitTimer.Dispose();
            _titleFont.Dispose();
            _subtitleFont.Dispose();
            _statusFont.Dispose();
            _valueFont.Dispose();
            _metricFont.Dispose();
            _detailFont.Dispose();
            _cardTitleFont.Dispose();
            _cardChipFont.Dispose();
            _cardHeroFont.Dispose();
            _cardRowFont.Dispose();
            _cardRowStrongFont.Dispose();
            _cardSmallFont.Dispose();
            _textFormats.Dispose();
        }
        base.Dispose(disposing);
    }
}
