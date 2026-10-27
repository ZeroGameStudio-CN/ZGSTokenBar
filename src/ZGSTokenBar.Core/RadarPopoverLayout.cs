using System.Drawing;

namespace ZGSTokenBar.Core;

public readonly record struct RadarPopoverColumn(int Left, int Right)
{
    public int Width => Right - Left;

    public Rectangle InRow(Rectangle row) =>
        new(Left, row.Top, Width, row.Height);
}

public sealed record RadarPopoverColumns(
    RadarPopoverColumn Marker,
    RadarPopoverColumn Model,
    RadarPopoverColumn Status,
    RadarPopoverColumn IqCurrent,
    RadarPopoverColumn IqAverage,
    RadarPopoverColumn Samples,
    RadarPopoverColumn AverageTime,
    RadarPopoverColumn Cost);

public sealed record RadarModelGroupHeader(string ModelKey, bool Collapsed, Rectangle Bounds);

public sealed class RadarPopoverLayout
{
    public const int LogicalWidth = 476;
    public const int LogicalTail = 8;
    public const int LogicalGap = 3;
    public const int LogicalRowHeight = 19;
    public const int LogicalModelGroupGap = 6;
    public const int LogicalModelGroupHeaderHeight = 22;
    public const int LogicalFooterExpansion = 16;
    public const int LogicalSpendSummaryHeight = 34;
    public const int LogicalSpendCardHeight = 28;
    public const int LogicalTokenWidth = 240;
    public const int LogicalTokenHeight = 162;
    public const int ConversationHeaderHeight = 36;
    public const int ConversationRowHeight = 46;
    public const int LogicalRadarColumnWidth = 400;
    public const int LogicalRecentColumnLeft = 428;
    public const int LogicalRecentColumnWidth = 476;
    public const int LogicalRecentColumnInset = 8;
    public const int LogicalSplitWidth = LogicalRecentColumnLeft + LogicalRecentColumnWidth + 14;

    private RadarPopoverLayout(
        int dpi,
        bool tokenOnly,
        bool hasOpenResetWindow,
        Size bodySize,
        int tailSize,
        int gap,
        Rectangle logoBounds,
        Rectangle titleBounds,
        Rectangle subtitleBounds,
        Rectangle resetBounds,
        Rectangle stateBounds,
        Rectangle tableHeaderBounds,
        int separatorY,
        IReadOnlyList<Rectangle> rowBounds,
        Rectangle errorBounds,
        Rectangle emptyBounds,
        Rectangle footerSpendBounds,
        Rectangle footerSourceBounds,
        Rectangle footerLegendBounds,
        RadarPopoverColumns columns)
    {
        Dpi = dpi;
        TokenOnly = tokenOnly;
        HasOpenResetWindow = hasOpenResetWindow;
        BodySize = bodySize;
        TailSize = tailSize;
        Gap = gap;
        LogoBounds = logoBounds;
        TitleBounds = titleBounds;
        SubtitleBounds = subtitleBounds;
        ResetBounds = resetBounds;
        StateBounds = stateBounds;
        TableHeaderBounds = tableHeaderBounds;
        SeparatorY = separatorY;
        RowBounds = rowBounds;
        ErrorBounds = errorBounds;
        EmptyBounds = emptyBounds;
        FooterSpendBounds = footerSpendBounds;
        FooterSourceBounds = footerSourceBounds;
        FooterLegendBounds = footerLegendBounds;
        Columns = columns;
    }

    public int Dpi { get; }
    public bool TokenOnly { get; }
    public bool HasOpenResetWindow { get; }
    public Size BodySize { get; }
    public int TailSize { get; }
    public int Gap { get; }
    public Rectangle LogoBounds { get; }
    public Rectangle TitleBounds { get; }
    public Rectangle SubtitleBounds { get; }
    public Rectangle ResetBounds { get; }
    public Rectangle StateBounds { get; }
    public Rectangle TableHeaderBounds { get; }
    public int SeparatorY { get; }
    public IReadOnlyList<Rectangle> RowBounds { get; }
    public Rectangle ErrorBounds { get; }
    public Rectangle EmptyBounds { get; }
    public Rectangle FooterSpendBounds { get; }
    public Rectangle FooterSourceBounds { get; }
    public Rectangle FooterLegendBounds { get; }
    public Rectangle FooterCompactBounds { get; private init; }
    public RadarPopoverColumns Columns { get; }
    public IReadOnlyList<RadarModelGroupHeader> GroupHeaders { get; private init; } = [];
    public Rectangle TableViewport { get; private init; }
    public int ScrollOffset { get; private init; }
    public int MaximumScrollOffset { get; private init; }
    public Rectangle ScrollTrackBounds { get; private init; }
    public Rectangle ScrollThumbBounds { get; private init; }
    public Rectangle RecentTurnsBounds { get; private init; }
    public Rectangle RecentColumnBounds { get; private init; }
    public bool HasRecentColumn => !RecentColumnBounds.IsEmpty;
    public int RecentPageSize { get; private init; } = CodexConversationPage.PageSize;
    public Rectangle RecentPreviousBounds => RecentPageButton(64);
    public Rectangle RecentNextBounds => RecentPageButton(20);
    public IReadOnlyList<RadarPopoverColumn> RecentColumns
    {
        get
        {
            int Scale(int value) => (int)Math.Round(value * Dpi / 96d);
            var widths = new[] { 42, 42, 42, 54, 56, 38 }.Select(Scale).ToArray();
            var timeWidth = Scale(60);
            var gap = Scale(5);
            var inset = Scale(LogicalRecentColumnInset);
            var modelWidth = Math.Max(1, RecentTurnsBounds.Width - inset * 2 - timeWidth - widths.Sum() - 7 * gap);
            var columns = new List<RadarPopoverColumn>();
            var left = RecentTurnsBounds.Left + inset;
            foreach (var width in new[] { timeWidth, modelWidth }.Concat(widths))
            {
                columns.Add(new(left, left + width));
                left += width + gap;
            }
            return columns;
        }
    }
    private Rectangle RecentPageButton(int rightOffset) => RecentTurnsBounds.IsEmpty ? Rectangle.Empty
        : new Rectangle(RecentTurnsBounds.Right - (int)Math.Round((rightOffset + LogicalRecentColumnInset) * Dpi / 96d),
            RecentTurnsBounds.Top, (int)Math.Round(20 * Dpi / 96d), (int)Math.Round(16 * Dpi / 96d));

    public int RecentTurnIndexAt(Point point, int offset, int total)
    {
        var top = RecentTurnsBounds.Top + (int)Math.Round(ConversationHeaderHeight * Dpi / 96d);
        var rowHeight = (int)Math.Round(ConversationRowHeight * Dpi / 96d);
        if (!RecentTurnsBounds.Contains(point) || point.Y < top) return -1;
        var index = (point.Y - top) / rowHeight;
        var page = CodexConversationPage.Create(offset, total, RecentPageSize);
        return index < page.Count ? page.Offset + index : -1;
    }

    public static RadarPopoverLayout CreateGrouped(
        int dpi,
        IReadOnlyList<string?> modelKeys,
        IReadOnlySet<string> collapsedModels,
        bool hasInlineError,
        bool hasOpenResetWindow = false,
        bool hasSpendSummary = false,
        int maximumBodyHeight = int.MaxValue,
        int scrollOffset = 0,
        int recentTurnCount = 0,
        bool showLegend = true) =>
        CreateCore(dpi, modelKeys.Count, modelKeys, hasInlineError, hasOpenResetWindow,
            hasSpendSummary, collapsedModels, maximumBodyHeight, scrollOffset, recentTurnCount, showLegend);

    public static RadarPopoverLayout Create(
        int dpi,
        int rowCount,
        bool hasInlineError,
        bool hasOpenResetWindow = false,
        bool hasSpendSummary = false)
    {
        return CreateCore(
            dpi,
            rowCount,
            null,
            hasInlineError,
            hasOpenResetWindow,
            hasSpendSummary);
    }

    public static RadarPopoverLayout Create(
        int dpi,
        IReadOnlyList<string?> modelKeys,
        bool hasInlineError,
        bool hasOpenResetWindow = false,
        bool hasSpendSummary = false)
    {
        ArgumentNullException.ThrowIfNull(modelKeys);
        return CreateCore(
            dpi,
            modelKeys.Count,
            modelKeys,
            hasInlineError,
            hasOpenResetWindow,
            hasSpendSummary);
    }

    private static RadarPopoverLayout CreateCore(
        int dpi,
        int rowCount,
        IReadOnlyList<string?>? modelKeys,
        bool hasInlineError,
        bool hasOpenResetWindow,
        bool hasSpendSummary,
        IReadOnlySet<string>? collapsedModels = null,
        int maximumBodyHeight = int.MaxValue,
        int scrollOffset = 0,
        int recentTurnCount = 0,
        bool showLegend = true)
    {
        dpi = Math.Max(96, dpi);
        rowCount = Math.Max(0, rowCount);
        var splitRecent = recentTurnCount > 0;
        var radarContentWidth = splitRecent ? LogicalRadarColumnWidth : LogicalWidth - 28;
        var recentHeight = RecentTurnsHeight(recentTurnCount);
        var footerStart = showLegend ? 48 : 30;
        var logicalErrorHeight = hasInlineError ? 14 : 0;
        var resetBannerOffset = hasOpenResetWindow ? 36 : 0;
        var modelGroupGaps = CountModelGroupGaps(rowCount, modelKeys);
        var logicalHeight = (rowCount == 0
            ? 164 + LogicalFooterExpansion + resetBannerOffset
            : 104
                + LogicalFooterExpansion
                + rowCount * LogicalRowHeight
                + modelGroupGaps * LogicalModelGroupGap
                + logicalErrorHeight
                + resetBannerOffset)
            + (hasSpendSummary ? LogicalSpendSummaryHeight : 0)
            + (splitRecent ? 0 : recentHeight)
            - (showLegend ? 0 : 18);

        int Scale(int value) => Math.Max(
            1,
            (int)Math.Round(value * dpi / 96d, MidpointRounding.AwayFromZero));

        Rectangle Rect(int x, int y, int width, int height) => Rectangle.FromLTRB(
            Scale(x),
            Scale(y),
            Scale(x + width),
            Scale(y + height));

        RadarPopoverColumn Column(int left, int right) =>
            new(Scale(14 + left), Scale(14 + right));

        var rows = new List<Rectangle>(rowCount);
        var headers = new List<RadarModelGroupHeader>();
        var rowY = 68 + resetBannerOffset;
        if (collapsedModels is not null && modelKeys is not null && rowCount > 0)
        {
            rows.AddRange(Enumerable.Repeat(Rectangle.Empty, rowCount));
            foreach (var group in modelKeys.Select((key, index) => (key, index))
                .GroupBy(item => item.key ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                if (headers.Count > 0) rowY += LogicalModelGroupGap;
                var collapsed = collapsedModels.Contains(group.Key);
                headers.Add(new RadarModelGroupHeader(group.Key, collapsed,
                    Rect(14, rowY, radarContentWidth, LogicalModelGroupHeaderHeight)));
                rowY += LogicalModelGroupHeaderHeight;
                if (collapsed) continue;
                foreach (var item in group)
                {
                    rows[item.index] = Rect(14, rowY, radarContentWidth, LogicalRowHeight);
                    rowY += LogicalRowHeight;
                }
            }
            logicalHeight = rowY + 52 + logicalErrorHeight
                + (hasSpendSummary ? LogicalSpendSummaryHeight : 0)
                + (splitRecent ? 0 : recentHeight)
                - (showLegend ? 0 : 18);
        }
        else
        {
            for (var index = 0; index < rowCount; index++)
            {
                rows.Add(Rect(14, rowY, radarContentWidth, LogicalRowHeight));
                rowY += LogicalRowHeight;
                if (IsModelGroupStart(index + 1, rowCount, modelKeys)) rowY += LogicalModelGroupGap;
            }
        }
        var recentPageSize = CodexConversationPage.PageSize;
        if (splitRecent)
        {
            logicalHeight = Math.Max(
                logicalHeight,
                24 + ConversationHeaderHeight + ConversationRowHeight);
            recentPageSize = Math.Min(
                recentTurnCount,
                Math.Max(1, (logicalHeight - 60) / ConversationRowHeight));
        }
        var rowsBottom = rowY;
        var maximumScrollOffset = 0;
        var viewport = Rectangle.Empty;
        var scrollTrack = Rectangle.Empty;
        var scrollThumb = Rectangle.Empty;
        if (headers.Count > 0)
        {
            var contentHeight = Scale(rowY) - Scale(68 + resetBannerOffset);
            var chromeHeight = logicalHeight - rowY + 68 + resetBannerOffset;
            var maximumLogicalHeight = (int)Math.Min(int.MaxValue, maximumBodyHeight * 96d / dpi);
            logicalHeight = Math.Min(logicalHeight, Math.Max(chromeHeight + LogicalModelGroupHeaderHeight, maximumLogicalHeight));
            if (splitRecent)
            {
                logicalHeight = Math.Max(
                    logicalHeight,
                    24 + ConversationHeaderHeight + ConversationRowHeight);
                recentPageSize = Math.Min(
                    recentTurnCount,
                    Math.Max(1, (logicalHeight - 60) / ConversationRowHeight));
            }
            rowsBottom = logicalHeight - (chromeHeight - 68 - resetBannerOffset);
            viewport = Rect(14, 68 + resetBannerOffset, radarContentWidth, rowsBottom - 68 - resetBannerOffset);
            maximumScrollOffset = Math.Max(0, contentHeight - viewport.Height);
            scrollOffset = Math.Clamp(scrollOffset, 0, maximumScrollOffset);
            for (var index = 0; index < rows.Count; index++)
            {
                if (!rows[index].IsEmpty)
                {
                    var bounds = rows[index];
                    bounds.Offset(0, -scrollOffset);
                    rows[index] = bounds;
                }
            }
            headers = headers.Select(header => header with
            {
                Bounds = new Rectangle(header.Bounds.X, header.Bounds.Y - scrollOffset,
                    header.Bounds.Width, header.Bounds.Height),
            }).ToList();
            if (maximumScrollOffset > 0)
            {
                scrollTrack = new Rectangle(Scale(16 + radarContentWidth), viewport.Top, Scale(8), viewport.Height);
                var thumbHeight = Math.Min(viewport.Height, Math.Max(Scale(20),
                    (int)((long)viewport.Height * viewport.Height / contentHeight)));
                var thumbTop = viewport.Top + (int)((long)scrollOffset * (viewport.Height - thumbHeight) / maximumScrollOffset);
                scrollThumb = new Rectangle(scrollTrack.Left, thumbTop, scrollTrack.Width, thumbHeight);
            }
        }
        return new RadarPopoverLayout(
            dpi,
            false,
            hasOpenResetWindow,
            new Size(Scale(splitRecent ? LogicalSplitWidth : LogicalWidth), Scale(logicalHeight)),
            Scale(LogicalTail),
            Scale(LogicalGap),
            Rect(14, 12, 22, 22),
            Rect(44, 11, 120, 16),
            Rect(44, 26, 160, 12),
            hasOpenResetWindow
                ? Rect(14, 44, radarContentWidth, 32)
                : splitRecent
                    ? Rect(160, 26, radarContentWidth - 146, 12)
                    : Rect(208, 26, 254, 12),
            splitRecent
                ? Rect(160, 12, radarContentWidth - 146, 16)
                : Rect(160, 12, 302, 16),
            Rect(14, 48 + resetBannerOffset, radarContentWidth, 16),
            Scale(65 + resetBannerOffset),
            rows,
            hasInlineError ? Rect(14, rowsBottom, radarContentWidth, 14) : Rectangle.Empty,
            Rect(14, 62 + resetBannerOffset, radarContentWidth, 32),
            hasSpendSummary
                ? splitRecent
                    ? Rect(14, logicalHeight - 34, 154, LogicalSpendCardHeight)
                    : Rect(
                        14,
                        logicalHeight - footerStart - LogicalSpendSummaryHeight,
                        radarContentWidth,
                        LogicalSpendCardHeight)
                : Rectangle.Empty,
            splitRecent ? Rectangle.Empty : Rect(14, logicalHeight - 30, radarContentWidth, 24),
            showLegend ? Rect(14, logicalHeight - 48, radarContentWidth, 14) : Rectangle.Empty,
            new RadarPopoverColumns(
                Column(0, 22),
                splitRecent ? Column(24, 132) : Column(24, 164),
                splitRecent ? Column(136, 144) : Column(168, 176),
                splitRecent ? Column(148, 192) : Column(180, 226),
                splitRecent ? Column(198, 230) : Column(232, 264),
                splitRecent ? Column(236, 276) : Column(270, 310),
                splitRecent ? Column(284, 332) : Column(318, 366),
                splitRecent ? Column(342, 392) : Column(382, 440)))
        {
            GroupHeaders = headers,
            TableViewport = viewport,
            ScrollOffset = scrollOffset,
            MaximumScrollOffset = maximumScrollOffset,
            ScrollTrackBounds = scrollTrack,
            ScrollThumbBounds = scrollThumb,
            RecentTurnsBounds = splitRecent
                ? Rect(
                    LogicalRecentColumnLeft,
                    12,
                    LogicalRecentColumnWidth,
                    Math.Min(
                        Math.Max(1, logicalHeight - 24),
                        ConversationHeaderHeight + recentPageSize * ConversationRowHeight + 12))
                : recentHeight == 0 ? Rectangle.Empty
                    : Rect(14, logicalHeight - footerStart - (hasSpendSummary ? LogicalSpendSummaryHeight : 0) - recentHeight,
                        radarContentWidth, recentHeight - 6),
            RecentColumnBounds = splitRecent
                ? Rect(
                    LogicalRecentColumnLeft,
                    12,
                    LogicalRecentColumnWidth,
                    Math.Min(
                        Math.Max(1, logicalHeight - 24),
                        ConversationHeaderHeight + recentPageSize * ConversationRowHeight + 12))
                : Rectangle.Empty,
            RecentPageSize = recentPageSize,
            FooterCompactBounds = splitRecent && hasSpendSummary
                ? Rect(14, logicalHeight - 34, radarContentWidth, 28)
                : Rectangle.Empty,
        };
    }

    public string? ModelGroupAt(Point bodyPoint) => TableViewport.Contains(bodyPoint)
        ? GroupHeaders.FirstOrDefault(header => header.Bounds.Contains(bodyPoint))?.ModelKey
        : null;

    public int ScrollOffsetForThumbTop(int top)
    {
        var travel = ScrollTrackBounds.Height - ScrollThumbBounds.Height;
        return travel <= 0 ? 0 : (int)Math.Clamp(
            (long)(top - ScrollTrackBounds.Top) * MaximumScrollOffset / travel, 0, MaximumScrollOffset);
    }

    private static int CountModelGroupGaps(
        int rowCount,
        IReadOnlyList<string?>? modelKeys)
    {
        if (rowCount < 2 || modelKeys is null) return 0;
        return Enumerable.Range(1, rowCount - 1)
            .Count(index => IsModelGroupStart(index, rowCount, modelKeys));
    }

    private static bool IsModelGroupStart(
        int index,
        int rowCount,
        IReadOnlyList<string?>? modelKeys)
    {
        if (index <= 0 || index >= rowCount || modelKeys is null || index >= modelKeys.Count)
        {
            return false;
        }

        return !string.Equals(
            modelKeys[index - 1],
            modelKeys[index],
            StringComparison.OrdinalIgnoreCase);
    }

    private static int RecentTurnsHeight(int count) => count <= 0 ? 0 : 44 + Math.Min(CodexConversationPage.PageSize, count) * ConversationRowHeight;

    public static RadarPopoverLayout CreateTokenUsage(int dpi, int recentTurnCount = 0)
    {
        dpi = Math.Max(96, dpi);
        var recentHeight = RecentTurnsHeight(recentTurnCount);
        var width = recentHeight == 0 ? LogicalTokenWidth : LogicalRecentColumnWidth + 24;

        int Scale(int value) => Math.Max(
            1,
            (int)Math.Round(value * dpi / 96d, MidpointRounding.AwayFromZero));

        Rectangle Rect(int x, int y, int width, int height) => Rectangle.FromLTRB(
            Scale(x),
            Scale(y),
            Scale(x + width),
            Scale(y + height));

        var emptyColumns = new RadarPopoverColumns(
            default,
            default,
            default,
            default,
            default,
            default,
            default,
            default);
        return new RadarPopoverLayout(
            dpi,
            true,
            false,
            new Size(Scale(width), Scale(LogicalTokenHeight + recentHeight)),
            Scale(LogicalTail),
            Scale(LogicalGap),
            Rect(12, 10, 24, 24),
            Rect(44, 9, width - 56, 16),
            Rect(44, 24, width - 56, 12),
            Rectangle.Empty,
            Rectangle.Empty,
            Rectangle.Empty,
            0,
            [],
            Rectangle.Empty,
            Rectangle.Empty,
            Rect(12, 126, width - 24, LogicalSpendCardHeight),
            Rectangle.Empty,
            Rectangle.Empty,
            emptyColumns)
        {
            RecentTurnsBounds = recentHeight == 0 ? Rectangle.Empty
                : Rect(12, LogicalTokenHeight + 2, width - 24, recentHeight - 6),
        };
    }
}
