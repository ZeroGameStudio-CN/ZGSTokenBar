using System.Drawing.Drawing2D;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>Sub2API sections: pool, usage, quota, account availability, legacy and unavailable states.</summary>
internal sealed partial class QuotaPopoverForm
{
    private void DrawSub2ApiPoolServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiPoolAvailability pool)
    {
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(UiPalette.Slate100);
        using var mutedBrush = new SolidBrush(UiPalette.Slate400);
        using var valueBrush = new SolidBrush(Sub2ApiPoolStatusColor(pool.Status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiPool,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(UiPalette.Slate600, 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiPoolStatus(pool.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiPoolAvailableAccounts(pool),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiPoolFreeConcurrency(pool),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 76, body.Width - 24, 18),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiPoolIssues(pool),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 94, body.Width - 24, 18),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiPoolUpdatedShort(pool.ObservedAt),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, y + 112, 108, 10),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiUsageServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiUsageSummary usage)
    {
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(UiPalette.Slate100);
        using var mutedBrush = new SolidBrush(UiPalette.Slate400);
        using var valueBrush = new SolidBrush(Sub2ApiUsageStatusColor(usage.Status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiUsage,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(UiPalette.Slate600, 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiUsageStatus(usage.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiUsageTodayTokens(usage),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiUsageTotalTokens(usage),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 76, body.Width - 24, 18),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiUsageRequests(usage),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 94, body.Width - 24, 18),
            StringAlignment.Near);
        if (content.Card.Sub2ApiPool is { } pool)
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsagePool(pool),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 112, body.Width - 24, 10),
                StringAlignment.Near);
            DrawString(
                graphics,
                _text.Sub2ApiUsageUpdatedShort(usage.ObservedAt),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 129, 108, 10),
                StringAlignment.Near);
        }
        else
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsageUpdatedShort(usage.ObservedAt),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 112, 108, 10),
                StringAlignment.Near);
        }
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiQuotaServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiQuotaSummary quota)
    {
        var preferred = Sub2ApiQuotaFormatting.PreferredWindow(quota)!;
        var other = Sub2ApiQuotaFormatting.OtherWindow(quota);
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(UiPalette.Slate100);
        using var mutedBrush = new SolidBrush(UiPalette.Slate400);
        using var valueBrush = new SolidBrush(Sub2ApiQuotaStatusColor(quota.Status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiQuota,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(UiPalette.Slate600, 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaStatus(quota.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiQuotaHeadline(preferred),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaWindowDetails(preferred),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 76, body.Width - 24, 18),
            StringAlignment.Near);
        if (other is not null)
        {
            DrawString(
                graphics,
                _text.Sub2ApiQuotaWindowDetails(other),
                _detailFont,
                mutedBrush,
                new RectangleF(x + 12, y + 94, body.Width - 24, 18),
                StringAlignment.Near);
        }
        var poolRowY = other is null ? y + 94 : y + 130;
        if (content.Card.Sub2ApiUsage is { } usage)
        {
            DrawString(
                graphics,
                _text.Sub2ApiQuotaProxyTokens(usage),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 112, body.Width - 24, 10),
                StringAlignment.Near);
        }
        if (content.Card.Sub2ApiPool is { } pool)
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsagePool(pool),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, poolRowY, body.Width - 24, 10),
                StringAlignment.Near);
        }
        DrawString(
            graphics,
            _text.Sub2ApiQuotaUpdatedShort(quota.ObservedAt),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, y + 148, 108, 10),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 152, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiAccountAvailabilityServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiAccountAvailabilitySummary availability,
        bool includeProgressRail = false)
    {
        var accounts = availability.Accounts!;
        var columns = AccountAvailabilityColumnCount(accounts.Count);
        var rows = (accounts.Count + columns - 1) / columns;
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(UiPalette.Slate100);
        using var mutedBrush = new SolidBrush(UiPalette.Slate400);
        using var valueBrush = new SolidBrush(Sub2ApiQuotaStatusColor(availability.Status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiAccountAvailability,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(UiPalette.Slate600, 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaStatus(availability.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiAccountAvailabilityHeadline(availability),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 20),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiAccountAvailabilityCoverage(availability),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, y + 64, body.Width - 24, 10),
            StringAlignment.Near);

        var columnWidth = (body.Width - 24) / columns;
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            var column = index % columns;
            var row = index / columns;
            var rowX = x + 12 + column * columnWidth;
            var rowY = y + AccountAvailabilityFirstRowY + row * AccountAvailabilityRowHeight;
            var accountColor = account.RemainingPercent is { } remaining
                ? QuotaColorScale.ForRemaining(remaining)
                : UiPalette.Slate400;
            using var accountBrush = new SolidBrush(accountColor);
            DrawString(
                graphics,
                _text.Sub2ApiAccountAvailabilitySlot(account.Slot),
                _subtitleFont,
                mutedBrush,
                new RectangleF(rowX, rowY, 20, 12),
                StringAlignment.Near);
            DrawString(
                graphics,
                _text.Sub2ApiAccountAvailabilityPercent(account),
                _metricFont,
                accountBrush,
                new RectangleF(rowX + 20, rowY - 1, columnWidth - 20, 14),
                StringAlignment.Far);
        }

        var progressRailY = y + AccountAvailabilityFirstRowY + rows * AccountAvailabilityRowHeight + 4;
        if (includeProgressRail
            && Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(availability) is { } aggregate)
        {
            DrawSub2ApiProgressRail(
                graphics,
                new RectangleF(x + 12, progressRailY, body.Width - 24, 5),
                aggregate,
                valueBrush.Color);
        }

        var poolRowY = progressRailY + (includeProgressRail ? 14 : 0);
        if (content.Card.Sub2ApiPool is { } pool)
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsagePool(pool),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, poolRowY, body.Width - 24, 10),
                StringAlignment.Near);
        }
        var footerY = poolRowY + 18;
        DrawString(
            graphics,
            _text.Sub2ApiQuotaUpdatedShort(availability.ObservedAt),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, footerY, 108, 10),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, footerY, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiLegacyQuotaServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        Sub2ApiQuotaWindow legacy)
    {
        var status = content.Card.Sub2ApiQuota?.Status ?? Sub2ApiQuotaStatus.Available;
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(UiPalette.Slate100);
        using var mutedBrush = new SolidBrush(UiPalette.Slate400);
        using var valueBrush = new SolidBrush(Sub2ApiQuotaStatusColor(status));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiQuota,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(UiPalette.Slate600, 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaStatus(status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiLegacyQuotaHeadline(legacy),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawSub2ApiProgressRail(
            graphics,
            new RectangleF(x + 12, y + 68, body.Width - 24, 5),
            legacy.RemainingPercent,
            valueBrush.Color);
        DrawString(
            graphics,
            _text.Sub2ApiLegacyQuotaDetails(legacy),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 78, body.Width - 24, 18),
            StringAlignment.Near);

        var poolRowY = y + 112;
        if (content.Card.Sub2ApiPool is { } pool)
        {
            DrawString(
                graphics,
                _text.Sub2ApiUsagePool(pool),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, poolRowY, body.Width - 24, 10),
                StringAlignment.Near);
        }
        var footerY = content.Card.Sub2ApiPool is null ? y + 112 : y + 130;
        DrawString(
            graphics,
            _text.Sub2ApiQuotaUpdatedShort(content.Card.Sub2ApiQuota?.ObservedAt),
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 12, footerY, 108, 10),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private void DrawSub2ApiUnavailableServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content)
    {
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(UiPalette.Slate100);
        using var mutedBrush = new SolidBrush(UiPalette.Slate400);
        using var valueBrush = new SolidBrush(Sub2ApiQuotaStatusColor(Sub2ApiQuotaStatus.Unavailable));
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiAccountAvailability,
            _subtitleFont,
            mutedBrush,
            new RectangleF(x + 44, y + 22, body.Width - 118, 13),
            StringAlignment.Near);

        var statusBounds = new RectangleF(body.Right - 62, y + 9, 50, 16);
        using var statusFill = new SolidBrush(_backgroundTheme.QuotaGroup);
        using var statusBorder = new Pen(UiPalette.Slate600, 1);
        using var statusPath = RoundedRectangle(statusBounds, 5);
        graphics.FillPath(statusFill, statusPath);
        graphics.DrawPath(statusBorder, statusPath);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaStatus(Sub2ApiQuotaStatus.Unavailable),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.Sub2ApiUnavailable,
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.Sub2ApiQuotaUpdatedShort(null),
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 78, body.Width - 24, 18),
            StringAlignment.Near);
        DrawString(
            graphics,
            content.Pinned ? _text.ClosePinnedHint : _text.PinHint,
            _subtitleFont,
            mutedBrush,
            new RectangleF(body.Right - 112, y + 132, 100, 10),
            StringAlignment.Far);
    }

    private static void DrawSub2ApiProgressRail(
        Graphics graphics,
        RectangleF bounds,
        double remaining,
        Color valueColor)
    {
        UiPaint.ProgressBar(graphics, new RectangleF(bounds.X, bounds.Y + 1, bounds.Width, 4),
            Math.Clamp(remaining, 0, 100) / 100, valueColor, UiPalette.Slate800);
    }

    private static Color Sub2ApiPoolStatusColor(Sub2ApiPoolStatus status) => status switch
    {
        Sub2ApiPoolStatus.Available => UiPalette.Teal300,
        Sub2ApiPoolStatus.Stale => UiPalette.Amber400,
        Sub2ApiPoolStatus.Unavailable => UiPalette.Rose400,
        _ => UiPalette.Slate400,
    };

    private static Color Sub2ApiUsageStatusColor(Sub2ApiUsageStatus status) => status switch
    {
        Sub2ApiUsageStatus.Available => UiPalette.Teal300,
        Sub2ApiUsageStatus.Stale => UiPalette.Amber400,
        Sub2ApiUsageStatus.Unavailable => UiPalette.Rose400,
        _ => UiPalette.Slate400,
    };

    private static Color Sub2ApiQuotaStatusColor(Sub2ApiQuotaStatus status) => status switch
    {
        Sub2ApiQuotaStatus.Available => UiPalette.Teal300,
        Sub2ApiQuotaStatus.Stale => UiPalette.Amber400,
        Sub2ApiQuotaStatus.Unavailable => UiPalette.Rose400,
        _ => UiPalette.Slate400,
    };
}
