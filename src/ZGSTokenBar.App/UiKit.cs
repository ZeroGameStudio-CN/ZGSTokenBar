using System.Drawing.Drawing2D;

namespace ZGSTokenBar.App;

/// <summary>
/// The shared visual language of every surface: taskbar hover cards, popovers, the Router panel and
/// settings. Values are logical pixels at 96 DPI; multiply by the surface scale.
/// </summary>
internal static class UiTokens
{
    // Spacing scale.
    internal const float Space1 = 2, Space2 = 4, Space3 = 8, Space4 = 12, Space5 = 16, Space6 = 24;

    // Corner radii: pills and chips, interactive controls, inner cards, outer panels.
    internal const float RadiusPill = 5, RadiusControl = 6, RadiusCard = 8, RadiusPanel = 10;

    // Type scale (pixel sizes) for captions, body text, section titles and hero numbers.
    internal const float FontCaption = 10, FontBody = 11, FontTitle = 12.5f, FontDisplay = 16;

    // Row rhythm shared by lists, grids and metric rows.
    internal const float RowHeight = 26, PillHeight = 23, HeaderHeight = 30;

    internal static readonly Color Text = Color.FromArgb(224, 232, 242);
    internal static readonly Color Muted = Color.FromArgb(142, 155, 174);
    internal static readonly Color Disabled = Color.FromArgb(104, 117, 135);
    internal static readonly Color Accent = Color.FromArgb(139, 187, 255);

    /// <summary>Semantic state colours, one meaning everywhere (queue, history, quota, health).</summary>
    internal static Color State(string? tone) => tone switch
    {
        "positive" or "running" or "completed" => Color.FromArgb(111, 213, 173),
        "warning" or "cleanup" or "cancelled" => Color.FromArgb(232, 191, 113),
        "negative" or "attention" or "failed" or "danger" => Color.FromArgb(241, 145, 151),
        "idle" or "pending" or "offline" => Color.FromArgb(155, 168, 185),
        _ => Accent,
    };

    internal static Color Mix(Color first, Color second, float amount) => Color.FromArgb(
        (int)(first.R + (second.R - first.R) * amount),
        (int)(first.G + (second.G - first.G) * amount),
        (int)(first.B + (second.B - first.B) * amount));

    /// <summary>Card fill, raised input fill and hairline border derived from a surface background.</summary>
    internal static Color Surface(Color background) => Mix(background, Color.White, .035f);
    internal static Color Input(Color background) => Mix(background, Color.White, .065f);
    internal static Color Border(Color background) => Mix(background, Color.White, .10f);
}

/// <summary>Reusable drawing components built on <see cref="UiTokens"/>.</summary>
internal static class UiPaint
{
    /// <summary>The one rounded-rectangle path used by every surface.</summary>
    internal static GraphicsPath RoundedPath(RectangleF bounds, float radius)
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

    /// <summary>Integer variant with a 2 px minimum corner, for pixel-snapped controls.</summary>
    internal static GraphicsPath RoundedPath(Rectangle bounds, int radius, bool clampToBounds = true)
    {
        var diameter = Math.Max(2, clampToBounds ? Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)) : radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Card shell: rounded fill with a hairline border.</summary>
    internal static void CardShell(Graphics g, RectangleF bounds, Color fill, Color border, float radius, float borderWidth = 1)
    {
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (border.A > 0 && borderWidth > 0)
        {
            using var pen = new Pen(border, borderWidth);
            g.DrawPath(pen, path);
        }

        g.SmoothingMode = smoothing;
    }

    /// <summary>Status pill / badge: tinted fill, accent text, optional leading dot.</summary>
    internal static void Pill(Graphics g, RectangleF bounds, string text, Font font, Color accent, Color background, float scale, bool dot = false)
    {
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundedPath(bounds, UiTokens.RadiusPill * scale))
        using (var fill = new SolidBrush(UiTokens.Mix(background, accent, .10f)))
        {
            g.FillPath(fill, path);
        }

        var textBounds = Rectangle.Round(bounds);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        if (dot)
        {
            var size = 4.4f * scale;
            using var brush = new SolidBrush(accent);
            g.FillEllipse(brush, bounds.X + UiTokens.Space3 * scale, bounds.Y + bounds.Height / 2 - size / 2, size, size);
            var inset = (int)(18 * scale);
            textBounds = new Rectangle(textBounds.X + inset, textBounds.Y, Math.Max(0, textBounds.Width - inset - (int)(2 * scale)), textBounds.Height);
            flags |= TextFormatFlags.Left;
        }
        else
        {
            flags |= TextFormatFlags.HorizontalCenter;
        }

        TextRenderer.DrawText(g, text, font, textBounds, accent, flags);
        g.SmoothingMode = smoothing;
    }

    /// <summary>Preferred pill size for <paramref name="text"/>.</summary>
    internal static SizeF PillSize(string text, Font font, float scale, bool dot = false) =>
        new(TextRenderer.MeasureText(text, font).Width + (dot ? 26 : 16) * scale, UiTokens.PillHeight * scale);

    /// <summary>Horizontal progress bar: rounded track with a rounded fill of <paramref name="fraction"/>.</summary>
    internal static void ProgressBar(Graphics g, RectangleF bounds, double? fraction, Color fill, Color track)
    {
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var radius = bounds.Height / 2;
        using (var path = RoundedPath(bounds, radius))
        using (var brush = new SolidBrush(track))
        {
            g.FillPath(brush, path);
        }

        if (fraction is { } value && value > 0)
        {
            var width = (float)(bounds.Width * Math.Clamp(value, 0, 1));
            using var path = RoundedPath(new RectangleF(bounds.X, bounds.Y, Math.Max(bounds.Height, width), bounds.Height), radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        g.SmoothingMode = smoothing;
    }

    /// <summary>
    /// Progress ring starting at 12 o'clock, clockwise. <paramref name="bounds"/> is the stroke's
    /// centre line; the caller owns the smoothing mode.
    /// </summary>
    internal static void Ring(Graphics g, RectangleF bounds, double? fraction, Color fill, Color track, float thickness)
    {
        using (var pen = new Pen(track, thickness))
        {
            g.DrawEllipse(pen, bounds);
        }

        if (fraction is not { } value || value <= 0) return;
        using var arc = new Pen(fill, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (value >= 1) g.DrawEllipse(arc, bounds);
        else g.DrawArc(arc, bounds, -90, (float)(360 * value));
    }

    /// <summary>Hairline section divider.</summary>
    internal static void Divider(Graphics g, float left, float right, float y, Color color)
    {
        using var pen = new Pen(color, 1);
        g.DrawLine(pen, left, y, right, y);
    }

    /// <summary>Section header: title on the left, muted detail after it, vertically centred.</summary>
    internal static void SectionHeader(Graphics g, Rectangle bounds, string title, string? detail, Font titleFont, Font detailFont, Color titleColor, Color mutedColor, float scale)
    {
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, title, titleFont, bounds, titleColor, flags);
        if (string.IsNullOrEmpty(detail)) return;
        var titleWidth = TextRenderer.MeasureText(g, title, titleFont, bounds.Size, flags).Width;
        var gap = (int)(UiTokens.Space3 * scale);
        var rest = new Rectangle(bounds.X + titleWidth + gap, bounds.Y, Math.Max(0, bounds.Width - titleWidth - gap), bounds.Height);
        TextRenderer.DrawText(g, detail, detailFont, rest, mutedColor, flags);
    }

    /// <summary>Label / value row: muted label left, value right-aligned.</summary>
    internal static void MetricRow(Graphics g, Rectangle bounds, string label, string value, Font font, Color labelColor, Color valueColor)
    {
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, label, font, bounds, labelColor, flags | TextFormatFlags.Left);
        TextRenderer.DrawText(g, value, font, bounds, valueColor, flags | TextFormatFlags.Right);
    }
}
