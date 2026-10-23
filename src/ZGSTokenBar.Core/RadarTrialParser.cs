using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZGSTokenBar.Core;

public static class RadarTrialParser
{
    public static RadarTrialFeed? Parse(string html)
    {
        var match = Regex.Match(html,
            "<script\\b[^>]*\\bdata-dgr-facts\\b[^>]*>(.*?)</script>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!match.Success || match.Groups[1].Length > 1024 * 1024) return null;
        using var document = JsonDocument.Parse(match.Groups[1].Value);
        var root = document.RootElement;
        if (root.ArrayProperty("settings") is not { } settings
            || root.ArrayProperty("tasks") is not { } tasks
            || root.ObjectProperty("snapshot") is not { } snapshot
            || !DateTimeOffset.TryParse(snapshot.StringProperty("observed_at"),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observedAt)
            || snapshot.ObjectProperty("results")?.ObjectProperty("codex") is not { } results)
            return null;

        var selectors = tasks.EnumerateArray().Select(task => task.StringProperty("selector"))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Take(128).ToArray();
        var models = new List<RadarModel>();
        foreach (var setting in settings.EnumerateArray().Take(128))
        {
            var model = setting.StringProperty("model");
            var effort = setting.StringProperty("effort");
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort)
                || results.ObjectProperty($"{model}|{effort}") is not { } outcomes) continue;
            var rewards = new List<double>();
            var durations = new List<double>();
            foreach (var selector in selectors)
            {
                if (outcomes.ObjectProperty(selector!) is not { } outcome
                    || outcome.StringProperty("status") != "scored"
                    || outcome.NumberProperty("reward") is not { } reward
                    || !double.IsFinite(reward) || reward is < 0 or > 1) continue;
                rewards.Add(reward);
                if (outcome.NumberProperty("seconds") is { } seconds
                    && double.IsFinite(seconds) && seconds >= 0) durations.Add(seconds);
            }
            if (rewards.Count == 0) continue;
            models.Add(new RadarModel(model, $"{model} {effort}", effort,
                rewards.Average() * 100, null, null, rewards.Count, null,
                durations.Count == rewards.Count ? durations.Average() : null, null));
        }
        return models.Count == 0 ? null : new RadarTrialFeed(observedAt, models);
    }
}
