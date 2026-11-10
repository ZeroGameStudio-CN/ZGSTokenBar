using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>One visible ring item: the ring, its official icon and the name shown under the percentage.</summary>
internal sealed record GlassDockItemVisual(GlassDockRing Ring, Image? Icon, string Name, bool Hovered);

internal sealed record GlassDockScene(
    bool Light,
    int Width,
    float DevicePixel,
    GlassDockPlan Plan,
    IReadOnlyList<GlassDockItemVisual> Items,
    bool Expanded,
    bool ChipHovered,
    MiniCardContribution? Router,
    bool RouterHovered,
    double? Cpu,
    double? Memory,
    double? Gpu,
    bool SystemHovered,
    double? TokensPerSecond,
    bool RateEstimated,
    bool RateHovered,
    IReadOnlyList<double> Spark);

internal static class GlassDockPainter
{
    public static void Draw(Graphics graphics, GlassDockScene scene, Font badgeFont)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        DrawGlass(graphics, scene);

        for (var index = 0; index < scene.Plan.Slots.Count; index++)
        {
            var slot = scene.Plan.Slots[index];
            if (slot.Chip)
            {
                DrawChip(graphics, slot.Bounds, scene);
                continue;
            }

            if (index < scene.Items.Count) DrawItem(graphics, slot.Bounds, scene.Items[index], scene.Light);
        }

        var systemLeft = GlassDockLayout.SystemLeftFor(scene.Width);
        var dividerX = systemLeft - GlassDockLayout.DividerGap / 2f;
        using (var divider = new LinearGradientBrush(
                   new RectangleF(dividerX, 8, 1, GlassDockLayout.Height - 16),
                   Color.FromArgb(0, Ink(scene.Light)),
                   Color.FromArgb(scene.Light ? 60 : 70, Ink(scene.Light)),
                   LinearGradientMode.Vertical))
        {
            divider.SetBlendTriangularShape(.5f);
            graphics.FillRectangle(divider, dividerX - .5f, 8, 1f, GlassDockLayout.Height - 16);
        }

        var routerBounds = GlassDockLayout.RouterBoundsFor(scene.Width);
        if (scene.RouterHovered) DrawHover(graphics, RectangleF.Inflate(routerBounds, 1, -2), scene.Light);
        if (scene.Router is { } router)
        {
            RouterCardRenderer.Draw(graphics, routerBounds, router, badgeFont, collapsed: true);
        }

        var barsBounds = GlassDockLayout.BarsBoundsFor(scene.Width);
        if (scene.SystemHovered) DrawHover(graphics, RectangleF.Inflate(barsBounds, 4, 5), scene.Light);
        DrawBars(graphics, barsBounds, scene);
        var rateBounds = GlassDockLayout.RateBoundsFor(scene.Width);
        var sparkBounds = GlassDockLayout.SparkBoundsFor(scene.Width);
        if (scene.RateHovered)
        {
            DrawHover(graphics, RectangleF.FromLTRB(rateBounds.Left - 3, 6, sparkBounds.Right + 3, GlassDockLayout.Height - 6), scene.Light);
        }

        DrawRate(graphics, rateBounds, scene);
        DrawSpark(graphics, sparkBounds, scene.Spark, scene.Light);
    }

    /// <summary>
    /// Liquid-glass material: a thin tint over the DWM blur, a soft refraction highlight on the
    /// upper half, a gradient specular rim that fades out towards the bottom (no hard white border)
    /// and a faint dark contact edge so the pill reads on light taskbars.
    /// </summary>
    private static void DrawGlass(Graphics graphics, GlassDockScene scene)
    {
        var light = scene.Light;
        var inset = scene.DevicePixel;
        var bounds = new RectangleF(inset, inset, scene.Width - inset * 2, GlassDockLayout.Height - inset * 2);
        using var path = Rounded(bounds, GlassDockLayout.Corner);
        using (var tint = new LinearGradientBrush(
                   bounds,
                   light ? Color.FromArgb(150, 252, 253, 255) : Color.FromArgb(118, 52, 56, 72),
                   light ? Color.FromArgb(118, 236, 240, 248) : Color.FromArgb(150, 14, 16, 24),
                   LinearGradientMode.Vertical))
        {
            graphics.FillPath(tint, path);
        }

        var state = graphics.Save();
        graphics.SetClip(path);
        var sheen = new RectangleF(bounds.X - bounds.Width * .1f, bounds.Y - bounds.Height * .9f, bounds.Width * 1.2f, bounds.Height * 1.45f);
        using (var sheenPath = new GraphicsPath())
        {
            sheenPath.AddEllipse(sheen);
            using var sheenBrush = new PathGradientBrush(sheenPath)
            {
                CenterColor = Color.FromArgb(light ? 120 : 46, 255, 255, 255),
                SurroundColors = [Color.FromArgb(0, 255, 255, 255)],
                CenterPoint = new PointF(sheen.X + sheen.Width * .42f, sheen.Y + sheen.Height * .62f),
                FocusScales = new PointF(.55f, .25f),
            };
            graphics.FillEllipse(sheenBrush, sheen);
        }

        var caustic = new RectangleF(bounds.X + bounds.Width * .2f, bounds.Bottom - 7, bounds.Width * .6f, 12);
        using (var causticPath = new GraphicsPath())
        {
            causticPath.AddEllipse(caustic);
            using var causticBrush = new PathGradientBrush(causticPath)
            {
                CenterColor = Color.FromArgb(light ? 60 : 22, 255, 255, 255),
                SurroundColors = [Color.FromArgb(0, 255, 255, 255)],
            };
            graphics.FillEllipse(causticBrush, caustic);
        }

        graphics.Restore(state);
        var rimBounds = RectangleF.Inflate(bounds, -.6f, -.6f);
        using (var rimPath = Rounded(rimBounds, GlassDockLayout.Corner - .6f))
        using (var rimBrush = new LinearGradientBrush(
                   rimBounds,
                   Color.FromArgb(light ? 210 : 96, 255, 255, 255),
                   Color.FromArgb(light ? 40 : 6, 255, 255, 255),
                   LinearGradientMode.Vertical))
        {
            rimBrush.SetSigmaBellShape(0f, 1f);
            using var rim = new Pen(rimBrush, 1.1f);
            graphics.DrawPath(rim, rimPath);
        }

        using var contact = new Pen(Color.FromArgb(light ? 34 : 70, 0, 0, 0), Math.Max(.5f, inset));
        graphics.DrawPath(contact, path);
    }

    private static void DrawItem(Graphics graphics, Rectangle slot, GlassDockItemVisual item, bool light)
    {
        var ring = item.Ring;
        if (item.Hovered) DrawHover(graphics, slot, light);
        var size = GlassDockLayout.RingSize;
        var ringBounds = new RectangleF(slot.X + 1, (GlassDockLayout.Height - size) / 2f, size, size);
        var color = Band(ring.RemainingPercent, light);
        var stroke = 3.4f;
        var ellipse = RectangleF.Inflate(ringBounds, -stroke / 2, -stroke / 2);
        using (var track = new Pen(light ? Color.FromArgb(46, 15, 23, 42) : Color.FromArgb(52, 255, 255, 255), stroke))
        {
            graphics.DrawEllipse(track, ellipse);
        }

        var sweep = ring.BalanceText is not null && ring.RemainingPercent is null
            ? 360f
            : GlassDockLayout.RingSweep(ring.RemainingPercent);
        if (ring.BalanceText is not null && ring.RemainingPercent is null) color = Accent(ring.Accent, light);
        if (sweep > 0)
        {
            using var glow = new Pen(Color.FromArgb(light ? 40 : 70, color), stroke + 2.2f);
            using var ink = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (sweep >= 360f)
            {
                graphics.DrawEllipse(glow, ellipse);
                graphics.DrawEllipse(ink, ellipse);
            }
            else
            {
                graphics.DrawArc(glow, ellipse, -90, sweep);
                graphics.DrawArc(ink, ellipse, -90, sweep);
            }
        }

        var iconBounds = RectangleF.Inflate(ringBounds, -7f, -7f);
        DrawIcon(graphics, iconBounds, item, light);
        if (ring.AccountNumber > 0)
        {
            var badge = new RectangleF(ringBounds.Right - 10.5f, ringBounds.Bottom - 10.5f, 11.5f, 11.5f);
            using var badgeFill = new SolidBrush(light ? Color.FromArgb(255, 30, 41, 59) : Color.FromArgb(255, 241, 245, 249));
            using var badgeEdge = new Pen(light ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(230, 15, 18, 26), 1.2f);
            graphics.FillEllipse(badgeFill, badge);
            graphics.DrawEllipse(badgeEdge, badge);
            using var badgeFont = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var badgeText = new SolidBrush(light ? Color.White : Color.FromArgb(15, 23, 42));
            DrawCentered(graphics, ring.AccountNumber.ToString(CultureInfo.InvariantCulture), badgeFont, badgeText, RectangleF.Inflate(badge, 1, 0));
        }

        var textLeft = ringBounds.Right + 3;
        var textWidth = slot.Right - textLeft;
        var value = ring.RemainingPercent is null && ring.BalanceText is { } balance
            ? balance
            : GlassDockLayout.PercentText(ring.RemainingPercent);
        using var valueBrush = new SolidBrush(ValueColor(ring, light));
        using var valueFont = FitFont(graphics, value, textWidth, 13.5f, 9.5f, FontStyle.Bold);
        graphics.DrawString(value, valueFont, valueBrush, new RectangleF(textLeft - 1.5f, 6.5f, textWidth + 3, 17), Format(StringAlignment.Near));
        using var nameFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var nameBrush = new SolidBrush(light ? Color.FromArgb(200, 30, 41, 59) : Color.FromArgb(200, 226, 232, 240));
        graphics.DrawString(item.Name, nameFont, nameBrush, new RectangleF(textLeft - 1, 23.5f, textWidth + 2, 13), Format(StringAlignment.Near));
    }

    private static void DrawIcon(Graphics graphics, RectangleF bounds, GlassDockItemVisual item, bool light)
    {
        using var clip = new GraphicsPath();
        clip.AddEllipse(bounds);
        if (item.Icon is { } icon)
        {
            var state = graphics.Save();
            graphics.SetClip(clip);
            graphics.DrawImage(icon, RectangleF.Inflate(bounds, .5f, .5f));
            graphics.Restore(state);
            return;
        }

        using var fill = new SolidBrush(Accent(item.Ring.Accent, light));
        graphics.FillEllipse(fill, bounds);
        using var font = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.White);
        var letter = string.IsNullOrWhiteSpace(item.Ring.Title) ? "?" : item.Ring.Title[..1].ToUpperInvariant();
        DrawCentered(graphics, letter, font, text, bounds);
    }

    private static void DrawChip(Graphics graphics, Rectangle bounds, GlassDockScene scene)
    {
        var light = scene.Light;
        var rect = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        using var path = Rounded(rect, rect.Height / 2);
        using var fill = new SolidBrush(scene.ChipHovered
            ? light ? Color.FromArgb(70, 15, 23, 42) : Color.FromArgb(70, 255, 255, 255)
            : light ? Color.FromArgb(34, 15, 23, 42) : Color.FromArgb(36, 255, 255, 255));
        graphics.FillPath(fill, path);
        using var text = new SolidBrush(light ? Color.FromArgb(235, 15, 23, 42) : Color.FromArgb(240, 255, 255, 255));
        using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
        var label = scene.Expanded ? "‹" : "+" + scene.Plan.OverflowCount.ToString(CultureInfo.InvariantCulture);
        DrawCentered(graphics, label, font, text, rect);
        if (scene.Expanded || !scene.Plan.OverflowUrgent) return;
        using var dot = new SolidBrush(Color.FromArgb(255, 80, 80));
        graphics.FillEllipse(dot, rect.Right - 5, rect.Top - 1, 6, 6);
    }

    private static void DrawBars(Graphics graphics, Rectangle bounds, GlassDockScene scene)
    {
        double?[] values = [scene.Cpu, scene.Memory, scene.Gpu];
        for (var index = 0; index < values.Length; index++)
        {
            var x = bounds.X + index * (GlassDockLayout.BarWidth + GlassDockLayout.BarGap);
            var track = new RectangleF(x, bounds.Y, GlassDockLayout.BarWidth, bounds.Height);
            using var trackBrush = new SolidBrush(scene.Light ? Color.FromArgb(40, 15, 23, 42) : Color.FromArgb(46, 255, 255, 255));
            graphics.FillRoundedRectangle(trackBrush, track, GlassDockLayout.BarWidth / 2f);
            if (values[index] is not { } value || value <= 0) continue;
            var fraction = (float)Math.Clamp(value / 100d, 0, 1);
            var height = Math.Max(GlassDockLayout.BarWidth, bounds.Height * fraction);
            using var ink = new SolidBrush(LoadColor(value, scene.Light));
            graphics.FillRoundedRectangle(ink, new RectangleF(x, bounds.Bottom - height, GlassDockLayout.BarWidth, height), GlassDockLayout.BarWidth / 2f);
        }
    }

    private static void DrawRate(Graphics graphics, Rectangle bounds, GlassDockScene scene)
    {
        var rate = BarForm.CompactOutputRate(scene.TokensPerSecond);
        if (scene.RateEstimated && scene.TokensPerSecond is not null) rate = "~" + rate;
        using var font = FitFont(graphics, rate, bounds.Width + 2, 13.5f, 9.5f, FontStyle.Bold);
        using var small = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        var color = scene.TokensPerSecond is null
            ? Muted(scene.Light)
            : scene.RateEstimated
                ? scene.Light ? Color.FromArgb(180, 110, 0) : Color.FromArgb(251, 191, 36)
                : scene.Light ? Color.FromArgb(4, 120, 87) : Color.FromArgb(110, 231, 183);
        using var text = new SolidBrush(color);
        using var muted = new SolidBrush(Muted(scene.Light));
        graphics.DrawString(rate, font, text, new RectangleF(bounds.X - 1.5f, 6.5f, bounds.Width + 4, 17), Format(StringAlignment.Near));
        graphics.DrawString("tok/s", small, muted, new RectangleF(bounds.X - 1, 23.5f, bounds.Width + 4, 12), Format(StringAlignment.Near));
    }

    private static void DrawSpark(Graphics graphics, Rectangle bounds, IReadOnlyList<double> spark, bool light)
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

        var color = light ? Color.FromArgb(5, 150, 105) : Color.FromArgb(52, 211, 153);
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        graphics.DrawLines(pen, points);
    }

    private static void DrawHover(Graphics graphics, RectangleF bounds, bool light)
    {
        using var path = Rounded(bounds, Math.Min(10, bounds.Height / 2));
        using var fill = new SolidBrush(light ? Color.FromArgb(30, 15, 23, 42) : Color.FromArgb(30, 255, 255, 255));
        graphics.FillPath(fill, path);
    }

    private static Font FitFont(Graphics graphics, string text, float width, float size, float minimum, FontStyle style)
    {
        while (true)
        {
            var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            if (size <= minimum || graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width <= width) return font;
            font.Dispose();
            size -= .5f;
        }
    }

    private static void DrawCentered(Graphics graphics, string text, Font font, Brush brush, RectangleF bounds) =>
        graphics.DrawString(text, font, brush, bounds, Format(StringAlignment.Center));

    private static StringFormat Format(StringAlignment alignment) => new(StringFormat.GenericTypographic)
    {
        Alignment = alignment,
        LineAlignment = alignment == StringAlignment.Center ? StringAlignment.Center : StringAlignment.Near,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
    };

    private static Color ValueColor(GlassDockRing ring, bool light)
    {
        if (ring.RemainingPercent is null) return light ? Color.FromArgb(15, 23, 42) : Color.FromArgb(248, 250, 252);
        return Band(ring.RemainingPercent, light);
    }

    private static Color Ink(bool light) => light ? Color.FromArgb(15, 23, 42) : Color.FromArgb(255, 255, 255);

    private static Color Muted(bool light) => light ? Color.FromArgb(190, 51, 65, 85) : Color.FromArgb(190, 203, 213, 225);

    private static Color LoadColor(double value, bool light) => value switch
    {
        > 80 => light ? Color.FromArgb(220, 38, 38) : Color.FromArgb(255, 107, 107),
        > 50 => light ? Color.FromArgb(202, 138, 4) : Color.FromArgb(252, 196, 64),
        _ => light ? Color.FromArgb(5, 150, 105) : Color.FromArgb(52, 211, 153),
    };

    private static Color Band(double? remaining, bool light) => GlassDockLayout.Band(remaining) switch
    {
        "red" => light ? Color.FromArgb(220, 38, 38) : Color.FromArgb(255, 99, 99),
        "amber" => light ? Color.FromArgb(194, 120, 3) : Color.FromArgb(252, 196, 64),
        "green" => light ? Color.FromArgb(5, 150, 105) : Color.FromArgb(52, 220, 150),
        _ => light ? Color.FromArgb(100, 116, 139) : Color.FromArgb(203, 213, 225),
    };

    private static Color Accent(string accent, bool light)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(accent)) return ColorTranslator.FromHtml(accent);
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
        }

        return light ? Color.FromArgb(79, 70, 229) : Color.FromArgb(129, 140, 248);
    }

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
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
