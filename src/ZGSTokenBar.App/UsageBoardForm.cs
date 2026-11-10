using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class UsageBoardForm : Form, IPluginPopoverContent
{
    private readonly float _scale;
    private readonly bool _chinese;
    private readonly Action? _refresh;
    private readonly Action? _settings;
    private readonly HashSet<string> _ledgerClosed = new(StringComparer.Ordinal);
    private readonly List<(RectangleF Bounds, Action Action)> _hits = [];
    private UsageTheme _theme;
    private UsageBoard _board = UsageBoard.Empty;
    private bool _compact;
    private bool _backdrop;
    private Font? _titleFont;
    private Font? _bodyFont;
    private Font? _smallFont;

    public UsageBoardForm(float scale, bool chinese, bool light, Action? refresh, Action? settings)
    {
        _scale = scale <= 0 ? 1 : scale;
        _chinese = chinese;
        _refresh = refresh;
        _settings = settings;
        _theme = light ? UsageThemes.Light : UsageThemes.Dark;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        MouseClick += (_, args) =>
        {
            var point = new PointF(args.X / _scale, args.Y / _scale);
            foreach (var hit in _hits)
            {
                if (!hit.Bounds.Contains(point)) continue;
                hit.Action();
                return;
            }
        };
    }

    public bool InteractionOpen => false;

    public bool Light => _theme.Light;

    public void SetPinned(bool pinned)
    {
    }

    public void ShowBoard(UsageBoard board)
    {
        _board = board;
        _compact = UsageBoardLayout.PreferCompact(board);
        ApplySize();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyBackdrop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.ScaleTransform(_scale, _scale);
        EnsureFonts();
        _hits.Clear();
        var theme = _theme;
        var width = UsageBoardLayout.Width;
        var height = LogicalHeight();
        PaintWash(graphics, theme, width, height);
        using var text = new SolidBrush(ColorOf(theme.Text));
        using var muted = new SolidBrush(ColorOf(theme.Muted));
        DrawLabel(graphics, _chinese ? "用量" : "Usage", _titleFont!, text, 24, 16, 220, 28);
        DrawLabel(graphics, UsageFormatting.Overall(_board, _chinese), _smallFont!, muted, 24, 46, 250, 16);
        var chipY = 22f;
        AddChip(graphics, theme, width - 168, chipY, 46, _chinese ? "刷新" : "Refresh", () => _refresh?.Invoke());
        AddChip(graphics, theme, width - 116, chipY, 46, theme.Light ? (_chinese ? "深色" : "Dark") : (_chinese ? "浅色" : "Light"), ToggleTheme);
        AddChip(graphics, theme, width - 64, chipY, 40, _chinese ? "设置" : "Set", () => _settings?.Invoke());
        var y = (float)(UsageBoardLayout.Padding + UsageBoardLayout.HeaderHeight);
        foreach (var provider in _board.Providers)
        {
            var cardHeight = UsageBoardLayout.CardHeight(provider, _compact && _ledgerClosed.Count == 0 ? _compact : _compact);
            if (_compact) cardHeight = UsageBoardLayout.CardHeight(provider, true);
            else if (_ledgerClosed.Contains(provider.ProviderId) && UsageBoardLayout.HasLedger(provider))
            {
                cardHeight -= UsageBoardLayout.LedgerHeight - 22;
            }

            PaintProvider(graphics, theme, provider, 16, y, width - 32, cardHeight);
            y += cardHeight + UsageBoardLayout.CardGap;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose();
            _bodyFont?.Dispose();
            _smallFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PaintProvider(Graphics graphics, UsageTheme theme, UsageProviderSnapshot provider, float x, float y, float width, float height)
    {
        PaintGlass(graphics, theme, new RectangleF(x, y, width, height), 22, false);
        var accent = UsageColor.Parse(provider.Accent);
        using var accentBrush = new SolidBrush(Color.FromArgb(accent.R, accent.G, accent.B));
        graphics.FillEllipse(accentBrush, x + 16, y + 16, 24, 24);
        using var white = new SolidBrush(Color.White);
        using var text = new SolidBrush(ColorOf(theme.Text));
        using var muted = new SolidBrush(ColorOf(theme.Muted));
        DrawLabel(graphics, provider.DisplayName[..1], _bodyFont!, white, x + 16, y + 18, 24, 20);
        DrawLabel(graphics, provider.DisplayName, _bodyFont!, text, x + 48, y + 12, 180, 18);
        DrawLabel(graphics, _chinese ? $"{provider.Accounts.Count} 个账号" : $"{provider.Accounts.Count} accounts", _smallFont!, muted, x + 48, y + 30, 160, 14);
        var tight = Tightest(provider);
        DrawRing(graphics, x + width - 78, y + 16, 14, tight);
        using var percentBrush = new SolidBrush(tight is { } value ? ColorOf(UsageThemes.ForRemaining(value)) : ColorOf(theme.Muted));
        DrawLabel(graphics, UsageFormatting.Percent(tight), _bodyFont!, percentBrush, x + width - 58, y + 16, 48, 18);
        var rowY = y + UsageBoardLayout.CardPad + UsageBoardLayout.CardHeader;
        var rowHeight = _compact ? UsageBoardLayout.CompactAccountRow : UsageBoardLayout.AccountRow;
        foreach (var account in provider.Accounts)
        {
            PaintAccount(graphics, theme, account, x + 14, rowY, width - 28, rowHeight - 6);
            rowY += rowHeight;
        }

        if (_compact || !UsageBoardLayout.HasLedger(provider)) return;
        var open = !_ledgerClosed.Contains(provider.ProviderId);
        var ledgerBounds = new RectangleF(x + 14, rowY, 70, 20);
        _hits.Add((ledgerBounds, () =>
        {
            if (!_ledgerClosed.Add(provider.ProviderId)) _ledgerClosed.Remove(provider.ProviderId);
            ApplySize();
            Invalidate();
        }));
        DrawLabel(graphics, (open ? "▾ " : "▸ ") + (_chinese ? "账本" : "Ledger"), _smallFont!, muted, ledgerBounds.X, ledgerBounds.Y, 80, 16);
        if (!open) return;
        var ledger = provider.Accounts.Select(account => account.Ledger).FirstOrDefault(item => item is not null);
        if (ledger is null) return;
        DrawLabel(graphics, Period(_chinese ? "今日" : "Today", ledger.Today), _smallFont!, text, x + 96, rowY, 110, 16);
        DrawLabel(graphics, Period("7d", ledger.SevenDays), _smallFont!, text, x + 206, rowY, 100, 16);
        DrawLabel(graphics, Period("30d", ledger.ThirtyDays), _smallFont!, text, x + 310, rowY, 110, 16);
    }

    private void PaintAccount(Graphics graphics, UsageTheme theme, UsageAccountSnapshot account, float x, float y, float width, float height)
    {
        var window = UsageFormatting.Primary(account);
        var remaining = UsageFormatting.Remaining(window);
        using var text = new SolidBrush(ColorOf(theme.Text));
        using var muted = new SolidBrush(ColorOf(theme.Muted));
        DrawLabel(graphics, account.Label, _bodyFont!, text, x, y, width - 58, 16);
        if (!_compact)
        {
            var caption = window?.Label ?? UsageFormatting.Error(account.ErrorKey, _chinese);
            DrawLabel(graphics, caption + " · " + UsageFormatting.Reset(window?.ResetsAt, DateTimeOffset.Now, _chinese), _smallFont!, muted, x, y + 16, width - 8, 14);
        }

        var bar = new RectangleF(x, y + (_compact ? 20 : 34), width - 52, 6);
        using var track = new SolidBrush(Color.FromArgb(theme.Light ? 36 : 48, theme.Track.R, theme.Track.G, theme.Track.B));
        graphics.FillRoundedRectangle(track, bar, 3);
        if (remaining is { } left && left > 0)
        {
            var fill = new RectangleF(bar.X, bar.Y, Math.Max(6, bar.Width * (float)(left / 100)), bar.Height);
            var end = ColorOf(UsageThemes.ForRemaining(left));
            var start = ColorOf(UsageThemes.Glow(left));
            using var glow = new SolidBrush(Color.FromArgb(70, end));
            graphics.FillRoundedRectangle(glow, new RectangleF(fill.X, fill.Y - 1, fill.Width, fill.Height + 2), 4);
            using var gradient = new LinearGradientBrush(fill, start, end, LinearGradientMode.Horizontal);
            graphics.FillRoundedRectangle(gradient, fill, 3);
        }

        using var percentBrush = new SolidBrush(remaining is { } shown ? ColorOf(UsageThemes.ForRemaining(shown)) : ColorOf(theme.Muted));
        DrawLabel(graphics, UsageFormatting.Percent(remaining), _smallFont!, percentBrush, bar.Right + 6, bar.Y - 5, 46, 16);
        _ = height;
    }

    private void PaintWash(Graphics graphics, UsageTheme theme, int width, int height)
    {
        var bounds = new RectangleF(0, 0, width, height);
        if (!_backdrop)
        {
            using var wash = new LinearGradientBrush(bounds, ColorOf(theme.Wash), ColorOf(theme.Accent), 35f);
            graphics.FillRectangle(wash, bounds);
        }

        PaintGlass(graphics, theme, bounds, UsageBoardLayout.Corner, true);
    }

    private void PaintGlass(Graphics graphics, UsageTheme theme, RectangleF bounds, float radius, bool window)
    {
        var alpha = window
            ? (_backdrop ? (theme.Light ? 70 : 84) : (theme.Light ? 150 : 168))
            : (theme.Light ? 120 : 96);
        var fill = theme.Light ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 20, 24, 32);
        using var brush = new SolidBrush(fill);
        graphics.FillRoundedRectangle(brush, bounds, radius);
        using var border = new Pen(Color.FromArgb(theme.Light ? 170 : 90, 255, 255, 255), 1);
        using var path = Rounded(bounds, radius);
        graphics.DrawPath(border, path);
        var shine = new RectangleF(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, 14);
        using var highlight = new SolidBrush(Color.FromArgb(theme.Light ? 90 : 40, 255, 255, 255));
        graphics.FillRoundedRectangle(highlight, shine, Math.Max(8, radius - 8));
    }

    private void AddChip(Graphics graphics, UsageTheme theme, float x, float y, float width, string label, Action action)
    {
        var bounds = new RectangleF(x, y, width, 26);
        using var fill = new SolidBrush(Color.FromArgb(48, theme.Accent.R, theme.Accent.G, theme.Accent.B));
        graphics.FillRoundedRectangle(fill, bounds, 13);
        using var border = new Pen(Color.FromArgb(120, 255, 255, 255), 1);
        using var path = Rounded(bounds, 13);
        graphics.DrawPath(border, path);
        using var text = new SolidBrush(ColorOf(theme.Text));
        DrawLabel(graphics, label, _smallFont!, text, x + 8, y + 4, width - 12, 16);
        _hits.Add((bounds, action));
    }

    private static void DrawRing(Graphics graphics, float x, float y, float radius, double? remaining)
    {
        var bounds = new RectangleF(x, y, radius * 2, radius * 2);
        using var track = new Pen(Color.FromArgb(70, 255, 255, 255), 2.4f);
        graphics.DrawArc(track, bounds, 0, 360);
        if (remaining is not { } value || value <= 0) return;
        using var pen = new Pen(ColorOf(UsageThemes.ForRemaining(value)), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawArc(pen, bounds, -90, (float)(360 * value / 100));
    }

    private static double? Tightest(UsageProviderSnapshot provider)
    {
        double? tightest = null;
        foreach (var account in provider.Accounts)
        {
            var remaining = UsageFormatting.Remaining(UsageFormatting.Primary(account));
            if (remaining is { } value && (tightest is null || value < tightest)) tightest = value;
        }

        return tightest;
    }

    private static string Period(string label, UsageLedgerPeriod period)
    {
        var cost = period.ApiEquivalentCost is { } value ? " " + UsageFormatting.Money("USD", value) : "";
        return label + " " + UsageFormatting.Tokens(period.Tokens) + cost;
    }

    private void ToggleTheme()
    {
        _theme = _theme.Light ? UsageThemes.Dark : UsageThemes.Light;
        ApplyBackdrop();
        ApplySize();
        Invalidate();
    }

    private void ApplyBackdrop()
    {
        if (!IsHandleCreated) return;
        _backdrop = WindowBackdrop.TryApply(Handle, _theme.Light, transient: true);
        BackColor = _theme.Light ? Color.FromArgb(244, 246, 251) : Color.FromArgb(16, 18, 24);
    }

    private static void DrawLabel(Graphics graphics, string value, Font font, Brush brush, float x, float y, float width, float height) =>
        graphics.DrawString(value, font, brush, new RectangleF(x, y, width, height), new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        });

    private void ApplySize()
    {
        var logical = LogicalHeight();
        var width = (int)Math.Ceiling(UsageBoardLayout.Width * _scale);
        var height = (int)Math.Ceiling(logical * _scale);
        var screen = IsHandleCreated
            ? Screen.FromControl(this).WorkingArea
            : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        if (height > screen.Height - 24 && !_compact)
        {
            _compact = true;
            logical = LogicalHeight();
            height = Math.Min((int)Math.Ceiling(logical * _scale), screen.Height - 24);
        }

        ClientSize = new Size(width, height);
        using var path = Rounded(new RectangleF(0, 0, width, height), UsageBoardLayout.Corner * _scale);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    private int LogicalHeight()
    {
        if (!_compact && _ledgerClosed.Count == 0) return UsageBoardLayout.Height(_board, false);
        var height = UsageBoardLayout.Padding + UsageBoardLayout.HeaderHeight;
        foreach (var provider in _board.Providers)
        {
            var card = UsageBoardLayout.CardHeight(provider, _compact);
            if (!_compact && _ledgerClosed.Contains(provider.ProviderId) && UsageBoardLayout.HasLedger(provider))
            {
                card -= UsageBoardLayout.LedgerHeight - 22;
            }

            height += card + UsageBoardLayout.CardGap;
        }

        return Math.Max(height + UsageBoardLayout.Padding - UsageBoardLayout.CardGap, 188);
    }

    private void EnsureFonts()
    {
        _titleFont ??= UiFont(22, FontStyle.Bold);
        _bodyFont ??= UiFont(13, FontStyle.Bold);
        _smallFont ??= UiFont(11, FontStyle.Regular);
    }

    private static Font UiFont(float pixels, FontStyle style)
    {
        foreach (var name in new[] { "Segoe UI Variable Display", "Segoe UI Variable", "Segoe UI" })
        {
            try
            {
                return new Font(name, pixels, style, GraphicsUnit.Pixel);
            }
            catch (ArgumentException)
            {
                continue;
            }
        }

        return new Font(FontFamily.GenericSansSerif, pixels, style, GraphicsUnit.Pixel);
    }

    private static Color ColorOf(UsageColor color) => Color.FromArgb(color.R, color.G, color.B);

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
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
}
