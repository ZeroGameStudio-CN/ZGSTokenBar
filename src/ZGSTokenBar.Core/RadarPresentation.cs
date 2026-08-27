using System.Globalization;

namespace ZGSTokenBar.Core;

public enum RadarStatusIndicator
{
    Stable,
    Watch,
    Degraded,
    Unknown,
}

public sealed record RadarDisplayRow(
    RadarModel Model,
    int SourceIndex,
    int? Rank,
    IReadOnlyList<int> RecommendationGroupIndexes)
{
    public string ModelText => RadarPresentation.FormatModelLabel(Model);
    public string ScoreText => Model.Score?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
    public RadarIqComparison? IqComparison => RadarPresentation.FormatIqComparison(Model);
    public string SampleCountText => RadarPresentation.FormatSampleCount(Model);
    public string PassText => RadarPresentation.FormatPass(Model);
    public string AverageCostText => RadarPresentation.FormatAverageCost(Model);
    public RadarStatusIndicator Indicator => RadarStatus.Indicator(Model.Status);
}

public readonly record struct RadarIqComparison(
    string DirectionText,
    string AverageText);

public sealed record RadarPresentationResult(
    IReadOnlyList<RadarDisplayRow> Rows,
    RadarModel? IqLeader,
    IReadOnlyList<RadarRecommendationGroup> Recommendations);

public static class RadarStatus
{
    public static RadarStatusIndicator Indicator(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "green" => RadarStatusIndicator.Stable,
            "yellow" => RadarStatusIndicator.Watch,
            "red" => RadarStatusIndicator.Degraded,
            _ => RadarStatusIndicator.Unknown,
        };
}

public static class RadarPresentation
{
    public static RadarPresentationResult DeepSeekOnly(RadarPresentationResult presentation)
    {
        var filtered = Filter(presentation, IsDeepSeekModel);
        return filtered with
        {
            Rows = filtered.Rows
                .OrderBy(row => DeepSeekFamilyOrder(row.Model))
                .ThenBy(row => DeepSeekLaneOrder(row.Model))
                .ThenByDescending(row => EffortRank(row.Model.ReasoningEffort))
                .ThenBy(row => row.SourceIndex)
                .ToArray(),
        };
    }

    public static RadarPresentationResult CodexOnly(RadarPresentationResult presentation)
    {
        return Filter(presentation, model => !IsDeepSeekModel(model));
    }

    public static bool IsDeepSeekModel(RadarModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return IsDeepSeekModel(model.Model) || IsDeepSeekModel(model.Label);
    }

    public static RadarPresentationResult Build(ProviderRadarSnapshot snapshot)
    {
        var sourceModels = new[] { snapshot.Primary }.Concat(snapshot.Comparisons).ToArray();
        var localEvaluation = RadarScenarioEvaluator.Evaluate(sourceModels);
        var recommendations = new[]
        {
            BuildRecommendationGroup(
                RadarScenarioEvaluator.DailyDevelopmentKey,
                "Daily development",
                localEvaluation.DailyDevelopment),
            BuildRecommendationGroup(
                RadarScenarioEvaluator.HardProblemsKey,
                "Hard problems",
                localEvaluation.HardProblems),
            BuildRecommendationGroup(
                RadarScenarioEvaluator.TaskExecutionKey,
                "Task execution",
                localEvaluation.TaskExecution),
            BuildRecommendationGroup(
                RadarScenarioEvaluator.BackgroundAutomationKey,
                "Background automation",
                localEvaluation.BackgroundAutomation),
        };
        var models = sourceModels;
        var modelOrder = models
            .GroupBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .Select((model, index) => (model, index))
            .ToDictionary(item => item.model.Key, item => item.index, StringComparer.OrdinalIgnoreCase);
        var unmarkedRows = models
            .Select((model, index) => new RadarDisplayRow(model, index, null, []))
            .ToArray();
        var iqLeaderRow = SelectIqLeader(unmarkedRows);
        var rows = unmarkedRows
            .Select(row => row with
            {
                Rank = row.SourceIndex == iqLeaderRow?.SourceIndex ? 1 : null,
                RecommendationGroupIndexes = recommendations
                    .Select((group, groupIndex) => (group, groupIndex))
                    .Where(entry => entry.group.Items.Any(item =>
                        SameModel(item.Model, row.Model)))
                    .Select(entry => entry.groupIndex)
                    .ToArray(),
            })
            .OrderBy(row => modelOrder[row.Model.Model])
            .ThenByDescending(row => EffortRank(row.Model.ReasoningEffort))
            .ThenBy(row => row.SourceIndex)
            .ToArray();

        return new RadarPresentationResult(rows, iqLeaderRow?.Model, recommendations);
    }

    private static bool IsDeepSeekModel(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Contains("deepseek", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("dsh-", StringComparison.OrdinalIgnoreCase));

    private static int DeepSeekFamilyOrder(RadarModel model)
    {
        var value = $"{model.Model} {model.Label}";
        if (ContainsFamily(value, "pro")) return 0;
        if (ContainsFamily(value, "flash")) return 1;
        return 2;
    }

    private static int DeepSeekLaneOrder(RadarModel model)
    {
        var value = $"{model.Model} {model.Label}";
        return value.Contains("dsh-", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static bool ContainsFamily(string value, string family) =>
        value.Contains($"-{family}", StringComparison.OrdinalIgnoreCase)
        || value.Contains($" {family}", StringComparison.OrdinalIgnoreCase);

    private static RadarPresentationResult Filter(
        RadarPresentationResult presentation,
        Func<RadarModel, bool> include)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(include);
        var rows = presentation.Rows
            .Where(row => include(row.Model))
            .Select(row => row with { Rank = null })
            .ToArray();
        var leaderRow = SelectIqLeader(rows);
        rows = rows
            .Select(row => row with
            {
                Rank = row.SourceIndex == leaderRow?.SourceIndex ? 1 : null,
            })
            .ToArray();
        return presentation with
        {
            Rows = rows,
            IqLeader = leaderRow?.Model,
        };
    }

    private static RadarDisplayRow? SelectIqLeader(IEnumerable<RadarDisplayRow> rows) => rows
        .Where(row => IsFinite(row.Model.Score)
            && RadarScenarioEvaluator.HasSufficientSamples(row.Model))
        .OrderByDescending(row => row.Model.Score)
        .ThenByDescending(row => row.Model.Passed.HasValue)
        .ThenByDescending(row => row.Model.Passed)
        .ThenByDescending(row => IsFinite(row.Model.CostUsd))
        .ThenBy(row => FiniteOrMax(row.Model.CostUsd))
        .ThenByDescending(row => IsFinite(row.Model.AverageTaskSeconds))
        .ThenBy(row => FiniteOrMax(row.Model.AverageTaskSeconds))
        .ThenBy(row => row.SourceIndex)
        .FirstOrDefault();

    public static string FormatPass(RadarModel model)
    {
        if (model.Passed is not { } passed || model.ValidTasks is not > 0) return "—";
        var percentage = (int)Math.Round(
            passed * 100d / model.ValidTasks.Value,
            MidpointRounding.AwayFromZero);
        return $"{passed}/{model.ValidTasks} ({percentage}%)";
    }

    public static RadarIqComparison? FormatIqComparison(RadarModel model)
    {
        var score = model.Score?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
        var latest = model.IqHistory.Count == 0
            ? (DateTimeOffset?)null
            : model.IqHistory.Max(sample => sample.ObservedAt);
        var history = model.IqHistory
            .Where(sample => latest is not null
                && sample.ObservedAt >= latest.Value.AddHours(-24)
                && double.IsFinite(sample.Score))
            .Select(sample => sample.Score)
            .ToArray();
        if (history.Length < 2 || model.Score is not { } current || !double.IsFinite(current)) return null;

        var average = history.Average();
        var averageText = average.ToString("0.0", CultureInfo.InvariantCulture);
        var direction = score == averageText
            ? "→"
            : current > average
                ? "↑"
                : "↓";
        return new RadarIqComparison(direction, averageText);
    }

    public static string FormatAverageCost(RadarModel model)
    {
        if (model.CostUsd is not { } cost || !double.IsFinite(cost) || cost < 0) return "—";
        var value = cost is > 0 and < 0.01
            ? "<$0.01"
            : "$" + cost.ToString("0.00", CultureInfo.InvariantCulture);
        return model.IncompleteCostSamples is > 0 ? $"≈{value}" : value;
    }

    public static string FormatSampleCount(RadarModel model) =>
        model.ValidTasks is > 0
            ? model.ValidTasks.Value.ToString(CultureInfo.InvariantCulture)
            : "—";

    public static string FormatModelLabel(RadarModel model)
    {
        if (model.Model.StartsWith("dsh-", StringComparison.OrdinalIgnoreCase))
        {
            var label = string.IsNullOrWhiteSpace(model.Label)
                ? model.Model[4..]
                : model.Label.Trim();
            if (label.StartsWith("dsh-", StringComparison.OrdinalIgnoreCase))
            {
                label = label[4..];
            }
            return $"DSH {label}";
        }

        if (!model.Model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(model.Label) ? model.Model : model.Label.Trim();
        }

        var parts = model.Model[4..].Split('-', StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length == 0
            ? "GPT"
            : "GPT-" + parts[0] + string.Concat(parts.Skip(1).Select(part => $" {Capitalize(part)}"));
        return string.Join(' ', new[] { name, FormatEffort(model.ReasoningEffort) }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string? FormatEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort)) return null;
        return effort.Equals("xhigh", StringComparison.OrdinalIgnoreCase)
            ? "XHigh"
            : Capitalize(effort);
    }

    private static int EffortRank(string? effort) => effort?.ToLowerInvariant() switch
    {
        "ultra" => 6,
        "max" => 5,
        "xhigh" => 4,
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static bool SameModel(RadarModel left, RadarModel right) =>
        string.Equals(left.Model, right.Model, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            left.ReasoningEffort,
            right.ReasoningEffort,
            StringComparison.OrdinalIgnoreCase);

    private static RadarRecommendationGroup BuildRecommendationGroup(
        string key,
        string title,
        RadarModel? selected)
    {
        if (selected is null)
        {
            return new RadarRecommendationGroup(
                key,
                title,
                RadarScenarioEvaluator.RuleFor(key),
                []);
        }

        return new RadarRecommendationGroup(
            key,
            title,
            RadarScenarioEvaluator.RuleFor(key),
            [
                new RadarRecommendationItem(
                    selected,
                    null,
                    null,
                    null,
                    null,
                    null),
            ]);
    }

    private static bool IsFinite(double? value) => value is { } number && double.IsFinite(number);
    private static double FiniteOrMax(double? value) => IsFinite(value) ? value!.Value : double.MaxValue;
}
