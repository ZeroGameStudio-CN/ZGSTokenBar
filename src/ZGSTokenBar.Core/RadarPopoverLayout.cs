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

public sealed class RadarPopoverLayout
{
    public const int LogicalWidth = 476;
    public const int LogicalTail = 8;
    public const int LogicalGap = 3;
    public const int LogicalRowHeight = 19;
    public const int LogicalModelGroupGap = 6;
    public const int LogicalFooterExpansion = 16;
    public const int LogicalTokenWidth = 240;
    public const int LogicalTokenHeight = 144;

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
    public Rectangle FooterSourceBounds { get; }
    public Rectangle FooterLegendBounds { get; }
    public RadarPopoverColumns Columns { get; }

    public static RadarPopoverLayout Create(
        int dpi,
        int rowCount,
        bool hasInlineError,
        bool hasOpenResetWindow = false)
    {
        return CreateCore(dpi, rowCount, null, hasInlineError, hasOpenResetWindow);
    }

    public static RadarPopoverLayout Create(
        int dpi,
        IReadOnlyList<string?> modelKeys,
        bool hasInlineError,
        bool hasOpenResetWindow = false)
    {
        ArgumentNullException.ThrowIfNull(modelKeys);
        return CreateCore(dpi, modelKeys.Count, modelKeys, hasInlineError, hasOpenResetWindow);
    }

    private static RadarPopoverLayout CreateCore(
        int dpi,
        int rowCount,
        IReadOnlyList<string?>? modelKeys,
        bool hasInlineError,
        bool hasOpenResetWindow)
    {
        dpi = Math.Max(96, dpi);
        rowCount = Math.Max(0, rowCount);
        var logicalErrorHeight = hasInlineError ? 14 : 0;
        var resetBannerOffset = hasOpenResetWindow ? 36 : 0;
        var modelGroupGaps = CountModelGroupGaps(rowCount, modelKeys);
        var logicalHeight = rowCount == 0
            ? 164 + LogicalFooterExpansion + resetBannerOffset
            : 104
                + LogicalFooterExpansion
                + rowCount * LogicalRowHeight
                + modelGroupGaps * LogicalModelGroupGap
                + logicalErrorHeight
                + resetBannerOffset;

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
        var rowY = 68 + resetBannerOffset;
        for (var index = 0; index < rowCount; index++)
        {
            rows.Add(Rect(14, rowY, 448, LogicalRowHeight));
            rowY += LogicalRowHeight;
            if (IsModelGroupStart(index + 1, rowCount, modelKeys))
            {
                rowY += LogicalModelGroupGap;
            }
        }
        var rowsBottom = rowY;
        return new RadarPopoverLayout(
            dpi,
            false,
            hasOpenResetWindow,
            new Size(Scale(LogicalWidth), Scale(logicalHeight)),
            Scale(LogicalTail),
            Scale(LogicalGap),
            Rect(14, 12, 22, 22),
            Rect(44, 11, 120, 16),
            Rect(44, 26, 160, 12),
            hasOpenResetWindow
                ? Rect(14, 44, 448, 32)
                : Rect(208, 26, 254, 12),
            Rect(160, 12, 302, 16),
            Rect(14, 48 + resetBannerOffset, 448, 16),
            Scale(65 + resetBannerOffset),
            rows,
            hasInlineError ? Rect(14, rowsBottom, 448, 14) : Rectangle.Empty,
            Rect(14, 62 + resetBannerOffset, 448, 32),
            Rect(14, logicalHeight - 30, 448, 24),
            Rect(14, logicalHeight - 48, 448, 14),
            new RadarPopoverColumns(
                Column(0, 22),
                Column(24, 164),
                Column(168, 176),
                Column(180, 226),
                Column(232, 264),
                Column(270, 310),
                Column(318, 366),
                Column(382, 440)));
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

    public static RadarPopoverLayout CreateTokenUsage(int dpi)
    {
        dpi = Math.Max(96, dpi);

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
            new Size(Scale(LogicalTokenWidth), Scale(LogicalTokenHeight)),
            Scale(LogicalTail),
            Scale(LogicalGap),
            Rect(12, 10, 24, 24),
            Rect(44, 9, 184, 16),
            Rect(44, 24, 184, 12),
            Rectangle.Empty,
            Rectangle.Empty,
            Rectangle.Empty,
            0,
            [],
            Rectangle.Empty,
            Rectangle.Empty,
            Rectangle.Empty,
            Rectangle.Empty,
            emptyColumns);
    }
}
