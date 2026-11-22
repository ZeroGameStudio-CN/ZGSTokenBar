using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace ZGSTokenBar.App;

internal readonly record struct IntakeOverviewMetrics(
    bool CountsKnown,
    int InFlight,
    int PendingReview,
    int ProtectedArchive,
    int Unconfirmed,
    int TodayHistory,
    bool HasTimedHistory,
    int ClosedHistory,
    string FallbackMessage,
    bool Chinese);

internal readonly record struct MonitorKpiItem(
    string Label,
    string Value,
    Color ValueColor,
    string? Suffix = null,
    Color? SuffixColor = null);

internal static class MonitorStripPaint
{
    internal const TextFormatFlags MeasureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
    internal const TextFormatFlags DrawFlags = MeasureFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
    private const TextFormatFlags DrawEllipsisFlags = DrawFlags | TextFormatFlags.EndEllipsis;

    internal static int MeasureWidth(Graphics g, string text, Font font) =>
        string.IsNullOrEmpty(text)
            ? 0
            : TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), MeasureFlags).Width + 1;

    internal static RectangleF DrawInsetCard(Graphics g, Control owner, RouterVisualPalette palette, float scale)
    {
        var surface = owner.Parent?.BackColor ?? palette.Surface;
        g.Clear(surface);
        UiPaint.PrepareQuality(g);
        var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1f, owner.Width - 1f), Math.Max(1f, owner.Height - 1f));
        var fill = UiTokens.Mix(surface, Color.White, .032f);
        var border = UiTokens.Border(surface);
        UiPaint.CardShell(g, bounds, fill, border, UiTokens.RadiusControl * scale);
        return bounds;
    }

    internal static float DrawKpiItems(
        Graphics g,
        float startX,
        float maxX,
        float y,
        float height,
        IReadOnlyList<MonitorKpiItem> items,
        Font labelFont,
        Font valueFont,
        Font suffixFont,
        float scale)
    {
        if (items.Count == 0 || maxX <= startX) return startX;
        var labelGap = 4f * scale;
        var suffixGap = 4f * scale;
        var measured = new (MonitorKpiItem Item, int LabelW, int ValueW, int SuffixW, float TotalW)[items.Count];
        var sumW = 0f;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lw = MeasureWidth(g, item.Label, labelFont);
            var vw = MeasureWidth(g, item.Value, valueFont);
            var sw = MeasureWidth(g, item.Suffix ?? "", suffixFont);
            var total = lw + labelGap + vw + (sw > 0 ? suffixGap + sw : 0);
            measured[i] = (item, lw, vw, sw, total);
            sumW += total;
        }

        var dividerCount = Math.Max(0, items.Count - 1);
        var halfGap = 10f * scale;
        var dividerStep = halfGap * 2 + 1f;
        var available = maxX - startX;
        if (dividerCount > 0 && sumW + dividerCount * dividerStep > available)
        {
            halfGap = Math.Max(4f * scale, (available - sumW - dividerCount) / (2f * dividerCount));
            dividerStep = halfGap * 2 + 1f;
        }

        var rowY = (int)Math.Round(y);
        var rowH = Math.Max(1, (int)Math.Round(height));
        var cursor = startX;
        using var dividerPen = new Pen(UiPalette.Slate700, 1f);
        for (var i = 0; i < measured.Length; i++)
        {
            if (i > 0)
            {
                var dx = cursor + halfGap;
                if (dx >= maxX - 8f * scale) break;
                g.DrawLine(dividerPen, dx, y + height * .22f, dx, y + height * .78f);
                cursor += dividerStep;
            }
            if (cursor >= maxX) break;
            var entry = measured[i];
            var remaining = (int)Math.Max(0, Math.Floor(maxX - cursor));
            if (remaining < 12) break;

            var labelW = Math.Min(entry.LabelW, remaining);
            TextRenderer.DrawText(g, entry.Item.Label, labelFont,
                new Rectangle((int)Math.Round(cursor), rowY, labelW, rowH), UiTokens.Muted,
                entry.LabelW <= remaining ? DrawFlags : DrawEllipsisFlags);
            cursor += labelW + labelGap;

            remaining = (int)Math.Max(0, Math.Floor(maxX - cursor));
            if (remaining < 4) break;
            var valueW = Math.Min(entry.ValueW, remaining);
            TextRenderer.DrawText(g, entry.Item.Value, valueFont,
                new Rectangle((int)Math.Round(cursor), rowY, valueW, rowH), entry.Item.ValueColor,
                entry.ValueW <= remaining ? DrawFlags : DrawEllipsisFlags);
            cursor += valueW;

            if (entry.SuffixW > 0)
            {
                cursor += suffixGap;
                remaining = (int)Math.Max(0, Math.Floor(maxX - cursor));
                if (remaining > 6)
                {
                    var suffixW = Math.Min(entry.SuffixW, remaining);
                    TextRenderer.DrawText(g, entry.Item.Suffix, suffixFont,
                        new Rectangle((int)Math.Round(cursor), rowY, suffixW, rowH),
                        entry.Item.SuffixColor ?? UiTokens.TextSecondary,
                        entry.SuffixW <= remaining ? DrawFlags : DrawEllipsisFlags);
                    cursor += suffixW;
                }
            }
        }
        return cursor;
    }
}

/// <summary>Compact owner-drawn KPI and 3-way slot breakdown bar for Router history.</summary>
internal sealed class RouterHistoryMetricsStrip : Control
{
    private RouterDailyThroughput _data = new(0, 0, 0, null, "", "");
    private RouterVisualPalette _palette = new(UiTokens.CardSurface);
    private bool _compact;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal RouterDailyThroughput Data => _data;

    internal int DesiredLogicalHeight =>
        _data.RangeKind == RouterHistoryRangeKind.All
            ? (_data.ActiveDays == 0 ? 0 : (_data.HasDurations && !_compact ? 58 : 28))
            : (_data.TotalProcessed == 0
                ? (_data.HasAnyHistory ? 28 : 0)
                : (_data.HasDurations && !_compact ? 58 : 28));

    internal RouterHistoryMetricsStrip()
    {
        AccessibleName = "历史处理统计";
        Margin = new Padding(0, 0, 0, 4);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    internal void Update(RouterDailyThroughput data, bool compact, string accessibleDescription)
    {
        _data = data;
        _compact = compact;
        Text = data.SummaryText;
        AccessibleDescription = accessibleDescription;
        Invalidate();
    }

    internal void ApplyPalette(RouterVisualPalette palette)
    {
        _palette = palette;
        BackColor = Parent?.BackColor ?? palette.Surface;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = RouterVisualGeometry.Scale(this);
        MonitorStripPaint.DrawInsetCard(g, this, _palette, s);
        if (DesiredLogicalHeight == 0 || Width <= 24 || Height <= 10) return;

        var padX = 10f * s;
        var left = padX;
        var right = Width - padX;
        using var labelFont = new Font(Font.FontFamily, 10f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font(Font.FontFamily, 10.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var captionFont = new Font(Font.FontFamily, 9.8f * s, FontStyle.Regular, GraphicsUnit.Pixel);

        var isSingleDay = _data.RangeKind != RouterHistoryRangeKind.All;
        var dayLabel = _data.RangeKind == RouterHistoryRangeKind.Today
            ? "今日"
            : (_data.RangeDate.Length == 10 ? _data.RangeDate[5..] : _data.RangeDate);

        if (isSingleDay && _data.TotalProcessed == 0)
        {
            var emptyItems = new MonitorKpiItem[]
            {
                new(dayLabel, "0 条", UiTokens.Muted),
                new("状态", "暂无已结束任务", UiTokens.Muted),
            };
            DrawCompactStripRow(g, left, right, emptyItems, "点「全部」看历史均值", labelFont, valueFont, captionFont, s);
            return;
        }

        var avgText = _data.AveragePerActiveDay?.ToString("0.#", CultureInfo.InvariantCulture) ?? "0";
        if (!_data.HasDurations || _compact)
        {
            var compactItems = new List<MonitorKpiItem>(3);
            if (isSingleDay)
            {
                compactItems.Add(new(dayLabel, string.Create(CultureInfo.InvariantCulture, $"{_data.TotalProcessed:N0} 条"), UiTokens.TextStrong,
                    _data.HasDurations && _data.TotalOccupiedSeconds > 0 ? "占 " + RouterTaskPresentation.FormatDuration(_data.TotalOccupiedSeconds) : null, UiTokens.Info));
                if (_data.HasDurations && _data.AverageTaskOccupiedSeconds is { } singleAvg)
                {
                    compactItems.Add(new("单任务均占", RouterTaskPresentation.FormatDuration(singleAvg), UiTokens.TextStrong));
                }
                else if (_data.SelectedCompleted > 0 || _data.SelectedFailed > 0)
                {
                    compactItems.Add(new("成功", string.Create(CultureInfo.InvariantCulture, $"{_data.SelectedCompleted:N0}"), UiTokens.Positive));
                    compactItems.Add(new("失败", string.Create(CultureInfo.InvariantCulture, $"{_data.SelectedFailed:N0}"),
                        _data.SelectedFailed > 0 ? UiTokens.Negative : UiTokens.Muted));
                }
            }
            else
            {
                compactItems.Add(new("日均", string.Create(CultureInfo.InvariantCulture, $"{avgText} 条/天"), UiTokens.Info));
                if (_data.HasDurations && _data.AverageTaskOccupiedSeconds is { } avgTask)
                    compactItems.Add(new("单任务均占", RouterTaskPresentation.FormatDuration(avgTask), UiTokens.TextStrong));
                else
                    compactItems.Add(new("活跃", string.Create(CultureInfo.InvariantCulture, $"{_data.ActiveDays:N0} 天"), UiTokens.TextSecondary));
                compactItems.Add(new("累计", string.Create(CultureInfo.InvariantCulture, $"{_data.TotalProcessed:N0} 条"), UiTokens.TextSecondary));
            }
            DrawCompactStripRow(g, left, right, compactItems, isSingleDay ? "悬停查看近14日明细" : "悬停查看每日明细", labelFont, valueFont, captionFont, s);
            return;
        }

        // Row 1: Scoped KPIs + right badge
        var badgeText = _data.ZeroEditorScriptCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $"无 Editor 脚本 {_data.ZeroEditorScriptCount:N0} 条")
            : _data.TotalUnmeasuredSeconds > 0.05 && _data.DispatchedCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"已拆解 {_data.MeasuredCount:N0}/{_data.DispatchedCount:N0}")
                : _data.DispatchedCount > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"已拆解 {_data.DispatchedCount:N0} 条")
                    : "";
        var isWarningBadge = _data.ZeroEditorScriptCount > 0;
        var badgeW = 0f;
        var badgeBounds = RectangleF.Empty;
        if (badgeText.Length > 0)
        {
            if (isWarningBadge)
            {
                var pillSize = UiPaint.PillSize(badgeText, captionFont, s);
                badgeW = pillSize.Width;
                badgeBounds = new RectangleF(right - badgeW, 3f * s, badgeW, Math.Min(18f * s, pillSize.Height));
            }
            else
            {
                badgeW = MonitorStripPaint.MeasureWidth(g, badgeText, captionFont);
                badgeBounds = new RectangleF(right - badgeW, 2f * s, badgeW, 19f * s);
            }
        }

        var kpis = isSingleDay
            ? new List<MonitorKpiItem>(3)
            {
                new(dayLabel, string.Create(CultureInfo.InvariantCulture, $"{_data.TotalProcessed:N0} 条"), UiTokens.TextStrong),
                new("总占用", RouterTaskPresentation.FormatDuration(_data.TotalOccupiedSeconds), UiTokens.Info),
                new("单任务均占", _data.AverageTaskOccupiedSeconds is { } singleTask ? RouterTaskPresentation.FormatDuration(singleTask) : "—", UiTokens.TextStrong),
            }
            : new List<MonitorKpiItem>(3)
            {
                new("日均", string.Create(CultureInfo.InvariantCulture, $"{avgText} 条"), UiTokens.TextStrong,
                    _data.AverageDailyOccupiedSeconds is { } avgDay ? RouterTaskPresentation.FormatDuration(avgDay) + "/天" : null, UiTokens.TextSecondary),
                new("单任务均占", _data.AverageTaskOccupiedSeconds is { } allTask ? RouterTaskPresentation.FormatDuration(allTask) : "—", UiTokens.Info),
                new("累计", string.Create(CultureInfo.InvariantCulture, $"{_data.ActiveDays:N0} 天"), UiTokens.TextStrong,
                    RouterTaskPresentation.FormatDuration(_data.TotalOccupiedSeconds), UiTokens.TextSecondary),
            };
        var leftMaxX = badgeText.Length > 0 ? right - badgeW - 10f * s : right;
        var usedX = MonitorStripPaint.DrawKpiItems(g, left, leftMaxX, 2f * s, 19f * s, kpis, labelFont, valueFont, captionFont, s);
        if (badgeText.Length > 0 && badgeBounds.Left >= usedX + 6f * s)
        {
            if (isWarningBadge)
                UiPaint.Pill(g, badgeBounds, badgeText, captionFont, UiTokens.Warning, UiTokens.Mix(Parent?.BackColor ?? _palette.Surface, Color.White, .032f), s);
            else
                TextRenderer.DrawText(g, badgeText, captionFont, Rectangle.Round(badgeBounds), UiTokens.Muted,
                    MonitorStripPaint.MeasureFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        // Row 2: Segmented stacked bar
        var barRect = new RectangleF(left, 24f * s, Math.Max(8f, right - left), 5.5f * s);
        using (var trackBrush = new SolidBrush(UiPalette.Slate800))
            UiPaint.FillRounded(g, trackBrush, barRect, barRect.Height / 2f);

        var occupied = _data.TotalOccupiedSeconds;
        if (occupied > 0.001)
        {
            var segments = new List<(double Seconds, Color Color)>(4);
            if (_data.TotalEditorActiveSeconds > 0.001) segments.Add((_data.TotalEditorActiveSeconds, UiTokens.Positive));
            if (_data.TotalRouterOverheadSeconds > 0.001) segments.Add((_data.TotalRouterOverheadSeconds, UiTokens.Warning));
            if (_data.TotalNonEditorScriptSeconds > 0.001) segments.Add((_data.TotalNonEditorScriptSeconds, UiPalette.Violet400));
            if (_data.TotalUnmeasuredSeconds > 0.05) segments.Add((_data.TotalUnmeasuredSeconds, UiPalette.Slate500));

            if (segments.Count > 0)
            {
                var gap = 1.5f * s;
                var usableBarW = Math.Max(4f, barRect.Width - gap * (segments.Count - 1));
                var minSegW = 3f * s;
                var segTotalSec = segments.Sum(seg => seg.Seconds);
                var rawWidths = segments.Select(seg => Math.Max(minSegW, (float)(usableBarW * seg.Seconds / Math.Max(0.001, segTotalSec)))).ToArray();
                var rawSum = rawWidths.Sum();
                var scaleFactor = rawSum > usableBarW ? usableBarW / rawSum : 1f;
                var segX = barRect.X;
                for (var i = 0; i < segments.Count; i++)
                {
                    var w = rawWidths[i] * scaleFactor;
                    if (w < 1f) continue;
                    using var brush = new SolidBrush(segments[i].Color);
                    var r = i == segments.Count - 1 ? Math.Max(w, barRect.Right - segX) : w;
                    UiPaint.FillRounded(g, brush, new RectangleF(segX, barRect.Y, Math.Min(r, barRect.Right - segX), barRect.Height), barRect.Height / 2f);
                    segX += w + gap;
                    if (segX >= barRect.Right) break;
                }
            }
        }

        // Row 3: Legend
        var legend = new List<(Color Color, string Text)>(4)
        {
            (UiTokens.Positive, $"真实 Editor {RouterTaskPresentation.FormatShare(_data.TotalEditorActiveSeconds, occupied)} ({RouterTaskPresentation.FormatDuration(_data.TotalEditorActiveSeconds)})"),
            (UiTokens.Warning, $"Router 开销 {RouterTaskPresentation.FormatShare(_data.TotalRouterOverheadSeconds, occupied)} ({RouterTaskPresentation.FormatDuration(_data.TotalRouterOverheadSeconds)})"),
            (UiPalette.Violet400, $"非 Editor {RouterTaskPresentation.FormatShare(_data.TotalNonEditorScriptSeconds, occupied)} ({RouterTaskPresentation.FormatDuration(_data.TotalNonEditorScriptSeconds)})"),
        };
        if (_data.TotalUnmeasuredSeconds > 0.05)
        {
            legend.Add((UiPalette.Slate500, $"旧未拆分 {RouterTaskPresentation.FormatShare(_data.TotalUnmeasuredSeconds, occupied)} ({RouterTaskPresentation.FormatDuration(_data.TotalUnmeasuredSeconds)})"));
        }

        var legendY = 33f * s;
        var legendH = Math.Max(12f * s, Height - legendY - 2f * s);
        var dotSize = 5.5f * s;
        var legendGap = 12f * s;
        var lx = left;
        var itemWidths = legend.Select(item => dotSize + 4.5f * s + MonitorStripPaint.MeasureWidth(g, item.Text, captionFont)).ToArray();
        var neededLegend = itemWidths.Sum() + legendGap * Math.Max(0, legend.Count - 1);
        if (legend.Count > 1 && neededLegend > right - left)
            legendGap = Math.Max(5f * s, ((right - left) - itemWidths.Sum()) / (legend.Count - 1));

        for (var i = 0; i < legend.Count; i++)
        {
            if (lx + dotSize + 10f * s >= right) break;
            using var dotBrush = new SolidBrush(legend[i].Color);
            g.FillEllipse(dotBrush, lx, legendY + (legendH - dotSize) / 2f, dotSize, dotSize);
            var textX = (int)Math.Round(lx + dotSize + 4.5f * s);
            var availW = (int)Math.Max(0, Math.Floor(right - textX));
            if (availW <= 8) break;
            var measuredW = MonitorStripPaint.MeasureWidth(g, legend[i].Text, captionFont);
            TextRenderer.DrawText(g, legend[i].Text, captionFont,
                new Rectangle(textX, (int)Math.Round(legendY), Math.Min(measuredW, availW), (int)Math.Ceiling(legendH)),
                UiTokens.TextSecondary,
                measuredW <= availW ? MonitorStripPaint.DrawFlags : MonitorStripPaint.DrawFlags | TextFormatFlags.EndEllipsis);
            lx += dotSize + 4.5f * s + measuredW + legendGap;
        }
    }

    private void DrawCompactStripRow(
        Graphics g,
        float left,
        float right,
        IReadOnlyList<MonitorKpiItem> items,
        string hintText,
        Font labelFont,
        Font valueFont,
        Font captionFont,
        float s)
    {
        var hintW = MonitorStripPaint.MeasureWidth(g, hintText, captionFont);
        var contentEnd = MonitorStripPaint.DrawKpiItems(g, left, right, 2f * s, Height - 4f * s, items, labelFont, valueFont, captionFont, s);
        if (right - contentEnd >= hintW + 14f * s)
        {
            TextRenderer.DrawText(g, hintText, captionFont,
                new Rectangle((int)Math.Round(right - hintW), 0, hintW, Height), UiTokens.Muted,
                MonitorStripPaint.MeasureFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
    }
}

/// <summary>Compact owner-drawn KPI summary strip for the Intake service status card.</summary>
internal sealed class IntakeOverviewStrip : Control
{
    private IntakeOverviewMetrics _metrics = new(false, 0, 0, 0, 0, 0, false, 0, "", true);
    private RouterVisualPalette _palette = new(UiTokens.CardSurface);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IntakeOverviewMetrics Metrics => _metrics;

    internal IntakeOverviewStrip()
    {
        AccessibleName = "分拣概览";
        Margin = Padding.Empty;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    internal void Update(IntakeOverviewMetrics metrics, string summaryText, string? accessibleDescription = null)
    {
        _metrics = metrics;
        Text = summaryText;
        AccessibleDescription = accessibleDescription ?? summaryText;
        Invalidate();
    }

    internal void ApplyPalette(RouterVisualPalette palette)
    {
        _palette = palette;
        BackColor = Parent?.BackColor ?? palette.Surface;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = RouterVisualGeometry.Scale(this);
        MonitorStripPaint.DrawInsetCard(g, this, _palette, s);
        if (Width <= 24 || Height <= 10) return;

        var padX = 10f * s;
        using var labelFont = new Font(Font.FontFamily, 10f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var valueFont = new Font(Font.FontFamily, 10.5f * s, FontStyle.Bold, GraphicsUnit.Pixel);

        if (!_metrics.CountsKnown)
        {
            TextRenderer.DrawText(g, _metrics.FallbackMessage, labelFont,
                new Rectangle((int)Math.Round(padX), 0, Math.Max(1, Width - (int)Math.Round(padX * 2)), Height),
                UiTokens.Muted,
                MonitorStripPaint.DrawFlags | TextFormatFlags.EndEllipsis);
            return;
        }

        string T(string zh, string en) => _metrics.Chinese ? zh : en;
        var items = new List<MonitorKpiItem>(6)
        {
            new(T("在途", "In flight"), string.Create(CultureInfo.InvariantCulture, $"{_metrics.InFlight:N0}"),
                _metrics.InFlight > 0 ? UiTokens.Info : UiTokens.Muted),
            new(_metrics.Unconfirmed > 0 ? T("已知待核", "Known review") : T("待核对", "Needs review"),
                string.Create(CultureInfo.InvariantCulture, $"{_metrics.PendingReview:N0}"),
                _metrics.PendingReview > 0 ? UiTokens.Warning : UiTokens.Muted),
        };
        if (_metrics.Unconfirmed > 0)
        {
            items.Add(new(T("待确认", "Unconfirmed"), string.Create(CultureInfo.InvariantCulture, $"{_metrics.Unconfirmed:N0}"), UiTokens.Warning));
        }
        items.Add(new(T("归档保护", "Archive protected"), string.Create(CultureInfo.InvariantCulture, $"{_metrics.ProtectedArchive:N0}"),
            _metrics.ProtectedArchive > 0 ? UiTokens.Accent : UiTokens.Muted));
        if (_metrics.HasTimedHistory)
        {
            items.Add(new(T("今日记录", "Today"), string.Create(CultureInfo.InvariantCulture, $"{_metrics.TodayHistory:N0}"),
                _metrics.TodayHistory > 0 ? UiTokens.TextStrong : UiTokens.Muted));
        }
        if (_metrics.ClosedHistory > 0)
        {
            items.Add(new(T("已结案", "Closed"), string.Create(CultureInfo.InvariantCulture, $"{_metrics.ClosedHistory:N0}"), UiTokens.Positive));
        }

        MonitorStripPaint.DrawKpiItems(g, padX, Width - padX, 0f, Height, items, labelFont, valueFont, labelFont, s);
    }
}
