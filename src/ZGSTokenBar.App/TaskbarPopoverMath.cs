using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal static class TaskbarPopoverMath
{
    internal static bool ContainsHoverPoint(Rectangle anchor, Rectangle popup, Point cursor, int margin)
    {
        if (anchor.Contains(cursor) || popup.Contains(cursor)) return true;
        if (anchor.IsEmpty || popup.IsEmpty) return false;
        var maxGap = margin * 8;
        Rectangle bridge;
        if (popup.Bottom <= anchor.Top && anchor.Top - popup.Bottom <= maxGap)
            bridge = Rectangle.FromLTRB(anchor.Left - margin, popup.Bottom, anchor.Right + margin, anchor.Top);
        else if (anchor.Bottom <= popup.Top && popup.Top - anchor.Bottom <= maxGap)
            bridge = Rectangle.FromLTRB(anchor.Left - margin, anchor.Bottom, anchor.Right + margin, popup.Top);
        else if (popup.Right <= anchor.Left && anchor.Left - popup.Right <= maxGap)
            bridge = Rectangle.FromLTRB(popup.Right, anchor.Top - margin, anchor.Left, anchor.Bottom + margin);
        else if (anchor.Right <= popup.Left && popup.Left - anchor.Right <= maxGap)
            bridge = Rectangle.FromLTRB(anchor.Right, anchor.Top - margin, popup.Left, anchor.Bottom + margin);
        else return false;
        return bridge.Contains(cursor);
    }

    public static Point OffsetFromAnchor(Point anchor, PopoverTailSide tailSide, int offset) =>
        tailSide switch
        {
            PopoverTailSide.Top => new Point(anchor.X, anchor.Y - offset),
            PopoverTailSide.Right => new Point(anchor.X + offset, anchor.Y),
            PopoverTailSide.Left => new Point(anchor.X - offset, anchor.Y),
            _ => new Point(anchor.X, anchor.Y + offset),
        };

    public static double EntranceEase(double progress) =>
        1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);

    public static double ExitEase(double progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        return clamped * clamped;
    }

    public static Point Interpolate(Point from, Point to, double eased) =>
        new(
            (int)Math.Round(from.X + (to.X - from.X) * eased),
            (int)Math.Round(from.Y + (to.Y - from.Y) * eased));

    public static double FadeIn(double fromOpacity, double eased) =>
        Math.Clamp(fromOpacity + (1 - fromOpacity) * eased, .01, 1);

    public static double FadeOut(double fromOpacity, double eased) =>
        Math.Clamp(fromOpacity * (1 - eased), .01, 1);

    public static RectangleF BodyBounds(
        PopoverTailSide tailSide,
        float bodyWidth,
        float bodyHeight,
        float tailSize) =>
        tailSide switch
        {
            PopoverTailSide.Top => new RectangleF(0, tailSize, bodyWidth, bodyHeight),
            PopoverTailSide.Left => new RectangleF(tailSize, 0, bodyWidth, bodyHeight),
            _ => new RectangleF(0, 0, bodyWidth, bodyHeight),
        };

    public static PointF[] TailPoints(
        PopoverTailSide tailSide,
        RectangleF body,
        float tailOffset,
        float tailSize) =>
        tailSide switch
        {
            PopoverTailSide.Top =>
            [
                new PointF(tailOffset - tailSize, body.Top + 1),
                new PointF(tailOffset, 0),
                new PointF(tailOffset + tailSize, body.Top + 1),
            ],
            PopoverTailSide.Right =>
            [
                new PointF(body.Right - 1, tailOffset - tailSize),
                new PointF(body.Right + tailSize - .5f, tailOffset),
                new PointF(body.Right - 1, tailOffset + tailSize),
            ],
            PopoverTailSide.Left =>
            [
                new PointF(body.Left + 1, tailOffset - tailSize),
                new PointF(0, tailOffset),
                new PointF(body.Left + 1, tailOffset + tailSize),
            ],
            _ =>
            [
                new PointF(tailOffset - tailSize, body.Bottom - 1),
                new PointF(tailOffset, body.Bottom + tailSize - .5f),
                new PointF(tailOffset + tailSize, body.Bottom - 1),
            ],
        };
}
