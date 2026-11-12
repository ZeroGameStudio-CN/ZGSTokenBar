namespace ZGSTokenBar.Core;

/// <summary>
/// Antigravity reports several quota groups (agy <c>command.data.groups[]</c>: "Gemini Models",
/// "Claude and GPT models", "Gemini 4 Argon"). The bar ring follows one primary group, chosen in
/// Settings (default: the group whose name contains "argon"); "auto" keeps the most usable group.
/// </summary>
public static class AntigravityGroups
{
    public const string DefaultPrimary = "argon";
    public const string Auto = "auto";

    /// <summary>The group part of a window id/label ("Gemini 4 Argon · 7d" → "Gemini 4 Argon").</summary>
    public static string GroupOf(UsageWindow window)
    {
        var text = string.IsNullOrWhiteSpace(window.Id) ? window.Label : window.Id;
        var dot = text.LastIndexOf(" · ", StringComparison.Ordinal);
        return dot > 0 ? text[..dot] : text;
    }

    /// <summary>Distinct group names in the order Antigravity listed them.</summary>
    public static IReadOnlyList<string> Groups(UsageBoard board) =>
        board.Providers
            .Where(provider => provider.ProviderId == UsageProviderIds.Antigravity)
            .SelectMany(provider => provider.Accounts)
            .SelectMany(account => account.Windows)
            .Select(GroupOf)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>True when <paramref name="window"/> belongs to the group matching <paramref name="preferred"/>.</summary>
    public static bool Matches(UsageWindow window, string? preferred) =>
        !IsAuto(preferred)
        && (GroupOf(window).Contains(preferred!.Trim(), StringComparison.OrdinalIgnoreCase)
            || window.Label.Contains(preferred.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsAuto(string? preferred) =>
        string.IsNullOrWhiteSpace(preferred) || string.Equals(preferred.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Moves the preferred group's windows (tightest first, as the parser orders each group) to the
    /// front, so the ring, its % chip and the hover headline follow that group. Without a match the
    /// parser's order (most usable group first) is kept.
    /// </summary>
    public static IReadOnlyList<UsageWindow> Pin(IReadOnlyList<UsageWindow> windows, string? preferred)
    {
        if (IsAuto(preferred) || windows.Count == 0) return windows;
        var primary = windows.Where(window => Matches(window, preferred)).ToArray();
        if (primary.Length == 0 || primary.Length == windows.Count) return windows;
        var ordered = primary
            .OrderByDescending(window => window.UsedPercent ?? -1)
            .ThenByDescending(window => window.Duration)
            .ToList();
        ordered.AddRange(windows.Where(window => !Matches(window, preferred)));
        return ordered;
    }

    public static UsageBoard Pin(UsageBoard board, string? preferred)
    {
        if (IsAuto(preferred) || board.Providers.All(provider => provider.ProviderId != UsageProviderIds.Antigravity)) return board;
        return board with
        {
            Providers = board.Providers
                .Select(provider => provider.ProviderId != UsageProviderIds.Antigravity
                    ? provider
                    : provider with
                    {
                        Accounts = provider.Accounts
                            .Select(account => account with { Windows = Pin(account.Windows, preferred) })
                            .ToArray(),
                    })
                .ToArray(),
        };
    }

    /// <summary>
    /// True when a group other than the headline's is nearly exhausted (the same red band the rings
    /// use, under 20% left), so the ring's chip can warn even while the primary group is fine.
    /// </summary>
    public static bool OtherGroupLow(IReadOnlyList<UsageWindow> windows)
    {
        var headline = windows.FirstOrDefault(window => window.UsedPercent is not null && !window.Unlimited);
        if (headline is null) return false;
        var group = GroupOf(headline);
        return windows.Any(window => window.UsedPercent is { } used && !window.Unlimited
            && !string.Equals(GroupOf(window), group, StringComparison.OrdinalIgnoreCase)
            && GlassDockLayout.Band(100 - used) == "red");
    }
}
