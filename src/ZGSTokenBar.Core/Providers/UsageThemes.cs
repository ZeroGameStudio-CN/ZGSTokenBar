namespace ZGSTokenBar.Core;

public readonly record struct UsageColor(byte R, byte G, byte B)
{
    public string Hex => FormattableString.Invariant($"#{R:X2}{G:X2}{B:X2}");

    public string Rgba(double alpha) =>
        FormattableString.Invariant($"rgba({R},{G},{B},{alpha.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})");

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
    bool Light,
    UsageColor Canvas,
    UsageColor Card,
    UsageColor Text,
    UsageColor Muted,
    UsageColor Track,
    UsageColor Border,
    UsageColor Header,
    UsageColor Accent,
    UsageColor Wash);

public static class UsageThemes
{
    public static UsageTheme Dark { get; } = new(
        "dark",
        false,
        new(16, 18, 24),
        new(255, 255, 255),
        new(248, 250, 252),
        new(186, 198, 216),
        new(255, 255, 255),
        new(255, 255, 255),
        new(255, 255, 255),
        new(108, 182, 255),
        new(18, 22, 32));

    public static UsageTheme Light { get; } = new(
        "light",
        true,
        new(244, 246, 251),
        new(255, 255, 255),
        new(22, 28, 40),
        new(78, 90, 110),
        new(22, 28, 40),
        new(255, 255, 255),
        new(255, 255, 255),
        new(10, 100, 200),
        new(244, 246, 251));

    public static UsageColor ForRemaining(double remaining)
    {
        var clamped = Math.Clamp(remaining, 0, 100);
        return clamped <= 50
            ? Lerp(new(255, 92, 122), new(255, 176, 64), clamped / 50)
            : Lerp(new(255, 176, 64), new(48, 209, 138), (clamped - 50) / 50);
    }

    public static UsageColor Glow(double remaining)
    {
        var color = ForRemaining(remaining);
        return Lerp(color, new(255, 255, 255), 0.35);
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
    public const int Width = 480;
    public const int Padding = 18;
    public const int HeaderHeight = 78;
    public const int CardHeader = 46;
    public const int AccountRow = 54;
    public const int CompactAccountRow = 40;
    public const int LedgerHeight = 48;
    public const int CardPad = 12;
    public const int CardGap = 12;
    public const int Corner = 28;

    public static bool PreferCompact(UsageBoard board) => board.AccountCount > 5;

    public static bool HasLedger(UsageProviderSnapshot provider) =>
        provider.Accounts.Any(account => account.Ledger is not null);

    public static int CardHeight(UsageProviderSnapshot provider, bool compact)
    {
        var rows = Math.Max(1, provider.Accounts.Count);
        var row = compact ? CompactAccountRow : AccountRow;
        var ledger = !compact && HasLedger(provider) ? LedgerHeight : 0;
        return CardPad + CardHeader + rows * row + ledger + CardPad;
    }

    public static int Height(UsageBoard board, bool compact)
    {
        var height = Padding + HeaderHeight;
        if (board.Providers.Count == 0) height += 72;
        foreach (var provider in board.Providers)
        {
            height += CardHeight(provider, compact) + CardGap;
        }

        return Math.Max(height + Padding - (board.Providers.Count == 0 ? 0 : CardGap), 188);
    }
}
