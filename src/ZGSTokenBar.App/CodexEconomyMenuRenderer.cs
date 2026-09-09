using System.Drawing.Drawing2D;
using System.Drawing.Text;

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

        using var path = RoundedRectangle(bounds, Scale(7));
        using var fill = new SolidBrush(fillColor);
        e.Graphics.FillPath(fill, path);
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
