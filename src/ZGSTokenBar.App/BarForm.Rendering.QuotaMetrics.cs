using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Rendering of quota metrics: capsules, dual/service/Sub2Api metrics, rails, logos and card rows.</summary>
internal sealed partial class BarForm
{
    private void DrawTaskbarDualCapsule(
        Graphics graphics,
        RectangleF bounds,
        MiniQuotaTarget first,
        MiniQuotaTarget second)
    {
        var firstHover = MiniTargetEmphasis(first.Id);
        var secondHover = MiniTargetEmphasis(second.Id);
        var firstPulse = SnapshotPulse(first.Id);
        var secondPulse = SnapshotPulse(second.Id);

        using var path = RoundedRectangle(bounds, 4);
        using var fill = new SolidBrush(_backgroundTheme.QuotaGroup);
        graphics.FillPath(fill, path);

        var state = graphics.Save();
        graphics.SetClip(path);
        DrawTaskbarDualHover(graphics, first.Bounds, firstHover);
        DrawTaskbarDualHover(graphics, second.Bounds, secondHover);
        graphics.Restore(state);

        using var border = new Pen(MixColor(
            UiPalette.Slate700.WithAlpha(58),
            UiPalette.Slate200.WithAlpha(154),
            Math.Max(Math.Max(firstHover, secondHover) * .45f, Math.Max(firstPulse, secondPulse) * .55f)), 1);
        using var separator = new Pen(UiPalette.Slate600.WithAlpha(58), 1);
        graphics.DrawPath(border, path);
        graphics.DrawLine(separator, first.Bounds.Right, bounds.Top + 3, first.Bounds.Right, bounds.Bottom - 3);

        DrawTaskbarDualMetric(graphics, first);
        DrawTaskbarDualMetric(graphics, second);
    }

    private float MiniTargetEmphasis(string id)
    {
        if (string.Equals(_popoverTargetId, id, StringComparison.Ordinal)) return 1;
        return string.Equals(_hoverQuotaTarget?.Id, id, StringComparison.Ordinal)
            ? HoverProgress(HoverTarget.QuotaWindow)
            : 0;
    }

    private static void DrawTaskbarDualHover(Graphics graphics, RectangleF bounds, float emphasis)
    {
        if (emphasis <= 0) return;
        using var brush = new SolidBrush(Color.FromArgb(
            (int)Math.Round(32 * emphasis),
            148,
            163,
            184));
        graphics.FillRectangle(brush, bounds);
    }

    private void DrawTaskbarDualMetric(Graphics graphics, MiniQuotaTarget target)
    {
        if (target.Card.IsService)
        {
            DrawTaskbarServiceMetric(graphics, target);
            return;
        }

        var used = AnimatedUsed(target.Card.Key, target.Window);
        var remaining = used is null ? (double?)null : Math.Clamp(100 - used.Value, 0, 100);
        var valueColor = remaining is null
            ? UiPalette.Slate500
            : MixColor(
                QuotaColorScale.ForRemaining(remaining.Value),
                UiPalette.Slate50,
                SnapshotPulse(target.Id) * .22f);
        var label = QuotaDisplayFormatting.FormatWindowTiny(target.Window);
        var labelWidth = label.Length > 1 ? 18f : 12f;
        using var labelBrush = new SolidBrush(UiPalette.Slate300);
        using var valueBrush = new SolidBrush(valueColor);
        DrawString(
            graphics,
            label,
            _badgeFont,
            labelBrush,
            new RectangleF(target.Bounds.X + 4, target.Bounds.Y, labelWidth, target.Bounds.Height),
            StringAlignment.Near);
        DrawString(
            graphics,
            remaining is null ? "--" : FormatPercent(remaining.Value),
            _rowFont,
            valueBrush,
            new RectangleF(
                target.Bounds.X + labelWidth,
                target.Bounds.Y,
                target.Bounds.Width - labelWidth - 3,
                target.Bounds.Height),
            StringAlignment.Far);

        var railStart = target.Bounds.X + 4;
        var railWidth = target.Bounds.Width - 8;
        var railY = target.Bounds.Bottom - 2;
        DrawTaskbarQuotaRail(
            graphics,
            railStart,
            railWidth,
            railY,
            remaining,
            valueColor);
    }

    private void DrawTaskbarCapsule(Graphics graphics, MiniQuotaTarget target)
    {
        if (target.Card.IsService)
        {
            DrawTaskbarServiceMetric(graphics, target);
            return;
        }

        var bounds = target.Bounds;
        var window = target.Window;
        var card = target.Card;
        var cardKey = card.Key;
        var windowKey = target.Id;
        var hovered = string.Equals(_hoverQuotaTarget?.Id, windowKey, StringComparison.Ordinal);
        var selected = string.Equals(_popoverTargetId, windowKey, StringComparison.Ordinal);
        var hover = selected
            ? 1
            : hovered
                ? HoverProgress(HoverTarget.QuotaWindow)
                : 0;
        var updatePulse = SnapshotPulse(windowKey);
        var now = DateTimeOffset.UtcNow;
        var weeklyBlockReset = QuotaDisplayFormatting.WeeklyBlockReset(card, window, now);
        var blockedByWeekly = weeklyBlockReset is not null;
        var used = AnimatedUsed(cardKey, window);
        var remaining = used is null ? (double?)null : Math.Clamp(100 - used.Value, 0, 100);
        var valueColor = blockedByWeekly || remaining is null
            ? UiPalette.Slate500
            : QuotaColorScale.ForRemaining(remaining.Value);
        valueColor = MixColor(valueColor, UiPalette.Slate50, updatePulse * .22f);

        using var path = RoundedRectangle(bounds, 4);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.QuotaGroup,
            UiPalette.Slate200,
            hover * .08f));
        using var border = new Pen(MixColor(
            UiPalette.Slate700.WithAlpha(58),
            Color.FromArgb(154, valueColor.R, valueColor.G, valueColor.B),
            Math.Max(hover * .62f, updatePulse * .85f)), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var accentPen = new Pen(valueColor, 2 + hover * .35f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(accentPen, bounds.X + 3.5f, bounds.Y + 3.5f, bounds.X + 3.5f, bounds.Bottom - 3.5f);
        DrawTaskbarProgressRail(graphics, bounds, remaining, valueColor);

        using var labelBrush = new SolidBrush(blockedByWeekly
            ? UiPalette.Slate500
            : UiPalette.Slate200);
        using var valueBrush = new SolidBrush(valueColor);
        using var resetBrush = new SolidBrush(blockedByWeekly || window.ResetsAt is null
            ? UiPalette.Slate500
            : UiPalette.Slate200);
        DrawString(
            graphics,
            QuotaDisplayFormatting.FormatWindowShort(window),
            _badgeFont,
            labelBrush,
            new RectangleF(bounds.X + 7, bounds.Y, 16, bounds.Height),
            StringAlignment.Near);
        DrawString(
            graphics,
            remaining is null ? "--" : FormatPercent(remaining.Value),
            _rowFont,
            valueBrush,
            new RectangleF(bounds.X + 21, bounds.Y, 28, bounds.Height),
            StringAlignment.Far);

        var resetIconBounds = new RectangleF(bounds.X + 49, bounds.Y, 12, bounds.Height);
        if (blockedByWeekly)
        {
            DrawTaskbarLockIcon(graphics, resetIconBounds);
        }
        else
        {
            DrawResetClockIcon(graphics, resetIconBounds);
        }
        DrawString(
            graphics,
            QuotaDisplayFormatting.FormatResetShort(weeklyBlockReset ?? window.ResetsAt, now),
            _resetFont,
            resetBrush,
            new RectangleF(bounds.X + 60, bounds.Y, bounds.Width - 63, bounds.Height),
            StringAlignment.Far);
    }

    private void DrawTaskbarServiceMetric(Graphics graphics, MiniQuotaTarget target)
    {
        var bounds = target.Bounds;
        var hovered = string.Equals(_hoverQuotaTarget?.Id, target.Id, StringComparison.Ordinal);
        var selected = string.Equals(_popoverTargetId, target.Id, StringComparison.Ordinal);
        var hover = selected
            ? 1
            : hovered
                ? HoverProgress(HoverTarget.QuotaWindow)
                : 0;
        using var path = RoundedRectangle(bounds, 4);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.QuotaGroup,
            UiPalette.Slate200,
            hover * .08f));
        using var border = new Pen(MixColor(
            UiPalette.Slate700.WithAlpha(58),
            UiPalette.Slate500.WithAlpha(154),
            Math.Max(hover * .62f, .1f)), 1);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        if (Sub2ApiServicePresentation.IsSub2ApiService(target.Card))
        {
            DrawTaskbarSub2ApiMetric(graphics, target, hover);
            return;
        }

        var accountAvailability = target.Card.Sub2ApiAccountAvailability;
        var accountAvailabilityRemaining = Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(accountAvailability);
        var quota = target.Card.Sub2ApiQuota;
        var quotaWindow = Sub2ApiQuotaFormatting.PreferredWindow(quota);
        var usage = target.Card.Sub2ApiUsage;
        var pool = target.Card.Sub2ApiPool;
        using var accentPen = new Pen(accountAvailabilityRemaining is not null
            ? Sub2ApiQuotaCompactStatusColor(accountAvailability!.Status)
            : quotaWindow is not null
            ? Sub2ApiQuotaCompactStatusColor(quota!.Status)
            : usage is not null
            ? Sub2ApiUsageCompactStatusColor(usage.Status)
            : pool is not null
                ? Sub2ApiPoolCompactStatusColor(pool.Status)
                : UiPalette.Slate500, 2 + hover * .35f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(accentPen, bounds.X + 3.5f, bounds.Y + 3.5f, bounds.X + 3.5f, bounds.Bottom - 3.5f);
        if (accountAvailabilityRemaining is not null)
        {
            var availabilityColor = Sub2ApiQuotaCompactStatusColor(accountAvailability!.Status);
            DrawTaskbarProgressRail(
                graphics,
                bounds,
                accountAvailabilityRemaining,
                availabilityColor);
            DrawTaskbarServiceCompactMetric(
                graphics,
                bounds,
                target.Card.DisplayLabel,
                _text.Sub2ApiAccountAvailabilityCompact(accountAvailability!),
                availabilityColor);
            return;
        }
        if (quotaWindow is not null)
        {
            var quotaColor = Sub2ApiQuotaCompactStatusColor(quota!.Status);
            DrawTaskbarProgressRail(
                graphics,
                bounds,
                quotaWindow.RemainingPercent,
                quotaColor);
            DrawTaskbarServiceCompactMetric(
                graphics,
                bounds,
                target.Card.DisplayLabel,
                _text.Sub2ApiQuotaCompact(quota!),
                quotaColor);
            return;
        }
        if (usage is not null)
        {
            DrawTaskbarServiceCompactMetric(
                graphics,
                bounds,
                target.Card.DisplayLabel,
                _text.Sub2ApiUsageCompact(usage),
                Sub2ApiUsageCompactStatusColor(usage.Status));
            return;
        }
        if (pool is not null)
        {
            DrawTaskbarServiceCompactMetric(
                graphics,
                bounds,
                target.Card.DisplayLabel,
                Sub2ApiPoolFormatting.AccountPair(pool),
                Sub2ApiPoolCompactStatusColor(pool.Status));
            return;
        }
        if (target.Card.Provider != ProviderKind.AiGateway)
        {
            using var labelBrush = new SolidBrush(UiPalette.Slate300);
            DrawString(
                graphics,
                target.Card.DisplayLabel,
                _rowFont,
                labelBrush,
                new RectangleF(bounds.X + 7, bounds.Y, bounds.Width - 12, bounds.Height),
                StringAlignment.Center);
            return;
        }

        using var valueBrush = new SolidBrush(UiPalette.Slate400);
        var value = target.Card.Balance is { } balance
            ? AiGatewayBalanceFormatting.Amount(balance.TotalBalance)
            : _text.ApiServiceConfiguredShort;
        DrawString(
            graphics,
            value,
            _rowFont,
            valueBrush,
            new RectangleF(bounds.X + 6, bounds.Y, bounds.Width - 12, bounds.Height),
            StringAlignment.Center);
    }

    private void DrawTaskbarSub2ApiMetric(
        Graphics graphics,
        MiniQuotaTarget target,
        float hover)
    {
        var bounds = target.Bounds;
        var presentation = Sub2ApiServicePresentation.Resolve(target.Card, _snapshot.CapturedAt);
        var valueColor = Sub2ApiPresentationCompactStatusColor(target.Card, presentation);
        using var accentPen = new Pen(valueColor, 2 + hover * .35f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(accentPen, bounds.X + 3.5f, bounds.Y + 3.5f, bounds.X + 3.5f, bounds.Bottom - 3.5f);

        switch (presentation.Kind)
        {
            case Sub2ApiServicePresentationKind.CompleteAvailability
                when presentation.Availability is { } availability
                && Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(availability) is { } remaining:
                DrawTaskbarProgressRail(graphics, bounds, remaining, valueColor);
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiAccountAvailabilityCompact(availability),
                    valueColor);
                return;
            case Sub2ApiServicePresentationKind.PartialAvailability
                or Sub2ApiServicePresentationKind.KnownNoneAvailability
                when presentation.Availability is { } coverage:
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiAccountAvailabilityCompact(coverage),
                    valueColor);
                return;
            case Sub2ApiServicePresentationKind.LegacyAggregateQuota
                when presentation.LegacyQuota is { } legacy:
                DrawTaskbarProgressRail(graphics, bounds, legacy.RemainingPercent, valueColor);
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiLegacyQuotaCompact(legacy),
                    valueColor);
                return;
            case Sub2ApiServicePresentationKind.Usage
                when presentation.Usage is { } usage:
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiUsageCompact(usage),
                    valueColor);
                return;
            case Sub2ApiServicePresentationKind.Pool
                when presentation.Pool is { } pool:
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    Sub2ApiPoolFormatting.AccountPair(pool),
                    valueColor);
                return;
            default:
                DrawTaskbarServiceCompactMetric(
                    graphics,
                    bounds,
                    target.Card.DisplayLabel,
                    _text.Sub2ApiUnavailable,
                    valueColor);
                return;
        }
    }

    private static Color Sub2ApiPresentationCompactStatusColor(
        QuotaCard card,
        Sub2ApiServicePresentationState presentation) => presentation.Kind switch
    {
        Sub2ApiServicePresentationKind.CompleteAvailability
            or Sub2ApiServicePresentationKind.PartialAvailability
            or Sub2ApiServicePresentationKind.KnownNoneAvailability
            when presentation.Availability is { } availability =>
                Sub2ApiQuotaCompactStatusColor(availability.Status),
        Sub2ApiServicePresentationKind.LegacyAggregateQuota
            when card.Sub2ApiQuota is { } quota => Sub2ApiQuotaCompactStatusColor(quota.Status),
        Sub2ApiServicePresentationKind.Usage
            when presentation.Usage is { } usage => Sub2ApiUsageCompactStatusColor(usage.Status),
        Sub2ApiServicePresentationKind.Pool
            when presentation.Pool is { } pool => Sub2ApiPoolCompactStatusColor(pool.Status),
        _ => UiPalette.Rose400,
    };

    private void DrawTaskbarServiceCompactMetric(
        Graphics graphics,
        RectangleF bounds,
        string label,
        string value,
        Color valueColor)
    {
        const float valueWidth = 44f;
        using var labelBrush = new SolidBrush(UiPalette.Slate300);
        using var valueBrush = new SolidBrush(valueColor);
        if (bounds.Width < 84f)
        {
            DrawString(
                graphics,
                value,
                _badgeFont,
                valueBrush,
                new RectangleF(bounds.X + 7, bounds.Y, bounds.Width - 12, bounds.Height),
                StringAlignment.Center);
            return;
        }

        DrawString(
            graphics,
            label,
            _badgeFont,
            labelBrush,
            new RectangleF(bounds.X + 7, bounds.Y, bounds.Width - valueWidth - 12, bounds.Height),
            StringAlignment.Near);
        DrawString(
            graphics,
            value,
            _badgeFont,
            valueBrush,
            new RectangleF(bounds.Right - valueWidth - 4, bounds.Y, valueWidth, bounds.Height),
            StringAlignment.Far);
    }

    private void DrawTaskbarLockIcon(Graphics graphics, RectangleF bounds)
    {
        var state = graphics.Save();
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(UiPalette.Slate500);
        graphics.DrawString(LockIconGlyph, _miniSystemIconFont, brush, bounds, _systemIconFormat);
        graphics.Restore(state);
    }

    private void DrawTaskbarProgressRail(
        Graphics graphics,
        RectangleF bounds,
        double? remaining,
        Color valueColor)
    {
        var railStart = bounds.X + 7;
        var railWidth = bounds.Width - 10;
        var railY = bounds.Bottom - 2;
        DrawTaskbarQuotaRail(
            graphics,
            railStart,
            railWidth,
            railY,
            remaining,
            valueColor);
    }

    private static void DrawTaskbarQuotaRail(
        Graphics graphics,
        float railStart,
        float railWidth,
        float railY,
        double? remaining,
        Color valueColor)
    {
        using var troughPen = new Pen(UiPalette.Slate800, 3)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(troughPen, railStart, railY, railStart + railWidth, railY);

        if (remaining is { } remainingValue)
        {
            var clampedRemaining = Math.Clamp(remainingValue, 0, 100);
            var splitX = railStart + railWidth * (float)(clampedRemaining / 100);
            if (clampedRemaining > 0)
            {
                using var activePen = new Pen(valueColor, 2)
                {
                    StartCap = LineCap.Round,
                    EndCap = clampedRemaining >= 100 ? LineCap.Round : LineCap.Flat,
                };
                graphics.DrawLine(activePen, railStart, railY, splitX, railY);
            }
            if (clampedRemaining < 100)
            {
                using var usedPen = new Pen(UiPalette.Slate600, 2)
                {
                    StartCap = clampedRemaining <= 0 ? LineCap.Round : LineCap.Flat,
                    EndCap = LineCap.Round,
                };
                graphics.DrawLine(usedPen, splitX, railY, railStart + railWidth, railY);
            }
        }

    }

    private void DrawResetClockIcon(Graphics graphics, RectangleF bounds) =>
        graphics.DrawImage(_resetClockIcon, CenteredSquare(bounds, MiniResetClockSize));

    private void DrawProviderLogo(Graphics graphics, RectangleF logoBounds, QuotaCard card) =>
        DrawProviderLogo(graphics, logoBounds, card, true);

    private void DrawProviderLogo(
        Graphics graphics,
        RectangleF logoBounds,
        QuotaCard card,
        bool showOrdinal)
    {
        graphics.DrawImage(ProviderLogo(card.Provider), logoBounds);

        if (!showOrdinal) return;
        var ordinal = CodexAccountOrdinal(card);
        if (ordinal is null) return;
        var badgeBounds = new RectangleF(logoBounds.Right - 7, logoBounds.Bottom - 6, 10, 8);
        DrawAccountOrdinalBadge(graphics, badgeBounds, ordinal);
    }

    private void DrawAccountOrdinalBadge(Graphics graphics, RectangleF badgeBounds, string ordinal)
    {
        using var badgeBrush = new SolidBrush(_backgroundTheme.Outer);
        using var border = new Pen(UiPalette.Slate600, 1);
        using var textBrush = new SolidBrush(UiPalette.Teal200);
        graphics.FillEllipse(badgeBrush, badgeBounds);
        graphics.DrawEllipse(border, badgeBounds);
        DrawString(graphics, ordinal, _accountOrdinalFont, textBrush, badgeBounds, StringAlignment.Center);
    }

    private static string? CodexAccountOrdinal(QuotaCard card)
    {
        if (card.Provider != ProviderKind.Codex) return null;
        var separator = card.Label.IndexOf('·');
        if (separator < 0) return null;
        return card.Label.AsSpan(separator + 1).Trim().ToString();
    }

    private void DrawCard(Graphics graphics, RectangleF bounds, QuotaCard card)
    {
        using var path = RoundedRectangle(bounds, 7);
        using var fill = new SolidBrush(_backgroundTheme.ProviderGroup);
        using var border = new Pen(UiPalette.Slate400.WithAlpha(36));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        var accent = ColorTranslator.FromHtml(card.Accent);
        using var accentBrush = new SolidBrush(accent);
        graphics.FillEllipse(accentBrush, bounds.X + 7, bounds.Y + 6, 6, 6);

        var badgeWidth = string.IsNullOrWhiteSpace(card.Badge)
            ? 0
            : Math.Min(48, Math.Max(22, MeasureWidth(graphics, card.Badge!, _badgeFont) + 6));
        var titleWidth = bounds.Width - 23 - badgeWidth;
        using var titleBrush = new SolidBrush(UiPalette.Slate50);
        DrawString(
            graphics,
            card.DisplayLabel,
            _cardTitleFont,
            titleBrush,
            new RectangleF(bounds.X + 17, bounds.Y + 2, titleWidth, 12),
            StringAlignment.Near);

        if (badgeWidth > 0)
        {
            var badgeBounds = new RectangleF(bounds.Right - badgeWidth - 6, bounds.Y + 3, badgeWidth, 10);
            using var badgePath = RoundedRectangle(badgeBounds, 3);
            using var badgeFill = new SolidBrush(UiPalette.Slate700.WithAlpha(34));
            using var badgeText = new SolidBrush(UiPalette.Slate300);
            graphics.FillPath(badgeFill, badgePath);
            DrawString(graphics, card.Badge!, _badgeFont, badgeText, badgeBounds, StringAlignment.Center);
        }

        if (card.IsService)
        {
            using var serviceBrush = new SolidBrush(UiPalette.Slate400);
            if (card.Sub2ApiQuota is { } quota && Sub2ApiQuotaFormatting.PreferredWindow(quota) is not null)
            {
                DrawString(
                    graphics,
                    _text.Sub2ApiQuotaSummaryShort(quota),
                    _rowFont,
                    serviceBrush,
                    new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                    StringAlignment.Near);
                return;
            }
            if (card.Sub2ApiUsage is { } usage)
            {
                DrawString(
                    graphics,
                    _text.Sub2ApiUsageSummaryShort(usage),
                    _rowFont,
                    serviceBrush,
                    new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                    StringAlignment.Near);
                return;
            }
            if (card.Sub2ApiPool is { } pool)
            {
                DrawString(
                    graphics,
                    $"{_text.Sub2ApiPoolAvailableAccounts(pool)} · {_text.Sub2ApiPoolStatusShort(pool.Status)}",
                    _rowFont,
                    serviceBrush,
                    new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                    StringAlignment.Near);
                return;
            }
            if (card.Balance is { } balance)
            {
                DrawString(
                    graphics,
                    $"{AiGatewayBalanceFormatting.Amount(balance.TotalBalance)} · {_text.AiGatewayStatusShort(balance.Status)}",
                    _rowFont,
                    serviceBrush,
                    new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                    StringAlignment.Near);
                return;
            }
            DrawString(
                graphics,
                _text.ApiServiceConfigured,
                _rowFont,
                serviceBrush,
                new RectangleF(bounds.X + 7, bounds.Y + 17, bounds.Width - 14, 12),
                StringAlignment.Near);
            return;
        }

        var windowCount = Math.Min(3, card.Windows.Count);
        if (windowCount == 3)
        {
            DrawQuotaRow(
                graphics,
                bounds.X + 7,
                bounds.Y + 15,
                bounds.Width - 14,
                card,
                card.Windows[0]);
            DrawDualQuotaRow(
                graphics,
                bounds.X + 7,
                bounds.Y + 24,
                bounds.Width - 14,
                card,
                card.Windows[1],
                card.Windows[2]);
            return;
        }

        for (var index = 0; index < windowCount; index++)
        {
            DrawQuotaRow(
                graphics,
                bounds.X + 7,
                bounds.Y + 15 + index * 9,
                bounds.Width - 14,
                card,
                card.Windows[index]);
        }
    }

    private void DrawDualQuotaRow(
        Graphics graphics,
        float x,
        float y,
        float width,
        QuotaCard card,
        QuotaWindow first,
        QuotaWindow second)
    {
        var gap = 7f;
        var segmentWidth = (width - gap) / 2;
        DrawCompactQuotaMetric(
            graphics,
            new RectangleF(x, y - 1, segmentWidth, 9),
            card,
            first);
        using var separator = new Pen(UiPalette.Slate600.WithAlpha(58), 1);
        var separatorX = x + segmentWidth + gap / 2;
        graphics.DrawLine(separator, separatorX, y, separatorX, y + 6);
        DrawCompactQuotaMetric(
            graphics,
            new RectangleF(x + segmentWidth + gap, y - 1, segmentWidth, 9),
            card,
            second);
    }

    private void DrawCompactQuotaMetric(
        Graphics graphics,
        RectangleF bounds,
        QuotaCard card,
        QuotaWindow window)
    {
        var label = QuotaDisplayFormatting.FormatWindowTiny(window);
        var labelWidth = label.Length > 1 ? 20f : 12f;
        var valueWidth = 27f;
        var trackX = bounds.X + labelWidth;
        var trackWidth = Math.Max(10, bounds.Width - labelWidth - valueWidth - 2);
        var used = AnimatedUsed(card.Key, window);
        var remaining = used is null ? (double?)null : Math.Clamp(100 - used.Value, 0, 100);
        var valueColor = remaining is null
            ? UiPalette.Slate500
            : MixColor(
                QuotaColorScale.ForRemaining(remaining.Value),
                UiPalette.Slate50,
                SnapshotPulse(WindowKey(card.Key, window.Label)) * .22f);
        using var labelBrush = new SolidBrush(UiPalette.Slate300);
        using var trackBrush = new SolidBrush(UiPalette.Slate800);
        using var valueBrush = new SolidBrush(valueColor);
        DrawString(
            graphics,
            label,
            _rowFont,
            labelBrush,
            new RectangleF(bounds.X, bounds.Y, labelWidth, bounds.Height),
            StringAlignment.Near);
        var track = new RectangleF(trackX, bounds.Y + 2, trackWidth, 5);
        graphics.FillRoundedRectangle(trackBrush, track, 2);
        if (remaining is > 0)
        {
            using var activeBrush = new SolidBrush(valueColor);
            graphics.FillRoundedRectangle(
                activeBrush,
                new RectangleF(track.X, track.Y, track.Width * (float)(remaining.Value / 100), track.Height),
                2);
        }
        DrawString(
            graphics,
            remaining is null ? "--" : FormatPercent(remaining.Value),
            _rowFont,
            valueBrush,
            new RectangleF(track.Right + 2, bounds.Y, valueWidth, bounds.Height),
            StringAlignment.Far);
    }

    private void DrawQuotaRow(
        Graphics graphics,
        float x,
        float y,
        float width,
        QuotaCard card,
        QuotaWindow window)
    {
        var cardKey = card.Key;
        var now = DateTimeOffset.UtcNow;
        var weeklyBlockReset = QuotaDisplayFormatting.WeeklyBlockReset(card, window, now);
        var blockedByWeekly = weeklyBlockReset is not null;
        var isWeek = window.Label is "1w" or "week" or "7d" or "30d";
        using var labelBrush = new SolidBrush(blockedByWeekly
            ? UiPalette.Slate500
            : UiPalette.Slate200);
        if (blockedByWeekly)
        {
            DrawTaskbarLockIcon(graphics, new RectangleF(x, y - 1, 8, 9));
        }
        else if (isWeek)
        {
            using var markPen = new Pen(labelBrush.Color, 1);
            graphics.DrawRectangle(markPen, x, y + 2, 7, 4);
        }
        else
        {
            graphics.FillEllipse(labelBrush, x + 1, y + 2, 5, 4);
        }
        DrawString(graphics, window.Label, _rowFont, labelBrush, new RectangleF(x + 9, y - 1, 17, 9), StringAlignment.Near);

        var metricsWidth = 64f;
        var trackX = x + 28;
        var trackWidth = Math.Max(12, width - 28 - metricsWidth - 3);
        var track = new RectangleF(trackX, y + 1, trackWidth, 5);
        using var trackBrush = new SolidBrush(UiPalette.Slate800);
        graphics.FillRoundedRectangle(trackBrush, track, isWeek ? 2 : 3);

        var used = AnimatedUsed(cardKey, window);
        if (used is not null)
        {
            var clampedUsed = Math.Clamp(used.Value, 0, 100);
            var remaining = 100 - clampedUsed;
            var resetAt = weeklyBlockReset ?? window.ResetsAt;
            var resetRemaining = resetAt is null ? (TimeSpan?)null : resetAt.Value - now;
            double? elapsedPercent = null;
            if (resetRemaining is { } reset && reset >= TimeSpan.Zero && reset <= window.Duration)
            {
                elapsedPercent = Math.Clamp((window.Duration - reset).TotalMilliseconds / window.Duration.TotalMilliseconds * 100, 0, 100);
                using var elapsedBrush = new SolidBrush(UiPalette.Slate700);
                graphics.FillRectangle(elapsedBrush, track.X, track.Y, track.Width * (float)(elapsedPercent.Value / 100), track.Height);
            }

            var remainingColor = blockedByWeekly
                ? UiPalette.Slate500
                : MixColor(
                    QuotaColorScale.ForRemaining(remaining),
                    UiPalette.Slate50,
                    SnapshotPulse(WindowKey(cardKey, window.Label)) * .22f);
            using var remainingBrush = new SolidBrush(remainingColor);
            var remainingBounds = new RectangleF(track.X, track.Y, track.Width * (float)(remaining / 100), track.Height);
            if (remainingBounds.Width > 0) graphics.FillRoundedRectangle(remainingBrush, remainingBounds, isWeek ? 2 : 3);
            if (elapsedPercent is not null)
            {
                var markerX = track.X + track.Width * (float)(elapsedPercent.Value / 100);
                using var markerPen = new Pen(clampedUsed - elapsedPercent.Value > 10
                    ? UiPalette.Rose400
                    : UiPalette.Slate500);
                graphics.DrawLine(markerPen, markerX, track.Y, markerX, track.Bottom);
            }

            using var percentBrush = new SolidBrush(remainingColor);
            using var separatorBrush = new SolidBrush(UiPalette.Slate400.WithAlpha(72));
            using var resetBrush = new SolidBrush(blockedByWeekly || resetAt is null
                ? UiPalette.Slate500
                : UiPalette.Slate200);
            var metricsX = track.Right + 3;
            DrawString(
                graphics,
                FormatPercent(remaining),
                _rowFont,
                percentBrush,
                new RectangleF(metricsX, y - 1, 26, 9),
                StringAlignment.Far);
            DrawString(
                graphics,
                "·",
                _resetFont,
                separatorBrush,
                new RectangleF(metricsX + 26, y - 1, 4, 9),
                StringAlignment.Center);
            DrawString(
                graphics,
                _text.FormatCompactReset(resetAt, now),
                _resetFont,
                resetBrush,
                new RectangleF(metricsX + 30, y - 1, metricsWidth - 30, 9),
                StringAlignment.Far);
        }
        else
        {
            using var waitingBrush = new SolidBrush(UiPalette.Slate500);
            graphics.FillRectangle(waitingBrush, track.X, track.Y, Math.Max(8, track.Width * .18f), track.Height);
            DrawString(
                graphics,
                _refreshing ? _text.Sync : _text.Wait,
                _rowFont,
                waitingBrush,
                new RectangleF(track.Right + 3, y - 1, metricsWidth, 9),
                StringAlignment.Far);
        }
    }
}
