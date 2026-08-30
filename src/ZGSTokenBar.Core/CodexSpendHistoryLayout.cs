using System.Drawing;

namespace ZGSTokenBar.Core;

public sealed class CodexSpendHistoryLayout
{
    public const int LogicalNarrowWidth = 360;
    public const int LogicalWideWidth = RadarPopoverLayout.LogicalWidth;
    public const int LogicalHeight = 270;
    public const int LogicalTail = RadarPopoverLayout.LogicalTail;
    public const int LogicalGap = RadarPopoverLayout.LogicalGap;
    public const int MaximumTrendDays = 30;

    private CodexSpendHistoryLayout(
        int dpi,
        bool wide,
        Size bodySize,
        int tailSize,
        int gap,
        Rectangle logoBounds,
        Rectangle titleBounds,
        Rectangle subtitleBounds,
        Rectangle backBounds,
        IReadOnlyList<Rectangle> summaryCardBounds,
        Rectangle trendTitleBounds,
        Rectangle selectedDayBounds,
        Rectangle trendChartBounds,
        IReadOnlyList<Rectangle> barBounds,
        Rectangle modelsTitleBounds,
        IReadOnlyList<Rectangle> modelRowBounds)
    {
        Dpi = dpi;
        Wide = wide;
        BodySize = bodySize;
        TailSize = tailSize;
        Gap = gap;
        LogoBounds = logoBounds;
        TitleBounds = titleBounds;
        SubtitleBounds = subtitleBounds;
        BackBounds = backBounds;
        SummaryCardBounds = summaryCardBounds;
        TrendTitleBounds = trendTitleBounds;
        SelectedDayBounds = selectedDayBounds;
        TrendChartBounds = trendChartBounds;
        BarBounds = barBounds;
        ModelsTitleBounds = modelsTitleBounds;
        ModelRowBounds = modelRowBounds;
    }

    public int Dpi { get; }
    public bool Wide { get; }
    public Size BodySize { get; }
    public int TailSize { get; }
    public int Gap { get; }
    public Rectangle LogoBounds { get; }
    public Rectangle TitleBounds { get; }
    public Rectangle SubtitleBounds { get; }
    public Rectangle BackBounds { get; }
    public IReadOnlyList<Rectangle> SummaryCardBounds { get; }
    public Rectangle TrendTitleBounds { get; }
    public Rectangle SelectedDayBounds { get; }
    public Rectangle TrendChartBounds { get; }
    public IReadOnlyList<Rectangle> BarBounds { get; }
    public Rectangle ModelsTitleBounds { get; }
    public IReadOnlyList<Rectangle> ModelRowBounds { get; }

    public Rectangle BodyBounds(PopoverTailSide tailSide) => tailSide switch
    {
        PopoverTailSide.Top => new Rectangle(0, TailSize, BodySize.Width, BodySize.Height),
        PopoverTailSide.Left => new Rectangle(TailSize, 0, BodySize.Width, BodySize.Height),
        _ => new Rectangle(0, 0, BodySize.Width, BodySize.Height),
    };

    public Rectangle InWindow(Rectangle bodyRelativeBounds, PopoverTailSide tailSide)
    {
        var body = BodyBounds(tailSide);
        return new Rectangle(
            body.Left + bodyRelativeBounds.Left,
            body.Top + bodyRelativeBounds.Top,
            bodyRelativeBounds.Width,
            bodyRelativeBounds.Height);
    }

    public static CodexSpendHistoryLayout Create(int dpi, bool wide, int dayCount)
    {
        dpi = Math.Max(96, dpi);
        dayCount = Math.Clamp(dayCount, 0, MaximumTrendDays);
        var logicalWidth = wide ? LogicalWideWidth : LogicalNarrowWidth;

        int Scale(int value) => Math.Max(
            1,
            (int)Math.Round(value * dpi / 96d, MidpointRounding.AwayFromZero));

        Rectangle Rect(int x, int y, int width, int height) => Rectangle.FromLTRB(
            Scale(x),
            Scale(y),
            Scale(x + width),
            Scale(y + height));

        var innerWidth = logicalWidth - 24;
        const int summaryGap = 6;
        var summaryCards = new List<Rectangle>(4);
        for (var index = 0; index < 4; index++)
        {
            var left = 12 + (innerWidth + summaryGap) * index / 4;
            var right = 12 + (innerWidth + summaryGap) * (index + 1) / 4 - summaryGap;
            summaryCards.Add(Rect(left, 50, right - left, 42));
        }

        var chartBounds = Rect(12, 116, innerWidth, 62);
        var bars = CreateBarBounds(chartBounds, dayCount, Scale(2), Scale(2));

        return new CodexSpendHistoryLayout(
            dpi,
            wide,
            new Size(Scale(logicalWidth), Scale(LogicalHeight)),
            Scale(LogicalTail),
            Scale(LogicalGap),
            Rect(12, 10, 24, 24),
            Rect(44, 8, wide ? 238 : 174, 18),
            Rect(44, 25, wide ? 310 : 224, 13),
            Rect(logicalWidth - 76, 9, 64, 25),
            summaryCards,
            Rect(12, 98, 116, 14),
            Rect(132, 98, innerWidth - 120, 14),
            chartBounds,
            bars,
            Rect(12, 184, innerWidth, 14),
            [
                Rect(12, 201, innerWidth, 18),
                Rect(12, 222, innerWidth, 18),
                Rect(12, 243, innerWidth, 18),
            ]);
    }

    private static IReadOnlyList<Rectangle> CreateBarBounds(
        Rectangle chartBounds,
        int dayCount,
        int sideInset,
        int gap)
    {
        if (dayCount == 0) return [];

        var left = chartBounds.Left + sideInset;
        var right = Math.Max(left + dayCount, chartBounds.Right - sideInset);
        var available = Math.Max(dayCount, right - left - gap * (dayCount - 1));
        var result = new List<Rectangle>(dayCount);
        for (var index = 0; index < dayCount; index++)
        {
            var barLeft = left + gap * index + available * index / dayCount;
            var barRight = left + gap * index + available * (index + 1) / dayCount;
            result.Add(new Rectangle(
                barLeft,
                chartBounds.Top,
                Math.Max(1, barRight - barLeft),
                chartBounds.Height));
        }
        return result;
    }
}
