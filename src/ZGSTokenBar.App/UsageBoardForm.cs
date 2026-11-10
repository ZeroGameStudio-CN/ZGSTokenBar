using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class UsageBoardForm : Form, IPluginPopoverContent
{
    private readonly float _scale;
    private readonly bool _chinese;
    private UsageTheme _theme;
    private UsageBoard _board = UsageBoard.Empty;
    private bool _compact;
    private RectangleF _themeButton;
    private Font? _titleFont;
    private Font? _bodyFont;
    private Font? _smallFont;

    public UsageBoardForm(float scale, bool chinese, bool light)
    {
        _scale = scale <= 0 ? 1 : scale;
        _chinese = chinese;
        _theme = light ? UsageThemes.Light : UsageThemes.Dark;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
        MouseClick += (_, args) =>
        {
            if (_themeButton.Contains(args.Location))
            {
                _theme = _theme.Id == "light" ? UsageThemes.Dark : UsageThemes.Light;
                ApplySize();
                Invalidate();
            }
        };
    }

    public bool InteractionOpen => false;

    public bool Light => _theme.Id == "light";

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

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.ScaleTransform(_scale, _scale);
        EnsureFonts();
        var theme = _theme;
        using var canvas = new SolidBrush(ColorOf(theme.Canvas));
        graphics.FillRectangle(canvas, 0, 0, UsageBoardLayout.Width, LogicalHeight());
        using var title = new SolidBrush(ColorOf(theme.Text));
        using var muted = new SolidBrush(ColorOf(theme.Muted));
        DrawLabel(graphics, _chinese ? "用量" : "Usage", _titleFont!, title, 20, 10, 220, 24);
        DrawLabel(graphics, _chinese ? "本地只读 · 账号分组" : "Local read-only · grouped accounts", _smallFont!, muted, 20, 34, 260, 16);
        _themeButton = new RectangleF(UsageBoardLayout.Width - 96, 16, 76, 22);
        var button = new RectangleF(_themeButton.X * _scale, _themeButton.Y * _scale, _themeButton.Width * _scale, _themeButton.Height * _scale);
        _themeButton = button;
        graphics.ResetTransform();
        using var buttonFill = new SolidBrush(ColorOf(theme.Card));
        graphics.FillRoundedRectangle(buttonFill, new RectangleF(button.X, button.Y, button.Width, button.Height), 8 * _scale);
        graphics.ScaleTransform(_scale, _scale);
        DrawLabel(
            graphics,
            _theme.Id == "light" ? (_chinese ? "深色" : "Dark") : (_chinese ? "浅色" : "Light"),
            _smallFont!,
            title,
            UsageBoardLayout.Width - 90,
            18,
            64,
            18);
        var y = (float)UsageBoardLayout.HeaderHeight;
        foreach (var provider in _board.Providers)
        {
            using var accent = new SolidBrush(ColorOf(UsageColor.Parse(provider.Accent)));
            graphics.FillEllipse(accent, 20, y + 6, 8, 8);
            DrawLabel(graphics, provider.DisplayName, _bodyFont!, title, 34, y + 2, 250, 18);
            DrawLabel(graphics, provider.Accounts.Count.ToString(), _smallFont!, muted, 360, y + 3, 40, 16);
            y += UsageBoardLayout.SectionHeight;
            foreach (var account in provider.Accounts)
            {
                var row = _compact ? UsageBoardLayout.CompactRowHeight : UsageBoardLayout.ExpandedCardHeight;
                DrawAccount(graphics, theme, account, 16, y, UsageBoardLayout.Width - 32, row - 2);
                y += row + UsageBoardLayout.CardGap;
            }
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

    private void DrawAccount(
        Graphics graphics,
        UsageTheme theme,
        UsageAccountSnapshot account,
        float x,
        float y,
        float width,
        float height)
    {
        using var fill = new SolidBrush(ColorOf(theme.Card));
        using var border = new Pen(ColorOf(theme.Border));
        var bounds = new RectangleF(x, y, width, height);
        graphics.FillRoundedRectangle(fill, bounds, 12);
        using (var outline = Rounded(bounds, 12)) graphics.DrawPath(border, outline);
        var window = UsageFormatting.Primary(account);
        var remaining = UsageFormatting.Remaining(window);
        using var text = new SolidBrush(ColorOf(theme.Text));
        using var muted = new SolidBrush(ColorOf(theme.Muted));
        DrawLabel(graphics, account.Label, _bodyFont!, text, x + 12, y + 6, width - 78, 16);
        var percent = UsageFormatting.Percent(remaining);
        using var percentBrush = new SolidBrush(remaining is { } value ? ColorOf(UsageThemes.ForRemaining(value)) : ColorOf(theme.Muted));
        DrawLabel(graphics, percent, _bodyFont!, percentBrush, x + width - 64, y + 6, 52, 16);
        var track = new RectangleF(x + 12, y + 26, width - 24, 6);
        using var trackBrush = new SolidBrush(ColorOf(theme.Track));
        graphics.FillRoundedRectangle(trackBrush, track, 3);
        if (remaining is { } left && left > 0)
        {
            using var active = new SolidBrush(ColorOf(UsageThemes.ForRemaining(left)));
            graphics.FillRoundedRectangle(active, new RectangleF(track.X, track.Y, Math.Max(4, track.Width * (float)(left / 100)), track.Height), 3);
        }

        if (_compact) return;
        var caption = window?.Label ?? UsageFormatting.Error(account.ErrorKey, _chinese);
        DrawLabel(graphics, caption, _smallFont!, muted, x + 12, y + 38, width / 2, 14);
        DrawLabel(graphics, UsageFormatting.Reset(window?.ResetsAt, DateTimeOffset.Now, _chinese), _smallFont!, muted, x + width / 2, y + 38, width / 2 - 16, 14);
        var detailY = y + 56;
        if (account.Credits?.PrepaidBalance is { } prepaid)
        {
            DrawLabel(graphics, (_chinese ? "预付 " : "Prepaid ") + UsageFormatting.Money(account.Credits.Currency, prepaid), _smallFont!, text, x + 12, detailY, width - 24, 14);
            detailY += 16;
        }
        else if (account.Credits?.OnDemandUsed is { } spent)
        {
            var cap = account.Credits.OnDemandCap is { } limit ? " / " + UsageFormatting.Money(account.Credits.Currency, limit) : "";
            DrawLabel(graphics, (_chinese ? "按量 " : "On-demand ") + UsageFormatting.Money(account.Credits.Currency, spent) + cap, _smallFont!, text, x + 12, detailY, width - 24, 14);
            detailY += 16;
        }

        if (account.Ledger is { } ledger)
        {
            DrawLabel(graphics, Period(_chinese ? "今日" : "Today", ledger.Today), _smallFont!, muted, x + 12, detailY, 120, 14);
            DrawLabel(graphics, Period("7d", ledger.SevenDays), _smallFont!, muted, x + 132, detailY, 100, 14);
            DrawLabel(graphics, Period("30d", ledger.ThirtyDays), _smallFont!, muted, x + 240, detailY, 120, 14);
            detailY += 16;
        }

        var chipX = x + 12;
        foreach (var product in account.Products.Take(3))
        {
            DrawLabel(graphics, product.Product + " " + product.Percent.ToString("0.#"), _smallFont!, muted, chipX, detailY, 110, 14);
            chipX += 112;
        }
    }

    private static string Period(string label, UsageLedgerPeriod period)
    {
        var cost = period.ApiEquivalentCost is { } value ? " " + UsageFormatting.Money("USD", value) : "";
        return label + " " + UsageFormatting.Tokens(period.Tokens) + cost;
    }

    private static void DrawLabel(Graphics graphics, string value, Font font, Brush brush, float x, float y, float width, float height) =>
        graphics.DrawString(value, font, brush, new RectangleF(x, y, width, height), new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        });

    private void ApplySize()
    {
        var logical = UsageBoardLayout.Height(_board, _compact);
        var width = (int)Math.Ceiling(UsageBoardLayout.Width * _scale);
        var height = (int)Math.Ceiling(logical * _scale);
        var screen = IsHandleCreated
            ? Screen.FromControl(this).WorkingArea
            : Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        if (height > screen.Height - 24)
        {
            _compact = true;
            logical = UsageBoardLayout.Height(_board, true);
            height = Math.Min((int)Math.Ceiling(logical * _scale), screen.Height - 24);
        }

        ClientSize = new Size(width, height);
        BackColor = ColorOf(_theme.Canvas);
    }

    private int LogicalHeight() => UsageBoardLayout.Height(_board, _compact);

    private void EnsureFonts()
    {
        _titleFont ??= new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        _bodyFont ??= new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        _smallFont ??= new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static Color ColorOf(UsageColor color) => Color.FromArgb(color.R, color.G, color.B);

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
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
