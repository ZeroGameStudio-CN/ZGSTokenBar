using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed class CodexEconomyMenuHeaderItem : ToolStripMenuItem
{
    public CodexEconomyMenuHeaderItem(
        string title,
        string description,
        Font titleFont,
        Font descriptionFont)
        : base(title)
    {
        Description = description;
        TitleFont = titleFont;
        DescriptionFont = descriptionFont;
        Enabled = false;
        AccessibleName = title;
        AccessibleDescription = description;
    }

    public string Description { get; }
    public Font TitleFont { get; }
    public Font DescriptionFont { get; }
}

internal sealed class CodexEconomyModeMenuItem : ToolStripMenuItem
{
    public CodexEconomyModeMenuItem(
        CodexEconomyMode mode,
        string title,
        string description,
        Color accent,
        bool current,
        Font titleFont,
        Font descriptionFont)
        : base(title)
    {
        Mode = mode;
        Description = description;
        Accent = accent;
        Current = current;
        TitleFont = titleFont;
        DescriptionFont = descriptionFont;
        Checked = current;
        CheckOnClick = false;
        AccessibleName = title;
        AccessibleDescription = description;
    }

    public CodexEconomyMode Mode { get; }
    public string Description { get; }
    public Color Accent { get; }
    public bool Current { get; }
    public Font TitleFont { get; }
    public Font DescriptionFont { get; }
}

internal sealed class CodexEconomyMenuRenderer(
    Color surface,
    Color hover,
    Color border,
    Color text,
    Color muted,
    float scale) : ToolStripProfessionalRenderer
{
    private int Scale(int value) => Math.Max(1, (int)Math.Round(value * scale));

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(surface);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is CodexEconomyMenuHeaderItem) return;

        var bounds = Rectangle.Inflate(new Rectangle(Point.Empty, e.Item.Size), -Scale(4), -Scale(2));
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var fillColor = e.Item.Selected ? hover : surface;
        var outlineColor = Color.Transparent;
        if (e.Item is CodexEconomyModeMenuItem modeItem && modeItem.Current)
        {
            fillColor = Blend(surface, modeItem.Accent, e.Item.Selected ? .18f : .10f);
            outlineColor = Color.FromArgb(e.Item.Selected ? 112 : 76, modeItem.Accent);
        }

        using var path = RoundedRectangle(bounds, Scale(7));
        using var fill = new SolidBrush(fillColor);
        e.Graphics.FillPath(fill, path);
        if (outlineColor.A > 0)
        {
            using var outline = new Pen(outlineColor);
            e.Graphics.DrawPath(outline, path);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        if (e.Item is CodexEconomyMenuHeaderItem header)
        {
            DrawText(
                e.Graphics,
                header.Text ?? string.Empty,
                header.TitleFont,
                text,
                new Rectangle(Scale(12), Scale(5), e.Item.Width - Scale(24), Scale(17)));
            DrawText(
                e.Graphics,
                header.Description,
                header.DescriptionFont,
                muted,
                new Rectangle(Scale(12), Scale(22), e.Item.Width - Scale(24), Scale(14)));
            return;
        }

        if (e.Item is CodexEconomyModeMenuItem modeItem)
        {
            var rowHeight = e.Item.Height;
            var markerSize = Scale(7);
            var markerX = Scale(15);
            var markerY = (rowHeight - markerSize) / 2;
            using var marker = new SolidBrush(modeItem.Accent);
            e.Graphics.FillEllipse(marker, markerX, markerY, markerSize, markerSize);

            if (modeItem.Current)
            {
                var indicator = new RectangleF(
                    Scale(5),
                    Scale(10),
                    Scale(2),
                    Math.Max(1, rowHeight - Scale(20)));
                using var indicatorPath = RoundedRectangle(indicator, Scale(1));
                e.Graphics.FillPath(marker, indicatorPath);
            }

            var textLeft = Scale(31);
            var checkSpace = Scale(30);
            var textWidth = Math.Max(1, e.Item.Width - textLeft - checkSpace);
            DrawText(
                e.Graphics,
                modeItem.Text ?? string.Empty,
                modeItem.TitleFont,
                e.Item.Enabled ? text : muted,
                new Rectangle(textLeft, Scale(6), textWidth, Scale(17)));
            DrawText(
                e.Graphics,
                modeItem.Description,
                modeItem.DescriptionFont,
                muted,
                new Rectangle(textLeft, Scale(25), textWidth, Scale(14)));

            if (modeItem.Current) DrawCheck(e.Graphics, modeItem.Accent, e.Item.Width, rowHeight);
            return;
        }

        e.TextColor = e.Item.Enabled ? text : muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var separator = new Pen(border);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(separator, Scale(10), y, Math.Max(Scale(10), e.Item.Width - Scale(10)), y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = new Rectangle(0, 0, Math.Max(0, e.ToolStrip.Width - 1), Math.Max(0, e.ToolStrip.Height - 1));
        using var path = RoundedRectangle(bounds, Scale(10));
        using var outline = new Pen(border);
        e.Graphics.DrawPath(outline, path);
    }

    internal void ApplyRoundedRegion(ContextMenuStrip menu)
    {
        if (menu.Width <= 0 || menu.Height <= 0) return;
        using var path = RoundedRectangle(new Rectangle(0, 0, menu.Width, menu.Height), Scale(10));
        var next = new Region(path);
        menu.Region?.Dispose();
        menu.Region = next;
    }

    private void DrawCheck(Graphics graphics, Color accent, int width, int height)
    {
        var size = Scale(18);
        var bounds = new Rectangle(width - Scale(12) - size, (height - size) / 2, size, size);
        using var fill = new SolidBrush(Color.FromArgb(36, accent));
        using var outline = new Pen(Color.FromArgb(112, accent));
        graphics.FillEllipse(fill, bounds);
        graphics.DrawEllipse(outline, bounds);
        using var check = new Pen(accent, Math.Max(1.5f, Scale(2)))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        graphics.DrawLines(check,
        [
            new PointF(bounds.Left + size * .28f, bounds.Top + size * .53f),
            new PointF(bounds.Left + size * .44f, bounds.Top + size * .68f),
            new PointF(bounds.Left + size * .73f, bounds.Top + size * .34f),
        ]);
    }

    private static void DrawText(
        Graphics graphics,
        string value,
        Font font,
        Color color,
        Rectangle bounds) =>
        TextRenderer.DrawText(
            graphics,
            value,
            font,
            bounds,
            color,
            TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.VerticalCenter);

    private static Color Blend(Color first, Color second, float amount)
    {
        var value = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(first.A + (second.A - first.A) * value),
            (int)Math.Round(first.R + (second.R - first.R) * value),
            (int)Math.Round(first.G + (second.G - first.G) * value),
            (int)Math.Round(first.B + (second.B - first.B) * value));
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
