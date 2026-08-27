namespace ZGSTokenBar.App;

internal sealed class DarkContextMenuRenderer(
    Color surface,
    Color hover,
    Color border,
    Color text,
    Color muted) : ToolStripProfessionalRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(surface);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        using var fill = new SolidBrush(e.Item.Selected ? hover : surface);
        e.Graphics.FillRectangle(fill, e.Item.ContentRectangle);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? text : muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var separator = new Pen(border);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(separator, 6, y, Math.Max(6, e.Item.Width - 6), y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var outline = new Pen(border);
        e.Graphics.DrawRectangle(
            outline,
            0,
            0,
            Math.Max(0, e.ToolStrip.Width - 1),
            Math.Max(0, e.ToolStrip.Height - 1));
    }
}
