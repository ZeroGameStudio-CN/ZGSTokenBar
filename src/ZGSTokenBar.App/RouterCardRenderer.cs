using System.Drawing.Drawing2D;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal static class RouterCardRenderer
{
    internal const int Width = 132;
    internal static void Draw(Graphics graphics, RectangleF bounds, MiniCardContribution card, Font badgeFont,
        bool collapsed = true, Font? titleFont = null, Font? detailFont = null)
    {
        var state = card.Summary.FirstOrDefault(item => item.LabelKey == "router.state")?.Status ?? "unknown";
        var queued = card.Summary.FirstOrDefault(item => item.LabelKey == "router.queued")?.Value.Integer ?? 0;
        var color = RouterVisualPalette.State(state is "cleanup" ? "warning" : state is "attention" ? "failed"
            : state is "idle" or "offline" or "unknown" ? "idle" : state);
        var saved = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var iconArea = collapsed ? bounds : new RectangleF(bounds.X, bounds.Y, 34, bounds.Height);
        var size = Math.Min(19, bounds.Height - 10);
        // Collapsed (taskbar dock): the icon never moves with the queue count, so the slot is
        // pixel-stable across idle/running/queued states.
        var icon = new RectangleF(iconArea.X + (iconArea.Width - size) / 2 - (!collapsed && queued > 0 ? 3 : 0), bounds.Y + (bounds.Height - size) / 2, size, size);
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
                DrawCenteredGlyph(graphics, "!", new RectangleF(icon.Left, icon.Top + size * .36f, size, size * .6f), fill);
                break;
            default:
                graphics.DrawEllipse(pen, icon);
                if (state == "waiting")
                { graphics.DrawLine(pen, icon.Left + size / 2, icon.Top + 4, icon.Left + size / 2, icon.Top + size / 2); graphics.DrawLine(pen, icon.Left + size / 2, icon.Top + size / 2, icon.Right - 4, icon.Top + size / 2); }
                else if (state == "offline") graphics.DrawLine(pen, icon.Left + 3, icon.Bottom - 3, icon.Right - 3, icon.Top + 3);
                else if (state != "idle")
                    DrawCenteredGlyph(graphics, "?", icon, fill);
                break;
        }
        if (queued > 0)
        {
            var text = queued > 99 ? "99+" : queued.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RectangleF badge;
            if (collapsed)
            {
                // Fixed-size pill on the icon's lower-right corner, sized for "99+".
                var fixedWidth = Math.Max(13, graphics.MeasureString("99+", badgeFont).Width + 2);
                badge = new RectangleF(icon.Right - fixedWidth / 2 - 2, icon.Bottom - 7, fixedWidth, 12);
            }
            else
            {
                var badgeWidth = Math.Max(13, graphics.MeasureString(text, badgeFont).Width + 4);
                badge = new RectangleF(iconArea.Right - badgeWidth - 1, bounds.Top + 3, badgeWidth, 12);
            }
            using var background = new SolidBrush(RouterVisualPalette.Mix(Color.FromArgb(22, 28, 37), color, .14f));
            graphics.FillRoundedRectangle(background, badge, 5);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            graphics.DrawString(text, badgeFont, fill, badge, format);
        }
        if (!collapsed)
        {
            var project = card.Summary.FirstOrDefault(item => item.LabelKey == "router.project")?.Value.Text;
            project = project switch { "ZGSProject_5" => "P5", "POB_Manual" => "Manual", null or "" => "Router", _ => project };
            var phase = card.Summary.FirstOrDefault(item => item.LabelKey == "router.phase")?.Value.Text ?? "状态未确认";
            var elapsed = card.Summary.FirstOrDefault(item => item.LabelKey == "router.elapsed")?.Value.Text;
            var textBounds = new RectangleF(bounds.X + 38, bounds.Y + 2, Math.Max(0, bounds.Width - 43), 15);
            using var foreground = new SolidBrush(Color.FromArgb(224, 232, 242));
            using var muted = new SolidBrush(Color.FromArgb(142, 155, 174));
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            graphics.DrawString(project, titleFont ?? badgeFont, foreground, textBounds, format);
            textBounds.Y += 16; textBounds.Height = 14;
            graphics.DrawString(phase + (string.IsNullOrWhiteSpace(elapsed) || elapsed == "—" ? "" : " " + elapsed),
                detailFont ?? badgeFont, muted, textBounds, format);
        }
        graphics.Restore(saved);
    }

    /// <summary>
    /// Draws a glyph optically centred inside <paramref name="icon"/> using its real outline bounds,
    /// so the mark sits in the middle of the circle at every DPI instead of relying on hand-tuned offsets.
    /// </summary>
    private static void DrawCenteredGlyph(Graphics graphics, string glyph, RectangleF icon, Brush brush)
    {
        using var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        path.AddString(glyph, family, (int)FontStyle.Bold, icon.Height * .66f, PointF.Empty, StringFormat.GenericTypographic);
        var glyphBounds = path.GetBounds();
        if (glyphBounds.Width <= 0 || glyphBounds.Height <= 0) return;
        using var move = new Matrix();
        move.Translate(
            icon.Left + icon.Width / 2 - (glyphBounds.Left + glyphBounds.Width / 2),
            icon.Top + icon.Height / 2 - (glyphBounds.Top + glyphBounds.Height / 2));
        path.Transform(move);
        graphics.FillPath(brush, path);
    }
}
