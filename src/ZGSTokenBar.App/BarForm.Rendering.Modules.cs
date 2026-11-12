using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Rendering of the plugin, live-metrics and system-usage modules.</summary>
internal sealed partial class BarForm
{

    private void DrawPluginMiniCard(
        Graphics graphics,
        RectangleF bounds,
        PluginMiniCardView view,
        bool collapsed)
    {
        DrawTaskbarCardShell(graphics, bounds);
        if (view.PluginId == MiniAreaIds.Router)
        {
            RouterCardRenderer.Draw(graphics, bounds, view.Card, _badgeFont, collapsed, _cardTitleFont, _subtitleFont);
            return;
        }
        var iconBounds = collapsed
            ? new RectangleF(bounds.X + 3, bounds.Y + 3, 15, 15)
            : new RectangleF(bounds.X + 5, bounds.Y + 6, 24, 24);
        var usageSummary = view.Card.Summary.FirstOrDefault();
        var usageRemaining = usageSummary?.Status == "usage-remaining" ? usageSummary.Value.Number : null;
        if (collapsed && usageRemaining is { } ringValue)
        {
            using var trackPen = new Pen(Color.FromArgb(90, 255, 255, 255), 2f);
            graphics.DrawEllipse(trackPen, iconBounds);
            using var ringPen = new Pen(QuotaColorScale.ForRemaining(ringValue), 2f);
            graphics.DrawArc(ringPen, iconBounds, -90, (float)(360 * Math.Clamp(ringValue, 0, 100) / 100));
        }
        else if (_pluginIcons.TryGetValue(view.PluginId, out var image))
        {
            graphics.DrawImage(image, iconBounds);
        }
        else
        {
        using var iconFill = new SolidBrush(PluginIconColor(view));
        using var iconText = new SolidBrush(Color.White);
        graphics.FillEllipse(iconFill, iconBounds);
        DrawString(
            graphics,
            PluginMonogram(view.Title),
            _badgeFont,
            iconText,
            iconBounds,
            StringAlignment.Center);
        }
        if (collapsed)
        {
            var compactSummary = view.Card.Summary.FirstOrDefault();
            if (view.Card.Kind == ContributionKind.Balance || compactSummary?.Status == "usage-remaining")
            {
                var color = compactSummary?.Status == "usage-remaining" && compactSummary.Value.Number is { } remaining
                    ? QuotaColorScale.ForRemaining(remaining)
                    : PluginCompactStatusColor(compactSummary?.Status);
                using var statusBrush = new SolidBrush(color);
                graphics.FillEllipse(statusBrush, bounds.Right - 7, bounds.Top + 4, 4, 4);
                using var valueBrush = new SolidBrush(color);
                var compactText = compactSummary?.Status == "usage-remaining" && compactSummary.Value.Number is { } compactRemaining
                    ? PercentFormat.Remaining(compactRemaining)
                    : compactSummary is null ? "—" : CompactPluginValue(compactSummary.Value);
                DrawString(
                    graphics,
                    compactText,
                    _badgeFont,
                    valueBrush,
                    new RectangleF(bounds.X + 2, bounds.Bottom - 12, bounds.Width - 4, 9),
                    StringAlignment.Center);
            }
            return;
        }

        using var title = new SolidBrush(UiPalette.Slate300);
        using var value = new SolidBrush(UiPalette.Slate50);
        DrawString(
            graphics,
            view.Title,
            _subtitleFont,
            title,
            new RectangleF(bounds.X + 34, bounds.Y + 5, bounds.Width - 39, 12),
            StringAlignment.Near);
        var summary = view.Card.Summary.FirstOrDefault();
        DrawString(
            graphics,
            summary is null ? "—" : PluginValue(summary.Value),
            _cardTitleFont,
            value,
            new RectangleF(bounds.X + 34, bounds.Y + 14, bounds.Width - 39, 14),
            StringAlignment.Near);
        if (summary?.Status == "usage-remaining" && summary.Value.Number is { } remainingPercent)
        {
            var track = new RectangleF(bounds.X + 34, bounds.Bottom - 7, Math.Max(8, bounds.Width - 40), 3);
            using var trackBrush = new SolidBrush(UiPalette.Slate800);
            graphics.FillRoundedRectangle(trackBrush, track, 1.5f);
            if (remainingPercent > 0)
            {
                using var active = new SolidBrush(QuotaColorScale.ForRemaining(remainingPercent));
                graphics.FillRoundedRectangle(
                    active,
                    new RectangleF(track.X, track.Y, track.Width * (float)(Math.Clamp(remainingPercent, 0, 100) / 100), track.Height),
                    1.5f);
            }
        }
    }

    private void DrawLiveMetricsCard(Graphics graphics, RectangleF bounds, bool collapsed)
    {
        DrawTaskbarCardShell(graphics, bounds);
        using var primary = new SolidBrush(UiPalette.Slate300);
        using var muted = new SolidBrush(UiPalette.Slate400);
        using var observed = new SolidBrush(Color.FromArgb(110, 231, 183));
        using var estimated = new SolidBrush(UiPalette.Amber400);
        using var receive = new SolidBrush(Color.FromArgb(125, 211, 252));
        using var send = new SolidBrush(Color.FromArgb(196, 181, 253));
        var currentRate = _systemUsage.CurrentOutputRate;
        var isEstimated = currentRate is not null && _systemUsage.LiveRate?.HasEstimate == true;
        var prefix = isEstimated ? "~" : "Σ";
        var rate = currentRate is { } output ? $"{output:0.#}" : "—";
        if (collapsed)
        {
            var compact = CompactOutputRate(currentRate);
            DrawString(graphics, isEstimated ? "~" + compact : compact, _cardTitleFont,
                currentRate is null ? muted : isEstimated ? estimated : observed,
                new RectangleF(bounds.X + 3, bounds.Y + 5, bounds.Width - 6, 15), StringAlignment.Center);
            DrawString(graphics, "tok/s", _resetFont, muted,
                new RectangleF(bounds.X + 3, bounds.Y + 23, bounds.Width - 6, 10), StringAlignment.Center);
            return;
        }
        using var accent = new SolidBrush(isEstimated ? estimated.Color : observed.Color);
        graphics.FillRoundedRectangle(accent, new RectangleF(bounds.X + 4, bounds.Y + 4, 2, 9), 1);
        DrawString(graphics, $"{prefix} {rate} tok/s", _cardTitleFont, isEstimated ? estimated : observed,
            new RectangleF(bounds.X + 10, bounds.Y + 2, bounds.Width - 14, 14), StringAlignment.Near);
        var detail = _systemUsage.LiveRate is { } live
            ? (_text.IsChinese ? $"生成 {live.Generating} · 工具 {live.Tools}" : $"Gen {live.Generating} · Tools {live.Tools}") : "—";
        DrawString(graphics, detail, _subtitleFont, muted,
            new RectangleF(bounds.X + 5, bounds.Y + 16, bounds.Width - 9, 10), StringAlignment.Near);
        using var divider = new Pen(UiPalette.Slate500.WithAlpha(38), 1);
        graphics.DrawLine(divider, bounds.X + 5, bounds.Y + 26, bounds.Right - 5, bounds.Y + 26);
        var receiveText = "↓" + NetworkUsageSampler.Compact(_systemUsage.CurrentNetworkReceive) + "/s";
        var sendText = "↑" + NetworkUsageSampler.Compact(_systemUsage.CurrentNetworkSend) + "/s";
        var halfWidth = (bounds.Width - 15) / 2;
        DrawString(graphics, receiveText, _subtitleFont, receive,
            new RectangleF(bounds.X + 5, bounds.Y + 27, halfWidth, 10), StringAlignment.Near);
        DrawString(graphics, sendText, _subtitleFont, send,
            new RectangleF(bounds.X + 11 + halfWidth, bounds.Y + 27, halfWidth, 10), StringAlignment.Near);
    }

    internal static string CompactOutputRate(double? value)
    {
        if (value is not { } number || !double.IsFinite(number) || number < 0) return "—";
        var (divisor, suffix) = number switch
        {
            >= 1e18 => (1e18, "E"),
            >= 1e15 => (1e15, "P"),
            >= 1e12 => (1e12, "T"),
            >= 1e9 => (1e9, "G"),
            >= 1e6 => (1e6, "M"),
            >= 1e3 => (1e3, "K"),
            _ => (1d, ""),
        };
        return $"{number / divisor:0.#}{suffix}";
    }

    private void DrawSystemUsageCard(Graphics graphics, RectangleF bounds, bool collapsed)
    {
        DrawSystemUsageFrame(graphics, bounds);
        if (collapsed) DrawCollapsedSystemUsage(graphics, bounds);
        else DrawSystemUsageRows(graphics, bounds);
    }

    private void DrawCollapsedSystemUsage(Graphics graphics, RectangleF bounds)
    {
        var trackWidth = Math.Min(20f, bounds.Width - 12);
        var trackX = bounds.Left + (bounds.Width - trackWidth) / 2;
        using var trackBrush = new SolidBrush(UiPalette.Slate500.WithAlpha(44));
        for (var index = 0; index < 4; index++)
        {
            var track = new RectangleF(trackX, bounds.Top + 8 + index * 5.5f, trackWidth, 2.5f);
            graphics.FillRoundedRectangle(trackBrush, track, 1.25f);
            var value = index switch
            {
                0 => _systemUsage.CpuPercent,
                1 => _systemUsage.MemoryPercent,
                2 => _systemUsage.DiskActivePercent,
                _ => _systemUsage.GpuPercent,
            };
            if (value is not { } usage || usage <= 0) continue;
            using var valueBrush = new SolidBrush(Color.FromArgb(130, UsageColor(usage)));
            graphics.FillRoundedRectangle(
                valueBrush,
                new RectangleF(
                    track.X,
                    track.Y,
                    Math.Max(1.5f, track.Width * (float)Math.Clamp(usage / 100, 0, 1)),
                    track.Height),
                1.25f);
        }
    }

    private void DrawSystemUsageFrame(Graphics graphics, RectangleF bounds)
    {
        var hover = HoverProgress(HoverTarget.SystemUsage);
        using var path = RoundedRectangle(bounds, 6);
        using var fill = new SolidBrush(MixColor(
            _backgroundTheme.QuotaGroup,
            Color.FromArgb(26, 42, 66),
            hover * .45f));
        using var border = new Pen(Color.FromArgb(
            (int)Math.Round(42 + hover * 68),
            100,
            116,
            139));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private void DrawSystemUsageRows(Graphics graphics, RectangleF bounds)
    {
        DrawSystemUsageRow(graphics, bounds, 0, "CPU", _systemUsage.CpuPercent);
        DrawSystemUsageRow(graphics, bounds, 1, "RAM", _systemUsage.MemoryPercent);
        DrawSystemUsageRow(graphics, bounds, 2, "I/O", _systemUsage.DiskActivePercent);
        DrawSystemUsageRow(graphics, bounds, 3, "GPU", _systemUsage.GpuPercent);
    }

    private void DrawSystemUsageRow(
        Graphics graphics,
        RectangleF cardBounds,
        int index,
        string label,
        double? percent)
    {
        var y = cardBounds.Y + 3 + index * 8.2f;
        var rowHeight = 6.5f;
        var track = new RectangleF(cardBounds.X + 25, y, cardBounds.Width - 29, rowHeight);
        using var trackBrush = new SolidBrush(UiPalette.Slate500.WithAlpha(38));
        graphics.FillRoundedRectangle(trackBrush, track, 3);
        if (percent is { } usage && usage > 0)
        {
            var color = UsageColor(usage);
            using var usageBrush = new SolidBrush(Color.FromArgb(82, color));
            graphics.FillRoundedRectangle(
                usageBrush,
                new RectangleF(
                    track.X,
                    track.Y,
                    Math.Max(1.5f, track.Width * (float)Math.Clamp(usage / 100, 0, 1)),
                    track.Height),
                3);
        }

        using var labelBrush = new SolidBrush(UiPalette.Slate400);
        using var valueBrush = new SolidBrush(percent is null
            ? UiPalette.Slate500
            : UiPalette.Slate200);
        DrawString(
            graphics,
            label,
            _accountOrdinalFont,
            labelBrush,
            new RectangleF(cardBounds.X + 3, y, 20, rowHeight),
            StringAlignment.Near);
        DrawString(
            graphics,
            CompactUsage(percent),
            _accountOrdinalFont,
            valueBrush,
            new RectangleF(track.X + 2, track.Y - .25f, track.Width - 4, rowHeight + .5f),
            StringAlignment.Far);
    }
}
