namespace ZGSTokenBar.Core;

public readonly record struct UsageColor(byte R, byte G, byte B)
{
    public string Hex => FormattableString.Invariant($"#{R:X2}{G:X2}{B:X2}");

    public static UsageColor Parse(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#')
        {
            return new UsageColor(
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16));
        }

        return new UsageColor(148, 163, 184);
    }
}

public sealed record UsageTheme(
    string Id,
    UsageColor Canvas,
    UsageColor Card,
    UsageColor Text,
    UsageColor Muted,
    UsageColor Track,
    UsageColor Border,
    UsageColor Header);

public static class UsageThemes
{
    public static UsageTheme Dark { get; } = new(
        "dark",
        new(12, 15, 22),
        new(23, 28, 39),
        new(248, 250, 252),
        new(148, 163, 184),
        new(30, 41, 59),
        new(42, 51, 68),
        new(15, 18, 28));

    public static UsageTheme Light { get; } = new(
        "light",
        new(244, 246, 251),
        new(255, 255, 255),
        new(15, 23, 42),
        new(100, 116, 139),
        new(226, 232, 240),
        new(226, 232, 240),
        new(255, 255, 255));

    public static UsageColor ForRemaining(double remaining)
    {
        var clamped = Math.Clamp(remaining, 0, 100);
        return clamped <= 50
            ? Lerp(new(251, 113, 133), new(251, 191, 36), clamped / 50)
            : Lerp(new(251, 191, 36), new(52, 211, 153), (clamped - 50) / 50);
    }

    private static UsageColor Lerp(UsageColor from, UsageColor to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return new UsageColor(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }
}

public static class UsageBoardLayout
{
    public const int Width = 440;
    public const int Padding = 16;
    public const int HeaderHeight = 58;
    public const int SectionHeight = 28;
    public const int CompactRowHeight = 44;
    public const int ExpandedCardHeight = 126;
    public const int CardGap = 8;

    public static bool PreferCompact(UsageBoard board) => board.AccountCount > 5;

    public static int Height(UsageBoard board, bool compact)
    {
        var height = HeaderHeight + Padding;
        foreach (var provider in board.Providers)
        {
            height += SectionHeight;
            var rows = Math.Max(1, provider.Accounts.Count);
            var rowHeight = compact ? CompactRowHeight : ExpandedCardHeight;
            height += rows * rowHeight + Math.Max(0, rows - 1) * CardGap;
            height += 10;
        }

        return Math.Max(height + Padding, 160);
    }
}
