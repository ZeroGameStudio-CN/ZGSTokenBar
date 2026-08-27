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
        DateTimeOffset now)
    {
        var health = summary is null
            ? new PluginHealth(
                PluginHealthCode.Waiting,
                false,
                true,
                now,
                "codex.usage.waiting")
            : new PluginHealth(
                PluginHealthCode.Current,
                true,
                false,
                summary.CapturedAt,
                "codex.usage.current");
        if (summary is null) return new(pluginId, now, health, [], [], []);

        var rows = new[]
        {
            new ContributionSummaryItem("usage.tokens.today", new("integer", Integer: summary.TodayTokens)),
            new ContributionSummaryItem("usage.tokens.total", new("integer", Integer: summary.LocalTokens)),
            new ContributionSummaryItem("usage.cache.today", new("percent", Number: summary.TodayCacheHitPercent)),
            new ContributionSummaryItem("usage.cache.total", new("percent", Number: summary.TotalCacheHitPercent)),
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
            ],
            []);
    }

    public static PluginDataSnapshot Radar(
        string pluginId,
        ProviderRadarSnapshot snapshot)
    {
        var rows = new[] { snapshot.Primary }
            .Concat(snapshot.Comparisons)
            .Take(256)
            .Select(model => new RadarModelRow(
                model.Label,
                model.Score,
                model.IqHistory.Count == 0 ? null : model.IqHistory.Average(sample => sample.Score),
                model.AverageTaskSeconds is null ? null : (int?)Math.Round(model.AverageTaskSeconds.Value / 60d),
                model.CostUsd is null ? null : (decimal?)model.CostUsd.Value,
                model.Status is null ? [] : [model.Status]))
            .ToArray();
        return new(
            pluginId,
            snapshot.CapturedAt,
            new(
                PluginHealthCode.Current,
                true,
                false,
                snapshot.CapturedAt,
                "radar.current"),
            [],
            [],
            [
                new(
                    "radar.models",
                    pluginId,
                    snapshot.SourceUpdatedAt ?? snapshot.CapturedAt,
                    rows,
                    []),
            ]);
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
