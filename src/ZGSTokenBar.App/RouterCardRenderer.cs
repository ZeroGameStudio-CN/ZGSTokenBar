using System.Drawing.Drawing2D;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal static class RouterCardRenderer
{
    internal const int Width = 132;
    internal static void Draw(Graphics graphics, RectangleF bounds, MiniCardContribution card, Font badgeFont,
        bool collapsed = true, Font? titleFont = null, Font? detailFont = null, RouterQueueChip? queueChip = null)
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
        DrawRouterGlyph(graphics, icon, state, color);
        if (queued > 0)
        {
            var text = queued > 99 ? "99+" : queued.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (collapsed)
            {
                // Collapsed (taskbar dock): the shared ring chip (GlassDockPainter.DrawChip) on the
                // icon's lower-left, the same corner and look as the ring % chips.
                var chip = queueChip ?? new RouterQueueChip(GlassDockPainter.DefaultLabelSize, null, false);
                GlassDockPainter.DrawChip(graphics, icon.X - 3f, chip.Bottom ?? icon.Bottom + 4f, text, chip.LabelSize, critical: false, chip.Light);
                graphics.Restore(saved);
                return;
            }
            var badgeWidth = Math.Max(13, graphics.MeasureString(text, badgeFont).Width + 4);
            var badge = new RectangleF(iconArea.Right - badgeWidth - 1, bounds.Top + 3, badgeWidth, 12);
            // Neutral UiKit pill (no outline, so it never bleeds into the icon's half): the count reads at every DPI without competing with the state dot.
            using var background = new SolidBrush(UiPalette.Slate700);
            using (var path = UiPaint.RoundedPath(badge, badge.Height / 2)) graphics.FillPath(background, path);
            using var ink = new SolidBrush(UiPalette.Slate100);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            graphics.DrawString(text, badgeFont, ink, badge, format);
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
    /// The Router mark: one input node fanning out to three outputs (a routing/branching glyph in the
    /// style of the provider logos). Strokes stay neutral Slate; only the input node and the active
    /// route carry the state colour, so the icon reads as "Router" first and the state second.
    /// Geometry is in fractions of the icon box and pixel-snapped strokes keep it crisp at every DPI.
    /// </summary>
    internal static void DrawRouterGlyph(Graphics graphics, RectangleF icon, string state, Color stateColor)
    {
        var dim = state is "offline" or "unknown";
        var stroke = dim ? UiPalette.Slate500 : UiPalette.Slate300;
        var s = icon.Width;
        var root = new PointF(icon.Left + s * .2f, icon.Top + s * .5f);
        var outX = icon.Left + s * .84f;
        PointF[] outs = [new(outX, icon.Top + s * .18f), new(outX, icon.Top + s * .5f), new(outX, icon.Top + s * .82f)];
        var active = state switch { "running" => 1, "cleanup" => 2, "waiting" => 0, _ => -1 };
        var width = Math.Max(1.3f, s / 12f);
        using var pen = new Pen(stroke, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var activePen = new Pen(stateColor, width + .2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var fork = new PointF(icon.Left + s * .44f, root.Y);
        for (var index = 0; index < outs.Length; index++)
        {
            var target = outs[index];
            using var path = new GraphicsPath();
            path.AddLine(root, fork);
            if (index == 1) path.AddLine(fork, target);
            else path.AddBezier(fork, new PointF(fork.X + s * .16f, fork.Y), new PointF(target.X - s * .2f, target.Y), new PointF(target.X - s * .04f, target.Y));
            graphics.DrawPath(index == active ? activePen : pen, path);
        }
        var node = Math.Max(3f, s * .2f);
        var outNode = Math.Max(2.4f, s * .15f);
        using var outFill = new SolidBrush(stroke);
        using var activeFill = new SolidBrush(stateColor);
        for (var index = 0; index < outs.Length; index++)
        {
            var target = outs[index];
            graphics.FillEllipse(index == active ? activeFill : outFill, target.X - outNode / 2, target.Y - outNode / 2, outNode, outNode);
        }
        var rootBounds = new RectangleF(root.X - node / 2, root.Y - node / 2, node, node);
        using (var surface = new SolidBrush(UiTokens.CardSurface)) graphics.FillEllipse(surface, RectangleF.Inflate(rootBounds, 1, 1));
        if (state is "unknown")
        {
            using var hollow = new Pen(stroke, width);
            graphics.DrawEllipse(hollow, rootBounds);
        }
        else
        {
            graphics.FillEllipse(dim ? outFill : activeFill, rootBounds);
        }
        if (state == "attention")
        {
            // Small alert dot on the upper-right, like an app-icon notification.
            var alert = Math.Max(4.5f, s * .3f);
            var dot = new RectangleF(icon.Right - alert * .7f, icon.Top - alert * .25f, alert, alert);
            using var ring = new SolidBrush(UiTokens.CardSurface);
            graphics.FillEllipse(ring, RectangleF.Inflate(dot, 1, 1));
            graphics.FillEllipse(activeFill, dot);
        }
    }

}

/// <summary>Size, baseline and theme of the collapsed Router queue chip (see GlassDockPainter.DrawChip).</summary>
internal sealed record RouterQueueChip(float LabelSize, float? Bottom, bool Light);
