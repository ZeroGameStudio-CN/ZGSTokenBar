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
    private static (long InstancesTicks, long AccountsTicks, long AuthTicks, long BridgeTicks, long RegisteredAuthTicks, string Home)? _homeAccountsStamp;
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
                FileStampTicks(InteractiveCodexHomes.RegistryPath),
                InteractiveCodexHomes.Read(InteractiveCodexHomes.RegistryPath).Select(home => FileStampTicks(Path.Combine(home, "auth.json")))
                    .Aggregate(0L, (stamp, ticks) => unchecked(stamp * 31 + ticks)),
                codexHome);
            lock (HomeAccountsGate)
            {
                if (_homeAccountsStamp != current)
                {
                    var bindings = new Dictionary<string, string>(CockpitCodexInstanceActivity.ReadHomeAccountIds(), StringComparer.OrdinalIgnoreCase);
                    foreach (var binding in InteractiveCodexHomes.AccountIds(InteractiveCodexHomes.RegistryPath)) bindings[binding.Key] = binding.Value;
                    _homeAccounts = bindings;
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
        IReadOnlyDictionary<string, string>? homeAccountIdsOverride = null,
        IReadOnlyList<string>? instanceHomesOverride = null)
    {
        if (rows.Count == 0) return rows;
        var codexCards = snapshot.Cards
            .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
            .ToArray();
        var homeAccounts = homeAccountIdsOverride ?? CachedHomeAccountIds();
        var instanceHomes = instanceHomesOverride ?? (homeAccountIdsOverride is null
            ? InteractiveCodexHomes.Read(InteractiveCodexHomes.RegistryPath) : []);
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
            var instanceNumber = instanceHomes.Select((home, index) => (Home: CockpitCodexInstanceActivity.NormalizeDirectory(home), Number: index + 1))
                .FirstOrDefault(entry => string.Equals(entry.Home, normalizedHome, StringComparison.OrdinalIgnoreCase)).Number;
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
            if (card is null
                && accountId is null
                && normalizedHome is not null
                && string.Equals(normalizedHome, defaultHome, StringComparison.OrdinalIgnoreCase))
            {
                var nativeKey = CodexQuotaService.StableCardKey("active");
                card = codexCards.FirstOrDefault(c => string.Equals(c.Key, nativeKey, StringComparison.Ordinal));
            }
            if (card is not null)
            {
                var ordinal = AccountOrdinal(card, codexCards);
                var remaining = RemainingOf(card.Windows);
                var label = ordinal > 0 ? $"Codex · {ordinal}" : "Codex";
                result[i] = turn with { InstanceLabel = instanceNumber > 0 ? $"Codex · {instanceNumber}" : label,
                    InstanceNumber = instanceNumber > 0 ? instanceNumber : ordinal, InstanceAccountNumber = ordinal, InstanceRemainingPercent = remaining };
            }
            else if (accountQuota is not null)
            {
                result[i] = turn with { InstanceLabel = instanceNumber > 0 ? $"Codex · {instanceNumber}" : "Codex",
                    InstanceNumber = instanceNumber, InstanceRemainingPercent = RemainingOf(accountQuota.Windows) };
            }
            else
            {
                result[i] = turn with { InstanceLabel = instanceNumber > 0 ? $"Codex · {instanceNumber}" : "Codex", InstanceNumber = instanceNumber };
            }
        }
        return result;
    }

    public static IReadOnlyList<CodexConversationTurn> ActivityRows(
        IReadOnlyList<CodexConversationTurn> conversations,
        int maximumRows = 10)
    {
        if (conversations.Count == 0 || maximumRows <= 0) return [];
        var ranked = conversations
            .Where(turn => turn.LiveRate is { Total: > 0 } || turn.State == "running")
            .OrderByDescending(turn => turn.State == "running")
            .ThenByDescending(ActivitySpeed)
            .ThenByDescending(turn => turn.ObservedAt)
            .ToArray();
        var selected = new List<CodexConversationTurn>(Math.Min(maximumRows, Math.Max(ranked.Length, 1)));
        var externalReps = new List<CodexConversationTurn>();
        var seenExternalProviders = new HashSet<string>(StringComparer.Ordinal);
        var hasCodex = false;
        foreach (var turn in ranked)
        {
            if (turn.ProviderId is { Length: > 0 } providerId
                && !string.Equals(providerId, ExternalProviderTokenLedger.CodexProviderId, StringComparison.Ordinal))
            {
                if (seenExternalProviders.Add(providerId)) externalReps.Add(turn);
            }
            else
            {
                hasCodex = true;
            }
        }

        var reservedExternal = Math.Min(externalReps.Count, Math.Max(0, maximumRows - (hasCodex ? 1 : 0)));
        var codexCap = Math.Max(0, maximumRows - reservedExternal);
        var seenInstances = new HashSet<int>();
        foreach (var turn in ranked)
        {
            if (selected.Count >= codexCap) break;
            if (turn.ProviderId is { Length: > 0 } providerId
                && !string.Equals(providerId, ExternalProviderTokenLedger.CodexProviderId, StringComparison.Ordinal))
            {
                continue;
            }
            if (turn.InstanceNumber > 0 && seenInstances.Add(turn.InstanceNumber)) selected.Add(turn);
        }

        for (var i = 0; i < reservedExternal && selected.Count < maximumRows; i++)
        {
            selected.Add(externalReps[i]);
        }

        if (selected.Count < maximumRows)
        {
            var chosen = new HashSet<CodexConversationTurn>(selected);
            foreach (var turn in ranked)
            {
                if (chosen.Add(turn)) selected.Add(turn);
                if (selected.Count == maximumRows) break;
            }
        }

        selected.Sort(CompareActivity);
        if (selected.Count < maximumRows)
        {
            var chosen = new HashSet<CodexConversationTurn>(selected);
            foreach (var turn in conversations.OrderByDescending(turn => turn.ObservedAt))
            {
                if (!chosen.Add(turn)) continue;
                selected.Add(turn);
                if (selected.Count == maximumRows) break;
            }
        }

        return selected;
    }

    private static int CompareActivity(CodexConversationTurn left, CodexConversationTurn right)
    {
        var running = (right.State == "running").CompareTo(left.State == "running");
        if (running != 0) return running;
        var speed = ActivitySpeed(right).CompareTo(ActivitySpeed(left));
        if (speed != 0) return speed;
        return right.ObservedAt.CompareTo(left.ObservedAt);
    }

    private static double ActivitySpeed(CodexConversationTurn turn) => turn.LiveRate?.Measured ?? 0;

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
