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
    internal static readonly Color Amber200 = Color.FromArgb(253, 230, 138);
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

    /// <summary>
    /// Expected-usage marker on a progress rail: a small pale-amber diamond with a dark core at
    /// <paramref name="fraction"/> of the width (where the fill would end if used evenly).
    /// </summary>
    internal static void PaceMarker(Graphics g, RectangleF rail, double fraction)
    {
        var x = rail.X + rail.Width * (float)Math.Clamp(fraction, 0, 1);
        var y = rail.Y + rail.Height / 2;
        var half = rail.Height / 2 + .75f;
        var core = half * .4f;
        using var brush = new SolidBrush(UiPalette.Amber200);
        using var coreBrush = new SolidBrush(UiPalette.Slate800);
        g.FillPolygon(brush, [new PointF(x, y - half), new PointF(x + half, y), new PointF(x, y + half), new PointF(x - half, y)]);
        g.FillPolygon(coreBrush, [new PointF(x, y - core), new PointF(x + core, y), new PointF(x, y + core), new PointF(x - core, y)]);
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
    /// <summary>The on/off switch shared by owner-drawn cards (Settings uses the same proportions).</summary>
    internal static void Switch(Graphics g, RectangleF bounds, bool on, bool hover)
    {
        var track = on ? UiTokens.AccentFill : hover ? UiPalette.Slate600 : UiPalette.Slate700;
        using (var brush = new SolidBrush(track)) FillRounded(g, brush, bounds, bounds.Height / 2);
        var inset = Math.Max(2f, bounds.Height / 7f);
        var knob = bounds.Height - inset * 2;
        var x = on ? bounds.Right - knob - inset : bounds.X + inset;
        using var knobBrush = new SolidBrush(on ? Color.White : UiPalette.Slate300);
        g.FillEllipse(knobBrush, x, bounds.Y + inset, knob, knob);
    }

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

/// <summary>
/// Small line glyphs drawn with GDI+ (never an emoji font), shared by every hover and panel. Each
/// draws inside <c>bounds</c> (logical pixels, usually 10×10) in one colour; strokes are 1.2 px.
/// </summary>
internal static class UiIcons
{
    private static Pen Stroke(Color color, float width = 1.2f) => new(color, width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    /// <summary>Downward arrow: received traffic.</summary>
    internal static void ArrowDown(Graphics g, RectangleF bounds, Color color)
    {
        using var pen = Stroke(color, 1.3f);
        var x = bounds.X + bounds.Width / 2;
        g.DrawLine(pen, x, bounds.Top + 1, x, bounds.Bottom - 1);
        g.DrawLines(pen, [new PointF(bounds.Left + 1.5f, bounds.Bottom - 4.5f), new PointF(x, bounds.Bottom - 1), new PointF(bounds.Right - 1.5f, bounds.Bottom - 4.5f)]);
    }

    /// <summary>Upward arrow: sent traffic.</summary>
    internal static void ArrowUp(Graphics g, RectangleF bounds, Color color)
    {
        using var pen = Stroke(color, 1.3f);
        var x = bounds.X + bounds.Width / 2;
        g.DrawLine(pen, x, bounds.Bottom - 1, x, bounds.Top + 1);
        g.DrawLines(pen, [new PointF(bounds.Left + 1.5f, bounds.Top + 4.5f), new PointF(x, bounds.Top + 1), new PointF(bounds.Right - 1.5f, bounds.Top + 4.5f)]);
    }

    /// <summary>Paired up/down arrows: network traffic in both directions.</summary>
    internal static void Traffic(Graphics g, RectangleF bounds, Color color)
    {
        var half = bounds.Width / 2;
        ArrowUp(g, new RectangleF(bounds.X - .5f, bounds.Y, half + 1, bounds.Height), color);
        ArrowDown(g, new RectangleF(bounds.X + half - .5f, bounds.Y, half + 1, bounds.Height), color);
    }

    /// <summary>Three rising bars: token volume.</summary>
    internal static void Tokens(Graphics g, RectangleF bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, bounds.Left + 1, bounds.Top + 6, 2, 4);
        g.FillRectangle(brush, bounds.Left + 4.5f, bounds.Top + 3, 2, 7);
        g.FillRectangle(brush, bounds.Left + 8, bounds.Top, 2, 10);
    }

    /// <summary>A small database cylinder: prompt-cache reads.</summary>
    internal static void Cache(Graphics g, RectangleF bounds, Color color)
    {
        using var pen = Stroke(color);
        var left = bounds.Left + 1;
        var right = bounds.Right - 1;
        var width = right - left;
        g.DrawEllipse(pen, left, bounds.Top + .5f, width, 3);
        g.DrawLine(pen, left, bounds.Top + 2, left, bounds.Bottom - 2);
        g.DrawLine(pen, right, bounds.Top + 2, right, bounds.Bottom - 2);
        g.DrawArc(pen, left, bounds.Top + 3.5f, width, 3, 0, 180);
        g.DrawArc(pen, left, bounds.Bottom - 3.5f, width, 3, 0, 180);
    }

    /// <summary>Calendar sheet; <paramref name="today"/> marks the current day with a dot.</summary>
    internal static void Calendar(Graphics g, RectangleF bounds, Color color, bool today = false)
    {
        using var pen = new Pen(color, 1.2f);
        g.DrawRectangle(pen, bounds.X + .5f, bounds.Y + 1.5f, bounds.Width - 1, bounds.Height - 2);
        g.DrawLine(pen, bounds.X + .5f, bounds.Y + 4.5f, bounds.Right - .5f, bounds.Y + 4.5f);
        g.DrawLine(pen, bounds.X + 3, bounds.Y, bounds.X + 3, bounds.Y + 2.5f);
        g.DrawLine(pen, bounds.Right - 3, bounds.Y, bounds.Right - 3, bounds.Y + 2.5f);
        if (!today) return;
        using var dot = new SolidBrush(color);
        g.FillRectangle(dot, bounds.X + bounds.Width / 2 - 1.25f, bounds.Y + 5.75f, 2.5f, 2.5f);
    }

    /// <summary>Clock face: a start time or history.</summary>
    internal static void Clock(Graphics g, RectangleF bounds, Color color)
    {
        using var pen = Stroke(color);
        g.DrawEllipse(pen, bounds.X + .6f, bounds.Y + .6f, bounds.Width - 1.2f, bounds.Height - 1.2f);
        var center = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        g.DrawLines(pen, [new PointF(center.X, bounds.Y + 2.6f), center, new PointF(bounds.Right - 3, center.Y + 1)]);
    }

    /// <summary>Rising trend line with an end dot: quota pace.</summary>
    internal static void Trend(Graphics g, RectangleF bounds, Color color)
    {
        using var pen = Stroke(color, 1.25f);
        g.DrawLines(pen,
        [
            new PointF(bounds.Left + 1, bounds.Bottom - 2),
            new PointF(bounds.Left + 4, bounds.Top + 5),
            new PointF(bounds.Left + 6, bounds.Top + 7),
            new PointF(bounds.Right - 1, bounds.Top + 2),
        ]);
        using var dot = new SolidBrush(color);
        g.FillEllipse(dot, bounds.Right - 2.5f, bounds.Top + .5f, 2.5f, 2.5f);
    }

    /// <summary>Coin: credits.</summary>
    internal static void Credits(Graphics g, RectangleF bounds, Color color)
    {
        using var pen = new Pen(color, 1.2f);
        g.DrawEllipse(pen, bounds.X + .5f, bounds.Y + .5f, bounds.Width - 1, bounds.Height - 1);
        g.DrawLine(pen, bounds.X + bounds.Width / 2, bounds.Y + 2.5f, bounds.X + bounds.Width / 2, bounds.Bottom - 2.5f);
    }

    /// <summary>A card with a circular arrow: reset cards.</summary>
    internal static void ResetCard(Graphics g, RectangleF bounds, Color color)
    {
        using var pen = new Pen(color, 1.2f);
        var card = new RectangleF(bounds.X + .5f, bounds.Y + 1.5f, bounds.Width - 1, bounds.Height - 3);
        using (var path = UiPaint.RoundedPath(card, 1.6f)) g.DrawPath(pen, path);
        g.DrawArc(pen, new RectangleF(card.X + card.Width / 2 - 2.2f, card.Y + card.Height / 2 - 2.2f, 4.4f, 4.4f), -60, 280);
    }

    /// <summary>Push pin: a pinned popover.</summary>
    internal static void Pin(Graphics g, RectangleF bounds, Color color)
    {
        using var pen = Stroke(color);
        g.DrawLines(pen,
        [
            new PointF(bounds.Left + 4, bounds.Top + 1), new PointF(bounds.Right - 1, bounds.Top + 6),
            new PointF(bounds.Right - 3, bounds.Top + 6), new PointF(bounds.Left + 5, bounds.Bottom - 2),
            new PointF(bounds.Left + 2, bounds.Top + 7), new PointF(bounds.Left + 5, bounds.Top + 3),
            new PointF(bounds.Left + 4, bounds.Top + 1),
        ]);
        g.DrawLine(pen, bounds.Left + 4, bounds.Top + 8, bounds.Left + 1, bounds.Bottom - 1);
    }
}
