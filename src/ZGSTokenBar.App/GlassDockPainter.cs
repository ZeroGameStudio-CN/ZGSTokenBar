using System.Drawing.Drawing2D;
using System.Globalization;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal static class GlassDockPainter
{
    public static void Draw(
        Graphics graphics,
        bool light,
        GlassDockPlan plan,
        bool routerWarning,
        double? cpu,
        double? memory,
        double? gpu,
        double? tokensPerSecond,
        IReadOnlyList<double> spark,
        bool chinese)
    {
        var bounds = new RectangleF(0.5f, 0.5f, GlassDockLayout.Width - 1f, GlassDockLayout.Height - 1f);
        using var path = Rounded(bounds, GlassDockLayout.Corner);
        using var wash = new LinearGradientBrush(
            bounds,
            light ? Color.FromArgb(210, 255, 255, 255) : Color.FromArgb(168, 36, 30, 42),
            light ? Color.FromArgb(150, 244, 246, 251) : Color.FromArgb(150, 16, 18, 26),
            LinearGradientMode.Vertical);
        using var border = new Pen(light ? Color.FromArgb(180, 255, 255, 255) : Color.FromArgb(90, 255, 255, 255), 1f);
        using var highlight = new Pen(Color.FromArgb(light ? 160 : 90, 255, 255, 255), 1f);
        graphics.FillPath(wash, path);
        graphics.DrawPath(border, path);
        graphics.DrawLine(highlight, 16, 1.5f, GlassDockLayout.Width - 16, 1.5f);

        for (var index = 0; index < plan.Slots.Count; index++)
        {
            var slot = plan.Slots[index];
            if (slot.Chip)
            {
                DrawChip(graphics, slot.Bounds, plan.OverflowCount, plan.OverflowUrgent, light);
                continue;
            }

            if (index < plan.Visible.Count) DrawRing(graphics, slot.Bounds, plan.Visible[index], light);
        }

        var dividerX = GlassDockLayout.SystemLeft - 6f;
        using var divider = new Pen(light ? Color.FromArgb(40, 20, 24, 32) : Color.FromArgb(56, 255, 255, 255), 1f);
        graphics.DrawLine(divider, dividerX, 12, dividerX, GlassDockLayout.Height - 12);
        DrawRouter(graphics, GlassDockLayout.RouterBounds, routerWarning);
        DrawBars(graphics, GlassDockLayout.BarsBounds, cpu, memory, gpu, light);
        DrawRate(graphics, GlassDockLayout.RateBounds, tokensPerSecond, light, chinese);
        DrawSpark(graphics, GlassDockLayout.SparkBounds, spark);
    }

    private static void DrawRing(Graphics graphics, Rectangle bounds, GlassDockRing ring, bool light)
    {
        var color = Band(ring.RemainingPercent);
        var ellipse = new RectangleF(bounds.X + 1.2f, bounds.Y + 1.2f, bounds.Width - 2.4f, bounds.Height - 2.4f);
        using var track = new Pen(light ? Color.FromArgb(36, 20, 24, 32) : Color.FromArgb(40, 255, 255, 255), 2.4f);
        graphics.DrawEllipse(track, ellipse);
        var sweep = ring.RemainingPercent is { } remaining
            ? (float)(360d * Math.Clamp(remaining, 0, 100) / 100d)
            : 0f;
        if (sweep > 0.5f)
        {
            using var glow = new Pen(Color.FromArgb(70, color), 4.2f);
            using var ink = new Pen(color, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(glow, ellipse, -90, sweep);
            graphics.DrawArc(ink, ellipse, -90, sweep);
        }

        var icon = RectangleF.Inflate(bounds, -6, -6);
        using var fill = new SolidBrush(light ? Color.White : Color.FromArgb(22, 22, 28));
        graphics.FillEllipse(fill, icon);
        using var font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(light ? Color.FromArgb(28, 36, 48) : Color.White);
        var letter = string.IsNullOrWhiteSpace(ring.Title) ? "?" : ring.Title[..1].ToUpperInvariant();
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(letter, font, text, icon, format);
        if (ring.AccountNumber <= 0) return;
        var badge = new RectangleF(bounds.Right - 10, bounds.Bottom - 10, 11, 11);
        using var badgeBrush = new SolidBrush(color);
        graphics.FillEllipse(badgeBrush, badge);
        using var badgeFont = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Pixel);
        graphics.DrawString(ring.AccountNumber.ToString(CultureInfo.InvariantCulture), badgeFont, text, badge, format);
    }

    private static void DrawChip(Graphics graphics, Rectangle bounds, int overflow, bool urgent, bool light)
    {
        var rect = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        using var path = Rounded(rect, 9);
        using var fill = new SolidBrush(light ? Color.FromArgb(24, 20, 24, 32) : Color.FromArgb(28, 255, 255, 255));
        using var text = new SolidBrush(light ? Color.FromArgb(220, 28, 36, 48) : Color.FromArgb(210, 255, 255, 255));
        using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.FillPath(fill, path);
        graphics.DrawString("+" + overflow.ToString(CultureInfo.InvariantCulture), font, text, rect, format);
        if (!urgent) return;
        using var dot = new SolidBrush(Color.FromArgb(255, 176, 32));
        graphics.FillEllipse(dot, bounds.Right - 4, bounds.Top - 1, 6, 6);
    }

    private static void DrawRouter(Graphics graphics, Rectangle bounds, bool warning)
    {
        var color = warning ? Color.FromArgb(245, 193, 74) : Color.FromArgb(70, 229, 154);
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (!warning)
        {
            graphics.DrawEllipse(pen, bounds);
            return;
        }

        graphics.DrawPolygon(pen, [
            new PointF(bounds.Left + bounds.Width / 2f, bounds.Top + 1),
            new PointF(bounds.Right - 1, bounds.Bottom - 1),
            new PointF(bounds.Left + 1, bounds.Bottom - 1),
        ]);
    }

    private static void DrawBars(Graphics graphics, Rectangle bounds, double? cpu, double? memory, double? gpu, bool light)
    {
        double?[] values = [cpu, memory, gpu];
        for (var index = 0; index < values.Length; index++)
        {
            var x = bounds.X + index * (GlassDockLayout.BarWidth + GlassDockLayout.BarGap);
            var track = new RectangleF(x, bounds.Y, GlassDockLayout.BarWidth, bounds.Height);
            using var trackBrush = new SolidBrush(light ? Color.FromArgb(36, 20, 24, 32) : Color.FromArgb(40, 255, 255, 255));
            graphics.FillRectangle(trackBrush, track);
            if (values[index] is not { } value || value <= 0) continue;
            var fraction = (float)Math.Clamp(value / 100d, 0, 1);
            var height = Math.Max(1.5f, bounds.Height * fraction);
            using var ink = new SolidBrush(LoadColor(value));
            graphics.FillRectangle(ink, new RectangleF(x, bounds.Bottom - height, GlassDockLayout.BarWidth, height));
        }
    }

    private static void DrawRate(Graphics graphics, Rectangle bounds, double? tokensPerSecond, bool light, bool chinese)
    {
        _ = chinese;
        var rate = tokensPerSecond is { } value && double.IsFinite(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : "—";
        using var font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(light ? Color.FromArgb(230, 20, 24, 32) : Color.FromArgb(235, 255, 255, 255));
        using var muted = new SolidBrush(light ? Color.FromArgb(150, 20, 24, 32) : Color.FromArgb(140, 255, 255, 255));
        graphics.DrawString(rate, font, text, bounds.X, bounds.Y - 2);
        graphics.DrawString("tok/s", small, muted, bounds.X, bounds.Bottom - 10);
    }

    private static void DrawSpark(Graphics graphics, Rectangle bounds, IReadOnlyList<double> spark)
    {
        if (spark.Count < 2) return;
        var min = spark.Min();
        var max = spark.Max();
        var span = Math.Max(0.001, max - min);
        var points = new PointF[spark.Count];
        for (var index = 0; index < spark.Count; index++)
        {
            var x = bounds.X + bounds.Width * index / (float)(spark.Count - 1);
            var y = bounds.Bottom - (float)((spark[index] - min) / span) * (bounds.Height - 1);
            points[index] = new PointF(x, y);
        }

        using var pen = new Pen(Color.FromArgb(70, 229, 154), 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawLines(pen, points);
    }

    private static Color LoadColor(double value) => value switch
    {
        > 80 => Color.FromArgb(255, 90, 90),
        > 50 => Color.FromArgb(245, 193, 74),
        _ => Color.FromArgb(70, 229, 154),
    };

    private static Color Band(double? remaining)
    {
        var color = GlassDockLayout.BandColor(remaining);
        return Color.FromArgb(color.R, color.G, color.B);
    }

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
