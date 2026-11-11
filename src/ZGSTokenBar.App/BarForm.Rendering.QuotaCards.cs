using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Rendering of quota cards and the Codex account pool, with their geometry and hit targets.</summary>
internal sealed partial class BarForm
{
    private void DrawTaskbarCard(Graphics graphics, RectangleF bounds, QuotaCard card)
    {
        DrawTaskbarCardShell(graphics, bounds);
        DrawTaskbarProviderLogo(graphics, bounds, card);

        var targets = TaskbarQuotaTargets(card, bounds);
        if (targets.Length == 3)
        {
            DrawTaskbarCapsule(graphics, targets[0]);
            DrawTaskbarDualCapsule(
                graphics,
                TaskbarCapsuleBounds(bounds, 2, 1),
                targets[1],
                targets[2]);
        }
        else
        {
            foreach (var target in targets)
            {
                DrawTaskbarCapsule(graphics, target);
            }
        }
        _taskbarWindowBounds.AddRange(targets);
    }

    private void DrawCollapsedAiGatewayCard(Graphics graphics, RectangleF bounds, QuotaCard card)
    {
        var balance = card.Balance;
        var color = balance is null
            ? UiPalette.Slate500
            : AiGatewayCompactStatusColor(balance.Status);
        DrawCollapsedProviderSummary(
            graphics,
            bounds,
            card,
            AiGatewayBalanceFormatting.CompactAmount(balance?.TotalBalance),
            color);
    }

    private void DrawCollapsedQuotaCard(Graphics graphics, RectangleF bounds, QuotaCard card)
    {
        var summary = CollapsedQuotaSummary(card);
        DrawCollapsedProviderSummary(graphics, bounds, card, summary.Value, summary.Color);
    }

    private void DrawCollapsedCodexPool(
        Graphics graphics,
        RectangleF bounds,
        IReadOnlyList<QuotaCard> cards)
    {
        var weekly = CodexPoolPresentation.Create(cards, DateTimeOffset.UtcNow)[0];
        var remaining = weekly.AggregateRemainingPercent;
        var value = CompactRemaining(remaining);
        var color = remaining is not null
            ? QuotaColorScale.ForRemaining(remaining.Value)
            : UiPalette.Slate500;
        var primaryCard = cards.FirstOrDefault(card => card.Active) ?? cards[0];
        DrawCollapsedProviderSummary(graphics, bounds, primaryCard, value, color);
    }

    private void DrawCollapsedProviderSummary(
        Graphics graphics,
        RectangleF bounds,
        QuotaCard card,
        string value,
        Color color)
    {
        DrawTaskbarCardShell(graphics, bounds);
        var logoBounds = new RectangleF(bounds.X + 3, bounds.Y + 3, 15, 15);
        DrawTaskbarProviderLogoAt(graphics, logoBounds, card, showOrdinal: false);

        using var statusBrush = new SolidBrush(color);
        graphics.FillEllipse(statusBrush, bounds.Right - 7, bounds.Top + 4, 4, 4);

        using var valueBrush = new SolidBrush(color);
        DrawString(
            graphics,
            value,
            _badgeFont,
            valueBrush,
            new RectangleF(bounds.X + 2, bounds.Bottom - 12, bounds.Width - 4, 9),
            StringAlignment.Center);
    }

    internal static (string Value, Color Color) CollapsedQuotaSummary(QuotaCard card)
    {
        var window = CollapsedQuotaWindow(card);
        var remaining = window?.UsedPercent is { } used
            ? Math.Clamp(100 - used, 0, 100)
            : (double?)null;
        return (
            CompactRemaining(remaining),
            remaining is null
                ? UiPalette.Slate500
                : QuotaColorScale.ForRemaining(remaining.Value));
    }

    internal static QuotaWindow? CollapsedQuotaWindow(QuotaCard card)
    {
        var windows = card.Provider == ProviderKind.Codex
            ? TaskbarMiniGrouping.CodexRowWindows(card)
            : TaskbarMiniLayoutMath.VisibleWindows(card.Windows);
        return windows
            .Where(window => window.UsedPercent is not null)
            .OrderBy(window => window.Duration > TimeSpan.Zero ? window.Duration : TimeSpan.MaxValue)
            .FirstOrDefault()
            ?? windows.FirstOrDefault();
    }

    private static Color AiGatewayCompactStatusColor(AiGatewayBalanceStatus? status) => status switch
    {
        AiGatewayBalanceStatus.Available => UiPalette.Emerald400,
        AiGatewayBalanceStatus.Stale => UiPalette.Amber400,
        AiGatewayBalanceStatus.Unavailable => UiPalette.Rose400,
        _ => UiPalette.Slate500,
    };

    private static Color Sub2ApiPoolCompactStatusColor(Sub2ApiPoolStatus status) => status switch
    {
        Sub2ApiPoolStatus.Available => UiPalette.Emerald400,
        Sub2ApiPoolStatus.Stale => UiPalette.Amber400,
        Sub2ApiPoolStatus.Unavailable => UiPalette.Rose400,
        _ => UiPalette.Slate500,
    };

    private static Color Sub2ApiUsageCompactStatusColor(Sub2ApiUsageStatus status) => status switch
    {
        Sub2ApiUsageStatus.Available => UiPalette.Emerald400,
        Sub2ApiUsageStatus.Stale => UiPalette.Amber400,
        Sub2ApiUsageStatus.Unavailable => UiPalette.Rose400,
        _ => UiPalette.Slate500,
    };

    private static Color Sub2ApiQuotaCompactStatusColor(Sub2ApiQuotaStatus status) => status switch
    {
        Sub2ApiQuotaStatus.Available => UiPalette.Emerald400,
        Sub2ApiQuotaStatus.Stale => UiPalette.Amber400,
        Sub2ApiQuotaStatus.Unavailable => UiPalette.Rose400,
        _ => UiPalette.Slate500,
    };

    private void DrawTaskbarCodexPool(
        Graphics graphics,
        RectangleF bounds,
        IReadOnlyList<QuotaCard> cards)
    {
        var now = DateTimeOffset.UtcNow;
        DrawTaskbarCardShell(graphics, bounds);
        var logoBounds = DrawTaskbarProviderLogo(graphics, bounds, cards[0], showOrdinal: false);
        DrawAccountOrdinalBadge(
            graphics,
            new RectangleF(logoBounds.Right - 7, logoBounds.Bottom - 6, 10, 8),
            cards.Count.ToString());

        foreach (var targetRow in TaskbarCodexPoolTargetRows(bounds, cards, now))
        {
            DrawTaskbarCodexPoolRow(graphics, targetRow);
            _taskbarWindowBounds.AddRange(targetRow.Targets);
        }
    }

    private void DrawTaskbarCodexPoolRow(
        Graphics graphics,
        CodexPoolTargetRow targetRow)
    {
        using var rowPath = RoundedRectangle(targetRow.Bounds, 4);
        using var rowFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var rowBorder = new Pen(UiPalette.Slate700.WithAlpha(58), 1);
        graphics.FillPath(rowFill, rowPath);
        graphics.DrawPath(rowBorder, rowPath);

        using var labelBrush = new SolidBrush(UiPalette.Slate300);
        DrawString(
            graphics,
            targetRow.Row.Label,
            _rowFont,
            labelBrush,
            new RectangleF(targetRow.Bounds.X + 2, targetRow.Bounds.Y, 15, targetRow.Bounds.Height),
            StringAlignment.Near);

        for (var index = 0; index < targetRow.Targets.Count; index++)
        {
            var target = targetRow.Targets[index];
            var segment = targetRow.Row.Segments[index];
            var railHeight = targetRow.Bounds.Height > 20 ? 8f : 5f;
            var railBounds = new RectangleF(
                target.Bounds.X,
                target.Bounds.Y + (target.Bounds.Height - railHeight) / 2,
                target.Bounds.Width,
                railHeight);
            using var trackBrush = new SolidBrush(UiPalette.Slate800);
            graphics.FillRoundedRectangle(trackBrush, railBounds, 2);

            double? remaining = null;
            if (segment.RemainingPercent is not null)
            {
                var used = AnimatedUsed(segment.Card.Key, target.Window);
                remaining = used is null
                    ? segment.RemainingPercent
                    : Math.Clamp(100 - used.Value, 0, 100);
            }
            if (remaining is { } segmentRemaining && segmentRemaining > 0)
            {
                var color = MixColor(
                    QuotaColorScale.ForRemaining(segmentRemaining),
                    UiPalette.Slate50,
                    SnapshotPulse(target.Id) * .22f);
                using var activeBrush = new SolidBrush(color);
                graphics.FillRoundedRectangle(
                    activeBrush,
                    new RectangleF(
                        railBounds.X,
                        railBounds.Y,
                        railBounds.Width * (float)(segmentRemaining / 100),
                        railBounds.Height),
                    2);
            }

            var emphasis = MiniTargetEmphasis(target.Id);
            if (emphasis > 0)
            {
                using var hoverPen = new Pen(Color.FromArgb(
                    (int)Math.Round(70 + 120 * emphasis),
                    226,
                    232,
                    240), 1);
                using var hoverPath = RoundedRectangle(RectangleF.Inflate(railBounds, .5f, .5f), 2);
                graphics.DrawPath(hoverPen, hoverPath);
            }
        }

        var aggregate = targetRow.Row.AggregateRemainingPercent;
        var aggregateColor = aggregate is { } remainingPercent
            ? QuotaColorScale.ForRemaining(remainingPercent)
            : UiPalette.Slate500;
        DrawTaskbarCodexPoolCapacitySummary(graphics, targetRow, aggregateColor);
    }

    private void DrawTaskbarCodexPoolCapacitySummary(
        Graphics graphics,
        CodexPoolTargetRow targetRow,
        Color remainingColor)
    {
        var summary = CodexPoolPresentation.CapacitySummary(targetRow.Row);
        var separatorIndex = summary.IndexOf('/');
        if (separatorIndex <= 0 || targetRow.Row.RemainingAccountEquivalents is null)
        {
            using var fallbackBrush = new SolidBrush(remainingColor);
            DrawString(
                graphics,
                summary,
                _rowFont,
                fallbackBrush,
                targetRow.ValueBounds,
                StringAlignment.Far);
            return;
        }

        var remainingText = summary[..separatorIndex];
        var capacityText = summary[separatorIndex..];
        var capacityWidth = Math.Min(
            targetRow.ValueBounds.Width,
            MeasureWidth(graphics, capacityText, _rowFont) + 1);
        var remainingBounds = new RectangleF(
            targetRow.ValueBounds.X,
            targetRow.ValueBounds.Y,
            Math.Max(1, targetRow.ValueBounds.Width - capacityWidth),
            targetRow.ValueBounds.Height);

        using var remainingBrush = new SolidBrush(remainingColor);
        using var capacityBrush = new SolidBrush(UiPalette.Slate400);
        DrawString(
            graphics,
            remainingText,
            _rowFont,
            remainingBrush,
            remainingBounds,
            StringAlignment.Far);
        DrawString(
            graphics,
            capacityText,
            _rowFont,
            capacityBrush,
            targetRow.ValueBounds,
            StringAlignment.Far);
    }

    private static CodexPoolTargetRow[] TaskbarCodexPoolTargetRows(
        RectangleF cardBounds,
        IReadOnlyList<QuotaCard> cards,
        DateTimeOffset now)
    {
        var rows = CodexPoolPresentation.Create(cards, now);
        var singleRow = rows.Count == 1;
        return rows
            .Select((row, rowIndex) =>
            {
                var rowHeight = singleRow ? 32f : 15f;
                var bounds = new RectangleF(
                    cardBounds.X + 33,
                    cardBounds.Y + 2 + (singleRow ? 0 : rowIndex * 17),
                    cardBounds.Width - 37,
                    rowHeight);
                var valueBounds = new RectangleF(bounds.Right - 50, bounds.Y, 48, bounds.Height);
                var trackLeft = bounds.Left + 17;
                var trackRight = valueBounds.Left - 2;
                var trackWidth = Math.Max(1, trackRight - trackLeft);
                var gap = row.Segments.Count > 1
                    && trackWidth >= row.Segments.Count * 2 - 1
                        ? 1f
                        : 0f;
                var totalGap = gap * Math.Max(0, row.Segments.Count - 1);
                var segmentWidth = row.Segments.Count == 0
                    ? 0
                    : Math.Max(0, (trackWidth - totalGap) / row.Segments.Count);
                var targets = row.Segments
                    .Select((segment, segmentIndex) =>
                    {
                        var window = segment.Window
                            ?? new QuotaWindow(row.Label, null, null, row.Duration);
                        return new MiniQuotaTarget(
                            new RectangleF(
                                trackLeft + segmentIndex * (segmentWidth + gap),
                                bounds.Y,
                                segmentWidth,
                                bounds.Height),
                            segment.Card,
                            window);
                    })
                    .ToArray();
                return new CodexPoolTargetRow(row, bounds, valueBounds, targets);
            })
            .ToArray();
    }

    private void DrawTaskbarCodexGroup(
        Graphics graphics,
        RectangleF bounds,
        IReadOnlyList<QuotaCard> cards)
    {
        DrawTaskbarCardShell(graphics, bounds);
        DrawTaskbarProviderLogo(graphics, bounds, cards[0], showOrdinal: false);
        DrawTaskbarAccountOrdinalColumn(graphics, bounds, cards);
        for (var index = 0; index < cards.Count; index++)
        {
            var rowBounds = TaskbarCodexCapsuleBounds(bounds, index, cards.Count);
            if (cards.Count > 2)
            {
                var target = TaskbarCompactCodexRowTarget(cards[index], rowBounds);
                DrawTaskbarCompactCodexCapsule(graphics, target);
                _taskbarWindowBounds.Add(target);
                continue;
            }

            var targets = TaskbarCodexRowTargets(cards[index], rowBounds);
            if (targets.Length == 1)
            {
                DrawTaskbarCapsule(graphics, targets[0]);
            }
            else
            {
                DrawTaskbarDualCapsule(graphics, rowBounds, targets[0], targets[1]);
            }
            _taskbarWindowBounds.AddRange(targets);
        }
    }

    private void DrawTaskbarAccountOrdinalColumn(
        Graphics graphics,
        RectangleF cardBounds,
        IReadOnlyList<QuotaCard> cards)
    {
        if (cards.Count >= 4)
        {
            for (var index = 0; index < cards.Count; index++)
            {
                var cell = TaskbarAccountOrdinalRowBounds(cardBounds, index, cards.Count);
                var style = TaskbarAccountPlanStyle(cards[index]);
                using var cellPath = RoundedRectangle(cell, Math.Min(3, cell.Height / 3));
                using var gridFill = new SolidBrush(style?.Fill ?? _backgroundTheme.Outer);
                using var gridBorder = new Pen(style?.Border ?? UiPalette.Slate600, 1);
                using var gridText = new SolidBrush(style?.Text ?? UiPalette.Teal200);
                graphics.FillPath(gridFill, cellPath);
                graphics.DrawPath(gridBorder, cellPath);
                DrawString(
                    graphics,
                    CodexAccountOrdinal(cards[index]) ?? (index + 1).ToString(),
                    _accountOrdinalFont,
                    gridText,
                    cell,
                    StringAlignment.Center);
            }
            return;
        }

        var bounds = new RectangleF(cardBounds.X + 31, cardBounds.Y + 2, 10, 32);
        using var path = RoundedRectangle(bounds, 4);
        using var fill = new SolidBrush(_backgroundTheme.Outer);
        using var border = new Pen(UiPalette.Slate600, 1);
        using var separator = new Pen(UiPalette.Slate600.WithAlpha(48), 1);
        graphics.FillPath(fill, path);
        var clipState = graphics.Save();
        graphics.SetClip(path);
        for (var index = 0; index < cards.Count; index++)
        {
            var style = TaskbarAccountPlanStyle(cards[index]);
            if (style is null) continue;
            using var rowFill = new SolidBrush(style.Value.Fill);
            graphics.FillRectangle(
                rowFill,
                TaskbarAccountOrdinalRowBounds(cardBounds, index, cards.Count));
        }
        graphics.Restore(clipState);
        graphics.DrawPath(border, path);
        for (var index = 1; index < cards.Count; index++)
        {
            var separatorY = bounds.Top + bounds.Height * index / cards.Count;
            graphics.DrawLine(separator, bounds.Left + 2, separatorY, bounds.Right - 2, separatorY);
        }

        for (var index = 0; index < cards.Count; index++)
        {
            var row = TaskbarAccountOrdinalRowBounds(cardBounds, index, cards.Count);
            var style = TaskbarAccountPlanStyle(cards[index]);
            using var text = new SolidBrush(style?.Text ?? UiPalette.Teal200);
            DrawString(
                graphics,
                CodexAccountOrdinal(cards[index]) ?? (index + 1).ToString(),
                _accountOrdinalFont,
                text,
                row,
                StringAlignment.Center);
        }
    }

    private static PlanBadgeStyle? TaskbarAccountPlanStyle(QuotaCard card)
    {
        var label = PlanBadgePresentation.Label(card.Badge);
        return PlanBadgePresentation.TryGetStyle(label, out var style) ? style : null;
    }

    private RectangleF DrawTaskbarProviderLogo(
        Graphics graphics,
        RectangleF bounds,
        QuotaCard card,
        bool showOrdinal = true)
    {
        var logoBounds = new RectangleF(bounds.X + 5, bounds.Y + 6, 24, 24);
        return DrawTaskbarProviderLogoAt(graphics, logoBounds, card, showOrdinal);
    }

    private RectangleF DrawTaskbarProviderLogoAt(
        Graphics graphics,
        RectangleF logoBounds,
        QuotaCard card,
        bool showOrdinal)
    {
        if (showOrdinal) DrawProviderLogo(graphics, logoBounds, card);
        else DrawProviderLogo(graphics, logoBounds, card, false);
        var radarEnabled = _radarProviders.Contains(card.Provider);
        var deepSeekRadarEnabled = card.Provider == ProviderKind.AiGateway
            && _radarProviders.Contains(ProviderKind.Codex);
        if (deepSeekRadarEnabled)
        {
            _taskbarRadarBounds.Add(new MiniRadarTarget(
                logoBounds,
                WindowKey(card.Key, "__deepseek-radar"),
                ProviderKind.AiGateway,
                ProviderKind.Codex,
                true,
                RadarSurfaceIds.DeepSeek));
            if (_radarState.HasUnreadFor(RadarSurfaceIds.DeepSeek)
                && _radarState.Snapshot?.Provider == ProviderKind.Codex)
            {
                DrawRadarUnreadDot(graphics, logoBounds);
            }
        }
        else if (HasProviderOverview(card.Provider, radarEnabled, _codexTokenUsage))
        {
            _taskbarRadarBounds.Add(new MiniRadarTarget(
                logoBounds,
                card.Key,
                card.Provider,
                card.Provider,
                false,
                RadarSurfaceIds.Codex));
            if (radarEnabled
                && _radarState.HasUnreadFor(RadarSurfaceIds.Codex)
                && _radarState.Snapshot?.Provider == card.Provider)
            {
                DrawRadarUnreadDot(graphics, logoBounds);
            }
        }
        return logoBounds;
    }

    internal static bool HasProviderOverview(
        ProviderKind provider,
        bool radarEnabled,
        CodexTokenUsageSummary? codexTokenUsage) =>
        radarEnabled || provider == ProviderKind.Codex && codexTokenUsage is not null;

    private static RectangleF TaskbarCapsuleBounds(RectangleF cardBounds, int windowCount, int index)
    {
        var capsuleX = cardBounds.X + 33;
        var capsuleWidth = cardBounds.Width - 37;
        var capsuleY = windowCount == 1 ? cardBounds.Y + 10.5f : cardBounds.Y + 2;
        return new RectangleF(capsuleX, capsuleY + index * 17, capsuleWidth, 15);
    }

    private static RectangleF TaskbarCodexCapsuleBounds(
        RectangleF cardBounds,
        int index,
        int accountCount)
    {
        if (accountCount <= 2)
        {
            return new RectangleF(
                cardBounds.X + 44,
                cardBounds.Y + 2 + index * 17,
                cardBounds.Width - 48,
                15);
        }

        if (accountCount >= 4)
        {
            var tile = TaskbarCompactCodexTileBounds(cardBounds, index, accountCount);
            return RectangleF.FromLTRB(tile.Left + 12, tile.Top, tile.Right, tile.Bottom);
        }

        var slotHeight = 32f / accountCount;
        return new RectangleF(
            cardBounds.X + 44,
            cardBounds.Y + 2 + index * slotHeight,
            cardBounds.Width - 48,
            Math.Max(1, slotHeight - 1));
    }

    private static RectangleF TaskbarAccountRowBounds(
        RectangleF cardBounds,
        int index,
        int accountCount)
    {
        if (accountCount >= 4)
        {
            return TaskbarCompactCodexTileBounds(cardBounds, index, accountCount);
        }

        var rowHeight = cardBounds.Height / accountCount;
        return new RectangleF(
            cardBounds.X,
            cardBounds.Y + index * rowHeight,
            cardBounds.Width,
            rowHeight);
    }

    private static RectangleF TaskbarAccountOrdinalRowBounds(
        RectangleF cardBounds,
        int index,
        int accountCount)
    {
        if (accountCount <= 2)
        {
            return new RectangleF(cardBounds.X + 31, cardBounds.Y + 2 + index * 17, 10, 15);
        }

        if (accountCount >= 4)
        {
            var tile = TaskbarCompactCodexTileBounds(cardBounds, index, accountCount);
            return new RectangleF(tile.X, tile.Y, 10, tile.Height);
        }

        var slotHeight = 32f / accountCount;
        return new RectangleF(
            cardBounds.X + 31,
            cardBounds.Y + 2 + index * slotHeight,
            10,
            Math.Max(1, slotHeight - 1));
    }

    private static RectangleF TaskbarCompactCodexTileBounds(
        RectangleF cardBounds,
        int index,
        int accountCount)
    {
        var rowCount = (accountCount + 1) / 2;
        var column = index % 2;
        var row = index / 2;
        const float gap = 2;
        var contentWidth = cardBounds.Width - 35;
        var tileWidth = (contentWidth - gap) / 2;
        var tileHeight = (32f - (rowCount - 1) * gap) / rowCount;
        return new RectangleF(
            cardBounds.X + 31 + column * (tileWidth + gap),
            cardBounds.Y + 2 + row * (tileHeight + gap),
            tileWidth,
            Math.Max(1, tileHeight));
    }

    private static MiniQuotaTarget TaskbarCompactCodexRowTarget(
        QuotaCard card,
        RectangleF rowBounds)
    {
        var windows = TaskbarMiniGrouping.CodexRowWindows(card);
        return new MiniQuotaTarget(rowBounds, card, windows[^1]);
    }

    private static MiniQuotaTarget[] TaskbarCodexRowTargets(
        QuotaCard card,
        RectangleF rowBounds)
    {
        var windows = TaskbarMiniGrouping.CodexRowWindows(card);
        if (windows.Count == 1)
        {
            return [new MiniQuotaTarget(rowBounds, card, windows[0])];
        }

        var midpoint = rowBounds.X + rowBounds.Width / 2;
        return
        [
            new MiniQuotaTarget(
                RectangleF.FromLTRB(rowBounds.Left, rowBounds.Top, midpoint, rowBounds.Bottom),
                card,
                windows[0]),
            new MiniQuotaTarget(
                RectangleF.FromLTRB(midpoint, rowBounds.Top, rowBounds.Right, rowBounds.Bottom),
                card,
                windows[1]),
        ];
    }

    private void DrawTaskbarCompactCodexCapsule(Graphics graphics, MiniQuotaTarget target)
    {
        var used = AnimatedUsed(target.Card.Key, target.Window);
        var remaining = used is null ? (double?)null : Math.Clamp(100 - used.Value, 0, 100);
        var valueColor = remaining is null
            ? UiPalette.Slate500
            : QuotaColorScale.ForRemaining(remaining.Value);
        valueColor = MixColor(
            valueColor,
            UiPalette.Slate50,
            SnapshotPulse(target.Id) * .22f);

        using var path = RoundedRectangle(target.Bounds, Math.Min(3, target.Bounds.Height / 3));
        using var fill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var border = new Pen(UiPalette.Slate700.WithAlpha(58), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        var railStart = target.Bounds.X + 4;
        var railWidth = Math.Max(1, target.Bounds.Width - 8);
        DrawTaskbarQuotaRail(
            graphics,
            railStart,
            railWidth,
            target.Bounds.Bottom - 1.5f,
            remaining,
            valueColor);

        if (target.Bounds.Height < 9) return;
        using var valueBrush = new SolidBrush(valueColor);
        DrawString(
            graphics,
            remaining is null ? "--" : FormatPercent(remaining.Value),
            _accountOrdinalFont,
            valueBrush,
            new RectangleF(
                target.Bounds.X + 5,
                target.Bounds.Y - .5f,
                target.Bounds.Width - 10,
                target.Bounds.Height - 1),
            StringAlignment.Center);
    }

    private static MiniQuotaTarget[] TaskbarQuotaTargets(
        QuotaCard card,
        RectangleF cardBounds)
    {
        var windows = TaskbarMiniLayoutMath.VisibleWindows(card.Windows);
        if (windows.Count != 3)
        {
            return windows
                .Select((window, index) => new MiniQuotaTarget(
                    TaskbarCapsuleBounds(cardBounds, windows.Count, index),
                    card,
                    window))
                .ToArray();
        }

        var firstBounds = TaskbarCapsuleBounds(cardBounds, 2, 0);
        var sharedBounds = TaskbarCapsuleBounds(cardBounds, 2, 1);
        var midpoint = sharedBounds.X + sharedBounds.Width / 2;
        return
        [
            new MiniQuotaTarget(firstBounds, card, windows[0]),
            new MiniQuotaTarget(
                RectangleF.FromLTRB(sharedBounds.Left, sharedBounds.Top, midpoint, sharedBounds.Bottom),
                card,
                windows[1]),
            new MiniQuotaTarget(
                RectangleF.FromLTRB(midpoint, sharedBounds.Top, sharedBounds.Right, sharedBounds.Bottom),
                card,
                windows[2]),
        ];
    }
}
