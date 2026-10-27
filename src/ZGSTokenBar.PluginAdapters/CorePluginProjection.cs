using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.PluginAdapters;

public static class CorePluginProjection
{
    public static PluginDataSnapshot Provider(
        string pluginId,
        string groupId,
        string iconKey,
        string accentToken,
        ProviderResult result)
    {
        var cards = result.Cards.Select((card, index) =>
        {
            var summary = card.Windows
                .Take(8)
                .Select(window => new ContributionSummaryItem(
                    StableKey(window.Label, "quota.window"),
                    new ContributionValue("percent", Number: window.UsedPercent),
                    window.ResetsAt is null ? null : "resets"))
                .ToList();
            if (card.Balance is { } balance)
            {
                summary.Insert(0, new(
                    "balance.total",
                    new ContributionValue(
                        "currency",
                        Text: balance.Currency,
                        Decimal: balance.TotalBalance),
                    balance.Status.ToString().ToLowerInvariant()));
            }
            return new MiniCardContribution(
                $"card.{StableToken(card.Key, index)}",
                pluginId,
                groupId,
                card.Balance is null ? ContributionKind.Quota : ContributionKind.Balance,
                index,
                StableKey(card.Label, "provider.title"),
                iconKey,
                accentToken,
                summary);
        }).ToArray();

        var details = result.Cards.Select((card, index) =>
            new DetailContribution(
                $"detail.{StableToken(card.Key, index)}",
                pluginId,
                [
                    new(
                        $"section.{StableToken(card.Key, index)}",
                        "provider.details",
                        index,
                        card.Windows.Select(window => new DetailRowContribution(
                            StableKey(window.Label, "quota.window"),
                            new ContributionValue("percent", Number: window.UsedPercent),
                            window.ResetsAt is null ? null : "resets",
                            card.CapturedAt)).ToArray()),
                ])).ToArray();

        return new(
            pluginId,
            result.Cards.Select(card => card.CapturedAt).Where(value => value is not null).Max()
                ?? DateTimeOffset.UtcNow,
            Health(result.Health),
            cards,
            details,
            []);
    }

    public static PluginDataSnapshot CodexUsage(
        string pluginId,
        CodexTokenUsageSummary? summary,
        DateTimeOffset now,
        bool cached = false)
    {
        var health = summary is null
            ? new PluginHealth(
                PluginHealthCode.Waiting,
                false,
                true,
                now,
                "codex.usage.waiting")
            : new PluginHealth(
                cached ? PluginHealthCode.Cached : PluginHealthCode.Current,
                true,
                false,
                summary.CapturedAt,
                cached ? "codex.usage.cached" : "codex.usage.current");
        if (summary is null) return new(pluginId, now, health, [], [], []);

        var rows = new[]
        {
            new ContributionSummaryItem("usage.tokens.today", new("integer", Integer: summary.TodayTokens)),
            new ContributionSummaryItem("usage.tokens.total", new("integer", Integer: summary.LocalTokens)),
            new ContributionSummaryItem("usage.cache.today", new("percent", Number: summary.TodayCacheHitPercent)),
            new ContributionSummaryItem("usage.cache.total", new("percent", Number: summary.TotalCacheHitPercent)),
            SpendRow("usage.cost.today", summary.TodaySpend),
            SpendRow("usage.cost.yesterday", summary.YesterdaySpend),
            SpendRow("usage.cost.30d", summary.Last30DaysSpend),
            new ContributionSummaryItem("usage.conversations.pending", new("integer", Integer: summary.PendingConversationCount)),
        };
        return new(
            pluginId,
            summary.CapturedAt,
            health,
            [
                new(
                    "card.codex.local-usage",
                    pluginId,
                    "codex",
                    ContributionKind.Metric,
                    0,
                    "codex.usage.title",
                    "provider.codex.icon",
                    "accent.codex",
                    rows),
            ],
            [
                new(
                    "detail.codex.local-usage",
                    pluginId,
                    [
                        new(
                            "section.codex.local-usage",
                            "codex.usage.details",
                            0,
                            rows.Select(row => new DetailRowContribution(
                                row.LabelKey,
                                row.Value,
                                row.Status,
                                summary.CapturedAt)).ToArray()),
                    ]),
                .. summary.RecentTurns.Take(12).Select((turn, index) => ConversationDetail(pluginId, turn, index)),
            ],
            []);
    }

    private static DetailContribution ConversationDetail(string pluginId, CodexConversationTurn turn, int index) =>
        new($"detail.codex.turn.{index}", pluginId,
        [
            new($"section.codex.turn.{index}", "codex.usage.turn", index,
            [
                new("usage.turn.session", new("text", Text: turn.SessionId), turn.State, turn.ObservedAt),
                new("usage.turn.id", new("text", Text: turn.TurnId)),
                new("usage.turn.project", new("text", Text: turn.ProjectName)),
                new("usage.turn.title", new("text", Text: turn.Title)),
                new("usage.turn.service-tier", new("text", Text: turn.ServiceTier)),
                new("usage.turn.model", new("text", Text: turn.Model)),
                new("usage.turn.effort", new("text", Text: turn.Effort)),
                new("usage.turn.output", new("integer", Integer: turn.OutputTokens)),
                new("usage.turn.new-input", new("integer", Integer: turn.NewInputTokens)),
                new("usage.turn.cached-input", new("integer", Integer: turn.CachedInputTokens)),
                new("usage.turn.cache-write-input", new("integer", Integer: turn.CacheWriteInputTokens)),
                new("usage.turn.api-equivalent", new("currency", Text: "USD", Decimal: turn.ApiEquivalentUsd),
                    turn.ApiEquivalentUsd is null ? "unpriced" : turn.CostIsPartial ? "partial" : "estimated"),
                new("usage.turn.seconds", new("number", Number: turn.ElapsedSeconds)),
                new("usage.turn.average-output-tps", new("number", Number: turn.OutputTokensPerSecond),
                    turn.UsageComplete ? turn.State : "partial", turn.ObservedAt),
                new("usage.turn.first-token-seconds", new("number", Number: turn.FirstTokenSeconds)),
            ]),
        ]);

    private static ContributionSummaryItem SpendRow(
        string labelKey,
        CodexSpendPeriod? period)
    {
        var status = period switch
        {
            null or { HasUsage: false } => "no-data",
            { IsPartiallyPriced: true } => "partial",
            { HasPricedUsage: false } => "unpriced",
            _ => "estimated",
        };
        return new ContributionSummaryItem(
            labelKey,
            new ContributionValue(
                "currency",
                Text: "USD",
                Decimal: period?.ApiEquivalentUsd),
            status);
    }

    public static PluginDataSnapshot Radar(
        string pluginId,
        ProviderRadarSnapshot snapshot)
    {
        var legacy = new[] { snapshot.Primary }
            .Concat(snapshot.Comparisons)
            .Take(256).ToArray();
        var trial = snapshot.TrialFeed is { Models.Count: > 0 } feed ? feed : null;
        var sourceAt = trial?.ObservedAt ?? snapshot.SourceUpdatedAt;
        var historical = sourceAt is null || snapshot.CapturedAt - sourceAt > TimeSpan.FromHours(24);
        RadarModelRow Row(RadarModel model) => new(
                model.Label,
                model.Score,
                model.IqHistory.Count == 0 ? null : model.IqHistory.Average(sample => sample.Score),
                model.AverageTaskSeconds is null ? null : (int?)Math.Round(model.AverageTaskSeconds.Value / 60d),
                model.CostUsd is null ? null : (decimal?)model.CostUsd.Value,
                model.Status is null ? [] : [model.Status])
        {
            SampleCount = model.ValidTasks,
            AverageSeconds = model.AverageTaskSeconds,
        };
        var contributions = new List<RadarContribution>
        {
            new("radar.models", pluginId, sourceAt ?? snapshot.CapturedAt,
                (trial?.Models ?? legacy).Select(Row).ToArray(),
                [new("radar.score.maximum", new("integer", Integer: trial is null ? 150 : 100)),
                 new("radar.benchmark", new("text", Text: trial is null ? "legacy" : "codex-trial"))]),
        };
        if (trial is not null)
            contributions.Add(new("radar.legacy", pluginId, snapshot.SourceUpdatedAt ?? snapshot.CapturedAt,
                legacy.Select(Row).ToArray(), [new("radar.score.maximum", new("integer", Integer: 150))]));
        return new(
            pluginId,
            snapshot.CapturedAt,
            new(
                historical ? PluginHealthCode.Cached : PluginHealthCode.Current,
                true,
                false,
                snapshot.CapturedAt,
                historical ? "radar.historical" : trial is null ? "radar.current" : "radar.trial"),
            [],
            [],
            contributions);
    }

    public static PluginHealth Health(ProviderHealth health) =>
        new(
            health.Code switch
            {
                ProviderHealthCode.Current => PluginHealthCode.Current,
                ProviderHealthCode.Cached => PluginHealthCode.Cached,
                ProviderHealthCode.Loading => PluginHealthCode.Loading,
                ProviderHealthCode.Waiting => PluginHealthCode.Waiting,
                ProviderHealthCode.MissingCredentials => PluginHealthCode.MissingCredentials,
                ProviderHealthCode.EndpointBlocked => PluginHealthCode.EndpointBlocked,
                ProviderHealthCode.OAuthExpired or ProviderHealthCode.OAuthRefreshFailed => PluginHealthCode.OAuthExpired,
                ProviderHealthCode.RateLimited => PluginHealthCode.RateLimited,
                ProviderHealthCode.HttpError => PluginHealthCode.HttpError,
                ProviderHealthCode.Timeout => PluginHealthCode.Timeout,
                _ => PluginHealthCode.Unavailable,
            },
            health.Connected,
            health.Code is ProviderHealthCode.RateLimited
                or ProviderHealthCode.HttpError
                or ProviderHealthCode.Timeout
                or ProviderHealthCode.Unavailable,
            DateTimeOffset.UtcNow,
            $"provider.health.{health.Code.ToString().ToLowerInvariant()}",
            health.HttpStatus,
            health.RetryAt);

    public static string StableToken(string value, int fallback)
    {
        var normalized = new string(value
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '.')
            .ToArray())
            .Trim('.')
            .Replace("..", ".", StringComparison.Ordinal);
        if (PluginValidation.IsStableId(normalized)) return normalized;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..12];
        return $"item.{fallback.ToString(CultureInfo.InvariantCulture)}.{digest}";
    }

    private static string StableKey(string value, string fallback)
    {
        var token = StableToken(value, 0);
        return PluginValidation.IsStableId(token) ? token : fallback;
    }
}
