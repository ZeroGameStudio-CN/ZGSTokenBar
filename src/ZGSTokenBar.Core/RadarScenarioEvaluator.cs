namespace ZGSTokenBar.Core;

public sealed record RadarScenarioEvaluation(
    string PolicyVersion,
    RadarModel? DailyDevelopment,
    RadarModel? HardProblems,
    RadarModel? TaskExecution,
    RadarModel? BackgroundAutomation);

public sealed record RadarScenarioRankings(
    string PolicyVersion,
    IReadOnlyList<RadarModel> DailyDevelopment,
    IReadOnlyList<RadarModel> HardProblems,
    IReadOnlyList<RadarModel> TaskExecution,
    IReadOnlyList<RadarModel> BackgroundAutomation);

public static class RadarScenarioEvaluator
{
    public const string PolicyVersion = "local-scenarios-v7";
    public const string DailyDevelopmentKey = "daily_development";
    public const string HardProblemsKey = "hard_problems";
    public const string TaskExecutionKey = "task_execution";
    public const string BackgroundAutomationKey = "background_automation";
    public const double DailyIqRetention = 0.80;
    public const double DailyMaximumIqLoss = 20.0;
    public const double DailyIqWeight = 0.50;
    public const double DailyTimeWeight = 0.25;
    public const double DailyCostWeight = 0.25;
    public const double TaskExecutionIqWeight = 0.45;
    public const double TaskExecutionCostWeight = 0.45;
    public const double TaskExecutionTimeWeight = 0.10;
    public const double BackgroundIqRetention = 0.70;
    public const double BackgroundIqWeight = 0.25;
    public const double BackgroundCostWeight = 0.70;
    public const double BackgroundTimeWeight = 0.05;
    public const long MinimumValidTasks = 50;
    public const int MinimumVolatilitySamples = 2;

    private const string DailyRule = "Local · valid tasks ≥ 50 · confidence + coverage-matched 24h stability · IQ ≥ max(80% of best, best−20) · Pareto · IQ 50% · time 25% · cost 25%";
    private const string PlanningRule = "Local · valid tasks ≥ 50 · confidence + coverage-matched 24h stability · highest guarded IQ · lowest cost × time on tie";
    private const string TaskExecutionRule = "Local · valid tasks ≥ 50 · confidence + coverage-matched 24h stability · IQ ≥ max(80% of best, best−20) · IQ 45% · cost 45% · time 10%";
    private const string BackgroundRule = "Local · valid tasks ≥ 50 · confidence + coverage-matched 24h stability · IQ ≥ 70% of best · cost 70% · IQ 25% · time 5%";

    public static RadarScenarioEvaluation Evaluate(IReadOnlyList<RadarModel> models)
    {
        var rankings = Rank(models);
        return new RadarScenarioEvaluation(
            rankings.PolicyVersion,
            rankings.DailyDevelopment.FirstOrDefault(),
            rankings.HardProblems.FirstOrDefault(),
            rankings.TaskExecution.FirstOrDefault(),
            rankings.BackgroundAutomation.FirstOrDefault());
    }

    public static RadarScenarioRankings Rank(IReadOnlyList<RadarModel> models)
    {
        var candidateModels = models
            .Where(IsValid)
            .ToArray();
        var applyVolatilityGuard = candidateModels.Length > 0
            && candidateModels.All(HasSufficientVolatilityHistory);
        var candidates = candidateModels
            .Select((model, sourceIndex) => new Candidate(
                sourceIndex,
                model,
                GuardedIq(model, applyVolatilityGuard)))
            .ToArray();
        if (candidates.Length == 0)
        {
            return new RadarScenarioRankings(PolicyVersion, [], [], [], []);
        }

        var iqLeader = candidates.Max(candidate => candidate.GuardedIq);
        return new RadarScenarioRankings(
            PolicyVersion,
            SelectDaily(candidates, iqLeader),
            SelectPlanning(candidates),
            SelectTaskExecution(candidates, iqLeader),
            SelectBackground(candidates, iqLeader));
    }

    public static string? RuleFor(string key) => key.ToLowerInvariant() switch
    {
        DailyDevelopmentKey => DailyRule,
        HardProblemsKey => PlanningRule,
        TaskExecutionKey => TaskExecutionRule,
        BackgroundAutomationKey => BackgroundRule,
        _ => null,
    };

    public static bool IsLunaModel(RadarModel model) =>
        string.Equals(model.Model, "gpt-5.6-luna", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RadarModel> SelectDaily(IReadOnlyList<Candidate> candidates, double iqLeader)
    {
        var qualityCandidates = candidates
            .Where(candidate => candidate.GuardedIq >= Math.Max(
                iqLeader * DailyIqRetention,
                iqLeader - DailyMaximumIqLoss))
            .ToArray();
        var frontier = qualityCandidates
            .Where(candidate => !qualityCandidates.Any(other =>
                other.SourceIndex != candidate.SourceIndex
                && Dominates(other, candidate)))
            .ToArray();
        var iqRange = Range(frontier, candidate => candidate.GuardedIq, logarithmic: false);
        var timeRange = Range(frontier, candidate => candidate.Model.AverageTaskSeconds!.Value, logarithmic: true);
        var costRange = Range(frontier, candidate => candidate.Model.CostUsd!.Value, logarithmic: true);

        return frontier
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = DailyIqWeight * Utility(candidate.GuardedIq, iqRange, invert: false, logarithmic: false)
                    + DailyTimeWeight * Utility(candidate.Model.AverageTaskSeconds!.Value, timeRange, invert: true, logarithmic: true)
                    + DailyCostWeight * Utility(candidate.Model.CostUsd!.Value, costRange, invert: true, logarithmic: true),
            })
            .Where(result => double.IsFinite(result.Score))
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Candidate.Model.Score)
            .ThenBy(result => result.Candidate.Model.CostUsd)
            .ThenBy(result => result.Candidate.Model.AverageTaskSeconds)
            .ThenBy(result => result.Candidate.SourceIndex)
            .Select(result => result.Candidate.Model)
            .ToArray();
    }

    private static IReadOnlyList<RadarModel> SelectPlanning(IReadOnlyList<Candidate> candidates) =>
        candidates
            .OrderByDescending(candidate => candidate.GuardedIq)
            .ThenByDescending(candidate => candidate.Model.Score)
            .ThenBy(candidate =>
                Math.Log(candidate.Model.CostUsd!.Value)
                + Math.Log(candidate.Model.AverageTaskSeconds!.Value))
            .ThenBy(candidate => candidate.Model.CostUsd)
            .ThenBy(candidate => candidate.Model.AverageTaskSeconds)
            .ThenBy(candidate => candidate.SourceIndex)
            .Select(candidate => candidate.Model)
            .ToArray();

    private static IReadOnlyList<RadarModel> SelectTaskExecution(IReadOnlyList<Candidate> candidates, double iqLeader)
    {
        var qualityCandidates = candidates
            .Where(candidate => candidate.GuardedIq >= Math.Max(
                iqLeader * DailyIqRetention,
                iqLeader - DailyMaximumIqLoss))
            .ToArray();
        var iqRange = Range(qualityCandidates, candidate => candidate.GuardedIq, logarithmic: false);
        var costRange = Range(qualityCandidates, candidate => candidate.Model.CostUsd!.Value, logarithmic: true);
        var timeRange = Range(qualityCandidates, candidate => candidate.Model.AverageTaskSeconds!.Value, logarithmic: true);

        return qualityCandidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = TaskExecutionIqWeight * Utility(candidate.GuardedIq, iqRange, invert: false, logarithmic: false)
                    + TaskExecutionCostWeight * Utility(candidate.Model.CostUsd!.Value, costRange, invert: true, logarithmic: true)
                    + TaskExecutionTimeWeight * Utility(candidate.Model.AverageTaskSeconds!.Value, timeRange, invert: true, logarithmic: true),
            })
            .Where(result => double.IsFinite(result.Score))
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Candidate.GuardedIq)
            .ThenBy(result => result.Candidate.Model.CostUsd)
            .ThenBy(result => result.Candidate.Model.AverageTaskSeconds)
            .ThenBy(result => result.Candidate.SourceIndex)
            .Select(result => result.Candidate.Model)
            .ToArray();
    }

    private static IReadOnlyList<RadarModel> SelectBackground(IReadOnlyList<Candidate> candidates, double iqLeader)
    {
        var qualityCandidates = candidates
            .Where(candidate => candidate.GuardedIq >= iqLeader * BackgroundIqRetention)
            .ToArray();
        var iqRange = Range(qualityCandidates, candidate => candidate.GuardedIq, logarithmic: false);
        var costRange = Range(qualityCandidates, candidate => candidate.Model.CostUsd!.Value, logarithmic: true);
        var timeRange = Range(qualityCandidates, candidate => candidate.Model.AverageTaskSeconds!.Value, logarithmic: true);

        return qualityCandidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = BackgroundIqWeight * Utility(candidate.GuardedIq, iqRange, invert: false, logarithmic: false)
                    + BackgroundCostWeight * Utility(candidate.Model.CostUsd!.Value, costRange, invert: true, logarithmic: true)
                    + BackgroundTimeWeight * Utility(candidate.Model.AverageTaskSeconds!.Value, timeRange, invert: true, logarithmic: true),
            })
            .Where(result => double.IsFinite(result.Score))
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Candidate.GuardedIq)
            .ThenBy(result => result.Candidate.Model.CostUsd)
            .ThenBy(result => result.Candidate.Model.AverageTaskSeconds)
            .ThenBy(result => result.Candidate.SourceIndex)
            .Select(result => result.Candidate.Model)
            .ToArray();
    }

    public static bool HasSufficientSamples(RadarModel model) =>
        model.ValidTasks is >= MinimumValidTasks;

    private static bool IsValid(RadarModel model) =>
        HasSufficientSamples(model)
        && model.Score is { } iq
        && double.IsFinite(iq)
        && iq > 0
        && model.CostUsd is { } cost
        && double.IsFinite(cost)
        && cost > 0
        && model.AverageTaskSeconds is { } duration
        && double.IsFinite(duration)
        && duration > 0;

    private static bool Dominates(Candidate candidate, Candidate model) =>
        candidate.GuardedIq >= model.GuardedIq
        && candidate.Model.CostUsd <= model.Model.CostUsd
        && candidate.Model.AverageTaskSeconds <= model.Model.AverageTaskSeconds
        && (candidate.GuardedIq > model.GuardedIq
            || candidate.Model.CostUsd < model.Model.CostUsd
            || candidate.Model.AverageTaskSeconds < model.Model.AverageTaskSeconds);

    private static double GuardedIq(RadarModel model, bool applyVolatilityGuard)
    {
        var confidenceIq = ConfidenceLowerBoundIq(model);
        var downsideIq = applyVolatilityGuard ? HistoryDownsideIq(model) : null;
        return Math.Max(0, downsideIq is { } downside
            ? Math.Min(confidenceIq, downside)
            : confidenceIq);
    }

    private static double ConfidenceLowerBoundIq(RadarModel model)
    {
        if (model.Passed is not { } passed
            || model.ValidTasks is not { } valid
            || valid <= 0
            || passed < 0
            || passed > valid)
        {
            return model.Score!.Value;
        }

        const double z = 1.96;
        var probability = (double)passed / valid;
        var denominator = 1 + z * z / valid;
        var center = (probability + z * z / (2 * valid)) / denominator;
        var halfWidth = z * Math.Sqrt(
            probability * (1 - probability) / valid
            + z * z / (4 * valid * valid)) / denominator;
        return 150 * Math.Max(0, center - halfWidth);
    }

    private static double? HistoryDownsideIq(RadarModel model)
    {
        var scores = RecentIqScores(model);
        if (scores.Length < MinimumVolatilitySamples) return null;

        var average = scores.Average();
        var variance = scores.Average(score => Math.Pow(score - average, 2));
        return Math.Max(0, average - Math.Sqrt(variance));
    }

    private static bool HasSufficientVolatilityHistory(RadarModel model) =>
        RecentIqScores(model).Length >= MinimumVolatilitySamples;

    private static double[] RecentIqScores(RadarModel model)
    {
        var latest = model.IqHistory.Count == 0
            ? (DateTimeOffset?)null
            : model.IqHistory.Max(sample => sample.ObservedAt);
        return model.IqHistory
            .Where(sample => latest is not null
                && sample.ObservedAt >= latest.Value.AddHours(-24)
                && double.IsFinite(sample.Score))
            .Select(sample => sample.Score)
            .ToArray();
    }

    private static (double Minimum, double Maximum) Range(
        IReadOnlyList<Candidate> candidates,
        Func<Candidate, double> selector,
        bool logarithmic)
    {
        var values = candidates
            .Select(selector)
            .Select(value => logarithmic ? Math.Log(value) : value)
            .ToArray();
        return (values.Min(), values.Max());
    }

    private static double Utility(
        double value,
        (double Minimum, double Maximum) range,
        bool invert,
        bool logarithmic)
    {
        value = logarithmic ? Math.Log(value) : value;
        if (range.Maximum == range.Minimum) return 0.5;
        var normalized = (value - range.Minimum) / (range.Maximum - range.Minimum);
        return invert ? 1 - normalized : normalized;
    }

    private sealed record Candidate(int SourceIndex, RadarModel Model, double GuardedIq);
}
