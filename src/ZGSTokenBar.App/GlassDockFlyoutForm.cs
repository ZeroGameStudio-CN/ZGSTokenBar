using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class GlassDockFlyoutForm : Form
{
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private readonly List<(RectangleF Bounds, Action Action)> _hits = [];
    private GlassDockFlyoutModel _model = GlassDockFlyoutModel.Empty;
    private float _scale = 1;
    private bool _light;
    private bool _chinese;
    private Action? _refresh;
    private Action? _settings;
    private Action? _open;
    private Action? _leaveIdle;

    public GlassDockFlyoutForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        MouseLeave += (_, _) => _leaveIdle?.Invoke();
        MouseClick += (_, args) =>
        {
            var point = new PointF(args.X / _scale, args.Y / _scale);
            foreach (var hit in _hits)
            {
                if (!hit.Bounds.Contains(point)) continue;
                hit.Action();
                return;
            }

            _open?.Invoke();
        };
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

    public void SetLeaveIdle(Action? leaveIdle) => _leaveIdle = leaveIdle;

    public void Present(
        GlassDockFlyoutModel model,
        Rectangle anchor,
        float scale,
        bool light,
        bool chinese,
        Action refresh,
        Action settings,
        Action open,
        Form owner)
    {
        _model = model;
        _scale = scale <= 0 ? 1 : scale;
        _light = light;
        _chinese = chinese;
        _refresh = refresh;
        _settings = settings;
        _open = open;
        var logical = Measure(model);
        ClientSize = new Size(Scale(GlassDockLayout.FlyoutWidth), Scale(logical));
        var screen = Screen.FromRectangle(anchor).WorkingArea;
        var x = Math.Clamp(anchor.Left, screen.Left, Math.Max(screen.Left, screen.Right - Width));
        var y = anchor.Top - Height + Scale(2);
        if (y < screen.Top) y = Math.Min(screen.Bottom - Height, anchor.Bottom + Scale(6));
        Location = new Point(x, y);
        if (!Visible) Show(owner);
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = WindowBackdrop.TryApply(Handle, _light, transient: true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.ScaleTransform(_scale, _scale);
        _hits.Clear();
        var width = GlassDockLayout.FlyoutWidth;
        var height = Measure(_model);
        var bounds = new RectangleF(0.5f, 0.5f, width - 1, height - 1);
        using var path = Rounded(bounds, 16);
        using var wash = new LinearGradientBrush(
            bounds,
            _light ? Color.FromArgb(228, 255, 255, 255) : Color.FromArgb(210, 40, 32, 48),
            _light ? Color.FromArgb(210, 244, 246, 251) : Color.FromArgb(220, 18, 16, 28),
            LinearGradientMode.Vertical);
        using var border = new Pen(_light ? Color.FromArgb(180, 255, 255, 255) : Color.FromArgb(80, 255, 255, 255), 1f);
        graphics.FillPath(wash, path);
        graphics.DrawPath(border, path);
        using var title = new Font("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var strong = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(_light ? Color.FromArgb(28, 32, 40) : Color.White);
        using var muted = new SolidBrush(_light ? Color.FromArgb(120, 28, 32, 40) : Color.FromArgb(150, 255, 255, 255));
        graphics.DrawString(_chinese ? "用量总览" : "Usage", title, ink, 14, 10);
        var refresh = new RectangleF(width - 92, 10, 36, 16);
        var settings = new RectangleF(width - 52, 10, 36, 16);
        DrawChip(graphics, refresh, _chinese ? "刷新" : "Refresh", small, muted);
        DrawChip(graphics, settings, _chinese ? "设置" : "Settings", small, muted);
        if (_refresh is { } refreshAction) _hits.Add((refresh, refreshAction));
        if (_settings is { } settingsAction) _hits.Add((settings, settingsAction));
        var y = 34f;
        foreach (var group in _model.Groups)
        {
            var rows = Math.Max(1, group.Accounts.Count);
            var block = 28 + rows * 18;
            using var card = Rounded(new RectangleF(10, y, width - 20, block), 12);
            using var cardFill = new SolidBrush(_light ? Color.FromArgb(18, 20, 24, 32) : Color.FromArgb(16, 255, 255, 255));
            graphics.FillPath(cardFill, card);
            graphics.DrawString(group.Title.ToUpperInvariant(), small, muted, 34, y + 7);
            var row = y + 26;
            var accounts = group.Accounts.Count == 0
                ? [new GlassDockAccountLine(group.Title, null, "", null)]
                : group.Accounts;
            foreach (var account in accounts)
            {
                graphics.DrawString(account.Label, body, ink, 18, row);
                var label = account.BalanceText ?? UsageFormatting.Percent(account.RemainingPercent);
                var color = Band(account.RemainingPercent);
                using var brush = new SolidBrush(color);
                graphics.DrawString(label, strong, brush, width - 78, row - 1);
                if (account.RemainingPercent is { } remaining)
                {
                    var bar = new RectangleF(78, row + 6, width - 170, 3.5f);
                    using var track = new SolidBrush(_light ? Color.FromArgb(30, 20, 24, 32) : Color.FromArgb(36, 255, 255, 255));
                    graphics.FillRectangle(track, bar);
                    graphics.FillRectangle(brush, new RectangleF(bar.X, bar.Y, bar.Width * (float)(Math.Clamp(remaining, 0, 100) / 100d), bar.Height));
                }

                row += 18;
            }

            y += block + 6;
        }

        graphics.DrawString(_chinese ? "系统" : "System", small, muted, 14, y + 2);
        y += 18;
        DrawMeter(graphics, 14, y, 90, "CPU", _model.Cpu, body, small, ink, muted);
        DrawMeter(graphics, 112, y, 90, "RAM", _model.Memory, body, small, ink, muted);
        DrawMeter(graphics, 210, y, 90, "GPU", _model.Gpu, body, small, ink, muted);
        y += 28;
        var rate = _model.TokensPerSecond is { } tokens ? "~" + tokens.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " tok/s" : "— tok/s";
        graphics.DrawString(rate, body, ink, 14, y);
        graphics.DrawString(_model.RouterWarning ? (_chinese ? "Router 警告" : "Router warning") : (_chinese ? "Router 正常" : "Router healthy"), body, muted, 150, y);
        if (_model.Ledger is { } ledger)
        {
            y += 20;
            graphics.DrawString(
                (_chinese ? "今日 " : "Today ") + UsageFormatting.Tokens(ledger.Today.Tokens)
                + (_chinese ? "   7天 " : "   7d ") + UsageFormatting.Tokens(ledger.SevenDays.Tokens)
                + (_chinese ? "   30天 " : "   30d ") + UsageFormatting.Tokens(ledger.ThirtyDays.Tokens),
                small,
                muted,
                14,
                y);
        }
    }

    private static int Measure(GlassDockFlyoutModel model)
    {
        var height = 40;
        foreach (var group in model.Groups)
        {
            height += 28 + Math.Max(1, group.Accounts.Count) * 18 + 6;
        }

        height += 18 + 28 + 22;
        if (model.Ledger is not null) height += 24;
        return height + 12;
    }

    private void DrawMeter(Graphics graphics, float x, float y, float width, string label, double? value, Font body, Font small, Brush ink, Brush muted)
    {
        graphics.DrawString(label, small, muted, x, y);
        graphics.DrawString(value is { } percent ? percent.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" : "—", body, ink, x + width - 36, y - 1);
        var bar = new RectangleF(x, y + 14, width, 3.5f);
        using var track = new SolidBrush(_light ? Color.FromArgb(30, 20, 24, 32) : Color.FromArgb(36, 255, 255, 255));
        graphics.FillRectangle(track, bar);
        if (value is not { } amount) return;
        using var fill = new SolidBrush(LoadColor(amount));
        graphics.FillRectangle(fill, new RectangleF(bar.X, bar.Y, bar.Width * (float)Math.Clamp(amount / 100d, 0, 1), bar.Height));
    }

    private static void DrawChip(Graphics graphics, RectangleF bounds, string text, Font font, Brush brush)
    {
        using var path = Rounded(bounds, 8);
        using var fill = new SolidBrush(Color.FromArgb(24, 255, 255, 255));
        graphics.FillPath(fill, path);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static Color Band(double? remaining)
    {
        var color = GlassDockLayout.BandColor(remaining);
        return Color.FromArgb(color.R, color.G, color.B);
    }

    private static Color LoadColor(double value) => value switch
    {
        > 80 => Color.FromArgb(255, 90, 90),
        > 50 => Color.FromArgb(245, 193, 74),
        _ => Color.FromArgb(70, 229, 154),
    };

    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * _scale));

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
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
}

internal sealed record GlassDockFlyoutModel(
    IReadOnlyList<GlassDockProviderGroup> Groups,
    double? Cpu,
    double? Memory,
    double? Gpu,
    double? TokensPerSecond,
    bool RouterWarning,
    UsageLedger? Ledger)
{
    public static GlassDockFlyoutModel Empty { get; } = new([], null, null, null, null, false, null);
}
