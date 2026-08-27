using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class RadarPopoverRenderer : IDisposable
{
    private static readonly Color StrongestColor = Color.FromArgb(246, 196, 83);
    private static readonly Color ResetOpenColor = Color.FromArgb(251, 113, 133);
    private static readonly Color[] RecommendationColors =
    [
        Color.FromArgb(52, 211, 153),
        Color.FromArgb(34, 211, 238),
        Color.FromArgb(167, 139, 250),
        Color.FromArgb(244, 114, 182),
    ];
    private static readonly Color[] MultiDistinctionColors =
        [StrongestColor, .. RecommendationColors];
    private static readonly float[] MultiDistinctionStops = Enumerable.Range(
            0,
            MultiDistinctionColors.Length)
        .Select(index => index / (MultiDistinctionColors.Length - 1f))
        .ToArray();
    private const TextFormatFlags BaseTextFlags =
        TextFormatFlags.NoPadding
        | TextFormatFlags.NoPrefix
        | TextFormatFlags.SingleLine
        | TextFormatFlags.VerticalCenter
        | TextFormatFlags.PreserveGraphicsClipping;
    private const TextFormatFlags CellTextFlags = BaseTextFlags | TextFormatFlags.EndEllipsis;
    private RadarPopoverFonts? _fonts;

    public void Draw(
        Graphics graphics,
        RadarPopoverLayout layout,
        PopoverTailSide tailSide,
        int tailOffset,
        RadarViewState state,
        RadarPresentationResult? presentation,
        Image? logo,
        NativeText text,
        CodexTokenUsageSummary? tokenUsage = null,
        AiGatewayUsageSummary? aiGatewayUsage = null,
        bool pinned = false,
        string? radarTitle = null)
    {
        var fonts = Fonts(layout.Dpi);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        var body = BodyBounds(layout, tailSide);
        DrawBody(graphics, layout, tailSide, tailOffset, body);
        if (layout.TokenOnly)
        {
            if (tokenUsage is not null)
            {
                DrawTokenOverview(graphics, layout, body.Location, tokenUsage, logo, fonts, text, pinned);
            }
            return;
        }
        var title = radarTitle ?? text.RadarTitle;
        DrawHeader(graphics, layout, body.Location, state, logo, fonts, text, pinned, title);

        if (presentation is null)
        {
            var message = state.Loading
                ? text.RadarLoading
                : state.Error is { } error
                    ? text.RadarError(error)
                    : text.RadarHoverToFetch;
            DrawText(
                graphics,
                message,
                fonts.Model,
                Color.FromArgb(148, 163, 184),
                Offset(layout.EmptyBounds, body.Location),
                CellTextFlags);
        }
        else
        {
            DrawTableHeader(graphics, layout, body.Location, fonts, text);
            DrawRows(graphics, layout, body.Location, presentation, fonts, text);
            if (!layout.ErrorBounds.IsEmpty && state.Error is { } error)
            {
                DrawText(
                    graphics,
                    text.RadarError(error),
                    fonts.Meta,
                    Color.FromArgb(251, 191, 36),
                    Offset(layout.ErrorBounds, body.Location),
                    CellTextFlags);
            }
        }

        DrawFooter(
            graphics,
            layout,
            body.Location,
            state,
            fonts,
            text,
            tokenUsage,
            aiGatewayUsage,
            title);
    }

    private static void DrawTokenOverview(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        CodexTokenUsageSummary tokenUsage,
        Image? logo,
        RadarPopoverFonts fonts,
        NativeText text,
        bool pinned)
    {
        Rectangle Rect(int x, int y, int width, int height) => Rectangle.FromLTRB(
            origin.X + ScaleCoordinate(layout.Dpi, x),
            origin.Y + ScaleCoordinate(layout.Dpi, y),
            origin.X + ScaleCoordinate(layout.Dpi, x + width),
            origin.Y + ScaleCoordinate(layout.Dpi, y + height));

        if (logo is not null) graphics.DrawImage(logo, Offset(layout.LogoBounds, origin));
        DrawText(
            graphics,
            text.CodexTokenTitle,
            fonts.Title,
            Color.FromArgb(248, 250, 252),
            Offset(layout.TitleBounds, origin),
            CellTextFlags);
        DrawText(
            graphics,
            text.CodexTokenPopoverSubtitle(pinned),
            fonts.Badge,
            Color.FromArgb(148, 163, 184),
            Offset(layout.SubtitleBounds, origin),
            CellTextFlags);

        using var divider = new Pen(Color.FromArgb(38, 71, 85, 105));
        graphics.DrawLine(
            divider,
            origin.X + ScaleCoordinate(layout.Dpi, 12),
            origin.Y + ScaleCoordinate(layout.Dpi, 44),
            origin.X + ScaleCoordinate(layout.Dpi, 228),
            origin.Y + ScaleCoordinate(layout.Dpi, 44));

        var labelColor = Color.FromArgb(148, 163, 184);
        var valueColor = Color.FromArgb(226, 232, 240);
        var today = text.CodexTodayTokens(tokenUsage.TodayTokens);
        var local = text.CodexLocalTokens(tokenUsage.LocalTokens);
        var todayCache = text.CodexTodayCacheHitRate(tokenUsage.TodayCacheHitPercent);
        var totalCache = text.CodexTotalCacheHitRate(tokenUsage.TotalCacheHitPercent);
        DrawTokenMetricGroup(
            graphics,
            layout.Dpi,
            Rect(12, 50, 104, 72),
            text.CodexTokenMetricTitle,
            text.CodexTodayMetricLabel,
            today.Value,
            text.CodexTotalMetricLabel,
            local.Value,
            Color.FromArgb(129, 140, 248),
            labelColor,
            valueColor,
            fonts);
        DrawTokenMetricGroup(
            graphics,
            layout.Dpi,
            Rect(124, 50, 104, 72),
            text.CodexCacheMetricTitle,
            text.CodexTodayMetricLabel,
            todayCache.Value,
            text.CodexTotalMetricLabel,
            totalCache.Value,
            Color.FromArgb(52, 211, 153),
            labelColor,
            valueColor,
            fonts);
        DrawText(
            graphics,
            text.CodexTokenScope(tokenUsage.SessionCount),
            fonts.Meta,
            labelColor,
            Rect(12, 126, 216, 12),
            CellTextFlags);
    }

    private static void DrawTokenMetricGroup(
        Graphics graphics,
        int dpi,
        Rectangle bounds,
        string title,
        string firstLabel,
        string firstValue,
        string secondLabel,
        string secondValue,
        Color accentColor,
        Color labelColor,
        Color valueColor,
        RadarPopoverFonts fonts)
    {
        var surfaceBounds = new Rectangle(
            bounds.X,
            bounds.Y,
            Math.Max(1, bounds.Width - 1),
            Math.Max(1, bounds.Height - 1));
        using var surface = RoundedRectangle(surfaceBounds, Scale(dpi, 6));
        using var fill = new SolidBrush(Color.FromArgb(25, 30, 41, 59));
        using var border = new Pen(Color.FromArgb(55, 71, 85, 105));
        graphics.FillPath(fill, surface);
        graphics.DrawPath(border, surface);

        var padding = Scale(dpi, 8);
        var titleBounds = new Rectangle(
            bounds.Left + padding,
            bounds.Top + ScaleCoordinate(dpi, 4),
            Math.Max(1, bounds.Width - padding * 2),
            Scale(dpi, 14));
        DrawText(graphics, title, fonts.Badge, accentColor, titleBounds, CellTextFlags);

        using var divider = new Pen(Color.FromArgb(35, 71, 85, 105));
        var dividerY = bounds.Top + ScaleCoordinate(dpi, 20);
        graphics.DrawLine(divider, bounds.Left + padding, dividerY, bounds.Right - padding, dividerY);

        DrawTokenMetricRow(
            graphics,
            dpi,
            new Rectangle(bounds.Left + padding, bounds.Top + ScaleCoordinate(dpi, 23), bounds.Width - padding * 2, Scale(dpi, 18)),
            firstLabel,
            firstValue,
            labelColor,
            valueColor,
            fonts);
        DrawTokenMetricRow(
            graphics,
            dpi,
            new Rectangle(bounds.Left + padding, bounds.Top + ScaleCoordinate(dpi, 44), bounds.Width - padding * 2, Scale(dpi, 18)),
            secondLabel,
            secondValue,
            labelColor,
            valueColor,
            fonts);
    }

    private static void DrawTokenMetricRow(
        Graphics graphics,
        int dpi,
        Rectangle bounds,
        string label,
        string value,
        Color labelColor,
        Color valueColor,
        RadarPopoverFonts fonts)
    {
        var labelWidth = Scale(dpi, 34);
        DrawText(
            graphics,
            label,
            fonts.Model,
            labelColor,
            new Rectangle(bounds.Left, bounds.Top, labelWidth, bounds.Height),
            CellTextFlags);
        DrawText(
            graphics,
            value,
            fonts.EmphasizedNumber,
            valueColor,
            new Rectangle(bounds.Left + labelWidth, bounds.Top, Math.Max(1, bounds.Width - labelWidth), bounds.Height),
            CellTextFlags | TextFormatFlags.Right);
    }

    private static void DrawBody(
        Graphics graphics,
        RadarPopoverLayout layout,
        PopoverTailSide tailSide,
        int tailOffset,
        Rectangle body)
    {
        var pathBounds = new Rectangle(
            body.X,
            body.Y,
            Math.Max(1, body.Width - 1),
            Math.Max(1, body.Height - 1));
        using var bodyPath = RoundedRectangle(pathBounds, Scale(layout.Dpi, 10));
        using var fill = new SolidBrush(Color.FromArgb(7, 12, 24));
        using var border = new Pen(Color.FromArgb(86, 100, 116, 139));
        graphics.FillPath(fill, bodyPath);
        graphics.DrawPath(border, bodyPath);
        DrawTail(graphics, layout, tailSide, tailOffset, fill, border, body);
    }

    private static void DrawHeader(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        RadarViewState state,
        Image? logo,
        RadarPopoverFonts fonts,
        NativeText text,
        bool pinned,
        string title)
    {
        if (logo is not null) graphics.DrawImage(logo, Offset(layout.LogoBounds, origin));
        DrawText(
            graphics,
            title,
            fonts.Title,
            Color.FromArgb(248, 250, 252),
            Offset(layout.TitleBounds, origin),
            CellTextFlags);
        DrawText(
            graphics,
            text.RadarPopoverSubtitle(pinned),
            fonts.Badge,
            Color.FromArgb(129, 140, 248),
            Offset(layout.SubtitleBounds, origin),
            CellTextFlags);

        var now = DateTimeOffset.UtcNow;
        if (layout.HasOpenResetWindow && state.Snapshot?.ResetWindow is { Open: true } openWindow)
        {
            DrawOpenResetWindowBanner(graphics, layout, origin, openWindow, fonts, text, now);
        }
        else
        {
            DrawResetWindow(
                graphics,
                layout,
                origin,
                state.Snapshot?.ResetWindow,
                fonts,
                text,
                now);
        }

        var fresh = !state.IsStale(now) && state.Error is null;
        var stateLabel = text.RadarState(state, now);
        var checkedLabel = text.RadarChecked(state.LastSuccessfulFetchAt, now);
        DrawText(
            graphics,
            $"{stateLabel} · {checkedLabel}",
            fonts.Badge,
            fresh ? Color.FromArgb(52, 211, 153) : Color.FromArgb(251, 191, 36),
            Offset(layout.StateBounds, origin),
            BaseTextFlags | TextFormatFlags.Right);
    }

    private static void DrawResetWindow(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        RadarResetWindow? window,
        RadarPopoverFonts fonts,
        NativeText text,
        DateTimeOffset now)
    {
        var label = text.RadarResetWindow(window, now);
        if (string.IsNullOrWhiteSpace(label)) return;

        var bounds = Offset(layout.ResetBounds, origin);
        var open = window?.Open == true;
        var font = open ? fonts.ResetOpen : fonts.Badge;
        var color = open ? ResetOpenColor : Color.FromArgb(148, 163, 184);
        if (open)
        {
            var labelWidth = TextRenderer.MeasureText(
                graphics,
                label,
                font,
                Size.Empty,
                BaseTextFlags).Width;
            var diameter = Scale(layout.Dpi, 6);
            var gap = Scale(layout.Dpi, 4);
            var dotX = Math.Max(
                bounds.Left,
                bounds.Right - Math.Min(bounds.Width, labelWidth) - gap - diameter);
            var dotY = bounds.Top + (bounds.Height - diameter) / 2;
            DrawFilledCircle(
                graphics,
                new Rectangle(dotX, dotY, diameter, diameter),
                ResetOpenColor);
        }

        DrawText(
            graphics,
            label,
            font,
            color,
            bounds,
            CellTextFlags | TextFormatFlags.Right);
    }

    private static void DrawOpenResetWindowBanner(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        RadarResetWindow window,
        RadarPopoverFonts fonts,
        NativeText text,
        DateTimeOffset now)
    {
        var bounds = Offset(layout.ResetBounds, origin);
        using var path = RoundedRectangle(bounds, Scale(layout.Dpi, 7));
        using var fill = new SolidBrush(Color.FromArgb(28, 251, 113, 133));
        using var border = new Pen(Color.FromArgb(116, 251, 113, 133));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        var padding = Scale(layout.Dpi, 12);
        var diameter = Scale(layout.Dpi, 7);
        var gap = Scale(layout.Dpi, 7);
        var label = text.RadarResetWindow(window, now);
        var availableLabelWidth = Math.Max(
            1,
            bounds.Width - padding * 2 - diameter - gap);
        var labelWidth = Math.Min(
            availableLabelWidth,
            TextRenderer.MeasureText(
                graphics,
                label,
                fonts.Title,
                Size.Empty,
                BaseTextFlags).Width);
        var contentWidth = diameter + gap + labelWidth;
        var contentX = bounds.Left + (bounds.Width - contentWidth) / 2;
        var dotBounds = new Rectangle(
            contentX,
            bounds.Top + (bounds.Height - diameter) / 2,
            diameter,
            diameter);
        DrawFilledCircle(graphics, dotBounds, ResetOpenColor);

        DrawText(
            graphics,
            label,
            fonts.Title,
            ResetOpenColor,
            new Rectangle(
                dotBounds.Right + gap,
                bounds.Top,
                labelWidth,
                bounds.Height),
            CellTextFlags);
    }

    private static void DrawTableHeader(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        RadarPopoverFonts fonts,
        NativeText text)
    {
        var color = Color.FromArgb(148, 163, 184);
        DrawColumnText(graphics, text.RadarModelHeader, fonts.Badge, color, layout.Columns.Model, layout.TableHeaderBounds, origin);
        DrawColumnText(graphics, text.RadarIqHeader, fonts.Badge, color, layout.Columns.IqCurrent, layout.TableHeaderBounds, origin);
        DrawColumnText(graphics, text.RadarIqAverageHeader, fonts.Badge, color, layout.Columns.IqAverage, layout.TableHeaderBounds, origin);
        DrawColumnText(graphics, text.RadarSampleHeader, fonts.Badge, color, layout.Columns.Samples, layout.TableHeaderBounds, origin);
        DrawColumnText(graphics, text.RadarAverageHeader, fonts.Badge, color, layout.Columns.AverageTime, layout.TableHeaderBounds, origin);
        DrawColumnText(graphics, text.RadarCostHeader, fonts.Badge, color, layout.Columns.Cost, layout.TableHeaderBounds, origin);

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.None;
        using var separator = new Pen(Color.FromArgb(38, 71, 85, 105));
        graphics.DrawLine(
            separator,
            origin.X + layout.TableHeaderBounds.Left,
            origin.Y + layout.SeparatorY,
            origin.X + layout.TableHeaderBounds.Right - 1,
            origin.Y + layout.SeparatorY);
        graphics.SmoothingMode = previousSmoothing;
    }

    private static void DrawRows(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        RadarPresentationResult presentation,
        RadarPopoverFonts fonts,
        NativeText text)
    {
        var count = Math.Min(presentation.Rows.Count, layout.RowBounds.Count);
        for (var index = 0; index < count; index++)
        {
            var row = presentation.Rows[index];
            var rowBounds = layout.RowBounds[index];
            var strongest = row.Rank == 1;
            var distinctionCount = (strongest ? 1 : 0)
                + row.RecommendationGroupIndexes.Count;
            var multipleDistinctions = distinctionCount > 1;
            var distinguished = distinctionCount > 0;
            var labelColor = Color.FromArgb(226, 232, 240);
            var recommendationColor = row.RecommendationGroupIndexes.Count == 0
                ? labelColor
                : RecommendationColor(row.RecommendationGroupIndexes[0]);
            var modelColor = strongest
                ? StrongestColor
                : recommendationColor;
            var modelFont = distinguished ? fonts.EmphasizedModel : fonts.Model;
            var numberFont = distinguished ? fonts.EmphasizedNumber : fonts.Number;

            if (multipleDistinctions)
            {
                var highlightBounds = Offset(rowBounds, origin);
                using var highlightPath = RoundedRectangle(
                    new Rectangle(
                        highlightBounds.X,
                        highlightBounds.Y,
                        Math.Max(1, highlightBounds.Width - 1),
                        Math.Max(1, highlightBounds.Height - 1)),
                    Scale(layout.Dpi, 3));
                DrawRainbowSurface(
                    graphics,
                    highlightPath,
                    highlightBounds,
                    Scale(layout.Dpi, 1),
                    24,
                    168);
            }
            else if (distinguished)
            {
                var highlightColor = strongest ? StrongestColor : recommendationColor;
                using var highlight = new SolidBrush(Color.FromArgb(18, highlightColor));
                graphics.FillRectangle(highlight, Offset(rowBounds, origin));
            }
            DrawStatusIndicator(
                graphics,
                layout,
                row.Indicator,
                Center(layout.Columns.Status.InRow(rowBounds), origin));
            if (multipleDistinctions)
            {
                DrawRainbowText(
                    graphics,
                    row.ModelText,
                    modelFont,
                    Offset(layout.Columns.Model.InRow(rowBounds), origin));
            }
            else
            {
                DrawColumnText(graphics, row.ModelText, modelFont, modelColor, layout.Columns.Model, rowBounds, origin);
            }
            if (distinguished)
            {
                DrawDistinctionIcons(
                    graphics,
                    layout,
                    Center(layout.Columns.Marker.InRow(rowBounds), origin),
                    strongest,
                    row.RecommendationGroupIndexes);
            }
            var comparison = row.IqComparison;
            DrawColumnText(
                graphics,
                comparison is null
                    ? row.ScoreText
                    : $"{row.ScoreText} {comparison.Value.DirectionText}",
                numberFont,
                StatusColor(row.Indicator),
                layout.Columns.IqCurrent,
                rowBounds,
                origin);
            if (comparison is { } value)
            {
                DrawColumnText(
                    graphics,
                    value.AverageText,
                    numberFont,
                    StatusColor(row.Indicator),
                    layout.Columns.IqAverage,
                    rowBounds,
                    origin);
            }
            DrawColumnText(graphics, row.SampleCountText, numberFont, labelColor, layout.Columns.Samples, rowBounds, origin);
            DrawColumnText(graphics, text.RadarAverageTime(row.Model), numberFont, labelColor, layout.Columns.AverageTime, rowBounds, origin);
            DrawColumnText(graphics, row.AverageCostText, numberFont, labelColor, layout.Columns.Cost, rowBounds, origin);
        }
    }

    private static void DrawRainbowSurface(
        Graphics graphics,
        GraphicsPath path,
        Rectangle spectrumBounds,
        int borderWidth,
        int fillAlpha,
        int borderAlpha)
    {
        using var glow = RainbowBrush(spectrumBounds, fillAlpha);
        using var borderBrush = RainbowBrush(spectrumBounds, borderAlpha);
        using var border = new Pen(borderBrush, borderWidth) { Alignment = PenAlignment.Inset };
        graphics.FillPath(glow, path);
        graphics.DrawPath(border, path);
    }

    private static LinearGradientBrush RainbowBrush(Rectangle bounds, int alpha)
    {
        var brush = new LinearGradientBrush(
            bounds,
            Color.FromArgb(alpha, MultiDistinctionColors[0]),
            Color.FromArgb(alpha, MultiDistinctionColors[^1]),
            LinearGradientMode.Horizontal);
        brush.InterpolationColors = new ColorBlend(MultiDistinctionColors.Length)
        {
            Colors = MultiDistinctionColors
                .Select(color => Color.FromArgb(alpha, color))
                .ToArray(),
            Positions = MultiDistinctionStops,
        };
        return brush;
    }

    private static void DrawRainbowText(
        Graphics graphics,
        string text,
        Font font,
        Rectangle bounds)
    {
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            HotkeyPrefix = HotkeyPrefix.None,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        var measured = graphics.MeasureString(text, font, PointF.Empty, format);
        var spectrumBounds = new Rectangle(
            bounds.X,
            bounds.Y,
            Math.Min(bounds.Width, Math.Max(1, (int)Math.Ceiling(measured.Width))),
            bounds.Height);
        using var brush = RainbowBrush(spectrumBounds, 255);
        var previousHint = graphics.TextRenderingHint;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString(text, font, brush, bounds, format);
        graphics.TextRenderingHint = previousHint;
    }

    private static void DrawDistinctionIcons(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point center,
        bool strongest,
        IReadOnlyList<int> recommendationGroupIndexes)
    {
        var visibleGroups = recommendationGroupIndexes.Take(4).ToArray();
        var count = (strongest ? 1 : 0) + visibleGroups.Length;
        var radius = Scale(layout.Dpi, count >= 4 ? 2 : 3);
        var spacing = Scale(layout.Dpi, count >= 4 ? 5 : 7);
        var x = center.X - spacing * (count - 1) / 2;
        if (strongest)
        {
            DrawStar(graphics, new Point(x, center.Y), radius + 1, StrongestColor);
            x += spacing;
        }
        foreach (var groupIndex in visibleGroups)
        {
            DrawRecommendationMarker(
                graphics,
                new Point(x, center.Y),
                radius,
                groupIndex);
            x += spacing;
        }
    }

    private static void DrawStar(Graphics graphics, Point center, int radius, Color color)
    {
        var points = Enumerable.Range(0, 10)
            .Select(index =>
            {
                var angle = -Math.PI / 2 + index * Math.PI / 5;
                var distance = index % 2 == 0 ? radius : radius * 0.45;
                return new PointF(
                    center.X + (float)(Math.Cos(angle) * distance),
                    center.Y + (float)(Math.Sin(angle) * distance));
            })
            .ToArray();
        using var brush = new SolidBrush(color);
        graphics.FillPolygon(brush, points);
    }

    private static void DrawRecommendationMarker(
        Graphics graphics,
        Point center,
        int radius,
        int groupIndex)
    {
        var markerIndex = Math.Abs(groupIndex % RecommendationColors.Length);
        var color = RecommendationColors[markerIndex];
        using var brush = new SolidBrush(color);
        switch (markerIndex)
        {
            case 1:
                graphics.FillPolygon(
                    brush,
                    [
                        new Point(center.X, center.Y - radius),
                        new Point(center.X + radius, center.Y),
                        new Point(center.X, center.Y + radius),
                        new Point(center.X - radius, center.Y),
                    ]);
                break;
            case 2:
                graphics.FillPolygon(
                    brush,
                    [
                        new Point(center.X, center.Y - radius),
                        new Point(center.X + radius, center.Y + radius),
                        new Point(center.X - radius, center.Y + radius),
                    ]);
                break;
            case 3:
                graphics.FillRectangle(
                    brush,
                    center.X - radius,
                    center.Y - radius,
                    radius * 2,
                    radius * 2);
                break;
            default:
                graphics.FillEllipse(
                    brush,
                    center.X - radius,
                    center.Y - radius,
                    radius * 2,
                    radius * 2);
                break;
        }
    }

    private static Color RecommendationColor(int groupIndex) =>
        RecommendationColors[Math.Abs(groupIndex % RecommendationColors.Length)];

    private static void DrawFooter(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        RadarViewState state,
        RadarPopoverFonts fonts,
        NativeText text,
        CodexTokenUsageSummary? tokenUsage,
        AiGatewayUsageSummary? aiGatewayUsage,
        string title)
    {
        var footerColor = Color.FromArgb(148, 163, 184);
        var legendBounds = Offset(layout.FooterLegendBounds, origin);
        using (var divider = new Pen(Color.FromArgb(42, 71, 85, 105)))
        {
            var dividerY = legendBounds.Top - Scale(layout.Dpi, 2);
            graphics.DrawLine(divider, legendBounds.Left, dividerY, legendBounds.Right, dividerY);
        }
        if (tokenUsage is null && aiGatewayUsage is null)
        {
            DrawText(
                graphics,
                $"{title} · {text.RadarSource} {text.RadarSourceTime(state.Snapshot?.SourceUpdatedAt)}",
                fonts.Meta,
                footerColor,
                Offset(layout.FooterSourceBounds, origin),
                CellTextFlags);
        }
        else if (tokenUsage is not null)
        {
            DrawTokenRadarFooter(
                graphics,
                layout,
                origin,
                tokenUsage,
                fonts,
                text,
                footerColor);
        }
        else
        {
            DrawGatewayUsageRadarFooter(
                graphics,
                layout,
                origin,
                aiGatewayUsage!,
                fonts,
                text,
                footerColor);
        }

        var items = new[]
        {
            (Strongest: false, UnknownStatus: true, GroupIndex: -1, Label: text.RadarUnknownStatusLegend),
            (Strongest: true, UnknownStatus: false, GroupIndex: -1, Label: text.RadarStrongestTitle),
            (Strongest: false, UnknownStatus: false, GroupIndex: 0, Label: text.RadarDailyScenarioTitle),
            (Strongest: false, UnknownStatus: false, GroupIndex: 1, Label: text.RadarPlanningScenarioTitle),
            (Strongest: false, UnknownStatus: false, GroupIndex: 2, Label: text.RadarExecutionScenarioTitle),
            (Strongest: false, UnknownStatus: false, GroupIndex: 3, Label: text.RadarBackgroundScenarioTitle),
        };
        var markerWidth = Scale(layout.Dpi, 8);
        var markerGap = Scale(layout.Dpi, 3);
        var itemGap = Scale(layout.Dpi, 4);
        var widths = items
            .Select(item => markerWidth
                + markerGap
                + TextRenderer.MeasureText(
                    graphics,
                    item.Label,
                    fonts.Meta,
                    Size.Empty,
                    BaseTextFlags).Width)
            .ToArray();
        var totalWidth = widths.Sum() + itemGap * (items.Length - 1);
        var x = Math.Max(legendBounds.Left, legendBounds.Right - totalWidth);
        var centerY = legendBounds.Top + legendBounds.Height / 2;
        var noteGap = Scale(layout.Dpi, 8);
        DrawText(
            graphics,
            text.RadarConfidenceNote,
            fonts.Meta,
            footerColor,
            new Rectangle(
                legendBounds.Left,
                legendBounds.Top,
                Math.Max(0, x - legendBounds.Left - noteGap),
                legendBounds.Height),
            BaseTextFlags);
        for (var index = 0; index < items.Length; index++)
        {
            var iconCenter = new Point(x + markerWidth / 2, centerY);
            if (items[index].UnknownStatus)
            {
                DrawStatusIndicator(
                    graphics,
                    layout,
                    RadarStatusIndicator.Unknown,
                    iconCenter);
            }
            else if (items[index].Strongest)
            {
                DrawStar(
                    graphics,
                    iconCenter,
                    Scale(layout.Dpi, 3),
                    StrongestColor);
            }
            else
            {
                DrawRecommendationMarker(
                    graphics,
                    iconCenter,
                    Scale(layout.Dpi, 2.5),
                    items[index].GroupIndex);
            }
            x += markerWidth + markerGap;
            var textBounds = new Rectangle(
                x,
                legendBounds.Top,
                widths[index] - markerWidth - markerGap,
                legendBounds.Height);
            DrawText(
                graphics,
                items[index].Label,
                fonts.Meta,
                footerColor,
                textBounds,
                BaseTextFlags);
            x += textBounds.Width + itemGap;
        }
    }

    private static void DrawTokenRadarFooter(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        CodexTokenUsageSummary tokenUsage,
        RadarPopoverFonts fonts,
        NativeText text,
        Color labelColor)
    {
        var bounds = Offset(layout.FooterSourceBounds, origin);
        var groupGap = Scale(layout.Dpi, 16);
        var groupWidth = (bounds.Width - groupGap) / 2;
        var valueColor = Color.FromArgb(226, 232, 240);
        DrawTokenRadarFooterGroup(
            graphics,
            layout.Dpi,
            new Rectangle(bounds.Left, bounds.Top, groupWidth, bounds.Height),
            text.CodexTokenRadarMetricTitle,
            text.CodexTodayMetricLabel,
            NativeText.FormatTokenCount(tokenUsage.TodayTokens),
            text.CodexTotalMetricLabel,
            NativeText.FormatTokenCount(tokenUsage.LocalTokens),
            Color.FromArgb(129, 140, 248),
            labelColor,
            valueColor,
            fonts);
        DrawTokenRadarFooterGroup(
            graphics,
            layout.Dpi,
            new Rectangle(bounds.Right - groupWidth, bounds.Top, groupWidth, bounds.Height),
            text.CodexCacheRadarMetricTitle,
            text.CodexTodayMetricLabel,
            NativeText.FormatCacheHitPercent(tokenUsage.TodayCacheHitPercent),
            text.CodexTotalMetricLabel,
            NativeText.FormatCacheHitPercent(tokenUsage.TotalCacheHitPercent),
            Color.FromArgb(52, 211, 153),
            labelColor,
            valueColor,
            fonts);
    }

    private static void DrawGatewayUsageRadarFooter(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        AiGatewayUsageSummary usage,
        RadarPopoverFonts fonts,
        NativeText text,
        Color labelColor)
    {
        var bounds = Offset(layout.FooterSourceBounds, origin);
        var groupGap = Scale(layout.Dpi, 16);
        var groupWidth = (bounds.Width - groupGap) / 2;
        var valueColor = Color.FromArgb(226, 232, 240);
        DrawTokenRadarFooterGroup(
            graphics,
            layout.Dpi,
            new Rectangle(bounds.Left, bounds.Top, groupWidth, bounds.Height),
            text.AiGatewayTokenRadarMetricTitle,
            text.CodexTodayMetricLabel,
            NativeText.FormatTokenCount(usage.Today.TotalTokens),
            text.CodexTotalMetricLabel,
            NativeText.FormatTokenCount(usage.Total.TotalTokens),
            Color.FromArgb(129, 140, 248),
            labelColor,
            valueColor,
            fonts);
        DrawTokenRadarFooterGroup(
            graphics,
            layout.Dpi,
            new Rectangle(bounds.Right - groupWidth, bounds.Top, groupWidth, bounds.Height),
            text.AiGatewayCacheRadarMetricTitle,
            text.CodexTodayMetricLabel,
            NativeText.FormatCacheHitPercent(ToDouble(usage.Today.CacheHitRatePercent)),
            text.CodexTotalMetricLabel,
            NativeText.FormatCacheHitPercent(ToDouble(usage.Total.CacheHitRatePercent)),
            Color.FromArgb(52, 211, 153),
            labelColor,
            valueColor,
            fonts);
    }

    private static double? ToDouble(decimal? value) =>
        value is { } parsed ? (double)parsed : null;

    private static void DrawTokenRadarFooterGroup(
        Graphics graphics,
        int dpi,
        Rectangle bounds,
        string metric,
        string todayLabel,
        string todayValue,
        string totalLabel,
        string totalValue,
        Color accentColor,
        Color labelColor,
        Color valueColor,
        RadarPopoverFonts fonts)
    {
        var surfaceBounds = new Rectangle(
            bounds.X,
            bounds.Y,
            Math.Max(1, bounds.Width - 1),
            Math.Max(1, bounds.Height - 1));
        using var surface = RoundedRectangle(surfaceBounds, Scale(dpi, 5));
        using var fill = new SolidBrush(Color.FromArgb(18, accentColor.R, accentColor.G, accentColor.B));
        using var border = new Pen(Color.FromArgb(42, accentColor.R, accentColor.G, accentColor.B));
        graphics.FillPath(fill, surface);
        graphics.DrawPath(border, surface);

        var padding = Scale(dpi, 8);
        var metricWidth = Scale(dpi, 36);
        var fieldGap = Scale(dpi, 6);
        var innerBounds = new Rectangle(
            bounds.Left + padding,
            bounds.Top,
            Math.Max(1, bounds.Width - padding * 2),
            bounds.Height);
        DrawText(
            graphics,
            metric,
            fonts.EmphasizedModel,
            accentColor,
            new Rectangle(innerBounds.Left, innerBounds.Top, metricWidth, innerBounds.Height),
            CellTextFlags);

        var fieldsLeft = innerBounds.Left + metricWidth + fieldGap;
        var interFieldGap = Scale(dpi, 14);
        var fieldWidth = Math.Max(1, (innerBounds.Right - fieldsLeft - interFieldGap) / 2);
        DrawTokenRadarFooterField(
            graphics,
            dpi,
            new Rectangle(fieldsLeft, innerBounds.Top, fieldWidth, innerBounds.Height),
            todayLabel,
            todayValue,
            labelColor,
            valueColor,
            fonts);
        DrawTokenRadarFooterField(
            graphics,
            dpi,
            new Rectangle(
                fieldsLeft + fieldWidth + interFieldGap,
                innerBounds.Top,
                innerBounds.Right - fieldsLeft - fieldWidth - interFieldGap,
                innerBounds.Height),
            totalLabel,
            totalValue,
            labelColor,
            valueColor,
            fonts);

        using var divider = new Pen(Color.FromArgb(38, accentColor.R, accentColor.G, accentColor.B));
        var dividerX = fieldsLeft + fieldWidth + interFieldGap / 2;
        var dividerInset = Scale(dpi, 6);
        graphics.DrawLine(
            divider,
            dividerX,
            bounds.Top + dividerInset,
            dividerX,
            bounds.Bottom - dividerInset);
    }

    private static void DrawTokenRadarFooterField(
        Graphics graphics,
        int dpi,
        Rectangle bounds,
        string label,
        string value,
        Color labelColor,
        Color valueColor,
        RadarPopoverFonts fonts)
    {
        var labelWidth = TextRenderer.MeasureText(
            graphics,
            label,
            fonts.Meta,
            Size.Empty,
            BaseTextFlags).Width;
        var gap = Scale(dpi, 3);
        DrawText(
            graphics,
            label,
            fonts.Meta,
            labelColor,
            new Rectangle(bounds.Left, bounds.Top, labelWidth, bounds.Height),
            CellTextFlags);
        DrawText(
            graphics,
            value,
            fonts.EmphasizedNumber,
            valueColor,
            new Rectangle(
                bounds.Left + labelWidth + gap,
                bounds.Top,
                Math.Max(1, bounds.Width - labelWidth - gap),
                bounds.Height),
            CellTextFlags | TextFormatFlags.Right);
    }

    private static void DrawColumnText(
        Graphics graphics,
        string text,
        Font font,
        Color color,
        RadarPopoverColumn column,
        Rectangle row,
        Point origin)
    {
        DrawText(graphics, text, font, color, Offset(column.InRow(row), origin), CellTextFlags);
    }

    private static void DrawStatusIndicator(
        Graphics graphics,
        RadarPopoverLayout layout,
        RadarStatusIndicator indicator,
        Point center)
    {
        var color = StatusColor(indicator);
        var radius = Scale(layout.Dpi, indicator == RadarStatusIndicator.Watch ? 4 : 3);
        using var brush = new SolidBrush(color);
        using var pen = new Pen(color);
        switch (indicator)
        {
            case RadarStatusIndicator.Stable:
                DrawFilledCircle(
                    graphics,
                    new Rectangle(center.X - radius, center.Y - radius, radius * 2, radius * 2),
                    color);
                break;
            case RadarStatusIndicator.Watch:
                graphics.FillPolygon(
                    brush,
                    [
                        new Point(center.X, center.Y - radius),
                        new Point(center.X + radius, center.Y + radius - 1),
                        new Point(center.X - radius, center.Y + radius - 1),
                    ]);
                break;
            case RadarStatusIndicator.Degraded:
                radius = Scale(layout.Dpi, 4);
                graphics.FillPolygon(
                    brush,
                    [
                        new Point(center.X, center.Y - radius),
                        new Point(center.X + radius, center.Y),
                        new Point(center.X, center.Y + radius),
                        new Point(center.X - radius, center.Y),
                    ]);
                break;
            default:
                graphics.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2, radius * 2);
                break;
        }
    }

    private static void DrawFilledCircle(Graphics graphics, Rectangle bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, bounds);
    }

    private static Color StatusColor(RadarStatusIndicator indicator) => indicator switch
    {
        RadarStatusIndicator.Stable => Color.FromArgb(52, 211, 153),
        RadarStatusIndicator.Watch => Color.FromArgb(251, 191, 36),
        RadarStatusIndicator.Degraded => Color.FromArgb(251, 113, 133),
        _ => Color.FromArgb(148, 163, 184),
    };

    private static void DrawTail(
        Graphics graphics,
        RadarPopoverLayout layout,
        PopoverTailSide tailSide,
        int tailOffset,
        Brush fill,
        Pen border,
        Rectangle body)
    {
        var points = TailPoints(layout, tailSide, tailOffset, body);
        graphics.FillPolygon(fill, points);
        graphics.DrawLines(border, points);
    }

    internal static Region CreateWindowRegion(
        RadarPopoverLayout layout,
        PopoverTailSide tailSide,
        int tailOffset)
    {
        var body = BodyBounds(layout, tailSide);
        var pathBounds = new Rectangle(
            body.X,
            body.Y,
            Math.Max(1, body.Width - 1),
            Math.Max(1, body.Height - 1));
        using var bodyPath = RoundedRectangle(pathBounds, Scale(layout.Dpi, 10));
        using var tailPath = new GraphicsPath();
        tailPath.AddPolygon(TailPoints(layout, tailSide, tailOffset, body));
        var region = new Region(bodyPath);
        region.Union(tailPath);
        return region;
    }

    private static Point[] TailPoints(
        RadarPopoverLayout layout,
        PopoverTailSide tailSide,
        int tailOffset,
        Rectangle body)
    {
        var tail = layout.TailSize;
        return tailSide switch
        {
            PopoverTailSide.Top =>
                [new(tailOffset - tail, body.Top), new(tailOffset, 0), new(tailOffset + tail, body.Top)],
            PopoverTailSide.Bottom =>
                [
                    new(tailOffset - tail, body.Bottom - 1),
                    new(tailOffset, body.Bottom + tail - 1),
                    new(tailOffset + tail, body.Bottom - 1),
                ],
            PopoverTailSide.Left =>
                [new(body.Left, tailOffset - tail), new(0, tailOffset), new(body.Left, tailOffset + tail)],
            _ =>
                [
                    new(body.Right - 1, tailOffset - tail),
                    new(body.Right + tail - 1, tailOffset),
                    new(body.Right - 1, tailOffset + tail),
                ],
        };
    }

    private static Rectangle BodyBounds(RadarPopoverLayout layout, PopoverTailSide tailSide) =>
        tailSide switch
        {
            PopoverTailSide.Top => new Rectangle(0, layout.TailSize, layout.BodySize.Width, layout.BodySize.Height),
            PopoverTailSide.Left => new Rectangle(layout.TailSize, 0, layout.BodySize.Width, layout.BodySize.Height),
            _ => new Rectangle(0, 0, layout.BodySize.Width, layout.BodySize.Height),
        };

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(2, radius * 2);
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawText(
        Graphics graphics,
        string? text,
        Font font,
        Color color,
        Rectangle bounds,
        TextFormatFlags flags)
    {
        TextRenderer.DrawText(graphics, text ?? string.Empty, font, bounds, color, flags);
    }

    private RadarPopoverFonts Fonts(int dpi)
    {
        if (_fonts?.Dpi == dpi) return _fonts;
        _fonts?.Dispose();
        _fonts = new RadarPopoverFonts(dpi);
        return _fonts;
    }

    private static Rectangle Offset(Rectangle bounds, Point origin) =>
        new(bounds.X + origin.X, bounds.Y + origin.Y, bounds.Width, bounds.Height);

    private static Point Center(Rectangle bounds, Point origin) =>
        new(origin.X + bounds.Left + bounds.Width / 2, origin.Y + bounds.Top + bounds.Height / 2);

    private static int Scale(int dpi, double value) => Math.Max(
        1,
        (int)Math.Round(value * dpi / 96d, MidpointRounding.AwayFromZero));

    private static int ScaleCoordinate(int dpi, double value) =>
        (int)Math.Round(value * dpi / 96d, MidpointRounding.AwayFromZero);

    public void Dispose()
    {
        _fonts?.Dispose();
        _fonts = null;
    }

    private sealed class RadarPopoverFonts : IDisposable
    {
        public RadarPopoverFonts(int dpi)
        {
            Dpi = dpi;
            Title = Create("Segoe UI", 11, FontStyle.Bold, dpi);
            Meta = Create("Segoe UI", 8, FontStyle.Regular, dpi);
            Badge = Create("Segoe UI", 7, FontStyle.Bold, dpi);
            ResetOpen = Create("Segoe UI", 8.5, FontStyle.Bold, dpi);
            Model = Create("Segoe UI", 8.5, FontStyle.Regular, dpi);
            EmphasizedModel = Create("Segoe UI", 8.5, FontStyle.Bold, dpi);
            Number = Create("Cascadia Mono", 8.5, FontStyle.Regular, dpi);
            EmphasizedNumber = Create("Cascadia Mono", 8.5, FontStyle.Bold, dpi);
        }

        public int Dpi { get; }
        public Font Title { get; }
        public Font Meta { get; }
        public Font Badge { get; }
        public Font ResetOpen { get; }
        public Font Model { get; }
        public Font EmphasizedModel { get; }
        public Font Number { get; }
        public Font EmphasizedNumber { get; }

        public void Dispose()
        {
            Title.Dispose();
            Meta.Dispose();
            Badge.Dispose();
            ResetOpen.Dispose();
            Model.Dispose();
            EmphasizedModel.Dispose();
            Number.Dispose();
            EmphasizedNumber.Dispose();
        }

        private static Font Create(string family, double logicalPixels, FontStyle style, int dpi) =>
            new(
                family,
                Math.Max(
                    1,
                    (float)Math.Round(logicalPixels * dpi / 96d, MidpointRounding.AwayFromZero)),
                style,
                GraphicsUnit.Pixel);
    }
}
