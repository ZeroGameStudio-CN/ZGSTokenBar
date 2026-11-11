using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>One visible ring item: the ring, its official icon badge and the account name (hover only).</summary>
internal sealed record GlassDockItemVisual(GlassDockRing Ring, Image? Icon, string Name, bool Hovered);

/// <summary>How a ring shows its provider: a large translucent watermark under the % (default) or alternatives for comparison.</summary>
internal enum GlassRingStyle
{
    Watermark,
    WatermarkFaint,
    WatermarkStrong,
    IconPercentBelow,
}

/// <summary>Where the icon-in-ring layout puts its % label.</summary>
internal enum GlassPercentPosition
{
    /// <summary>A compact pill on the ring's lower-left, mirroring the index badge (default).</summary>
    BottomLeft,
    /// <summary>Centred under the ring.</summary>
    Below,
    /// <summary>Centred above the ring.</summary>
    Above,
}

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
    double? Disk,
    double? Gpu,
    bool SystemHovered,
    double? TokensPerSecond,
    bool RateEstimated,
    bool RateHovered,
    GlassRingStyle RingStyle = GlassRingStyle.IconPercentBelow,
    float IconRingSize = GlassDockPainter.DefaultIconRingSize,
    float LabelSize = GlassDockPainter.DefaultLabelSize,
    GlassPercentPosition PercentPosition = GlassPercentPosition.BottomLeft,
    bool VisibilityHovered = false);

internal static class GlassDockPainter
{
    /// <summary>Icon-in-ring layout: ring diameter and the % label size above it (logical px).</summary>
    internal const float DefaultIconRingSize = 26f;
    internal const float DefaultLabelSize = 12f;
    /// <summary>Ring and label sizes for the stacked (above/below) label positions.</summary>
    internal const float StackedIconRingSize = 25f;
    internal const float StackedLabelSize = 10f;

    public static void Draw(Graphics graphics, GlassDockScene scene, Font badgeFont)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        DrawGlass(graphics, scene);
        // Content is laid out against the full dock height; lift it so it centres on the card.
        graphics.TranslateTransform(0, -ShadowDepth / 2f);

        // Chips sit between rings (the "‹" chip keeps the collapsed chip's spot), so rings use their own index.
        var itemIndex = 0;
        for (var index = 0; index < scene.Plan.Slots.Count; index++)
        {
            var slot = scene.Plan.Slots[index];
            if (slot.Visibility)
            {
                DrawVisibilityChip(graphics, slot.Bounds, scene);
                continue;
            }
            if (slot.Chip)
            {
                DrawChip(graphics, slot.Bounds, scene);
                continue;
            }

            if (itemIndex < scene.Items.Count) DrawItem(graphics, slot.Bounds, scene.Items[itemIndex], scene.Light, scene.RingStyle, scene.IconRingSize, scene.LabelSize, scene.PercentPosition);
            itemIndex++;
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
        if (scene.RouterHovered) DrawHover(graphics, RectangleF.Inflate(routerBounds, 3, 3), scene.Light);
        if (scene.Router is { } router)
        {
            RouterCardRenderer.Draw(graphics, routerBounds, router, badgeFont, collapsed: true);
        }

        var barsBounds = GlassDockLayout.BarsBoundsFor(scene.Width);
        if (scene.SystemHovered) DrawHover(graphics, RectangleF.Inflate(barsBounds, 4, 3), scene.Light);
        DrawMetrics(graphics, barsBounds, scene);
        var rateBounds = GlassDockLayout.RateBoundsFor(scene.Width);
        if (scene.RateHovered) DrawHover(graphics, RectangleF.Inflate(rateBounds, 3, 3), scene.Light);
        DrawRate(graphics, rateBounds, scene);
    }

    /// <summary>Space kept under the card for its soft drop shadow (logical px).</summary>
    internal const float ShadowDepth = 3f;

    /// <summary>
    /// Solid dark card (refined from the v3.0.0 dark card): an almost opaque graphite body with a
    /// barely-there vertical gradient, a 1px hairline border, a soft drop shadow underneath and
    /// rounded corners. Opaque on purpose so ring tracks, arcs and labels keep full contrast.
    /// </summary>
    private static void DrawGlass(Graphics graphics, GlassDockScene scene)
    {
        var hairline = scene.DevicePixel;
        var card = CardBounds(scene);
        for (var layer = 3; layer >= 1; layer--)
        {
            using var shadowPath = Rounded(new RectangleF(card.X + .5f, card.Y + layer * .9f, card.Width - 1f, card.Height), GlassDockLayout.Corner);
            using var shadow = new SolidBrush(Color.FromArgb(layer switch { 3 => 22, 2 => 38, _ => 60 }, 0, 0, 0));
            graphics.FillPath(shadow, shadowPath);
        }

        using var path = Rounded(card, GlassDockLayout.Corner);
        using (var body = new LinearGradientBrush(
                   RectangleF.Inflate(card, 0, 1),
                   Color.FromArgb(250, 30, 32, 39),
                   Color.FromArgb(250, 19, 20, 25),
                   LinearGradientMode.Vertical))
        {
            graphics.FillPath(body, path);
        }

        var edge = RectangleF.Inflate(card, -hairline / 2, -hairline / 2);
        using var edgePath = Rounded(edge, GlassDockLayout.Corner - hairline / 2);
        using var border = new Pen(Color.FromArgb(34, 255, 255, 255), hairline);
        graphics.DrawPath(border, edgePath);
    }

    /// <summary>The card itself: full width, leaving <see cref="ShadowDepth"/> underneath for the shadow.</summary>
    private static RectangleF CardBounds(GlassDockScene scene) =>
        new(scene.DevicePixel, scene.DevicePixel, scene.Width - scene.DevicePixel * 2, GlassDockLayout.Height - ShadowDepth - scene.DevicePixel);

    /// <summary>
    /// Compact ring: the official provider icon sits large and translucent in the middle of the ring
    /// as a watermark, the bold remaining percentage is drawn on top, and the account ordinal is a
    /// small badge on the lower-right.
    /// </summary>
    private static void DrawItem(Graphics graphics, Rectangle slot, GlassDockItemVisual item, bool light, GlassRingStyle style,
        float iconRingSize, float labelSize, GlassPercentPosition position = GlassPercentPosition.BottomLeft)
    {
        var ring = item.Ring;
        var below = style == GlassRingStyle.IconPercentBelow;
        var stacked = below && position != GlassPercentPosition.BottomLeft;
        var size = below ? iconRingSize : GlassDockLayout.RingSize;
        var labelHeight = labelSize * 1.22f;
        // Stacked layouts centre label + ring as one unit; the corner pill keeps the ring centred.
        var stackTop = (GlassDockLayout.Height - (labelHeight + size - 1.5f)) / 2f;
        var ringTop = !stacked
            ? (GlassDockLayout.Height - size) / 2f
            : position == GlassPercentPosition.Above ? stackTop + labelHeight - 1.5f : stackTop;
        var ringBounds = new RectangleF(slot.X + (slot.Width - size) / 2f, ringTop, size, size);
        if (item.Hovered)
        {
            using var hover = new SolidBrush(light ? UiPalette.Slate900.WithAlpha(34) : Color.FromArgb(34, 255, 255, 255));
            graphics.FillEllipse(hover, RectangleF.Inflate(ringBounds, 1, 1));
        }

        var color = Band(ring.RemainingPercent, light);
        var stroke = below ? Math.Clamp(size * .1f, 2.5f, 3.2f) : 3.2f;
        var ellipse = RectangleF.Inflate(ringBounds, -stroke / 2 - .5f, -stroke / 2 - .5f);
        using (var track = new Pen(light ? UiPalette.Slate900.WithAlpha(40) : Color.FromArgb(46, 255, 255, 255), stroke))
        {
            graphics.DrawEllipse(track, ellipse);
        }

        var balanceOnly = ring.BalanceText is not null && ring.RemainingPercent is null;
        var sweep = balanceOnly ? 360f : GlassDockLayout.RingSweep(ring.RemainingPercent);
        if (balanceOnly) color = Accent(ring.Accent, light);
        if (sweep > 0)
        {
            using var ink = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (sweep >= 360f) graphics.DrawEllipse(ink, ellipse);
            else graphics.DrawArc(ink, ellipse, -90, sweep);
        }

        var inner = RectangleF.Inflate(ringBounds, -stroke - 2.5f, -stroke - 2.5f);
        using var valueBrush = new SolidBrush(ValueColor(ring, light));
        var core = RectangleF.Inflate(ringBounds, -stroke - 1f, -stroke - 1f);
        var chipRight = float.NaN;
        if (below)
        {
            DrawWatermark(graphics, core, item, light, 1f, .86f);
            var text = balanceOnly ? ring.BalanceText! : GlassDockLayout.PercentText(ring.RemainingPercent);
            if (!stacked)
            {
                DrawCornerPercent(graphics, slot, ringBounds, text, labelSize, ring.AccountNumber > 0,
                    !balanceOnly && GlassDockLayout.Band(ring.RemainingPercent) == "red", light, out chipRight);
            }
            else
            {
                using var label = FitFont(graphics, text, slot.Width + 3, labelSize, labelSize - 2f, FontStyle.Bold);
                var labelTop = position == GlassPercentPosition.Above ? stackTop : ringBounds.Bottom - 1.5f;
                var labelBounds = new RectangleF(slot.X - 4, labelTop, slot.Width + 8, labelHeight);
                using (var halo = new SolidBrush(light ? Color.FromArgb(140, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0)))
                {
                    DrawCentered(graphics, text, label, halo, new RectangleF(labelBounds.X, labelBounds.Y + .7f, labelBounds.Width, labelBounds.Height));
                }

                DrawCentered(graphics, text, label, valueBrush, labelBounds);
            }
        }
        else
        {
            var opacity = style switch
            {
                GlassRingStyle.WatermarkFaint => light ? .14f : .16f,
                GlassRingStyle.WatermarkStrong => light ? .34f : .38f,
                _ => light ? .22f : .26f,
            };
            DrawWatermark(graphics, core, item, light, opacity, .78f);
            if (balanceOnly)
            {
                using var balanceFont = FitFont(graphics, ring.BalanceText!, inner.Width + 1, 11f, 7.5f, FontStyle.Bold);
                DrawCentered(graphics, ring.BalanceText!, balanceFont, valueBrush, new RectangleF(inner.X - 2, inner.Y - 1, inner.Width + 4, inner.Height));
            }
            else
            {
                DrawPercent(graphics, ring.RemainingPercent, inner, valueBrush, light);
            }
        }

        if (ring.AccountNumber > 0)
        {
            var corner = below && !stacked;
            var badgeSide = corner ? 10.5f : below ? 11f : 12f;
            var badge = corner
                ? new RectangleF(Math.Max(ringBounds.Right - badgeSide + 2f, float.IsNaN(chipRight) ? 0f : chipRight - 1.5f), ringBounds.Bottom - badgeSide + 2.5f, badgeSide, badgeSide)
                : new RectangleF(ringBounds.Right - badgeSide + .5f, ringBounds.Bottom - badgeSide + 1.5f, badgeSide, badgeSide);
            using var badgeFill = new SolidBrush(light ? UiPalette.Slate800 : UiPalette.Slate100);
            using var badgeEdge = new Pen(Separator(light), 1.4f);
            graphics.FillEllipse(badgeFill, badge);
            graphics.DrawEllipse(badgeEdge, badge);
            using var badgeFont = new Font("Segoe UI", below ? 7.5f : 8f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var badgeText = new SolidBrush(light ? Color.White : UiPalette.Slate900);
            DrawCentered(graphics, ring.AccountNumber.ToString(CultureInfo.InvariantCulture), badgeFont, badgeText, RectangleF.Inflate(badge, 1, 0));
        }
    }

    /// <summary>Look of the corner % chip; <see cref="ChipStyle"/> holds the shipped one.</summary>
    internal sealed record CornerChipStyle(
        string Name,
        int FillAlpha,
        Color CriticalText,
        Color? CriticalFill,
        int CriticalOutlineAlpha);

    internal static readonly CornerChipStyle[] ChipStyleOptions =
    [
        new("A 55% 暗底 · 珊瑚字 + 描边", 140, Color.FromArgb(242, 119, 122), null, 150),
        new("B 65% 暗底 · 浅玫瑰字", 166, Color.FromArgb(255, 150, 150), null, 0),
        new("C 55% 暗底 · 半透珊瑚底白字", 140, Color.White, Color.FromArgb(158, 214, 88, 96), 0),
    ];

    internal static CornerChipStyle ChipStyle { get; set; } = ChipStyleOptions[0];

    /// <summary>
    /// The % as a chip on the ring's lower-left, mirroring the index badge on the lower-right.
    /// Low arcs start at 12 o'clock, so they never sit under it. The chip is a translucent dark
    /// pill so the provider logo still shows through; the text carries a soft shadow instead of
    /// an opaque fill. The font size is fixed for every value and the chip grows with the text;
    /// the index badge moves right to clear it (the ring gap leaves room for 100%).
    /// </summary>
    private static void DrawCornerPercent(Graphics graphics, Rectangle slot, RectangleF ringBounds, string text,
        float labelSize, bool hasBadge, bool critical, bool light, out float chipRight)
    {
        var style = ChipStyle;
        var left = Math.Max(8f, slot.X - 1.5f);
        var number = text.EndsWith('%') ? text[..^1] : text;
        var hasSign = text.EndsWith('%') && number.Length > 0;
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
        using var numberFont = new Font("Segoe UI", labelSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var signFont = new Font("Segoe UI", Math.Max(6.5f, labelSize * .66f), FontStyle.Bold, GraphicsUnit.Pixel);
        var numberWidth = graphics.MeasureString(number, numberFont, PointF.Empty, format).Width;
        var signWidth = hasSign ? graphics.MeasureString("%", signFont, PointF.Empty, format).Width + .2f : 0f;
        var height = Math.Max(11.5f, labelSize + 1.5f);
        var width = numberWidth + signWidth + 4.5f;
        var pill = new RectangleF(left, ringBounds.Bottom - height + 2.5f, width, height);
        chipRight = pill.Right;
        using (var path = Rounded(pill, height / 2f))
        {
            var fill = critical && style.CriticalFill is { } criticalFill
                ? criticalFill
                : light ? Color.FromArgb(style.FillAlpha, 255, 255, 255) : Color.FromArgb(style.FillAlpha, 10, 12, 17);
            using (var brush = new SolidBrush(fill)) graphics.FillPath(brush, path);
            var outline = critical && style.CriticalOutlineAlpha > 0
                ? Color.FromArgb(style.CriticalOutlineAlpha, style.CriticalText)
                : light ? UiPalette.Slate900.WithAlpha(70) : Color.FromArgb(70, 255, 255, 255);
            if (!(critical && style.CriticalFill is not null))
            {
                using var edge = new Pen(outline, 1f);
                graphics.DrawPath(edge, path);
            }
        }

        var ink = critical
            ? (light && style.CriticalFill is null ? Color.FromArgb(200, 38, 48) : style.CriticalText)
            : light ? UiPalette.Slate900 : Color.FromArgb(250, 250, 252);
        var x = pill.X + 2.2f;
        var numberHeight = numberFont.GetHeight(graphics);
        var centerY = pill.Y + pill.Height / 2f;
        var numberTop = centerY - numberHeight / 2f + .4f;
        var baseline = numberTop + numberFont.FontFamily.GetCellAscent(numberFont.Style) * numberFont.Size / numberFont.FontFamily.GetEmHeight(numberFont.Style);
        var signTop = baseline - signFont.FontFamily.GetCellAscent(signFont.Style) * signFont.Size / signFont.FontFamily.GetEmHeight(signFont.Style);
        // Soft shadow keeps the text readable over the logo showing through the chip.
        if (!light)
        {
            using var shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            foreach (var (dx, dy) in new[] { (0f, .8f), (.6f, .5f), (-.6f, .5f) })
            {
                graphics.DrawString(number, numberFont, shadow, x + dx, numberTop + dy, format);
                if (hasSign) graphics.DrawString("%", signFont, shadow, x + numberWidth + .2f + dx, signTop + dy, format);
            }
        }
        using var inkBrush = new SolidBrush(ink);
        graphics.DrawString(number, numberFont, inkBrush, x, numberTop, format);
        if (hasSign) graphics.DrawString("%", signFont, inkBrush, x + numberWidth + .2f, signTop, format);
    }

    /// <summary>Bold number with a smaller % sign, centred as one unit inside the ring.</summary>
    private static void DrawPercent(Graphics graphics, double? remaining, RectangleF inner, Brush brush, bool light)
    {
        var text = GlassDockLayout.PercentText(remaining);
        var number = text.EndsWith('%') ? text[..^1] : text;
        var hasSign = text.EndsWith('%');
        var numberSize = number.Length >= 3 ? 10.5f : 12.5f;
        using var numberFont = new Font("Segoe UI", numberSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var signFont = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces };
        var numberWidth = graphics.MeasureString(number, numberFont, PointF.Empty, format).Width;
        var signWidth = hasSign ? graphics.MeasureString("%", signFont, PointF.Empty, format).Width : 0f;
        var total = numberWidth + signWidth + (hasSign ? .3f : 0f);
        var left = inner.X + (inner.Width - total) / 2f;
        var centerY = inner.Y + inner.Height / 2f;
        var numberHeight = numberFont.GetHeight(graphics);
        var numberTop = centerY - numberHeight / 2f + .3f;
        using (var halo = new SolidBrush(light ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0)))
        {
            graphics.DrawString(number, numberFont, halo, left, numberTop + .7f, format);
        }

        graphics.DrawString(number, numberFont, brush, left, numberTop, format);
        if (!hasSign) return;
        using var signBrush = new SolidBrush(light ? UiPalette.Slate900.WithAlpha(170) : Color.FromArgb(190, 255, 255, 255));
        var baseline = numberTop + numberHeight * .80f;
        graphics.DrawString("%", signFont, signBrush, left + numberWidth + .3f, baseline - signFont.GetHeight(graphics) * .80f, format);
    }

    /// <summary>
    /// Draws the official provider icon centred in the ring, clipped to a circle, at the given
    /// opacity and fraction of the ring's inner diameter. Providers without an icon get a soft
    /// accent disc with their initial.
    /// </summary>
    private static void DrawWatermark(Graphics graphics, RectangleF core, GlassDockItemVisual item, bool light, float opacity, float fraction)
    {
        var side = core.Width * fraction;
        var bounds = new RectangleF(core.X + (core.Width - side) / 2f, core.Y + (core.Height - side) / 2f, side, side);
        if (item.Icon is { } original)
        {
            // Watermarks use the icon's mark only (its plate removed and tinted to the theme ink) so
            // dark-plated marks such as Grok stay visible; the full-colour icon is kept for opaque use.
            var icon = opacity >= .99f ? original : WatermarkGlyph(original, light);
            // Source icons are up to 1280px; resampling them every frame dominated idle render cost.
            // The circular, opacity-applied icon is baked once per device size and blitted 1:1.
            var target = RectangleF.Inflate(bounds, .5f, .5f);
            var deviceScale = DeviceScale(graphics);
            var baked = BakedIcon(icon, Math.Max(1, (int)MathF.Round(target.Width * deviceScale)), .5f * deviceScale, opacity);
            var interpolation = graphics.InterpolationMode;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(baked, target);
            graphics.InterpolationMode = interpolation;
            return;
        }
        using var clip = new GraphicsPath();
        clip.AddEllipse(bounds);
        var state = graphics.Save();
        graphics.SetClip(clip, CombineMode.Intersect);
        {
            using var fill = new SolidBrush(Color.FromArgb((int)(255 * opacity), Accent(item.Ring.Accent, light)));
            graphics.FillEllipse(fill, bounds);
            if (opacity >= .99f)
            {
                using var font = new Font("Segoe UI", Math.Max(7f, side * .5f), FontStyle.Bold, GraphicsUnit.Pixel);
                using var text = new SolidBrush(Color.White);
                var letter = string.IsNullOrWhiteSpace(item.Ring.Title) ? "?" : item.Ring.Title[..1].ToUpperInvariant();
                DrawCentered(graphics, letter, font, text, bounds);
            }
        }

        graphics.Restore(state);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, Dictionary<(int Pixels, int Inset, int Opacity), Bitmap>> BakedIcons = new();

    private static float DeviceScale(Graphics graphics)
    {
        using var matrix = graphics.Transform;
        var elements = matrix.Elements;
        return Math.Max(.01f, MathF.Sqrt(elements[0] * elements[0] + elements[1] * elements[1]));
    }

    /// <summary>
    /// Returns the icon resampled once to <paramref name="pixels"/> square, masked to an
    /// anti-aliased circle inset by <paramref name="inset"/> device pixels, at the given opacity.
    /// </summary>
    internal static Bitmap BakedIcon(Image icon, int pixels, float inset, float opacity)
    {
        var cache = BakedIcons.GetValue(icon, _ => []);
        var key = (pixels, (int)MathF.Round(inset * 100), (int)MathF.Round(Math.Clamp(opacity, 0f, 1f) * 255));
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            using var scaled = new Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = key.Item3 / 255f });
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(icon, new Rectangle(0, 0, pixels, pixels), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
            }
            var baked = new Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(baked))
            using (var brush = new TextureBrush(scaled, WrapMode.Clamp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.FillEllipse(brush, inset, inset, pixels - inset * 2, pixels - inset * 2);
            }
            // A handful of sizes per icon (DPI changes, hover states); bound it so it never grows.
            if (cache.Count >= 16)
            {
                foreach (var bitmap in cache.Values) bitmap.Dispose();
                cache.Clear();
            }
            cache[key] = baked;
            return baked;
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, Bitmap[]> Glyphs = new();

    /// <summary>
    /// Separates a provider mark from its icon plate: the dominant colour on the plate's rim is
    /// treated as background and every pixel's distance from it becomes the glyph alpha, tinted
    /// white on dark themes and slate on light themes.
    /// </summary>
    private static Bitmap WatermarkGlyph(Image icon, bool light)
    {
        var pair = Glyphs.GetValue(icon, _ => new Bitmap[2]);
        var slot = light ? 1 : 0;
        if (pair[slot] is { } cached) return cached;
        using var source = new Bitmap(icon);
        var width = source.Width;
        var height = source.Height;
        var counts = new Dictionary<int, int>();
        for (var step = 0; step < 72; step++)
        {
            var angle = step * Math.PI * 2 / 72;
            var x = (int)Math.Clamp(width / 2d + Math.Cos(angle) * width * .42, 0, width - 1);
            var y = (int)Math.Clamp(height / 2d + Math.Sin(angle) * height * .42, 0, height - 1);
            var pixel = source.GetPixel(x, y);
            if (pixel.A < 128) continue;
            var key = (pixel.R / 16 << 8) | (pixel.G / 16 << 4) | pixel.B / 16;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        Color? plate = null;
        if (counts.Count > 0)
        {
            var key = counts.MaxBy(entry => entry.Value).Key;
            plate = Color.FromArgb(((key >> 8) & 15) * 16 + 8, ((key >> 4) & 15) * 16 + 8, (key & 15) * 16 + 8);
        }

        var ink = light ? UiPalette.Slate900 : Color.White;
        var glyph = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = source.GetPixel(x, y);
                double strength;
                if (plate is { } background)
                {
                    var distance = Math.Sqrt(
                        Math.Pow(pixel.R - background.R, 2) + Math.Pow(pixel.G - background.G, 2) + Math.Pow(pixel.B - background.B, 2));
                    strength = Math.Clamp((distance - 24) / 110d, 0, 1) * pixel.A / 255d;
                }
                else
                {
                    strength = pixel.A / 255d;
                }

                glyph.SetPixel(x, y, Color.FromArgb((int)(strength * 255), ink));
            }
        }

        pair[slot] = glyph;
        return glyph;
    }

    private static void DrawChip(Graphics graphics, Rectangle bounds, GlassDockScene scene)
    {
        var light = scene.Light;
        var rect = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        using var path = Rounded(rect, rect.Height / 2);
        using var fill = new SolidBrush(scene.ChipHovered
            ? light ? UiPalette.Slate900.WithAlpha(70) : Color.FromArgb(70, 255, 255, 255)
            : light ? UiPalette.Slate900.WithAlpha(34) : Color.FromArgb(36, 255, 255, 255));
        graphics.FillPath(fill, path);
        using var text = new SolidBrush(light ? UiPalette.Slate900.WithAlpha(235) : Color.FromArgb(240, 255, 255, 255));
        using var font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel);
        var label = scene.Expanded ? "‹" : "+" + scene.Plan.OverflowCount.ToString(CultureInfo.InvariantCulture);
        DrawCentered(graphics, label, font, text, rect);
        if (scene.Expanded || !scene.Plan.OverflowUrgent) return;
        using var dot = new SolidBrush(Color.FromArgb(255, 80, 80));
        graphics.FillEllipse(dot, rect.Right - 5, rect.Top - 1, 6, 6);
    }

    /// <summary>The show/hide chip at the end of the expanded overflow: the same pill with an eye glyph.</summary>
    private static void DrawVisibilityChip(Graphics graphics, Rectangle bounds, GlassDockScene scene)
    {
        var light = scene.Light;
        var rect = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        using (var path = Rounded(rect, rect.Height / 2))
        using (var fill = new SolidBrush(scene.VisibilityHovered
            ? light ? UiPalette.Slate900.WithAlpha(70) : Color.FromArgb(70, 255, 255, 255)
            : light ? UiPalette.Slate900.WithAlpha(34) : Color.FromArgb(36, 255, 255, 255)))
        {
            graphics.FillPath(fill, path);
        }
        var ink = light ? UiPalette.Slate900.WithAlpha(235) : Color.FromArgb(240, 255, 255, 255);
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var w = Math.Min(rect.Width - 4, 11f);
        var h = w * .62f;
        using var pen = new Pen(ink, 1.3f) { LineJoin = LineJoin.Round };
        using (var eye = new GraphicsPath())
        {
            eye.AddBezier(cx - w / 2, cy, cx - w / 4, cy - h, cx + w / 4, cy - h, cx + w / 2, cy);
            eye.AddBezier(cx + w / 2, cy, cx + w / 4, cy + h, cx - w / 4, cy + h, cx - w / 2, cy);
            eye.CloseFigure();
            graphics.DrawPath(pen, eye);
        }
        using var pupil = new SolidBrush(ink);
        graphics.FillEllipse(pupil, cx - 1.8f, cy - 1.8f, 3.6f, 3.6f);
    }

    /// <summary>The four classic system metrics as labelled rows: CPU, RAM, I/O (disk) and GPU.</summary>
    private static void DrawMetrics(Graphics graphics, Rectangle bounds, GlassDockScene scene)
    {
        (string Label, double? Value)[] rows = [("CPU", scene.Cpu), ("RAM", scene.Memory), ("I/O", scene.Disk), ("GPU", scene.Gpu)];
        var rowHeight = bounds.Height / (float)rows.Length;
        using var labelFont = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var labelBrush = new SolidBrush(Muted(scene.Light));
        using var trackBrush = new SolidBrush(scene.Light ? UiPalette.Slate900.WithAlpha(36) : Color.FromArgb(40, 255, 255, 255));
        using var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        const float labelWidth = 20f;
        const float barHeight = 3.5f;
        for (var index = 0; index < rows.Length; index++)
        {
            var rowTop = bounds.Y + index * rowHeight;
            graphics.DrawString(rows[index].Label, labelFont, labelBrush, new RectangleF(bounds.X, rowTop, labelWidth + 2, rowHeight), format);
            var track = new RectangleF(bounds.X + labelWidth, rowTop + (rowHeight - barHeight) / 2f, bounds.Width - labelWidth, barHeight);
            graphics.FillRoundedRectangle(trackBrush, track, barHeight / 2f);
            if (rows[index].Value is not { } value || value <= 0) continue;
            var width = Math.Max(barHeight, track.Width * (float)Math.Clamp(value / 100d, 0, 1));
            using var ink = new SolidBrush(LoadColor(value, scene.Light));
            graphics.FillRoundedRectangle(ink, new RectangleF(track.X, track.Y, width, barHeight), barHeight / 2f);
        }
    }

    /// <summary>
    /// tok/s in a fixed slot: the number (constant size, one fixed decimal, right-aligned in a box sized
    /// for the widest value so the decimal point never moves) over a small "tok/s" caption, the pair
    /// centred vertically. No estimate marker: the live total is a sum of per-session rates either way.
    /// </summary>
    private static void DrawRate(Graphics graphics, Rectangle bounds, GlassDockScene scene)
    {
        var rate = FixedRateText(scene.TokensPerSecond);
        using var font = new Font("Segoe UI", RateFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", RateCaptionSize, FontStyle.Regular, GraphicsUnit.Pixel);
        var active = scene.TokensPerSecond is > 0;
        var color = scene.TokensPerSecond is null
            ? Muted(scene.Light)
            : scene.Light ? UiPalette.Slate900 : UiPalette.Slate50;
        using var text = new SolidBrush(active ? color : Color.FromArgb(scene.TokensPerSecond is null ? color.A : 170, color));
        using var muted = new SolidBrush(Muted(scene.Light));
        var box = RateNumberBox(graphics, bounds, font);
        using (var numberFormat = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = rate == "—" ? StringAlignment.Center : StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        })
        {
            graphics.DrawString(rate, font, text, box, numberFormat);
        }
        var label = new RectangleF(box.X - 2, box.Bottom + RateCaptionGap, box.Width + 4, RateCaptionHeight);
        graphics.DrawString("tok/s", small, muted, label, Format(StringAlignment.Center));
    }

    internal const float RateCaptionSize = 8f;
    private const float RateNumberHeight = 14f;
    private const float RateCaptionGap = 1f;
    private const float RateCaptionHeight = 10f;

    internal const float RateFontSize = 12.5f;

    /// <summary>Fixed number box for the rate: the widest value ("888.8" / "88.8K") at the rate font, centred in the slot.</summary>
    internal static RectangleF RateNumberBox(Graphics graphics, Rectangle bounds, Font font)
    {
        var widest = Math.Max(graphics.MeasureString("888.8", font, PointF.Empty, StringFormat.GenericTypographic).Width,
            graphics.MeasureString("88.8K", font, PointF.Empty, StringFormat.GenericTypographic).Width);
        var width = Math.Min(bounds.Width + 4f, widest + 1f);
        var top = bounds.Y + (bounds.Height - (RateNumberHeight + RateCaptionGap + RateCaptionHeight)) / 2f;
        return new RectangleF(bounds.X + (bounds.Width - width) / 2f, top, width, RateNumberHeight);
    }

    /// <summary>One fixed decimal (and K/M/G above 1000) so the text length only changes with magnitude.</summary>
    internal static string FixedRateText(double? value)
    {
        if (value is not { } number || !double.IsFinite(number) || number < 0) return "—";
        var (divisor, suffix) = number switch
        {
            >= 999.95e6 => (1e9, "G"),
            >= 999.95e3 => (1e6, "M"),
            >= 999.95 => (1e3, "K"),
            _ => (1d, ""),
        };
        var scaled = number / divisor;
        return (suffix.Length > 0 && scaled >= 99.95
            ? scaled.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : scaled.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) + suffix;
    }

    private static void DrawHover(Graphics graphics, RectangleF bounds, bool light)
    {
        using var path = Rounded(bounds, Math.Min(10, bounds.Height / 2));
        using var fill = new SolidBrush(light ? UiPalette.Slate900.WithAlpha(30) : Color.FromArgb(30, 255, 255, 255));
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

    /// <summary>Numbers stay neutral for colour restraint; only a critically low remainder turns red.</summary>
    private static Color ValueColor(GlassDockRing ring, bool light)
    {
        if (GlassDockLayout.Band(ring.RemainingPercent) == "red") return Band(ring.RemainingPercent, light);
        return light ? UiPalette.Slate900 : UiPalette.Slate50;
    }

    /// <summary>Colour of the thin moat drawn around badges so they separate cleanly from the ring.</summary>
    private static Color Separator(bool light) => light ? Color.FromArgb(240, 244, 246, 250) : Color.FromArgb(240, 34, 37, 48);

    private static Color Ink(bool light) => light ? UiPalette.Slate900 : Color.FromArgb(255, 255, 255);

    private static Color Muted(bool light) => light ? UiPalette.Slate700.WithAlpha(190) : UiPalette.Slate300.WithAlpha(190);

    private static Color LoadColor(double value, bool light) => value switch
    {
        > 80 => light ? UiPalette.Red600 : Color.FromArgb(255, 107, 107),
        > 50 => light ? Color.FromArgb(202, 138, 4) : Color.FromArgb(252, 196, 64),
        _ => light ? UiPalette.Emerald600 : UiPalette.Emerald400,
    };

    private static Color Band(double? remaining, bool light) => GlassDockLayout.Band(remaining) switch
    {
        "red" => light ? UiPalette.Red600 : Color.FromArgb(255, 99, 99),
        "amber" => light ? Color.FromArgb(194, 120, 3) : Color.FromArgb(252, 196, 64),
        "green" => light ? UiPalette.Emerald600 : Color.FromArgb(52, 220, 150),
        _ => light ? UiPalette.Slate500 : UiPalette.Slate300,
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

        return light ? Color.FromArgb(79, 70, 229) : UiPalette.Indigo400;
    }

    internal static GraphicsPath Rounded(RectangleF bounds, float radius) => UiPaint.RoundedPath(bounds, radius);
}
