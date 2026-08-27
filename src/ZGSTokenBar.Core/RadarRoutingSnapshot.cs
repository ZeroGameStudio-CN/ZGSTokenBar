namespace ZGSTokenBar.Core;

public sealed record RadarRoutingCandidate(
    string Model,
    string? Effort,
    int Rank);

public sealed record RadarRoutingScenario(
    IReadOnlyList<RadarRoutingCandidate> Overall,
    IReadOnlyList<RadarRoutingCandidate> LunaOnly);

public sealed record RadarRoutingSnapshot(
    int SchemaVersion,
    string EvaluatorPolicyVersion,
    string EventId,
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, RadarRoutingScenario> Scenarios);

public static class RadarRoutingSnapshotBuilder
{
    public const int SchemaVersion = 1;

    public static RadarRoutingSnapshot Build(ProviderRadarSnapshot snapshot)
    {
        var models = new[] { snapshot.Primary }.Concat(snapshot.Comparisons).ToArray();
        var overall = RadarScenarioEvaluator.Rank(models);
        var luna = RadarScenarioEvaluator.Rank(
            models.Where(RadarScenarioEvaluator.IsLunaModel).ToArray());
        return new RadarRoutingSnapshot(
            SchemaVersion,
            RadarScenarioEvaluator.PolicyVersion,
            snapshot.EventId,
            snapshot.CapturedAt,
            new Dictionary<string, RadarRoutingScenario>(StringComparer.Ordinal)
            {
                [RadarScenarioEvaluator.HardProblemsKey] = Scenario(
                    overall.HardProblems,
                    luna.HardProblems),
                [RadarScenarioEvaluator.TaskExecutionKey] = Scenario(
                    overall.TaskExecution,
                    luna.TaskExecution),
                [RadarScenarioEvaluator.BackgroundAutomationKey] = Scenario(
                    overall.BackgroundAutomation,
                    luna.BackgroundAutomation),
            });
    }

    private static RadarRoutingScenario Scenario(
        IReadOnlyList<RadarModel> overall,
        IReadOnlyList<RadarModel> lunaOnly) =>
        new(Candidates(overall), Candidates(lunaOnly));

    private static IReadOnlyList<RadarRoutingCandidate> Candidates(
        IReadOnlyList<RadarModel> models) =>
        models
            .Select((model, index) => new RadarRoutingCandidate(
                model.Model,
                model.ReasoningEffort,
                index + 1))
            .ToArray();
}
