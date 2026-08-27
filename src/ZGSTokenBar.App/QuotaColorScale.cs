using System.Drawing;

namespace ZGSTokenBar.App;

internal static class QuotaColorScale
{
    private static readonly Color Depleted = Color.FromArgb(251, 113, 133);
    private static readonly Color Half = Color.FromArgb(251, 191, 36);
    private static readonly Color Full = Color.FromArgb(52, 211, 153);

    public static Color ForRemaining(double remaining)
    {
        var clamped = Math.Clamp(remaining, 0, 100);
        if (clamped <= 50)
        {
            return Interpolate(Depleted, Half, SmoothStep(clamped / 50));
        }

        return Interpolate(Half, Full, SmoothStep((clamped - 50) / 50));
    }

    private static double SmoothStep(double amount) => amount * amount * (3 - 2 * amount);

    private static Color Interpolate(Color from, Color to, double amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));
}
