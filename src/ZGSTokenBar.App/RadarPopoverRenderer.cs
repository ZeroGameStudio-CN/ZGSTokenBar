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
        string? radarTitle = null,
        bool spendCardHovered = false)
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
                DrawTokenOverview(
                    graphics,
                    layout,
                    body.Location,
                    tokenUsage,
                    logo,
                    fonts,
                    text,
                    pinned,
                    spendCardHovered);
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
            title,
            spendCardHovered);
    }

    public void DrawSpendHistory(
        Graphics graphics,
        CodexSpendHistoryLayout layout,
        PopoverTailSide tailSide,
        int tailOffset,
        CodexTokenUsageSummary tokenUsage,
        Image? logo,
        NativeText text,
        int selectedDayIndex = -1,
        bool pinned = false)
    {
        var fonts = Fonts(layout.Dpi);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        var body = layout.BodyBounds(tailSide);
        DrawHistoryBody(graphics, layout, tailSide, tailOffset, body);
        var origin = body.Location;
        if (logo is not null) graphics.DrawImage(logo, Offset(layout.LogoBounds, origin));

        DrawText(
            graphics,
            text.CodexSpendHistoryTitle,
            fonts.Title,
            Color.FromArgb(248, 250, 252),
            Offset(layout.TitleBounds, origin),
            CellTextFlags);
        DrawText(
            graphics,
            text.CodexSpendHistorySubtitle(tokenUsage.SessionCount),
            fonts.Meta,
            pinned ? Color.FromArgb(165, 180, 252) : Color.FromArgb(148, 163, 184),
            Offset(layout.SubtitleBounds, origin),
            CellTextFlags);
        DrawText(
            graphics,
            text.CodexSpendHistoryBack,
            fonts.Badge,
            Color.FromArgb(165, 180, 252),
            Offset(layout.BackBounds, origin),
            CellTextFlags | TextFormatFlags.Right);

        using (var divider = new Pen(Color.FromArgb(42, 71, 85, 105)))
        {
            var dividerY = origin.Y + ScaleCoordinate(layout.Dpi, 44);
            graphics.DrawLine(
                divider,
                origin.X + ScaleCoordinate(layout.Dpi, 12),
                dividerY,
                origin.X + layout.BodySize.Width - ScaleCoordinate(layout.Dpi, 12),
                dividerY);
        }

        var history = tokenUsage.SpendHistory;
        var summaryPeriods = new CodexSpendPeriod?[]
        {
            tokenUsage.TodaySpend,
            tokenUsage.YesterdaySpend,
            history?.Last7DaysSpend,
            tokenUsage.Last30DaysSpend,
        };
        var summaryLabels = new[]
        {
            text.CodexTodayMetricLabel,
            text.CodexYesterdayMetricLabel,
            text.CodexLast7DaysMetricLabel,
            text.CodexLast30DaysMetricLabel,
        };
        for (var index = 0; index < layout.SummaryCardBounds.Count; index++)
        {
            DrawHistorySummaryCard(
                graphics,
                layout.Dpi,
                Offset(layout.SummaryCardBounds[index], origin),
                summaryLabels[index],
                summaryPeriods[index],
                index == 0,
                fonts,
                text);
        }

        var displayedDays = history?.Days
            .TakeLast(layout.BarBounds.Count)
            .ToArray() ?? [];
        DrawSpendTrend(
            graphics,
            layout,
            origin,
            displayedDays,
            selectedDayIndex,
            fonts,
            text);
        DrawSpendModels(
            graphics,
            layout,
            origin,
            history?.Models ?? [],
            fonts,
            text);
    }

    private static void DrawHistorySummaryCard(
        Graphics graphics,
        int dpi,
        Rectangle bounds,
        string label,
        CodexSpendPeriod? period,
        bool primary,
        RadarPopoverFonts fonts,
        NativeText text)
    {
        var accent = period?.HasUnpricedUsage == true
            ? Color.FromArgb(251, 191, 36)
            : Color.FromArgb(129, 140, 248);
        var surfaceBounds = new Rectangle(
            bounds.X,
            bounds.Y,
            Math.Max(1, bounds.Width - 1),
            Math.Max(1, bounds.Height - 1));
        using var surface = RoundedRectangle(surfaceBounds, Scale(dpi, 6));
        using var fill = new SolidBrush(Color.FromArgb(
            primary ? 34 : 22,
            accent.R,
            accent.G,
            accent.B));
        using var border = new Pen(Color.FromArgb(
            primary ? 82 : 52,
            accent.R,
            accent.G,
            accent.B));
        graphics.FillPath(fill, surface);
        graphics.DrawPath(border, surface);

        var left = bounds.Left + Scale(dpi, 7);
        var right = bounds.Right - Scale(dpi, 7);
        DrawText(
            graphics,
            label,
            fonts.Badge,
            period?.HasUnpricedUsage == true
                ? Color.FromArgb(251, 191, 36)
                : Color.FromArgb(165, 180, 252),
            new Rectangle(
                left,
                bounds.Top + ScaleCoordinate(dpi, 3),
                Math.Max(1, right - left),
                Scale(dpi, 12)),
            CellTextFlags);
        DrawText(
            graphics,
            text.CodexApiEquivalent(period),
            primary && bounds.Width >= Scale(dpi, 100)
                ? fonts.SpendNumber
                : fonts.EmphasizedNumber,
            Color.FromArgb(241, 245, 249),
            new Rectangle(
                left,
                bounds.Top + ScaleCoordinate(dpi, 17),
                Math.Max(1, right - left),
                Math.Max(1, bounds.Bottom - bounds.Top - ScaleCoordinate(dpi, 19))),
            CellTextFlags | TextFormatFlags.Right);
    }

    private static void DrawSpendTrend(
        Graphics graphics,
        CodexSpendHistoryLayout layout,
        Point origin,
        IReadOnlyList<CodexSpendDay> days,
        int selectedDayIndex,
        RadarPopoverFonts fonts,
        NativeText text)
    {
        DrawText(
            graphics,
            text.CodexSpendTrendTitle,
            fonts.Badge,
            Color.FromArgb(165, 180, 252),
            Offset(layout.TrendTitleBounds, origin),
            CellTextFlags);

        var selectedIndex = days.Count == 0
            ? -1
            : Math.Clamp(selectedDayIndex < 0 ? days.Count - 1 : selectedDayIndex, 0, days.Count - 1);
        var selected = selectedIndex >= 0 ? days[selectedIndex] : null;
        DrawText(
            graphics,
            selected is null
                ? "—"
                : $"{text.CodexSpendDate(selected.LocalDate)}  {text.CodexApiEquivalent(selected.Spend)}",
            fonts.EmphasizedNumber,
            selected?.Spend.HasUnpricedUsage == true
                ? Color.FromArgb(251, 191, 36)
                : Color.FromArgb(203, 213, 225),
            Offset(layout.SelectedDayBounds, origin),
            CellTextFlags | TextFormatFlags.Right);

        var chart = Offset(layout.TrendChartBounds, origin);
        var plotTop = chart.Top + ScaleCoordinate(layout.Dpi, 2);
        var plotBottom = chart.Bottom - ScaleCoordinate(layout.Dpi, 3);
        using (var grid = new Pen(Color.FromArgb(28, 71, 85, 105)))
        {
            for (var index = 0; index < 3; index++)
            {
                var y = plotTop + (plotBottom - plotTop) * index / 2;
                graphics.DrawLine(grid, chart.Left, y, chart.Right, y);
            }
        }

        if (days.Count == 0 || layout.BarBounds.Count == 0) return;
        var maximum = days.Max(day => day.Spend.ApiEquivalentUsd ?? 0m);
        var count = Math.Min(days.Count, layout.BarBounds.Count);
        var firstRecentIndex = Math.Max(0, count - 7);
        for (var index = 0; index < count; index++)
        {
            var day = days[index];
            var slot = Offset(layout.BarBounds[index], origin);
            var amount = day.Spend.ApiEquivalentUsd ?? 0m;
            var height = maximum <= 0m || amount <= 0m
                ? 0
                : Math.Max(
                    Scale(layout.Dpi, 1),
                    (int)Math.Round(
                        (plotBottom - plotTop) * (double)(amount / maximum),
                        MidpointRounding.AwayFromZero));
            var isRecent = index >= firstRecentIndex;
            var isSelected = index == selectedIndex;
            if (isSelected)
            {
                using var selection = new SolidBrush(Color.FromArgb(20, 165, 180, 252));
                graphics.FillRectangle(
                    selection,
                    new Rectangle(slot.Left - Scale(layout.Dpi, 1), plotTop, slot.Width + Scale(layout.Dpi, 2), plotBottom - plotTop + 1));
            }
            if (height > 0)
            {
                var bar = new Rectangle(
                    slot.Left,
                    plotBottom - height,
                    Math.Max(1, slot.Width),
                    height);
                using var fill = new SolidBrush(isSelected
                    ? Color.FromArgb(235, 165, 180, 252)
                    : isRecent
                        ? Color.FromArgb(190, 129, 140, 248)
                        : Color.FromArgb(78, 100, 116, 139));
                graphics.FillRectangle(fill, bar);
            }
            if (day.Spend.HasUnpricedUsage)
            {
                var markerY = height > 0
                    ? plotBottom - height
                    : plotBottom - Scale(layout.Dpi, 1);
                using var marker = new Pen(Color.FromArgb(251, 191, 36), Scale(layout.Dpi, 1));
                graphics.DrawLine(marker, slot.Left, markerY, slot.Right - 1, markerY);
            }
        }
    }

    private static void DrawSpendModels(
        Graphics graphics,
        CodexSpendHistoryLayout layout,
        Point origin,
        IReadOnlyList<CodexSpendModel> models,
        RadarPopoverFonts fonts,
        NativeText text)
    {
        DrawText(
            graphics,
            text.CodexSpendModelsTitle,
            fonts.Badge,
            Color.FromArgb(165, 180, 252),
            Offset(layout.ModelsTitleBounds, origin),
            CellTextFlags);

        var topModels = models
            .OrderByDescending(model => model.Spend.PricedApiEquivalentUsd)
            .ThenByDescending(model => model.Spend.TotalTokens)
            .Take(layout.ModelRowBounds.Count)
            .ToArray();
        if (topModels.Length == 0)
        {
            DrawText(
                graphics,
                "—",
                fonts.Model,
                Color.FromArgb(100, 116, 139),
                Offset(layout.ModelRowBounds[0], origin),
                CellTextFlags);
            return;
        }

        var maximum = topModels.Max(model => model.Spend.ApiEquivalentUsd ?? 0m);
        for (var index = 0; index < topModels.Length; index++)
        {
            var model = topModels[index];
            var row = Offset(layout.ModelRowBounds[index], origin);
            var labelWidth = Scale(layout.Dpi, layout.Wide ? 150 : 105);
            var amountWidth = Scale(layout.Dpi, 82);
            var gap = Scale(layout.Dpi, 7);
            var trackLeft = row.Left + labelWidth + gap;
            var trackRight = row.Right - amountWidth - gap;
            var trackHeight = Scale(layout.Dpi, 4);
            var track = new Rectangle(
                trackLeft,
                row.Top + (row.Height - trackHeight) / 2,
                Math.Max(1, trackRight - trackLeft),
                trackHeight);
            DrawText(
                graphics,
                string.Equals(model.Model, "unknown", StringComparison.OrdinalIgnoreCase)
                    ? text.CodexUnknownModel
                    : model.Model,
                index == 0 ? fonts.EmphasizedModel : fonts.Model,
                Color.FromArgb(203, 213, 225),
                new Rectangle(row.Left, row.Top, labelWidth, row.Height),
                CellTextFlags);

            using (var trackFill = new SolidBrush(Color.FromArgb(42, 71, 85, 105)))
            {
                graphics.FillRectangle(trackFill, track);
            }
            var amount = model.Spend.ApiEquivalentUsd ?? 0m;
            if (maximum > 0m && amount > 0m)
            {
                var fillWidth = Math.Max(
                    Scale(layout.Dpi, 2),
                    (int)Math.Round(track.Width * (double)(amount / maximum), MidpointRounding.AwayFromZero));
                using var fill = new SolidBrush(index == 0
                    ? Color.FromArgb(205, 129, 140, 248)
                    : Color.FromArgb(135, 99, 102, 241));
                graphics.FillRectangle(fill, new Rectangle(track.X, track.Y, Math.Min(track.Width, fillWidth), track.Height));
            }
            if (model.Spend.HasUnpricedUsage)
            {
                using var marker = new SolidBrush(Color.FromArgb(251, 191, 36));
                graphics.FillEllipse(
                    marker,
                    track.Right - Scale(layout.Dpi, 4),
                    track.Top,
                    Scale(layout.Dpi, 4),
                    Scale(layout.Dpi, 4));
            }
            DrawText(
                graphics,
                text.CodexApiEquivalent(model.Spend),
                fonts.EmphasizedNumber,
                model.Spend.HasUnpricedUsage
                    ? Color.FromArgb(251, 191, 36)
                    : Color.FromArgb(203, 213, 225),
                new Rectangle(row.Right - amountWidth, row.Top, amountWidth, row.Height),
                CellTextFlags | TextFormatFlags.Right);
        }
    }

    private static void DrawTokenOverview(
        Graphics graphics,
        RadarPopoverLayout layout,
        Point origin,
        CodexTokenUsageSummary tokenUsage,
        Image? logo,
        RadarPopoverFonts fonts,
        NativeText text,
        bool pinned,
        bool spendCardHovered)
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
        DrawSpendSummaryCard(
            graphics,
            layout.Dpi,
            Offset(layout.FooterSpendBounds, origin),
            tokenUsage,
            fonts,
            text,
            HasSpendHistory(tokenUsage),
            spendCardHovered);
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

    private static void DrawHistoryBody(
        Graphics graphics,
        CodexSpendHistoryLayout layout,
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
        var points = TailPoints(layout, tailSide, tailOffset, body);
        graphics.FillPolygon(fill, points);
        graphics.DrawLines(border, points);
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
        DrawColumnText(
            graphics,
            text.RadarCostHeader,
            fonts.Badge,
            color,
            layout.Columns.Cost,
            layout.TableHeaderBounds,
            origin,
            CellTextFlags | TextFormatFlags.Right);

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
            DrawColumnText(
                graphics,
                row.AverageCostText,
                numberFont,
                labelColor,
                layout.Columns.Cost,
                rowBounds,
                origin,
                CellTextFlags | TextFormatFlags.Right);
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
        string title,
        bool spendCardHovered)
    {
        var footerColor = Color.FromArgb(148, 163, 184);
        if (tokenUsage is not null && !layout.FooterSpendBounds.IsEmpty)
        {
            DrawSpendSummaryCard(
                graphics,
                layout.Dpi,
                Offset(layout.FooterSpendBounds, origin),
                tokenUsage,
                fonts,
                text,
                HasSpendHistory(tokenUsage),
                spendCardHovered);
        }
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

    private static bool HasSpendHistory(CodexTokenUsageSummary tokenUsage) =>
        tokenUsage.SpendHistory?.Days.Any(day => day.Spend.HasUsage) == true;

    private static void DrawSpendSummaryCard(
        Graphics graphics,
        int dpi,
        Rectangle bounds,
        CodexTokenUsageSummary tokenUsage,
        RadarPopoverFonts fonts,
        NativeText text,
        bool showHistoryAction,
        bool hovered)
    {
        var surfaceBounds = new Rectangle(
            bounds.X,
            bounds.Y,
            Math.Max(1, bounds.Width - 1),
            Math.Max(1, bounds.Height - 1));
        var accentColor = Color.FromArgb(129, 140, 248);
        using var surface = RoundedRectangle(surfaceBounds, Scale(dpi, 6));
        using var fill = new SolidBrush(Color.FromArgb(
            hovered && showHistoryAction ? 45 : 32,
            accentColor.R,
            accentColor.G,
            accentColor.B));
        using var border = new Pen(Color.FromArgb(
            hovered && showHistoryAction ? 104 : 70,
            accentColor.R,
            accentColor.G,
            accentColor.B));
        graphics.FillPath(fill, surface);
        graphics.DrawPath(border, surface);

        var padding = Scale(dpi, 8);
        var inner = new Rectangle(
            bounds.Left + padding,
            bounds.Top,
            Math.Max(1, bounds.Width - padding * 2),
            bounds.Height);
        var headerHeight = Scale(dpi, 12);
        var headerBounds = new Rectangle(
            inner.Left,
            inner.Top + ScaleCoordinate(dpi, 1),
            inner.Width,
            headerHeight);
        var actionWidth = showHistoryAction
            ? Math.Min(
                headerBounds.Width / 2,
                TextRenderer.MeasureText(
                    graphics,
                    text.CodexSpendHistoryAction,
                    fonts.Badge,
                    Size.Empty,
                    BaseTextFlags).Width + Scale(dpi, 2))
            : 0;
        var actionGap = showHistoryAction ? Scale(dpi, 6) : 0;
        var wideCard = bounds.Width >= Scale(dpi, 320);
        var titleWidth = showHistoryAction
            ? Math.Max(1, headerBounds.Width * (wideCard ? 2 : 3) / 5)
            : headerBounds.Width * 3 / 5;
        DrawText(
            graphics,
            text.CodexSpendMetricTitle,
            fonts.Badge,
            Color.FromArgb(165, 180, 252),
            new Rectangle(headerBounds.Left, headerBounds.Top, titleWidth, headerBounds.Height),
            CellTextFlags);
        if (!showHistoryAction || wideCard)
        {
            var sessionRight = showHistoryAction
                ? headerBounds.Right - actionWidth - actionGap
                : headerBounds.Right;
            DrawText(
                graphics,
                text.CodexSessionCount(tokenUsage.SessionCount),
                fonts.Meta,
                Color.FromArgb(100, 116, 139),
                new Rectangle(
                    headerBounds.Left + titleWidth,
                    headerBounds.Top,
                    Math.Max(1, sessionRight - headerBounds.Left - titleWidth),
                    headerBounds.Height),
                CellTextFlags | TextFormatFlags.Right);
        }
        if (showHistoryAction)
        {
            DrawText(
                graphics,
                text.CodexSpendHistoryAction,
                fonts.Badge,
                hovered
                    ? Color.FromArgb(224, 231, 255)
                    : Color.FromArgb(165, 180, 252),
                new Rectangle(
                    headerBounds.Right - actionWidth,
                    headerBounds.Top,
                    actionWidth,
                    headerBounds.Height),
                CellTextFlags | TextFormatFlags.Right);
        }

        using var horizontalDivider = new Pen(Color.FromArgb(38, accentColor.R, accentColor.G, accentColor.B));
        var dividerY = bounds.Top + headerHeight;
        graphics.DrawLine(horizontalDivider, inner.Left, dividerY, inner.Right, dividerY);

        var fieldsTop = dividerY + Scale(dpi, 1);
        var fieldsHeight = Math.Max(1, bounds.Bottom - fieldsTop - Scale(dpi, 1));
        var fieldGap = Scale(dpi, 10);
        var fieldWidth = Math.Max(1, (inner.Width - fieldGap) / 2);
        DrawSpendSummaryField(
            graphics,
            dpi,
            new Rectangle(inner.Left, fieldsTop, fieldWidth, fieldsHeight),
            text.CodexTodayMetricLabel,
            text.CodexApiEquivalent(tokenUsage.TodaySpend),
            fonts.SpendNumber,
            Color.FromArgb(248, 250, 252),
            fonts);
        DrawSpendSummaryField(
            graphics,
            dpi,
            new Rectangle(
                inner.Left + fieldWidth + fieldGap,
                fieldsTop,
                inner.Right - inner.Left - fieldWidth - fieldGap,
                fieldsHeight),
            text.CodexLast30DaysMetricLabel,
            text.CodexApiEquivalent(tokenUsage.Last30DaysSpend),
            fonts.EmphasizedNumber,
            Color.FromArgb(203, 213, 225),
            fonts);

        using var verticalDivider = new Pen(Color.FromArgb(38, accentColor.R, accentColor.G, accentColor.B));
        var dividerX = inner.Left + fieldWidth + fieldGap / 2;
        graphics.DrawLine(
            verticalDivider,
            dividerX,
            fieldsTop + Scale(dpi, 2),
            dividerX,
            bounds.Bottom - Scale(dpi, 3));
    }

    private static void DrawSpendSummaryField(
        Graphics graphics,
        int dpi,
        Rectangle bounds,
        string label,
        string value,
        Font valueFont,
        Color valueColor,
        RadarPopoverFonts fonts)
    {
        var labelWidth = TextRenderer.MeasureText(
            graphics,
            label,
            fonts.Meta,
            Size.Empty,
            BaseTextFlags).Width;
        var gap = Scale(dpi, 4);
        DrawText(
            graphics,
            label,
            fonts.Meta,
            Color.FromArgb(148, 163, 184),
            new Rectangle(bounds.Left, bounds.Top, labelWidth, bounds.Height),
            CellTextFlags);
        DrawText(
            graphics,
            value,
            valueFont,
            valueColor,
            new Rectangle(
                bounds.Left + labelWidth + gap,
                bounds.Top,
                Math.Max(1, bounds.Width - labelWidth - gap),
                bounds.Height),
            CellTextFlags | TextFormatFlags.Right);
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
        Point origin,
        TextFormatFlags flags = CellTextFlags)
    {
        DrawText(graphics, text, font, color, Offset(column.InRow(row), origin), flags);
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

    internal static Region CreateWindowRegion(
        CodexSpendHistoryLayout layout,
        PopoverTailSide tailSide,
        int tailOffset)
    {
        var body = layout.BodyBounds(tailSide);
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

    private static Point[] TailPoints(
        CodexSpendHistoryLayout layout,
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

    internal static Rectangle BodyBounds(RadarPopoverLayout layout, PopoverTailSide tailSide) =>
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
            SpendNumber = Create("Cascadia Mono", 10.5, FontStyle.Bold, dpi);
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
        public Font SpendNumber { get; }

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
            SpendNumber.Dispose();
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
