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

    // Semantic colours. Every surface uses these names; the values come from one palette.
    internal static readonly Color TextStrong = UiPalette.Slate100;
    internal static readonly Color Text = UiPalette.Slate200;
    internal static readonly Color TextSecondary = UiPalette.Slate300;
    internal static readonly Color Muted = UiPalette.Slate400;
    internal static readonly Color Disabled = UiPalette.Slate500;
    internal static readonly Color Faint = UiPalette.Slate600;
    internal static readonly Color Accent = UiPalette.Indigo300;
    internal static readonly Color AccentStrong = UiPalette.Indigo400;
    internal static readonly Color Positive = UiPalette.Emerald400;
    internal static readonly Color Warning = UiPalette.Amber400;
    internal static readonly Color Negative = UiPalette.Rose400;
    internal static readonly Color Info = UiPalette.Teal300;

    /// <summary>Fill of filled accent controls (toggles, primary buttons) with white text on top.</summary>
    internal static readonly Color AccentFill = UiPalette.Indigo500;

    // Hover cards and popovers: one solid graphite card with a white hairline, matching the dock.
    internal static readonly Color CardSurface = Color.FromArgb(19, 21, 28);
    internal static readonly Color Hairline = Color.White.WithAlpha(40);

    // Window chrome for full windows (settings): neutral steps from sidebar to borders.
    internal static readonly Color WindowSidebar = Color.FromArgb(18, 18, 20);
    internal static readonly Color WindowFooter = Color.FromArgb(21, 21, 24);
    internal static readonly Color WindowContent = Color.FromArgb(24, 24, 28);
    internal static readonly Color WindowSurface = Color.FromArgb(31, 31, 35);
    internal static readonly Color WindowHover = Color.FromArgb(39, 39, 45);
    internal static readonly Color WindowBorder = Color.FromArgb(44, 44, 49);
    internal static readonly Color WindowScrollThumb = Color.FromArgb(104, 108, 118);

    /// <summary>Semantic state colours, one meaning everywhere (queue, history, quota, health).</summary>
    internal static Color State(string? tone) => tone switch
    {
        "positive" or "running" or "completed" => Positive,
        "warning" or "cleanup" or "cancelled" => Warning,
        "negative" or "attention" or "failed" or "danger" => Negative,
        "idle" or "pending" or "offline" => Muted,
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

/// <summary>
/// The colour scale behind <see cref="UiTokens"/> (Tailwind's slate/indigo/emerald/amber/rose steps,
/// which the popovers already used). Surfaces reference these instead of RGB literals.
/// </summary>
internal static class UiPalette
{
    internal static readonly Color Slate50 = Color.FromArgb(248, 250, 252);
    internal static readonly Color Slate100 = Color.FromArgb(241, 245, 249);
    internal static readonly Color Slate200 = Color.FromArgb(226, 232, 240);
    internal static readonly Color Slate300 = Color.FromArgb(203, 213, 225);
    internal static readonly Color Slate400 = Color.FromArgb(148, 163, 184);
    internal static readonly Color Slate500 = Color.FromArgb(100, 116, 139);
    internal static readonly Color Slate600 = Color.FromArgb(71, 85, 105);
    internal static readonly Color Slate700 = Color.FromArgb(51, 65, 85);
    internal static readonly Color Slate800 = Color.FromArgb(30, 41, 59);
    internal static readonly Color Slate900 = Color.FromArgb(15, 23, 42);
    internal static readonly Color Indigo300 = Color.FromArgb(165, 180, 252);
    internal static readonly Color Indigo400 = Color.FromArgb(129, 140, 248);
    internal static readonly Color Indigo500 = Color.FromArgb(99, 102, 241);
    internal static readonly Color Violet400 = Color.FromArgb(167, 139, 250);
    internal static readonly Color Emerald400 = Color.FromArgb(52, 211, 153);
    internal static readonly Color Emerald600 = Color.FromArgb(5, 150, 105);
    internal static readonly Color Amber400 = Color.FromArgb(251, 191, 36);
    internal static readonly Color Rose400 = Color.FromArgb(251, 113, 133);
    internal static readonly Color Red600 = Color.FromArgb(220, 38, 38);
    internal static readonly Color Teal200 = Color.FromArgb(153, 246, 228);
    internal static readonly Color Teal300 = Color.FromArgb(94, 234, 212);

    /// <summary>The same colour at <paramref name="alpha"/> (0–255).</summary>
    internal static Color WithAlpha(this Color color, int alpha) => Color.FromArgb(alpha, color);
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

    /// <summary>
    /// Horizontal progress rail: rounded track plus a rounded fill of <paramref name="fraction"/>
    /// (0–1). <paramref name="minFill"/> keeps a sliver visible for tiny values. The caller owns the
    /// smoothing mode.
    /// </summary>
    internal static void ProgressBar(Graphics g, RectangleF bounds, double? fraction, Color fill, Color track, float minFill = 0)
    {
        var radius = bounds.Height / 2;
        using (var brush = new SolidBrush(track))
        {
            FillRounded(g, brush, bounds, radius);
        }

        if (fraction is not { } value || value <= 0) return;
        var width = Math.Max(minFill, (float)(bounds.Width * Math.Clamp(value, 0, 1)));
        using var active = new SolidBrush(fill);
        FillRounded(g, active, new RectangleF(bounds.X, bounds.Y, width, bounds.Height), radius);
    }

    /// <summary>Fills a rounded rectangle (a plain rectangle when the radius collapses).</summary>
    internal static void FillRounded(Graphics g, Brush brush, RectangleF bounds, float radius)
    {
        if (Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)) <= 0)
        {
            g.FillRectangle(brush, bounds);
            return;
        }

        using var path = RoundedPath(bounds, radius);
        g.FillPath(brush, path);
    }

    /// <summary>The render quality every hover card and popover paints with.</summary>
    internal static void PrepareQuality(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    /// <summary>
    /// The hover-card surface: a rounded body joined to its pointer tail, filled and outlined with a
    /// hairline (the tail's base stays open so it reads as one shape).
    /// </summary>
    internal static void TailedSurface(Graphics g, RectangleF body, PointF[] tail, Color fill, Color border, float radius)
    {
        using var bodyPath = RoundedPath(RectangleF.Inflate(body, -.5f, -.5f), radius);
        using var surfacePath = new GraphicsPath { FillMode = FillMode.Winding };
        surfacePath.AddPath(bodyPath, false);
        surfacePath.AddPolygon(tail);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border, 1);
        g.FillPath(brush, surfacePath);
        g.DrawPath(pen, bodyPath);
        g.DrawLines(pen, tail);
    }

    [ThreadStatic] private static StringFormat?[]? _formats;

    /// <summary>
    /// Single-line text, vertically centred, aligned by <paramref name="alignment"/>; trimmed with an
    /// ellipsis unless <paramref name="ellipsis"/> is false. Formats are cached per thread.
    /// </summary>
    internal static void Text(Graphics g, string text, Font font, Brush brush, RectangleF bounds, StringAlignment alignment, bool ellipsis = true)
    {
        var formats = _formats ??= new StringFormat?[6];
        var index = (int)alignment * 2 + (ellipsis ? 1 : 0);
        var format = formats[index] ??= new StringFormat
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.Character,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(text, font, brush, bounds, format);
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
