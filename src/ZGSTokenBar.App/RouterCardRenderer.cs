using System.Drawing.Drawing2D;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal static class RouterCardRenderer
{
    internal const int Width = 48;
    internal static void Draw(Graphics graphics, RectangleF bounds, MiniCardContribution card, Font badgeFont)
    {
        var state = card.Summary.FirstOrDefault(item => item.LabelKey == "router.state")?.Status ?? "unknown";
        var queued = card.Summary.FirstOrDefault(item => item.LabelKey == "router.queued")?.Value.Integer ?? 0;
        var color = RouterVisualPalette.State(state is "cleanup" ? "warning" : state is "attention" ? "failed"
            : state is "idle" or "offline" or "unknown" ? "idle" : state);
        var saved = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Min(19, bounds.Height - 10);
        var icon = new RectangleF(bounds.X + (bounds.Width - size) / 2 - (queued > 0 ? 3 : 0), bounds.Y + (bounds.Height - size) / 2, size, size);
        using var halo = new SolidBrush(Color.FromArgb(10, color));
        graphics.FillEllipse(halo, RectangleF.Inflate(icon, 4, 4));
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var fill = new SolidBrush(color);
        switch (state)
        {
            case "running":
                graphics.FillPolygon(fill, [new PointF(icon.Left + 5, icon.Top + 2), new PointF(icon.Right - 2, icon.Top + size / 2), new PointF(icon.Left + 5, icon.Bottom - 2)]);
                break;
            case "cleanup":
                graphics.DrawArc(pen, icon, 25, 125); graphics.DrawArc(pen, icon, 205, 125);
                graphics.FillEllipse(fill, icon.Right - 5, icon.Top + 2, 4, 4);
                break;
            case "attention":
                graphics.DrawPolygon(pen, [new PointF(icon.Left + size / 2, icon.Top), new PointF(icon.Right, icon.Bottom), new PointF(icon.Left, icon.Bottom)]);
                graphics.DrawLine(pen, icon.Left + size / 2, icon.Top + 7, icon.Left + size / 2, icon.Top + 12);
                graphics.FillEllipse(fill, icon.Left + size / 2 - 1, icon.Bottom - 5, 2, 2);
                break;
            default:
                graphics.DrawEllipse(pen, icon);
                if (state == "waiting")
                { graphics.DrawLine(pen, icon.Left + size / 2, icon.Top + 4, icon.Left + size / 2, icon.Top + size / 2); graphics.DrawLine(pen, icon.Left + size / 2, icon.Top + size / 2, icon.Right - 4, icon.Top + size / 2); }
                else if (state == "offline") graphics.DrawLine(pen, icon.Left + 3, icon.Bottom - 3, icon.Right - 3, icon.Top + 3);
                else if (state != "idle")
                { graphics.DrawArc(pen, icon.Left + 6, icon.Top + 4, 8, 7, 200, 250); graphics.FillEllipse(fill, icon.Left + 10, icon.Bottom - 5, 2, 2); }
                break;
        }
        if (queued > 0)
        {
            var text = queued > 99 ? "99+" : queued.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var badge = new RectangleF(bounds.Right - (text.Length > 2 ? 19 : 14), bounds.Top + 3, text.Length > 2 ? 18 : 13, 12);
            using var background = new SolidBrush(RouterVisualPalette.Mix(Color.FromArgb(22, 28, 37), color, .14f));
            graphics.FillRoundedRectangle(background, badge, 5);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(text, badgeFont, fill, badge, format);
        }
        graphics.Restore(saved);
    }
}
