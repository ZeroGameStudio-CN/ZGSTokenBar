namespace ZGSTokenBar.Core;

public static class CodexConversationDisplay
{
    public static IReadOnlyList<CodexConversationTurn> Select(CodexTokenUsageSummary? usage, DateTimeOffset? now = null)
    {
        if (usage is null) return [];
        return Select(usage.ConversationTurns, usage.LiveOutputRate, now ?? DateTimeOffset.UtcNow);
    }

    public static IReadOnlyList<CodexConversationTurn> Select(IReadOnlyList<CodexConversationTurn> turns,
        LiveOutputRate? liveRate, DateTimeOffset now, QuotaSnapshot? quotaSnapshot = null)
    {
        var rates = liveRate is { } live && now >= live.CapturedAt
            && now - live.CapturedAt <= TimeSpan.FromSeconds(15)
                ? live.Conversations.GroupBy(rate => (rate.SessionId, rate.TurnId))
                    .ToDictionary(group => group.Key, group => group.First())
                : null;
        var selected = turns.Select(turn =>
            {
                var rate = rates?.GetValueOrDefault((turn.SessionId, (string?)turn.TurnId));
                return ReferenceEquals(turn.LiveRate, rate) ? turn : turn with { LiveRate = rate };
            }).Where(HasMetrics).ToArray();
        return quotaSnapshot is null ? selected : AttachInstances(selected, quotaSnapshot);
    }

    private static readonly object HomeAccountsGate = new();
    private static (long InstancesTicks, long AccountsTicks, long AuthTicks, string Home)? _homeAccountsStamp;
    private static IReadOnlyDictionary<string, string> _homeAccounts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static long FileStampTicks(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.LastWriteTimeUtc.Ticks : -1;
    }

    private static IReadOnlyDictionary<string, string> CachedHomeAccountIds()
    {
        var cockpitHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".antigravity_cockpit");
        var codexHome = CodexQuotaService.CodexHome();
        try
        {
            var current = (
                FileStampTicks(Path.Combine(cockpitHome, "codex_instances.json")),
                FileStampTicks(Path.Combine(cockpitHome, "codex_accounts.json")),
                FileStampTicks(Path.Combine(codexHome, "auth.json")),
                codexHome);
            lock (HomeAccountsGate)
            {
                if (_homeAccountsStamp != current)
                {
                    _homeAccounts = CockpitCodexInstanceActivity.ReadHomeAccountIds();
                    _homeAccountsStamp = current;
                }
                return _homeAccounts;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return _homeAccounts;
        }
    }

    public static IReadOnlyList<CodexConversationTurn> AttachInstances(
        IReadOnlyList<CodexConversationTurn> rows,
        QuotaSnapshot snapshot,
        IReadOnlyDictionary<string, string>? homeAccountIdsOverride = null)
    {
        if (rows.Count == 0) return rows;
        var codexCards = snapshot.Cards
            .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
            .ToArray();
        var homeAccounts = homeAccountIdsOverride ?? CachedHomeAccountIds();
        var defaultHome = CockpitCodexInstanceActivity.NormalizeDirectory(CodexQuotaService.CodexHome());
        static double? RemainingOf(IReadOnlyList<QuotaWindow> windows)
        {
            var window = windows.Where(item => item.UsedPercent is not null)
                .OrderByDescending(item => item.UsedPercent)
                .FirstOrDefault();
            return window?.UsedPercent is { } used ? Math.Clamp(100 - used, 0, 100) : null;
        }
        static int AccountOrdinal(QuotaCard card, QuotaCard[] cards)
        {
            if (cards.Length <= 1) return 0;
            var separator = card.Label.IndexOf('·');
            if (separator >= 0 && int.TryParse(card.Label.AsSpan(separator + 1).Trim(), out var number))
                return number;
            return Array.IndexOf(cards, card) + 1;
        }
        var result = new CodexConversationTurn[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var turn = rows[i];
            if (!string.IsNullOrWhiteSpace(turn.InstanceLabel))
            {
                result[i] = turn;
                continue;
            }
            var normalizedHome = CockpitCodexInstanceActivity.NormalizeDirectory(turn.CodexHome);
            var accountId = normalizedHome is not null && homeAccounts.TryGetValue(normalizedHome, out var bound) ? bound : null;
            var quotaIdentity = accountId is null ? null : CodexQuotaService.StableCardKey($"strategy:{accountId}");
            var accountQuota = accountId is null
                ? null
                : snapshot.CodexAccounts.FirstOrDefault(a =>
                    string.Equals(a.AccountId, accountId, StringComparison.Ordinal)
                    || string.Equals(a.QuotaIdentity, quotaIdentity, StringComparison.Ordinal));
            var cardKey = accountQuota?.CardKey
                ?? (accountId is not null ? CodexQuotaService.StableCardKey($"cockpit:{accountId}") : null);
            QuotaCard? card = null;
            if (cardKey is not null)
            {
                card = codexCards.FirstOrDefault(c => string.Equals(c.Key, cardKey, StringComparison.Ordinal));
            }
            if (card is null && normalizedHome is not null && string.Equals(normalizedHome, defaultHome, StringComparison.OrdinalIgnoreCase))
            {
                var nativeKey = CodexQuotaService.StableCardKey("active");
                card = codexCards.FirstOrDefault(c => string.Equals(c.Key, nativeKey, StringComparison.Ordinal))
                    ?? (codexCards.Length == 1 ? codexCards[0] : null);
            }
            if (card is null && codexCards.Length == 1 && accountId is null)
            {
                card = codexCards[0];
            }
            if (card is not null)
            {
                var ordinal = AccountOrdinal(card, codexCards);
                var remaining = RemainingOf(card.Windows);
                var label = ordinal > 0 ? $"Codex · {ordinal}" : "Codex";
                result[i] = turn with { InstanceLabel = label, InstanceNumber = ordinal, InstanceRemainingPercent = remaining };
            }
            else if (accountQuota is not null)
            {
                result[i] = turn with { InstanceLabel = "Codex", InstanceNumber = 0, InstanceRemainingPercent = RemainingOf(accountQuota.Windows) };
            }
            else
            {
                result[i] = turn with { InstanceLabel = "Codex" };
            }
        }
        return result;
    }

    private static bool HasMetrics(CodexConversationTurn turn) => turn.LiveRate is not null || turn.NewInputTokens is not null
        || turn.UsageComplete || turn.OutputTokens > 0 || turn.CachedInputTokens is not null
        || turn.ApiEquivalentUsd is not null || turn.OutputTokensPerSecond is not null || turn.FirstTokenSeconds is not null;

    public static int ColumnMask(IReadOnlyList<CodexConversationTurn> rows)
    {
        var mask = 7; // Name, state and start time are always meaningful for retained rows.
        if (rows.Any(row => row.NewInputTokens is not null)) mask |= 1 << 3;
        if (rows.Any(row => row.UsageComplete || row.OutputTokens > 0)) mask |= 1 << 4;
        if (rows.Any(row => row.CachedInputTokens is not null)) mask |= 1 << 5;
        if (rows.Any(row => row.CacheHitPercent is not null)) mask |= 1 << 6;
        if (rows.Any(row => row.ApiEquivalentUsd is not null)) mask |= 1 << 7;
        if (rows.Any(row => row.OutputTokensPerSecond is { } rate && double.IsFinite(rate))) mask |= 1 << 8;
        if (rows.Any(row => row.FirstTokenSeconds is { } first && double.IsFinite(first))) mask |= 1 << 9;
        if (rows.Any(row => row.LiveRate is not null)) mask |= 1 << 10;
        return mask;
    }
}
