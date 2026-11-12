using System.Drawing;

namespace ZGSTokenBar.Core;

public sealed record GlassDockRing(
    string ProviderId,
    string Title,
    string AccountLabel,
    int AccountNumber,
    double? RemainingPercent,
    DateTimeOffset? ResetsAt,
    string? BalanceText,
    string Accent)
{
    /// <summary>Draw the % chip in the warning colour even though this ring's own band is fine
    /// (Antigravity: another quota group than the pinned one is nearly exhausted).</summary>
    public bool Warn { get; init; }
}

/// <summary>A dock slot: a ring or the +N/collapse chip.</summary>
public readonly record struct GlassDockSlot(Rectangle Bounds, bool Chip);

public sealed record GlassDockPlan(
    IReadOnlyList<GlassDockRing> Visible,
    int OverflowCount,
    bool OverflowUrgent,
    int Width,
    int Height,
    IReadOnlyList<GlassDockSlot> Slots);

public sealed record GlassDockAccountLine(
    string Label,
    double? RemainingPercent,
    string Detail,
    string? BalanceText);

public sealed record GlassDockProviderGroup(
    string ProviderId,
    string Title,
    string Accent,
    IReadOnlyList<GlassDockAccountLine> Accounts);

/// <summary>
/// Bounded taskbar pill. The collapsed width shrinks to the rings that report usage and is
/// capped at <see cref="Width"/>; only an explicit +N expansion widens it temporarily.
/// </summary>
public static class GlassDockLayout
{
    public const int Height = 44;
    public const int Corner = 18;
    public const int MaxVisibleRings = 4;
    public const int RingSize = 34;
    public const int ItemWidth = 36;
    // Room for the corner % chip of a neighbour showing 100% plus its index badge.
    public const int RingGap = 6;
    public const int PadLeft = 6;
    public const int PadRight = 10;
    public const int ChipWidth = 24;
    public const int ChipGap = 4;
    public const int DividerGap = 13;
    public const int RouterSize = 26;
    public const int ModuleGap = 7;
    public const int MetricsWidth = 54;
    public const int MetricsRows = 4;
    public const int RateWidth = 36;
    public const int SystemWidth = RouterSize + ModuleGap + MetricsWidth + ModuleGap + RateWidth;
    public const int FlyoutWidth = 316;
    public const int Width = PadLeft + MaxVisibleRings * ItemWidth + (MaxVisibleRings - 1) * RingGap
        + ChipGap + ChipWidth + DividerGap + SystemWidth + PadRight;

    /// <summary>Width of a pill that shows <paramref name="items"/> rings plus the +N/collapse chip.</summary>
    public static int WidthFor(int items, bool chip) =>
        PadLeft + Math.Max(0, items) * ItemWidth + Math.Max(0, items - 1) * RingGap
        + (chip ? ChipGap + ChipWidth : 0) + DividerGap + SystemWidth + PadRight;

    public static int SystemLeftFor(int width) => width - PadRight - SystemWidth;

    public static int SystemLeft => SystemLeftFor(Width);

    public static Rectangle RouterBoundsFor(int width) => new(
        SystemLeftFor(width),
        (Height - RouterSize) / 2,
        RouterSize,
        RouterSize);

    public static Rectangle RouterBounds => RouterBoundsFor(Width);

    /// <summary>CPU / RAM / I/O / GPU rows, matching the four metrics of the classic system card.</summary>
    public static Rectangle BarsBoundsFor(int width) => new(
        SystemLeftFor(width) + RouterSize + ModuleGap,
        (Height - 32) / 2,
        MetricsWidth,
        32);

    public static Rectangle BarsBounds => BarsBoundsFor(Width);

    public static Rectangle RateBoundsFor(int width) => new(
        width - PadRight - RateWidth,
        (Height - 26) / 2,
        RateWidth,
        26);

    public static Rectangle RateBounds => RateBoundsFor(Width);

    /// <summary>Minimum sweep so a small non-zero remaining value stays visible on the ring.</summary>
    public static float RingSweep(double? remaining) => remaining switch
    {
        null => 0f,
        <= 0 => 0f,
        { } value => Math.Max(22f, (float)(360d * Math.Clamp(value, 0, 100) / 100d)),
    };

    /// <summary>The ring chip: whole-number floor of the shared percent rule.</summary>
    public static string PercentText(double? remaining) => PercentFormat.RemainingWhole(remaining);

    public static string Band(double? remaining) => remaining switch
    {
        null => "unknown",
        < 20 => "red",
        <= 50 => "amber",
        _ => "green",
    };

    public static (int R, int G, int B) BandColor(double? remaining) => Band(remaining) switch
    {
        "red" => (255, 90, 90),
        "amber" => (245, 193, 74),
        "green" => (70, 229, 154),
        _ => (210, 214, 224),
    };

    public static int VisibleCapacity(int maxVisible)
    {
        maxVisible = ClampMax(maxVisible);
        var available = SystemLeft - DividerGap - PadLeft - ChipWidth - ChipGap;
        var count = 0;
        var used = 0;
        while (count < maxVisible)
        {
            var next = used + (count == 0 ? 0 : RingGap) + ItemWidth;
            if (next > available) break;
            used = next;
            count++;
        }

        return Math.Max(1, count);
    }

    public static GlassDockPlan Plan(IReadOnlyList<GlassDockRing> rings, int maxVisible = MaxVisibleRings)
    {
        var limit = VisibleCapacity(maxVisible);
        var ordered = Order(rings);
        var count = Math.Min(limit, ordered.Length);
        var visible = ordered.Take(count).ToArray();
        var hidden = ordered.Skip(count).ToArray();
        var urgent = hidden.Any(ring => ring.RemainingPercent is < 20);
        return new GlassDockPlan(
            visible,
            hidden.Length,
            urgent,
            WidthFor(visible.Length, hidden.Length > 0),
            Height,
            Slots(visible.Length, hidden.Length > 0));
    }

    /// <summary>
    /// Plan that shows every ring while the overflow is expanded: the collapsed rings keep their
    /// positions, the overflow rings follow, and the collapse chip sits at the far right end.
    /// </summary>
    public static GlassDockPlan PlanExpanded(IReadOnlyList<GlassDockRing> rings, int maxVisible = MaxVisibleRings)
    {
        var ordered = Order(rings);
        var slots = ExpandedSlots(ordered.Length, out var width);
        return new GlassDockPlan(ordered, 0, false, width, Height, slots);
    }

    /// <summary>Width of the expanded pill for <paramref name="items"/> rings (collapse chip included).</summary>
    public static int ExpandedWidthFor(int items, int maxVisible = MaxVisibleRings)
    {
        _ = ExpandedSlots(items, out var width);
        return width;
    }

    private static List<GlassDockSlot> ExpandedSlots(int count, out int width)
    {
        var slots = new List<GlassDockSlot>(count + 1);
        var x = PadLeft;
        for (var index = 0; index < count; index++)
        {
            slots.Add(new GlassDockSlot(new Rectangle(x, (Height - RingSize) / 2 - 1, ItemWidth, RingSize + 2), false));
            x += ItemWidth + RingGap;
        }

        x += ChipGap - RingGap;
        slots.Add(new GlassDockSlot(new Rectangle(x, (Height - 22) / 2, ChipWidth, 22), true));
        x += ChipWidth;
        width = x + DividerGap + SystemWidth + PadRight;
        return slots;
    }

    private static GlassDockRing[] Order(IReadOnlyList<GlassDockRing> rings) => rings
        .OrderBy(ring => ring.RemainingPercent ?? double.PositiveInfinity)
        .ThenBy(ring => ring.Title, StringComparer.Ordinal)
        .ThenBy(ring => ring.AccountNumber)
        .ThenBy(ring => ring.AccountLabel, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Item slots: each ring carries its percentage inside, so a slot is only the ring footprint.</summary>
    public static IReadOnlyList<GlassDockSlot> Slots(int visibleCount, bool overflow)
    {
        var slots = new List<GlassDockSlot>(visibleCount + 1);
        var x = PadLeft;
        for (var index = 0; index < visibleCount; index++)
        {
            slots.Add(new GlassDockSlot(new Rectangle(x, (Height - RingSize) / 2 - 1, ItemWidth, RingSize + 2), false));
            x += ItemWidth + RingGap;
        }

        if (overflow)
        {
            x += ChipGap - RingGap;
            slots.Add(new GlassDockSlot(new Rectangle(x, (Height - 22) / 2, ChipWidth, 22), true));
        }

        return slots;
    }

    private static int ClampMax(int maxVisible) =>
        Math.Clamp(maxVisible <= 0 ? MaxVisibleRings : maxVisible, 1, 8);
}

public static class GlassDockComposer
{
    public static IReadOnlyList<GlassDockRing> FromBoard(UsageBoard board)
    {
        var rings = new List<GlassDockRing>();
        foreach (var provider in board.Providers.OrderBy(item => item.Order).ThenBy(item => item.DisplayName, StringComparer.Ordinal))
        {
            var multi = provider.Accounts.Count > 1;
            for (var index = 0; index < provider.Accounts.Count; index++)
            {
                var account = provider.Accounts[index];
                var window = Tightest(account.Windows);
                var remaining = UsageFormatting.Remaining(window);
                string? balance = remaining is null && account.Credits?.PrepaidBalance is { } money
                    ? UsageFormatting.Money(account.Credits.Currency, money)
                    : null;
                rings.Add(new GlassDockRing(
                    provider.ProviderId,
                    provider.DisplayName,
                    string.IsNullOrWhiteSpace(account.Label) ? provider.DisplayName : account.Label,
                    multi ? index + 1 : 0,
                    remaining,
                    window?.ResetsAt,
                    balance,
                    provider.Accent));
            }
        }

        return rings;
    }

    public static IReadOnlyList<GlassDockRing> FromQuota(
        IReadOnlyList<QuotaCard> cards,
        IReadOnlyList<CodexAccountQuota> codexAccounts)
    {
        var rings = new List<GlassDockRing>();
        if (codexAccounts.Count > 0)
        {
            for (var index = 0; index < codexAccounts.Count; index++)
            {
                var account = codexAccounts[index];
                var window = TightestQuota(account.Windows);
                rings.Add(new GlassDockRing(
                    UsageProviderIds.Codex,
                    "Codex",
                    string.IsNullOrWhiteSpace(account.AccountId) ? "Codex" : account.AccountId,
                    codexAccounts.Count > 1 ? index + 1 : 0,
                    RemainingUsed(window?.UsedPercent),
                    window?.ResetsAt,
                    null,
                    "#10a37f"));
            }
        }

        foreach (var card in cards)
        {
            if (card.Provider == ProviderKind.Codex && codexAccounts.Count > 0) continue;
            var window = TightestQuota(card.Windows);
            var accent = string.IsNullOrWhiteSpace(card.Accent) ? "#10a37f" : card.Accent;
            var balanceText = card.Balance is { TotalBalance: { } amount } balance
                ? UsageFormatting.Money(balance.Currency, amount)
                : null;
            rings.Add(new GlassDockRing(
                card.Provider switch
                {
                    ProviderKind.Claude => UsageProviderIds.Claude,
                    ProviderKind.Codex => UsageProviderIds.Codex,
                    _ => card.Key,
                },
                card.DisplayLabel,
                card.DisplayLabel,
                0,
                RemainingUsed(window?.UsedPercent),
                window?.ResetsAt,
                balanceText,
                accent));
        }

        return rings;
    }

    public static IReadOnlyList<GlassDockProviderGroup> Groups(
        IReadOnlyList<GlassDockRing> rings,
        DateTimeOffset now,
        bool chinese)
    {
        return rings
            .GroupBy(ring => ring.ProviderId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new GlassDockProviderGroup(
                    group.Key,
                    first.Title,
                    first.Accent,
                    group.Select(ring => new GlassDockAccountLine(
                        ring.AccountNumber > 0
                            ? chinese ? $"账号{ring.AccountNumber}" : $"Account {ring.AccountNumber}"
                            : ring.AccountLabel,
                        ring.RemainingPercent,
                        UsageFormatting.Reset(ring.ResetsAt, now, chinese),
                        ring.BalanceText)).ToArray());
            })
            .ToArray();
    }

    public static UsageLedger? FirstLedger(UsageBoard board) =>
        board.Providers.SelectMany(provider => provider.Accounts).Select(account => account.Ledger).FirstOrDefault(ledger => ledger is not null);

    private static UsageWindow? Tightest(IReadOnlyList<UsageWindow> windows) =>
        windows.Where(window => window.UsedPercent is not null)
            .OrderByDescending(window => window.UsedPercent)
            .FirstOrDefault()
        ?? windows.FirstOrDefault();

    private static QuotaWindow? TightestQuota(IReadOnlyList<QuotaWindow> windows) =>
        windows.Where(window => window.UsedPercent is not null)
            .OrderByDescending(window => window.UsedPercent)
            .FirstOrDefault()
        ?? windows.FirstOrDefault();

    private static double? RemainingUsed(double? usedPercent) =>
        usedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;
}
