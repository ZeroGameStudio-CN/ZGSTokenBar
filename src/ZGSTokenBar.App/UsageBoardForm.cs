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
    private Font? _titleFont;
    private Font? _bodyFont;
    private Font? _smallFont;
    private Font? _strongFont;
    private Font? _figureFont;

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
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.ScaleTransform(_scale, _scale);
        EnsureFonts();
        _hits.Clear();
        var palette = Palette.For(_theme.Light);
        var width = UsageBoardLayout.Width;
        var height = LogicalHeight();
        PaintWindow(graphics, palette, width, height);
        using var text = new SolidBrush(palette.Text);
        using var muted = new SolidBrush(palette.Muted);
        DrawLabel(graphics, _chinese ? "用量" : "Usage", _titleFont!, text, 22, 17, 220, 28);
        DrawLabel(graphics, UsageFormatting.Overall(_board, _chinese), _smallFont!, muted, 23, 47, 260, 16);
        const float chipY = 21f;
        AddChip(graphics, palette, width - 172, chipY, 48, _chinese ? "刷新" : "Refresh", () => _refresh?.Invoke());
        AddChip(graphics, palette, width - 118, chipY, 48, _theme.Light ? (_chinese ? "深色" : "Dark") : (_chinese ? "浅色" : "Light"), ToggleTheme);
        AddChip(graphics, palette, width - 64, chipY, 44, _chinese ? "设置" : "Settings", () => _settings?.Invoke());
        var y = (float)(UsageBoardLayout.Padding + UsageBoardLayout.HeaderHeight);
        if (_board.Providers.Count == 0)
        {
            DrawLabel(graphics, _chinese ? "还没有可显示的用量" : "No usage to show yet", _bodyFont!, muted, 22, y + 20, width - 44, 20);
        }
        foreach (var provider in _board.Providers)
        {
            var cardHeight = UsageBoardLayout.CardHeight(provider, _compact);
            if (!_compact && _ledgerClosed.Contains(provider.ProviderId) && UsageBoardLayout.HasLedger(provider))
            {
                cardHeight -= UsageBoardLayout.LedgerHeight - 22;
            }

            PaintProvider(graphics, palette, provider, 16, y, width - 32, cardHeight);
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
            _strongFont?.Dispose();
            _figureFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Solid colours matching the taskbar dock's dark card: no glass, hairline borders, one
    /// accent per state. Remaining-quota colours use the dock's bands (red below 20%).
    /// </summary>
    private sealed record Palette(
        Color Canvas, Color Card, Color WindowBorder, Color CardBorder, Color Divider,
        Color Text, Color Muted, Color Track, Color ChipFill, Color ChipBorder,
        Color Red, Color Amber, Color Green)
    {
        internal static readonly Palette Dark = new(
            Color.FromArgb(17, 19, 25), Color.FromArgb(25, 28, 36), Color.FromArgb(38, 255, 255, 255),
            Color.FromArgb(16, 255, 255, 255), Color.FromArgb(14, 255, 255, 255),
            Color.FromArgb(241, 245, 249), Color.FromArgb(148, 163, 184), Color.FromArgb(28, 255, 255, 255),
            Color.FromArgb(14, 255, 255, 255), Color.FromArgb(30, 255, 255, 255),
            Color.FromArgb(255, 90, 95), Color.FromArgb(252, 196, 64), Color.FromArgb(52, 211, 153));

        internal static readonly Palette Light = new(
            Color.FromArgb(246, 247, 250), Color.White, Color.FromArgb(36, 15, 23, 42),
            Color.FromArgb(22, 15, 23, 42), Color.FromArgb(16, 15, 23, 42),
            Color.FromArgb(15, 23, 42), Color.FromArgb(100, 116, 139), Color.FromArgb(20, 15, 23, 42),
            Color.FromArgb(255, 255, 255), Color.FromArgb(34, 15, 23, 42),
            Color.FromArgb(220, 38, 38), Color.FromArgb(194, 120, 3), Color.FromArgb(5, 150, 105));

        internal static Palette For(bool light) => light ? Light : Dark;

        internal Color ForRemaining(double? remaining) => GlassDockLayout.Band(remaining) switch
        {
            "red" => Red,
            "amber" => Amber,
            "green" => Green,
            _ => Muted,
        };
    }

    private void PaintProvider(Graphics graphics, Palette palette, UsageProviderSnapshot provider, float x, float y, float width, float height)
    {
        var card = new RectangleF(x, y, width, height);
        using (var path = Rounded(card, CardCorner))
        using (var fill = new SolidBrush(palette.Card))
        using (var border = new Pen(palette.CardBorder, 1))
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        using var text = new SolidBrush(palette.Text);
        using var muted = new SolidBrush(palette.Muted);
        DrawProviderIcon(graphics, provider, new RectangleF(x + 14, y + 15, 28, 28));
        DrawLabel(graphics, provider.DisplayName, _bodyFont!, text, x + 52, y + 13, width - 170, 18);
        var plan = provider.Accounts.Select(account => account.Plan).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var accounts = _chinese ? $"{provider.Accounts.Count} 个账号" : $"{provider.Accounts.Count} account{(provider.Accounts.Count == 1 ? "" : "s")}";
        DrawLabel(graphics, plan is null ? accounts : accounts + " · " + plan, _smallFont!, muted, x + 52, y + 32, width - 170, 15);
        var tight = Tightest(provider);
        var tone = palette.ForRemaining(tight);
        using var toneBrush = new SolidBrush(tone);
        var percentText = UsageFormatting.Percent(tight);
        var percentWidth = graphics.MeasureString(percentText, _figureFont!, PointF.Empty, StringFormat.GenericTypographic).Width;
        var percentX = x + width - 16 - percentWidth;
        graphics.DrawString(percentText, _figureFont!, toneBrush, percentX, y + 17, StringFormat.GenericTypographic);
        DrawRing(graphics, palette, new RectangleF(percentX - 30, y + 16, 22, 22), tight);

        var rowY = y + UsageBoardLayout.CardPad + UsageBoardLayout.CardHeader;
        var rowHeight = _compact ? UsageBoardLayout.CompactAccountRow : UsageBoardLayout.AccountRow;
        using var divider = new Pen(palette.Divider, 1);
        foreach (var account in provider.Accounts)
        {
            graphics.DrawLine(divider, x + 14, rowY - 5, x + width - 14, rowY - 5);
            PaintAccount(graphics, palette, account, x + 14, rowY, width - 28, rowHeight - 6);
            rowY += rowHeight;
        }

        if (_compact || !UsageBoardLayout.HasLedger(provider)) return;
        var open = !_ledgerClosed.Contains(provider.ProviderId);
        graphics.DrawLine(divider, x + 14, rowY - 5, x + width - 14, rowY - 5);
        var ledgerBounds = new RectangleF(x + 14, rowY, 70, 20);
        _hits.Add((ledgerBounds, () =>
        {
            if (!_ledgerClosed.Add(provider.ProviderId)) _ledgerClosed.Remove(provider.ProviderId);
            ApplySize();
            Invalidate();
        }));
        DrawLabel(graphics, (open ? "▾ " : "▸ ") + (_chinese ? "账本" : "Ledger"), _smallFont!, muted, ledgerBounds.X, ledgerBounds.Y + 1, 80, 16);
        if (!open) return;
        var ledger = provider.Accounts.Select(account => account.Ledger).FirstOrDefault(item => item is not null);
        if (ledger is null) return;
        var column = (width - 28 - 84) / 3f;
        var columnX = x + 14 + 84;
        foreach (var (label, period) in new[]
                 {
                     (_chinese ? "今日" : "Today", ledger.Today),
                     ("7d", ledger.SevenDays),
                     ("30d", ledger.ThirtyDays),
                 })
        {
            DrawLabel(graphics, label, _smallFont!, muted, columnX, rowY + 1, column - 6, 14);
            DrawLabel(graphics, Period(period), _strongFont!, text, columnX, rowY + 17, column - 6, 17);
            columnX += column;
        }
    }

    private void PaintAccount(Graphics graphics, Palette palette, UsageAccountSnapshot account, float x, float y, float width, float height)
    {
        var window = UsageFormatting.Primary(account);
        var remaining = UsageFormatting.Remaining(window);
        using var text = new SolidBrush(palette.Text);
        using var muted = new SolidBrush(palette.Muted);
        DrawLabel(graphics, account.Label, _strongFont!, text, x, y, width - 64, 17);
        if (!_compact)
        {
            var caption = window?.Label ?? UsageFormatting.Error(account.ErrorKey, _chinese);
            DrawLabel(graphics, caption + " · " + UsageFormatting.Reset(window?.ResetsAt, DateTimeOffset.Now, _chinese), _smallFont!, muted, x, y + 17, width - 64, 14);
        }

        var tone = palette.ForRemaining(remaining);
        var bar = new RectangleF(x, y + (_compact ? 22 : 36), width - 60, 5);
        using var track = new SolidBrush(palette.Track);
        graphics.FillRoundedRectangle(track, bar, 2.5f);
        if (remaining is { } left && left > 0)
        {
            using var fill = new SolidBrush(tone);
            graphics.FillRoundedRectangle(fill, new RectangleF(bar.X, bar.Y, Math.Max(5, bar.Width * (float)(Math.Min(100, left) / 100)), bar.Height), 2.5f);
        }

        using var percentBrush = new SolidBrush(tone);
        var percent = UsageFormatting.Percent(remaining);
        var percentWidth = graphics.MeasureString(percent, _strongFont!, PointF.Empty, StringFormat.GenericTypographic).Width;
        graphics.DrawString(percent, _strongFont!, percentBrush, x + width - percentWidth, bar.Y - 6.5f, StringFormat.GenericTypographic);
        _ = height;
    }

    private const float WindowCorner = 14f;
    private const float CardCorner = 11f;

    private static void PaintWindow(Graphics graphics, Palette palette, int width, int height)
    {
        graphics.Clear(palette.Canvas);
        var edge = new RectangleF(.5f, .5f, width - 1f, height - 1f);
        using var path = Rounded(edge, WindowCorner);
        using var border = new Pen(palette.WindowBorder, 1);
        graphics.DrawPath(border, path);
    }

    private void DrawProviderIcon(Graphics graphics, UsageProviderSnapshot provider, RectangleF bounds)
    {
        if (ProviderIcons.For(provider.ProviderId) is { } icon)
        {
            var state = graphics.Save();
            using var clip = new GraphicsPath();
            clip.AddEllipse(bounds);
            graphics.SetClip(clip);
            graphics.DrawImage(icon, bounds);
            graphics.Restore(state);
            return;
        }

        var accent = UsageColor.Parse(provider.Accent);
        using var accentBrush = new SolidBrush(Color.FromArgb(accent.R, accent.G, accent.B));
        graphics.FillEllipse(accentBrush, bounds);
        using var white = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(provider.DisplayName.Length > 0 ? provider.DisplayName[..1] : "?", _strongFont!, white, bounds, format);
    }

    private void AddChip(Graphics graphics, Palette palette, float x, float y, float width, string label, Action action)
    {
        var bounds = new RectangleF(x, y, width, 26);
        using (var path = Rounded(bounds, 8))
        using (var fill = new SolidBrush(palette.ChipFill))
        using (var border = new Pen(palette.ChipBorder, 1))
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        using var text = new SolidBrush(palette.Text);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(label, _smallFont!, text, bounds, format);
        _hits.Add((bounds, action));
    }

    private static void DrawRing(Graphics graphics, Palette palette, RectangleF bounds, double? remaining)
    {
        using var track = new Pen(palette.Track, 3f);
        graphics.DrawEllipse(track, bounds);
        if (remaining is not { } value || value <= 0) return;
        using var pen = new Pen(palette.ForRemaining(value), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (value >= 100) graphics.DrawEllipse(pen, bounds);
        else graphics.DrawArc(pen, bounds, -90, (float)(360 * value / 100));
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

    private static string Period(UsageLedgerPeriod period)
    {
        var cost = period.ApiEquivalentCost is { } value ? " · " + UsageFormatting.Money("USD", value) : "";
        return UsageFormatting.Tokens(period.Tokens) + cost;
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
        // Solid card, like the taskbar dock: no acrylic/glass backdrop.
        BackColor = Palette.For(_theme.Light).Canvas;
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
        using var path = Rounded(new RectangleF(0, 0, width, height), WindowCorner * _scale);
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
        _titleFont ??= UiFont(20, FontStyle.Bold, display: true);
        _bodyFont ??= UiFont(14, FontStyle.Bold);
        _strongFont ??= UiFont(12.5f, FontStyle.Bold);
        _figureFont ??= UiFont(17, FontStyle.Bold, display: true);
        _smallFont ??= UiFont(11.5f, FontStyle.Regular);
    }

    private static Font UiFont(float pixels, FontStyle style, bool display = false)
    {
        foreach (var name in display
                     ? new[] { "Segoe UI Variable Display", "Segoe UI" }
                     : new[] { "Segoe UI Variable Text", "Segoe UI" })
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
