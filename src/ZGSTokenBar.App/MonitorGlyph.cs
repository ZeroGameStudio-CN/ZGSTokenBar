using System.Drawing.Drawing2D;

namespace ZGSTokenBar.App;

internal enum IntakeGlyphState { Idle, Processing, Unconfirmed }

internal static class MonitorGlyph
{
    internal static RectangleF Centered(RectangleF bounds)
    {
        var size = Math.Min(17f, Math.Min(bounds.Width, bounds.Height));
        return new(bounds.X + (bounds.Width - size) / 2f, bounds.Y + (bounds.Height - size) / 2f, size, size);
    }

    internal static void DrawIntake(Graphics graphics, RectangleF icon, bool light = false,
        IntakeGlyphState state = IntakeGlyphState.Processing)
    {
        var outline = state == IntakeGlyphState.Unconfirmed ? UiPalette.Slate500
            : light ? UiPalette.Slate700 : UiPalette.Slate300;
        using var pen = new Pen(outline, Math.Max(1.3f, icon.Width / 12f))
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF At(float x, float y) => new(icon.Left + x * icon.Width, icon.Top + y * icon.Height);
        var lift = state == IntakeGlyphState.Idle ? .11f : 0f;
        graphics.DrawLines(pen, [At(.10f, .41f - lift), At(.07f, .55f - lift), At(.07f, .87f - lift),
            At(.93f, .87f - lift), At(.93f, .55f - lift), At(.90f, .41f - lift)]);
        switch (state)
        {
            case IntakeGlyphState.Processing:
                pen.Color = light ? UiPalette.Emerald600 : UiTokens.Info;
                pen.Width = Math.Max(1.6f, icon.Width / 10f);
                graphics.DrawLine(pen, At(.5f, .10f), At(.5f, .62f));
                graphics.DrawLines(pen, [At(.31f, .42f), At(.50f, .62f), At(.69f, .42f)]);
                break;
            case IntakeGlyphState.Unconfirmed:
                graphics.DrawLine(pen, At(.32f, .62f), At(.68f, .24f));
                break;
        }
    }
}
