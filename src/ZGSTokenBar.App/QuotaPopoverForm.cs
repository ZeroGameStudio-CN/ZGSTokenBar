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
    CodexQuotaTokenSummary? CodexQuotaTokens = null,
    CodexResetCards? ResetCards = null);

/// <summary>
/// Codex reset cards (重置卡) of one account: the count from the usage response's
/// <c>rate_limit_reset_credits.available_count</c> (or Cockpit's <c>reset_credits_available</c>)
/// and the earliest known card expiry.
/// </summary>
internal sealed record CodexResetCards(int Count, int? Applicable, DateTimeOffset? NextExpiry);

internal sealed partial class QuotaPopoverForm : Form
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
            if (!content.Card.IsService) return UnifiedBodyHeight(content);
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

    private const int DetailRowHeight = 18;
    private const int DetailSectionPadding = 6;

    /// <summary>Shared quota card height: the fixed core plus one row per detail (windows, credits, subscription).</summary>
    internal static int UnifiedBodyHeight(QuotaPopoverContent content)
    {
        var rows = DetailRowCount(content);
        return LogicalUnifiedQuotaBodyHeight + (rows == 0 ? 0 : rows * DetailRowHeight + DetailSectionPadding);
    }

    /// <summary>Windows listed under the hero: every window when there is more than one (each with its reset).</summary>
    internal static IReadOnlyList<QuotaWindow> DetailWindows(QuotaCard card) =>
        card.Windows.Count >= 2 ? card.Windows.Take(6).ToArray() : [];

    private static int DetailRowCount(QuotaPopoverContent content) =>
        DetailWindows(content.Card).Count
        + (content.Card.Provider == ProviderKind.Codex && content.Card.Credits is not null ? 1 : 0)
        + (content.Card.Provider == ProviderKind.Codex && content.ResetCards is not null ? 1 : 0)
        + 1; // subscription row (a dash when the source has no dates)

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
        BackColor = UiTokens.CardSurface;
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
            this.ShowPopover(BarForm.PopoverOwnerFor(owner));
            if (!TaskbarPlacement.ShowAt(Handle, _entranceLocation, placement.WindowSize)) Location = _entranceLocation;
            _entranceTimer.Start();
        }
        else
        {
            Opacity = 1;
            if (!wasVisible) this.ShowPopover(BarForm.PopoverOwnerFor(owner));
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
        UiPaint.PrepareQuality(graphics);
        graphics.ScaleTransform(_scale, _scale);

        var body = BodyBounds();
        // The shared hover-card surface: graphite card, white hairline, pointer tail.
        UiPaint.TailedSurface(graphics, body, TailPoints(body), UiTokens.CardSurface, UiTokens.Hairline, UiTokens.RadiusPanel);

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

    internal static Color CardSurface => UiTokens.CardSurface;
    private static readonly Color CardTitle = Color.FromArgb(243, 245, 249);
    private static readonly Color CardValue = UiPalette.Slate200;
    private static readonly Color CardMuted = Color.FromArgb(148, 157, 176);
    private static readonly Color CardDim = Color.FromArgb(100, 110, 130);
    private static readonly Color CardTeal = UiPalette.Teal300;
    private static readonly Color CardHairline = Color.FromArgb(22, 255, 255, 255);

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

    private static string FormatPercent(double remaining) => PercentFormat.Remaining(remaining);

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius) => UiPaint.RoundedPath(bounds, radius);

    private void DrawString(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        RectangleF bounds,
        StringAlignment alignment) =>
        UiPaint.Text(graphics, text, font, brush, bounds, alignment);

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
