using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

internal sealed record CodexRolloutRateLimitWindow(
    double UsedPercent,
    int WindowMinutes,
    DateTimeOffset? ResetsAt);

internal sealed record CodexRolloutRateLimitEvent(
    DateTimeOffset CapturedAt,
    CodexRolloutRateLimitWindow? Primary,
    CodexRolloutRateLimitWindow? Secondary,
    long? TotalTokens = null,
    string? SourceKey = null);

public sealed record CodexRolloutImportResult(
    IReadOnlyList<QuotaRateSample> Samples,
    int CandidateFiles,
    int ScannedFiles,
    int ParsedLines,
    int AcceptedEvents,
    int AcceptedChains,
    int AmbiguousChains,
    int OversizedLines)
{
    public IReadOnlyList<CodexQuotaTokenObservation> Observations { get; init; } = [];
}

public static class CodexRolloutQuotaImporter
{
    internal const int MaximumFiles = 64;
    internal const int MaximumLineBytes = 256 * 1024;
    internal const long MaximumBytesPerFile = 8L * 1024 * 1024;
    internal const long MaximumTotalBytes = 64L * 1024 * 1024;
    internal static readonly TimeSpan CandidateFileAge = TimeSpan.FromHours(3);
    internal static readonly TimeSpan EventAge = TimeSpan.FromHours(2);
    internal static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(2);
    internal const int WindowMinutesTolerance = 10;
    internal const double UsedPercentTolerance = 1;

    public static CodexRolloutImportResult Import(
        QuotaSnapshot liveSnapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Import(
            CodexQuotaService.CodexHome(),
            CockpitCodexInstanceActivity.ReadRolloutSources(),
            liveSnapshot,
            now,
            cancellationToken);

    internal static CodexRolloutImportResult Import(
        string codexHome,
        IReadOnlyList<CockpitCodexRolloutSource> cockpitSources,
        QuotaSnapshot liveSnapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var combined = Import(
            codexHome,
            liveSnapshot,
            now,
            cancellationToken);
        foreach (var source in cockpitSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var card = ResolveScopedCard(source, liveSnapshot, now);
            if (card is null) continue;

            var scopedSnapshot = liveSnapshot with { Cards = [card] };
            var scoped = Import(
                source.CodexHome,
                scopedSnapshot,
                now,
                cancellationToken,
                card.Key);
            combined = PreferScopedResult(combined, scoped, card.Key);
        }
        return combined;
    }

    internal static CodexRolloutImportResult Import(
        string codexHome,
        QuotaSnapshot liveSnapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Import(codexHome, liveSnapshot, now, cancellationToken, null);

    private static CodexRolloutImportResult Import(
        string codexHome,
        QuotaSnapshot liveSnapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? sourceNamespace)
    {
        var candidates = CandidateFiles(codexHome, now)
            .Take(MaximumFiles)
            .ToArray();
        var events = new List<CodexRolloutRateLimitEvent>();
        var parsedLines = 0;
        var oversizedLines = 0;
        var scannedFiles = 0;
        long remainingBytes = MaximumTotalBytes;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remainingBytes <= 0) break;
            var byteLimit = Math.Min(Math.Min(candidate.Length, MaximumBytesPerFile), remainingBytes);
            if (byteLimit <= 0) continue;
            try
            {
                var scan = ScanFile(
                    candidate.FullName,
                    byteLimit,
                    SourceKey(candidate, sourceNamespace),
                    cancellationToken);
                scannedFiles++;
                remainingBytes -= scan.BytesRead;
                parsedLines += scan.ParsedLines;
                oversizedLines += scan.OversizedLines;
                events.AddRange(scan.Events);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Rollout history is a best-effort source; one unreadable file must not block quota.
            }
        }

        var acceptedEvents = events
            .Where(item => item.CapturedAt >= now - EventAge)
            .Where(item => item.CapturedAt <= now.AddMinutes(1))
            .GroupBy(EventKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.TotalTokens ?? -1)
                .ThenByDescending(item => item.CapturedAt)
                .First())
            .OrderBy(item => item.CapturedAt)
            .ToArray();
        var matched = Match(acceptedEvents, liveSnapshot, now);
        return matched with
        {
            CandidateFiles = candidates.Length,
            ScannedFiles = scannedFiles,
            ParsedLines = parsedLines,
            AcceptedEvents = acceptedEvents.Length,
            OversizedLines = oversizedLines,
        };
    }

    private static CodexRolloutImportResult PreferScopedResult(
        CodexRolloutImportResult combined,
        CodexRolloutImportResult scoped,
        string cardKey)
    {
        var hasScopedMatch = scoped.Samples.Count > 0 || scoped.Observations.Count > 0;
        var samples = (hasScopedMatch
                ? combined.Samples.Where(sample =>
                    !string.Equals(sample.CardKey, cardKey, StringComparison.Ordinal))
                : combined.Samples)
            .Concat(scoped.Samples)
            .GroupBy(sample =>
                $"{sample.CardKey}\0{sample.WindowLabel}\0{sample.DurationTicks}\0{sample.CapturedAt:O}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(sample => sample.CapturedAt)
            .ToArray();
        var observations = (hasScopedMatch
                ? combined.Observations.Where(observation =>
                    !string.Equals(observation.CardKey, cardKey, StringComparison.Ordinal))
                : combined.Observations)
            .Concat(scoped.Observations)
            .GroupBy(ObservationKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.TotalTokens).First())
            .OrderBy(observation => observation.CapturedAt)
            .ToArray();
        return new CodexRolloutImportResult(
            samples,
            SaturatingAdd(combined.CandidateFiles, scoped.CandidateFiles),
            SaturatingAdd(combined.ScannedFiles, scoped.ScannedFiles),
            SaturatingAdd(combined.ParsedLines, scoped.ParsedLines),
            SaturatingAdd(combined.AcceptedEvents, scoped.AcceptedEvents),
            SaturatingAdd(combined.AcceptedChains, scoped.AcceptedChains),
            SaturatingAdd(combined.AmbiguousChains, scoped.AmbiguousChains),
            SaturatingAdd(combined.OversizedLines, scoped.OversizedLines))
        {
            Observations = observations,
        };
    }

    private static QuotaCard? ResolveScopedCard(
        CockpitCodexRolloutSource source,
        QuotaSnapshot snapshot,
        DateTimeOffset now)
    {
        var cards = FreshCodexCards(snapshot, now);
        var direct = cards
            .Where(card => string.Equals(card.Key, source.CardKey, StringComparison.Ordinal))
            .ToArray();
        if (direct.Length == 1) return direct[0];
        if (direct.Length > 1) return null;

        var accounts = snapshot.CodexAccounts
            .Where(account => string.Equals(account.AccountId, source.AccountId, StringComparison.Ordinal))
            .Where(account => string.IsNullOrWhiteSpace(account.Error))
            .Where(account => account.CapturedAt is { } capturedAt
                && QuotaPaceTracker.IsFreshCapture(capturedAt, snapshot.CapturedAt, now))
            .Where(account => account.Windows.Any(IsImportableLiveWindow))
            .ToArray();
        if (accounts.Length != 1) return null;

        var matches = cards
            .Where(card => AccountWindowsMatchCard(accounts[0].Windows, card.Windows))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool AccountWindowsMatchCard(
        IReadOnlyList<QuotaWindow> accountWindows,
        IReadOnlyList<QuotaWindow> cardWindows)
    {
        var comparable = accountWindows.Where(IsImportableLiveWindow).ToArray();
        return comparable.Length > 0
            && comparable.All(accountWindow => cardWindows.Count(cardWindow =>
                IsImportableLiveWindow(cardWindow)
                && SameWindowIdentity(accountWindow, cardWindow)
                && Math.Abs(accountWindow.UsedPercent!.Value - cardWindow.UsedPercent!.Value)
                    <= UsedPercentTolerance) == 1);
    }

    internal static CodexRolloutImportResult Match(
        IReadOnlyList<CodexRolloutRateLimitEvent> events,
        QuotaSnapshot liveSnapshot,
        DateTimeOffset now)
    {
        var cards = FreshCodexCards(liveSnapshot, now);
        if (cards.Count == 0 || events.Count == 0)
        {
            return EmptyResult();
        }

        var groups = BuildGroups(events);
        var chains = groups
            .Where(group => !group.Ambiguous)
            .Select(group => NormalizeChain(group.Events))
            .Where(chain => chain is not null)
            .Cast<CandidateChain>()
            .ToArray();
        if (chains.Length == 0) return EmptyResult();

        var edges = new Dictionary<int, List<int>>();
        for (var chainIndex = 0; chainIndex < chains.Length; chainIndex++)
        {
            for (var cardIndex = 0; cardIndex < cards.Count; cardIndex++)
            {
                if (!Matches(chains[chainIndex], cards[cardIndex])) continue;
                if (!edges.TryGetValue(chainIndex, out var targets))
                {
                    targets = [];
                    edges[chainIndex] = targets;
                }
                targets.Add(cardIndex);
            }
        }

        var assignments = new Dictionary<int, int>();
        var ambiguousChains = groups.Count(group => group.Ambiguous)
            + Enumerable.Range(0, chains.Length).Count(index => !edges.ContainsKey(index));
        var remainingChains = edges.Keys.ToHashSet();
        while (remainingChains.Count > 0)
        {
            var componentChains = new HashSet<int>();
            var componentCards = new HashSet<int>();
            var pendingChains = new Queue<int>();
            pendingChains.Enqueue(remainingChains.First());
            while (pendingChains.Count > 0)
            {
                var chainIndex = pendingChains.Dequeue();
                if (!componentChains.Add(chainIndex)) continue;
                remainingChains.Remove(chainIndex);
                foreach (var cardIndex in edges[chainIndex])
                {
                    if (!componentCards.Add(cardIndex)) continue;
                    foreach (var neighbor in edges.Where(pair => pair.Value.Contains(cardIndex)).Select(pair => pair.Key))
                    {
                        if (!componentChains.Contains(neighbor)) pendingChains.Enqueue(neighbor);
                    }
                }
            }

            var unique = UniqueFullAssignment(componentChains, edges);
            if (unique is null)
            {
                ambiguousChains += componentChains.Count;
                continue;
            }
            foreach (var pair in unique) assignments[pair.Key] = pair.Value;
        }

        var samples = new List<QuotaRateSample>();
        var observations = new List<CodexQuotaTokenObservation>();
        foreach (var assignment in assignments)
        {
            var chain = chains[assignment.Key];
            var card = cards[assignment.Value];
            AddSamples(samples, observations, chain, card);
        }

        var deduplicated = samples
            .GroupBy(sample =>
                $"{sample.CardKey}\0{sample.WindowLabel}\0{sample.DurationTicks}\0{sample.CapturedAt:O}",
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(sample => sample.CapturedAt)
            .ToArray();
        var deduplicatedObservations = observations
            .GroupBy(ObservationKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.TotalTokens)
                .First())
            .OrderBy(item => item.CapturedAt)
            .ToArray();
        return new CodexRolloutImportResult(
            deduplicated,
            0,
            0,
            0,
            events.Count,
            assignments.Count,
            ambiguousChains,
            0)
        {
            Observations = deduplicatedObservations,
        };
    }

    internal static bool TryParseLine(string line, out CodexRolloutRateLimitEvent? item)
    {
        item = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    timestampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var capturedAt)
                || !root.TryGetProperty("type", out var outerType)
                || outerType.ValueKind != JsonValueKind.String
                || !string.Equals(outerType.GetString(), "event_msg", StringComparison.Ordinal)
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("type", out var payloadType)
                || payloadType.ValueKind != JsonValueKind.String
                || !string.Equals(payloadType.GetString(), "token_count", StringComparison.Ordinal)
                || !payload.TryGetProperty("rate_limits", out var limits)
                || limits.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var primary = limits.TryGetProperty("primary", out var primaryElement)
                ? ParseWindow(primaryElement)
                : null;
            var secondary = limits.TryGetProperty("secondary", out var secondaryElement)
                ? ParseWindow(secondaryElement)
                : null;
            if (primary is null && secondary is null) return false;
            long? totalTokens = null;
            if (payload.TryGetProperty("info", out var info)
                && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("total_token_usage", out var totalUsage)
                && totalUsage.ValueKind == JsonValueKind.Object
                && totalUsage.TryGetProperty("total_tokens", out var totalTokensElement)
                && totalTokensElement.ValueKind == JsonValueKind.Number
                && totalTokensElement.TryGetInt64(out var parsedTotalTokens)
                && parsedTotalTokens >= 0)
            {
                totalTokens = parsedTotalTokens;
            }

            item = new CodexRolloutRateLimitEvent(capturedAt, primary, secondary, totalTokens);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<QuotaCard> FreshCodexCards(
        QuotaSnapshot snapshot,
        DateTimeOffset now)
    {
        var freshHealth = snapshot.Health.Any(health =>
            health.Provider == ProviderKind.Codex
            && health.Connected
            && health.Code is ProviderHealthCode.Current or ProviderHealthCode.Unknown);
        if (!freshHealth) return [];
        return snapshot.Cards
            .Where(card => card.Provider == ProviderKind.Codex)
            .Where(card => card.CapturedAt is { } capturedAt
                && QuotaPaceTracker.IsFreshCapture(capturedAt, snapshot.CapturedAt, now))
            .Where(card => card.Windows.Any(IsImportableLiveWindow))
            .ToArray();
    }

    private static IReadOnlyList<FileInfo> CandidateFiles(string codexHome, DateTimeOffset now)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        foreach (var directoryName in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(codexHome, directoryName);
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
                {
                    try
                    {
                        var file = new FileInfo(path);
                        if (file.Exists
                            && file.Length > 0
                            && file.LastWriteTimeUtc >= (now - CandidateFileAge).UtcDateTime)
                        {
                            var key = SourceKey(file, null);
                            if (!files.TryGetValue(key, out var previous)
                                || file.Length > previous.Length
                                || file.Length == previous.Length
                                && file.LastWriteTimeUtc > previous.LastWriteTimeUtc)
                            {
                                files[key] = file;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore files that disappear or become unreadable during discovery.
                    }
                }
            }
            catch
            {
                // A missing or unreadable rollout directory is a normal fallback state.
            }
        }

        return files.Values
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static FileScanResult ScanFile(
        string path,
        long byteLimit,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var start = Math.Max(0, stream.Length - byteLimit);
        stream.Seek(start, SeekOrigin.Begin);
        var discardPartialLine = start > 0;
        var readBuffer = new byte[64 * 1024];
        using var lineBuffer = new MemoryStream(Math.Min(MaximumLineBytes, 64 * 1024));
        var lineOversized = false;
        long bytesRead = 0;
        var parsedLines = 0;
        var oversizedLines = 0;
        var events = new List<CodexRolloutRateLimitEvent>();

        while (bytesRead < byteLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(readBuffer.Length, byteLimit - bytesRead);
            var count = stream.Read(readBuffer, 0, requested);
            if (count <= 0) break;
            bytesRead += count;
            for (var index = 0; index < count; index++)
            {
                var value = readBuffer[index];
                if (value == (byte)'\n')
                {
                    if (discardPartialLine)
                    {
                        discardPartialLine = false;
                    }
                    else if (lineOversized)
                    {
                        oversizedLines++;
                    }
                    else if (lineBuffer.Length > 0)
                    {
                        parsedLines++;
                        ParseBufferedLine(lineBuffer, sourceKey, events);
                    }
                    lineBuffer.SetLength(0);
                    lineOversized = false;
                    continue;
                }

                if (discardPartialLine || lineOversized) continue;
                if (lineBuffer.Length >= MaximumLineBytes)
                {
                    lineBuffer.SetLength(0);
                    lineOversized = true;
                    continue;
                }
                lineBuffer.WriteByte(value);
            }
        }

        if (!discardPartialLine)
        {
            if (lineOversized)
            {
                oversizedLines++;
            }
            else if (lineBuffer.Length > 0)
            {
                parsedLines++;
                ParseBufferedLine(lineBuffer, sourceKey, events);
            }
        }

        return new FileScanResult(events, bytesRead, parsedLines, oversizedLines);
    }

    private static void ParseBufferedLine(
        MemoryStream lineBuffer,
        string sourceKey,
        ICollection<CodexRolloutRateLimitEvent> events)
    {
        var span = lineBuffer.GetBuffer().AsSpan(0, (int)lineBuffer.Length);
        if (span.Length > 0 && span[^1] == (byte)'\r') span = span[..^1];
        if (span.Length == 0
            || span.IndexOf("\"token_count\""u8) < 0
            || span.IndexOf("\"rate_limits\""u8) < 0)
        {
            return;
        }
        var line = Encoding.UTF8.GetString(span);
        if (TryParseLine(line, out var item) && item is not null)
        {
            events.Add(item with { SourceKey = sourceKey });
        }
    }

    private static CodexRolloutRateLimitWindow? ParseWindow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("used_percent", out var usedElement)
            || usedElement.ValueKind != JsonValueKind.Number
            || !usedElement.TryGetDouble(out var used)
            || !double.IsFinite(used)
            || used is < 0 or > 100
            || !element.TryGetProperty("window_minutes", out var minutesElement)
            || minutesElement.ValueKind != JsonValueKind.Number
            || !minutesElement.TryGetDouble(out var minutesValue)
            || !double.IsFinite(minutesValue)
            || minutesValue <= 0
            || minutesValue > 525_600)
        {
            return null;
        }

        var minutes = (int)Math.Round(minutesValue);
        DateTimeOffset? resetsAt = null;
        if (element.TryGetProperty("resets_at", out var resetElement)
            && resetElement.ValueKind == JsonValueKind.Number
            && resetElement.TryGetDouble(out var resetValue)
            && double.IsFinite(resetValue)
            && resetValue > 0)
        {
            try
            {
                var timestamp = (long)resetValue;
                resetsAt = timestamp > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                    : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return new CodexRolloutRateLimitWindow(used, minutes, resetsAt);
    }

    private static List<EventGroup> BuildGroups(IReadOnlyList<CodexRolloutRateLimitEvent> events)
    {
        var groups = new List<EventGroup>();
        foreach (var item in events.OrderBy(item => item.CapturedAt))
        {
            var compatible = groups
                .Where(group => CompatibleCycle(group.Anchor, item))
                .ToArray();
            if (compatible.Length == 0)
            {
                groups.Add(new EventGroup(item));
                continue;
            }
            if (compatible.Length > 1)
            {
                foreach (var group in compatible) group.Ambiguous = true;
                continue;
            }
            compatible[0].Events.Add(item);
        }
        return groups;
    }

    private static CandidateChain? NormalizeChain(IReadOnlyList<CodexRolloutRateLimitEvent> source)
    {
        var normalized = new List<CodexRolloutRateLimitEvent>();
        foreach (var group in source
                     .OrderBy(item => item.CapturedAt)
                     .GroupBy(item => item.CapturedAt))
        {
            var atTime = group.ToArray();
            if (atTime.Skip(1).Any(item => !EquivalentUsage(atTime[0], item))) return null;
            normalized.Add(atTime[0]);
        }
        if (normalized.Count < 2) return null;

        for (var index = 1; index < normalized.Count; index++)
        {
            if (!CompatibleCycle(normalized[0], normalized[index])
                || DecreasedTooFar(normalized[index - 1].Primary, normalized[index].Primary)
                || DecreasedTooFar(normalized[index - 1].Secondary, normalized[index].Secondary))
            {
                return null;
            }
        }
        return new CandidateChain(normalized, source);
    }

    private static bool Matches(CandidateChain chain, QuotaCard card)
    {
        if (card.CapturedAt is not { } liveCapturedAt
            || chain.Events[^1].CapturedAt > liveCapturedAt.AddMinutes(1))
        {
            return false;
        }

        var liveWindows = card.Windows
            .Where(IsImportableLiveWindow)
            .Where(IsSupportedRolloutWindow)
            .ToArray();
        if (liveWindows.Length == 0) return false;

        foreach (var liveWindow in liveWindows)
        {
            var anchorWindow = FindWindow(chain.Anchor, liveWindow);
            if (anchorWindow is null || !MatchesWindow(anchorWindow, liveWindow)) return false;
            if (chain.Events.Any(item =>
                    FindWindow(item, liveWindow) is not { } imported
                    || imported.UsedPercent > liveWindow.UsedPercent + UsedPercentTolerance))
            {
                return false;
            }
        }
        return true;
    }

    private static Dictionary<int, int>? UniqueFullAssignment(
        IReadOnlySet<int> componentChains,
        IReadOnlyDictionary<int, List<int>> edges)
    {
        var ordered = componentChains
            .OrderBy(chain => edges[chain].Count)
            .ThenBy(chain => chain)
            .ToArray();
        Dictionary<int, int>? unique = null;
        var solutionCount = 0;
        var current = new Dictionary<int, int>();
        var usedCards = new HashSet<int>();

        void Search(int position)
        {
            if (solutionCount > 1) return;
            if (position == ordered.Length)
            {
                solutionCount++;
                if (solutionCount == 1) unique = new Dictionary<int, int>(current);
                return;
            }

            var chain = ordered[position];
            foreach (var card in edges[chain])
            {
                if (!usedCards.Add(card)) continue;
                current[chain] = card;
                Search(position + 1);
                current.Remove(chain);
                usedCards.Remove(card);
            }
        }

        Search(0);
        return solutionCount == 1 ? unique : null;
    }

    private static void AddSamples(
        ICollection<QuotaRateSample> samples,
        ICollection<CodexQuotaTokenObservation> observations,
        CandidateChain chain,
        QuotaCard card)
    {
        foreach (var liveWindow in card.Windows
                     .Where(IsImportableLiveWindow)
                     .Where(IsSupportedRolloutWindow))
        {
            foreach (var item in chain.Events)
            {
                if (FindWindow(item, liveWindow) is { } imported)
                {
                    samples.Add(ToSample(card, liveWindow, item.CapturedAt, imported));
                }
            }
            foreach (var item in chain.SourceEvents)
            {
                if (item.TotalTokens is not { } totalTokens
                    || string.IsNullOrWhiteSpace(item.SourceKey)
                    || FindWindow(item, liveWindow) is not { } imported)
                {
                    continue;
                }

                observations.Add(new CodexQuotaTokenObservation(
                    card.Key,
                    QuotaPaceTracker.NormalizeWindowLabel(liveWindow),
                    liveWindow.Duration.Ticks,
                    item.CapturedAt,
                    imported.UsedPercent,
                    liveWindow.ResetsAt,
                    item.SourceKey,
                    totalTokens));
            }
        }
    }

    private static QuotaRateSample ToSample(
        QuotaCard card,
        QuotaWindow liveWindow,
        DateTimeOffset capturedAt,
        CodexRolloutRateLimitWindow imported) =>
        new(
            card.Key,
            QuotaPaceTracker.NormalizeWindowLabel(liveWindow),
            liveWindow.Duration.Ticks,
            capturedAt,
            imported.UsedPercent,
            imported.ResetsAt,
            QuotaRateSampleSource.CodexRollout);

    private static bool IsImportableLiveWindow(QuotaWindow window) =>
        window.UsedPercent is { } used
        && double.IsFinite(used)
        && used is >= 0 and <= 100
        && window.ResetsAt is not null
        && window.Duration > TimeSpan.Zero;

    private static bool IsSupportedRolloutWindow(QuotaWindow window) =>
        Math.Abs(window.Duration.TotalMinutes - 300) <= WindowMinutesTolerance
        || Math.Abs(window.Duration.TotalMinutes - 10080) <= WindowMinutesTolerance;

    private static CodexRolloutRateLimitWindow? FindWindow(
        CodexRolloutRateLimitEvent item,
        QuotaWindow live)
    {
        var primaryMatches = item.Primary is { } primary && SameWindowIdentity(primary, live);
        var secondaryMatches = item.Secondary is { } secondary && SameWindowIdentity(secondary, live);
        if (primaryMatches == secondaryMatches) return null;
        return primaryMatches ? item.Primary : item.Secondary;
    }

    private static bool SameWindowIdentity(
        CodexRolloutRateLimitWindow imported,
        QuotaWindow live) =>
        imported.ResetsAt is { } importedReset
        && live.ResetsAt is { } liveReset
        && Math.Abs(imported.WindowMinutes - live.Duration.TotalMinutes) <= WindowMinutesTolerance
        && (importedReset - liveReset).Duration() <= ResetTolerance;

    private static bool SameWindowIdentity(
        QuotaWindow left,
        QuotaWindow right) =>
        left.ResetsAt is { } leftReset
        && right.ResetsAt is { } rightReset
        && Math.Abs(left.Duration.TotalMinutes - right.Duration.TotalMinutes) <= WindowMinutesTolerance
        && (leftReset - rightReset).Duration() <= ResetTolerance;

    private static bool MatchesWindow(
        CodexRolloutRateLimitWindow imported,
        QuotaWindow live) =>
        live.UsedPercent is { } liveUsed
        && SameWindowIdentity(imported, live)
        && Math.Abs(imported.UsedPercent - liveUsed) <= UsedPercentTolerance;

    private static bool CompatibleCycle(
        CodexRolloutRateLimitEvent left,
        CodexRolloutRateLimitEvent right) =>
        CompatibleWindow(left.Primary, right.Primary)
        && CompatibleWindow(left.Secondary, right.Secondary);

    private static bool CompatibleWindow(
        CodexRolloutRateLimitWindow? left,
        CodexRolloutRateLimitWindow? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (Math.Abs(left.WindowMinutes - right.WindowMinutes) > WindowMinutesTolerance) return false;
        if (left.ResetsAt is null || right.ResetsAt is null)
        {
            return left.ResetsAt is null && right.ResetsAt is null;
        }
        return (left.ResetsAt.Value - right.ResetsAt.Value).Duration() <= ResetTolerance;
    }

    private static bool EquivalentUsage(
        CodexRolloutRateLimitEvent left,
        CodexRolloutRateLimitEvent right) =>
        EquivalentWindowUsage(left.Primary, right.Primary)
        && EquivalentWindowUsage(left.Secondary, right.Secondary);

    private static bool EquivalentWindowUsage(
        CodexRolloutRateLimitWindow? left,
        CodexRolloutRateLimitWindow? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return Math.Abs(left.UsedPercent - right.UsedPercent) <= UsedPercentTolerance;
    }

    private static bool DecreasedTooFar(
        CodexRolloutRateLimitWindow? previous,
        CodexRolloutRateLimitWindow? current) =>
        previous is not null
        && current is not null
        && current.UsedPercent < previous.UsedPercent - QuotaPaceTracker.ResetDropThreshold;

    private static string EventKey(CodexRolloutRateLimitEvent item) =>
        string.Join(
            '\0',
            item.SourceKey ?? "-",
            item.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            WindowKey(item.Primary),
            WindowKey(item.Secondary));

    private static string ObservationKey(CodexQuotaTokenObservation item) =>
        string.Join(
            '\0',
            item.CardKey,
            item.WindowLabel,
            item.DurationTicks.ToString(CultureInfo.InvariantCulture),
            item.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            item.SourceKey);

    private static string SourceKey(FileInfo file, string? sourceNamespace) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            sourceNamespace is null
                ? file.Name
                : $"{sourceNamespace}\0{file.Name}"))).ToLowerInvariant();

    private static int SaturatingAdd(int left, int right) =>
        right > int.MaxValue - left ? int.MaxValue : left + right;

    private static string WindowKey(CodexRolloutRateLimitWindow? window) => window is null
        ? "-"
        : string.Join(
            ':',
            window.WindowMinutes.ToString(CultureInfo.InvariantCulture),
            window.UsedPercent.ToString("R", CultureInfo.InvariantCulture),
            window.ResetsAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-");

    private static CodexRolloutImportResult EmptyResult() =>
        new([], 0, 0, 0, 0, 0, 0, 0);

    private sealed class EventGroup(CodexRolloutRateLimitEvent anchor)
    {
        public CodexRolloutRateLimitEvent Anchor { get; } = anchor;
        public List<CodexRolloutRateLimitEvent> Events { get; } = [anchor];
        public bool Ambiguous { get; set; }
    }

    private sealed record CandidateChain(
        IReadOnlyList<CodexRolloutRateLimitEvent> Events,
        IReadOnlyList<CodexRolloutRateLimitEvent> SourceEvents)
    {
        public CodexRolloutRateLimitEvent Anchor => Events[^1];
    }

    private sealed record FileScanResult(
        IReadOnlyList<CodexRolloutRateLimitEvent> Events,
        long BytesRead,
        int ParsedLines,
        int OversizedLines);
}
