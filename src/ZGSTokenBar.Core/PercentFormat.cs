using System.Globalization;

namespace ZGSTokenBar.Core;

/// <summary>
/// The one percent rule of the app, so a ring, its chip, mini cards and every hover agree:
/// remaining is rounded <b>down</b> (never overstates what is left), used is rounded <b>up</b>
/// (never understates what is spent). Under 10% one decimal is kept; a sliver under 1% reads "&lt;1%".
/// </summary>
public static class PercentFormat
{
    private const double Epsilon = 1e-6;

    /// <summary>Remaining percent, floored: 91.43 → "91%", 3.61 → "3.6%", 0.4 → "&lt;1%".</summary>
    public static string Remaining(double? remaining, string missing = "—") => remaining is { } value
        ? Format(Math.Clamp(value, 0, 100), floor: true)
        : missing;

    /// <summary>
    /// Whole-number remaining for the small ring chip, floored the same way (3.61 → "3%",
    /// 91.43 → "91%"), so the chip never shows more than the hover's figure.
    /// </summary>
    public static string RemainingWhole(double? remaining, string missing = "—")
    {
        if (remaining is not { } value) return missing;
        value = Math.Clamp(value, 0, 100);
        if (value > 0 && value < 1) return "<1%";
        return Math.Floor(value + Epsilon).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>Remaining percent from a used percent (100 − used), floored.</summary>
    public static string RemainingFromUsed(double? used, string missing = "—") =>
        used is { } value ? Remaining(100 - value, missing) : missing;

    /// <summary>Used percent, rounded up: 8.57 → "9%", 96.39 → "97%".</summary>
    public static string Used(double? used, string missing = "—") => used is { } value
        ? Format(Math.Clamp(value, 0, 100), floor: false)
        : missing;

    /// <summary>The number shown for a remaining percent (same rule as <see cref="Remaining"/>).</summary>
    public static double RemainingValue(double remaining)
    {
        var value = Math.Clamp(remaining, 0, 100);
        return value < 10 ? Math.Floor(value * 10 + Epsilon) / 10 : Math.Floor(value + Epsilon);
    }

    private static string Format(double value, bool floor)
    {
        if (value <= 0) return "0%";
        if (value >= 100) return "100%";
        if (value < 1) return floor ? "<1%" : "1%";
        if (value < 10)
        {
            var tenth = floor ? Math.Floor(value * 10 + Epsilon) / 10 : Math.Ceiling(value * 10 - Epsilon) / 10;
            if (tenth >= 10) return "10%";
            return tenth.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        var whole = floor ? Math.Floor(value + Epsilon) : Math.Ceiling(value - Epsilon);
        return whole.ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
