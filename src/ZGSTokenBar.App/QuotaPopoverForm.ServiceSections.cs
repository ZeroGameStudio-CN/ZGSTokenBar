using System.Drawing.Drawing2D;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>Service sections: API-service and balance (AI Gateway) cards.</summary>
internal sealed partial class QuotaPopoverForm
{
    private void DrawServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content)
    {
        if (Sub2ApiServicePresentation.IsSub2ApiService(content.Card))
        {
            var presentation = Sub2ApiServicePresentation.Resolve(
                content.Card,
                _renderNow ?? content.CapturedAt);
            switch (presentation.Kind)
            {
                case Sub2ApiServicePresentationKind.CompleteAvailability
                    when presentation.Availability is { } completeAvailability:
                    DrawSub2ApiAccountAvailabilityServiceContent(
                        graphics,
                        body,
                        content,
                        completeAvailability,
                        includeProgressRail: true);
                    return;
                case Sub2ApiServicePresentationKind.PartialAvailability
                    or Sub2ApiServicePresentationKind.KnownNoneAvailability
                    when presentation.Availability is { } partialAvailability:
                    DrawSub2ApiAccountAvailabilityServiceContent(
                        graphics,
                        body,
                        content,
                        partialAvailability);
                    return;
                case Sub2ApiServicePresentationKind.LegacyAggregateQuota
                    when presentation.LegacyQuota is { } legacy:
                    DrawSub2ApiLegacyQuotaServiceContent(graphics, body, content, legacy);
                    return;
                case Sub2ApiServicePresentationKind.Usage
                    when presentation.Usage is { } usage:
                    DrawSub2ApiUsageServiceContent(graphics, body, content, usage);
                    return;
                case Sub2ApiServicePresentationKind.Pool
                    when presentation.Pool is { } pool:
                    DrawSub2ApiPoolServiceContent(graphics, body, content, pool);
                    return;
                default:
                    DrawSub2ApiUnavailableServiceContent(graphics, body, content);
                    return;
            }
        }

        if (content.Card.Sub2ApiAccountAvailability is { } availability
            && Sub2ApiAccountAvailabilityFormatting.IsRenderable(availability))
        {
            DrawSub2ApiAccountAvailabilityServiceContent(graphics, body, content, availability);
            return;
        }
        if (content.Card.Sub2ApiQuota is { } quota && Sub2ApiQuotaFormatting.PreferredWindow(quota) is not null)
        {
            DrawSub2ApiQuotaServiceContent(graphics, body, content, quota);
            return;
        }
        if (content.Card.Sub2ApiUsage is not null)
        {
            DrawSub2ApiUsageServiceContent(graphics, body, content, content.Card.Sub2ApiUsage);
            return;
        }
        if (content.Card.Sub2ApiPool is not null)
        {
            DrawSub2ApiPoolServiceContent(graphics, body, content, content.Card.Sub2ApiPool);
            return;
        }
        if (content.Card.Balance is not null)
        {
            DrawBalanceServiceContent(graphics, body, content, content.Card.Balance);
            return;
        }

        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(UiPalette.Slate100);
        using var mutedBrush = new SolidBrush(UiPalette.Slate400);
        using var valueBrush = new SolidBrush(UiPalette.Teal300);
        DrawString(
            graphics,
            content.Card.DisplayLabel,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.ApiServiceConfigured,
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
            content.Pinned ? _text.Pinned : _text.Preview,
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            _text.ApiServiceConfigured,
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.ApiServiceNoQuota,
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

    private void DrawBalanceServiceContent(
        Graphics graphics,
        RectangleF body,
        QuotaPopoverContent content,
        AiGatewayBalance balance)
    {
        var x = body.X;
        var y = body.Y;
        graphics.DrawImage(_providerLogo!, new RectangleF(x + 12, y + 10, 24, 24));
        using var titleBrush = new SolidBrush(UiPalette.Slate100);
        using var mutedBrush = new SolidBrush(UiPalette.Slate400);
        using var valueBrush = new SolidBrush(balance.Status == AiGatewayBalanceStatus.Available
            ? UiPalette.Teal300
            : UiPalette.Amber400);
        DrawString(
            graphics,
            _text.AiGateway,
            _titleFont,
            titleBrush,
            new RectangleF(x + 44, y + 8, body.Width - 118, 14),
            StringAlignment.Near);
        DrawString(
            graphics,
            _text.AiGatewayModel,
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
            _text.AiGatewayStatus(balance.Status),
            _statusFont,
            mutedBrush,
            statusBounds,
            StringAlignment.Center);

        DrawString(
            graphics,
            AiGatewayBalanceFormatting.Amount(balance.TotalBalance),
            _valueFont,
            valueBrush,
            new RectangleF(x + 12, y + 42, body.Width - 24, 23),
            StringAlignment.Near);
        DrawString(
            graphics,
            $"{_text.AiGatewayToppedUpBalance} {AiGatewayBalanceFormatting.Amount(balance.ToppedUpBalance)} · {_text.AiGatewayGrantedBalance} {AiGatewayBalanceFormatting.Amount(balance.GrantedBalance)}",
            _detailFont,
            mutedBrush,
            new RectangleF(x + 12, y + 76, body.Width - 24, 18),
            StringAlignment.Near);
        if (content.AiGatewayUsage is { } usage)
        {
            DrawString(
                graphics,
                _text.AiGatewayTodayUsage(usage),
                _detailFont,
                mutedBrush,
                new RectangleF(x + 12, y + 94, body.Width - 24, 18),
                StringAlignment.Near);
            DrawString(
                graphics,
                _text.AiGatewayUsageDetail(usage),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 112, body.Width - 24, 13),
                StringAlignment.Near);
            DrawString(
                graphics,
                _text.AiGatewayUpdatedShort(balance.ObservedAt),
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 129, 108, 10),
                StringAlignment.Near);
        }
        else
        {
            DrawString(
                graphics,
                $"{_text.AiGatewayUpdated(balance.ObservedAt)} · {_text.AiGatewayStatus(balance.Status)}",
                _subtitleFont,
                mutedBrush,
                new RectangleF(x + 12, y + 103, body.Width - 24, 13),
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
}
