using System.Drawing.Drawing2D;

namespace ZGSTokenBar.App;

internal static class MonitorGlyph
{
    internal static RectangleF Centered(RectangleF bounds)
    {
        var size = Math.Min(18f, Math.Min(bounds.Width, bounds.Height));
        return new(bounds.X + (bounds.Width - size) / 2f, bounds.Y + (bounds.Height - size) / 2f, size, size);
    }

    internal static void DrawIntake(Graphics graphics, RectangleF icon, bool light = false)
    {
        using var pen = new Pen(light ? UiPalette.Slate700 : UiTokens.Info, Math.Max(1.3f, icon.Width / 12f))
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF At(float x, float y) => new(icon.Left + x * icon.Width, icon.Top + y * icon.Height);
        graphics.DrawLines(pen, [At(.10f, .41f), At(.07f, .55f), At(.07f, .87f),
            At(.93f, .87f), At(.93f, .55f), At(.90f, .41f)]);
        graphics.DrawLine(pen, At(.5f, .10f), At(.5f, .62f));
        graphics.DrawLines(pen, [At(.31f, .42f), At(.50f, .62f), At(.69f, .42f)]);
    }
}
