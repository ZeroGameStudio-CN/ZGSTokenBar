using System.Text;
using System.Text.Json;
using ZGSTokenBar.Core;

internal readonly record struct RadarDeveloperCliResult(bool Handled, int ExitCode);

internal static class RadarDeveloperCli
{
    private const long MaximumInputBytes = 1024 * 1024;
    private const string HelpText = """
        Native developer commands:
          --radar-evaluate <path>  Parse a saved current.json payload
          --radar-live             Fetch Radar data plus upstream and local picks once
          --taskbar-mini-captures [directory]
                                   Render deterministic Mini layout PNGs
          --help                   Show this help
        """;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<RadarDeveloperCliResult> TryRunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<CancellationToken, Task<ProviderRadarSnapshot>>? fetchLive = null,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
        {
            return new RadarDeveloperCliResult(false, 0);
        }

        if (IsCommand(args[0], "--help"))
        {
            if (args.Length != 1) return InvalidArguments(error);
            output.WriteLine(HelpText);
            return new RadarDeveloperCliResult(true, 0);
        }

        if (IsCommand(args[0], "--radar-evaluate"))
        {
            if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1])) return InvalidArguments(error);
            return await EvaluateFileAsync(args[1], output, error, cancellationToken);
        }

        if (IsCommand(args[0], "--radar-live"))
        {
            if (args.Length != 1) return InvalidArguments(error);
            try
            {
                ProviderRadarSnapshot snapshot;
                if (fetchLive is not null)
                {
                    snapshot = await fetchLive(cancellationToken);
                }
                else
                {
                    using var radar = new RadarService();
                    snapshot = await radar.FetchAsync(cancellationToken);
                }
                WriteEvaluation(output, snapshot);
                return new RadarDeveloperCliResult(true, 0);
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                or IOException
                or JsonException
                or TaskCanceledException)
            {
                return Error(error, "live_fetch_failed");
            }
        }

        if (IsCommand(args[0], "--localization-captures")
            || IsCommand(args[0], "--taskbar-mini-captures")
            || IsCommand(args[0], "--live"))
        {
            return new RadarDeveloperCliResult(false, 0);
        }

        return InvalidArguments(error);
    }

    private static async Task<RadarDeveloperCliResult> EvaluateFileAsync(
        string path,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error(error, "input_not_found");
        }

        try
        {
            if (!file.Exists)
            {
                return Error(error, Directory.Exists(file.FullName) ? "input_not_file" : "input_not_found");
            }
            if ((file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return Error(error, "input_not_file");
            }
            if (file.Length > MaximumInputBytes) return Error(error, "input_too_large");
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return Error(error, "input_unreadable");
        }

        try
        {
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumInputBytes) return Error(error, "input_too_large");
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            var json = await reader.ReadToEndAsync(cancellationToken);
            if (stream.Position > MaximumInputBytes) return Error(error, "input_too_large");
            var snapshot = RadarParser.Parse(json, DateTimeOffset.UnixEpoch);
            WriteEvaluation(output, snapshot);
            return new RadarDeveloperCliResult(true, 0);
        }
        catch (JsonException)
        {
            return Error(error, "invalid_payload");
        }
        catch (DecoderFallbackException)
        {
            return Error(error, "invalid_payload");
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return Error(error, "input_unreadable");
        }
    }

    private static void WriteEvaluation(TextWriter output, ProviderRadarSnapshot snapshot)
    {
        var models = new[] { snapshot.Primary }.Concat(snapshot.Comparisons).ToArray();
        var feed = snapshot.RecommendationFeed;
        var local = RadarScenarioEvaluator.Evaluate(models);
        var document = new
        {
            SchemaVersion = 6,
            snapshot.EventId,
            snapshot.SourceUpdatedAt,
            LocalRecommendations = new
            {
                local.PolicyVersion,
                DailyDevelopment = CompactScenario(local.DailyDevelopment),
                HardProblems = CompactScenario(local.HardProblems),
                TaskExecution = CompactScenario(local.TaskExecution),
                BackgroundAutomation = CompactScenario(local.BackgroundAutomation),
            },
            RecommendationFeed = feed is null
                ? null
                : new
                {
                    feed.Schema,
                    feed.Mode,
                    feed.GeneratedAt,
                    feed.SourceUpdatedAt,
                    Recommendations = feed.Groups.Select(group => new
                    {
                        group.Key,
                        group.Title,
                        group.Rule,
                        Items = group.Items.Select(item => new
                        {
                            item.Model.Model,
                            Effort = item.Model.ReasoningEffort,
                            Iq = FiniteOrNull(item.Model.Score),
                            item.Model.Passed,
                            Samples = item.Model.ValidTasks,
                            AverageCostUsd = FiniteOrNull(item.Model.CostUsd),
                            item.CostSamples,
                            AverageDurationMinutes = FiniteOrNull(
                                item.Model.AverageTaskSeconds / 60),
                            item.DurationSamples,
                            item.CombinedCostIndex,
                            item.Slot,
                            item.Rule,
                        }),
                    }),
                },
            Rows = models.Select((model, sourceIndex) => new
            {
                SourceIndex = sourceIndex,
                model.Model,
                Effort = model.ReasoningEffort,
                Iq = FiniteOrNull(model.Score),
                model.Status,
                model.Passed,
                model.ValidTasks,
                Cost = FiniteOrNull(model.CostUsd),
                Duration = FiniteOrNull(model.AverageTaskSeconds),
            }),
        };
        output.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
    }

    private static object? CompactScenario(RadarModel? model) => model is null
        ? null
        : new
        {
            model.Model,
            Effort = model.ReasoningEffort,
            Iq = FiniteOrNull(model.Score),
            Cost = FiniteOrNull(model.CostUsd),
            Duration = FiniteOrNull(model.AverageTaskSeconds),
        };

    private static double? FiniteOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static bool IsCommand(string value, string command) =>
        string.Equals(value, command, StringComparison.OrdinalIgnoreCase);

    private static RadarDeveloperCliResult InvalidArguments(TextWriter error)
    {
        error.WriteLine("radar-evaluate: invalid_arguments; use --help");
        return new RadarDeveloperCliResult(true, 1);
    }

    private static RadarDeveloperCliResult Error(TextWriter error, string category)
    {
        error.WriteLine($"radar-evaluate: {category}");
        return new RadarDeveloperCliResult(true, 1);
    }
}
