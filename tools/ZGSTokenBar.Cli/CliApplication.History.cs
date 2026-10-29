using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Cli;

internal static partial class CliApplication
{
    private static async Task<int> CodexHistoryCommandAsync(string[] args, CliArguments options)
    {
        const string command = "codex-usage export";
        if (options.Profile == "headless")
        {
            CliOutput.Write(options.Json, command, null, new("command_requires_desktop", "History export requires the desktop profile."));
            return 4;
        }
        if (args.Length == 0 || args[0] != "export") return CliOutput.Unknown(command, options.Json);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ascending = false;
        for (var index = 1; index < args.Length; index++)
        {
            var key = args[index];
            if (key == "--ascending") { ascending = true; continue; }
            if (key is not ("--output" or "--range" or "--model" or "--project" or "--search" or "--sort")
                || index + 1 == args.Length || !values.TryAdd(key, args[++index]))
                return CliOutput.Invalid(options.Json, command, "Use export --output <new.csv> [--range retained|today|7d|30d] [--model <name>] [--project <name>] [--search <text>] [--sort <column>] [--ascending].");
        }
        if (!values.TryGetValue("--output", out var output) || string.IsNullOrWhiteSpace(output))
            return CliOutput.Invalid(options.Json, command, "--output requires a new CSV path.");
        output = Path.GetFullPath(output);
        if (File.Exists(output)) return CliOutput.Invalid(options.Json, command, "The output file already exists; choose a new CSV path.");
        var period = values.GetValueOrDefault("--range", "retained");
        var sort = values.GetValueOrDefault("--sort", "recent") switch
        {
            "recent" => -1, "name" => 0, "state" => 1, "started" => 2, "input" => 3,
            "output" => 4, "cache" => 5, "cache-hit" => 6, "cost" => 7, "speed" => 8, "first" => 9, _ => -2,
        };
        if (sort == -2 || period is not ("retained" or "today" or "7d" or "30d"))
            return CliOutput.Invalid(options.Json, command, "The range or sort column is invalid.");
        var request = new CodexHistoryExportRequest(period, values.GetValueOrDefault("--model"),
            values.GetValueOrDefault("--project"), values.GetValueOrDefault("--search", ""), sort, !ascending);
        var watch = Stopwatch.StartNew();
        using var content = new MemoryStream();
        CodexHistoryExportChunk? chunk = null;
        while (chunk is null || content.Length < chunk.TotalBytes)
        {
            var remaining = options.Timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new OperationCanceledException();
            var parameters = JsonSerializer.SerializeToElement(request, ApiJsonContext.Default.CodexHistoryExportRequest);
            var response = await InvokeApiAsync("ui.codex.history.export", parameters, options with { Timeout = remaining });
            if (!response.Ok) return WriteApiFailure(command, options.Json, response);
            chunk = response.Result is { } result ? JsonSerializer.Deserialize(result, ApiJsonContext.Default.CodexHistoryExportChunk) : null;
            if (chunk is null || chunk.Data is null || chunk.Snapshot.Length != 64 || chunk.TotalBytes is < 1 or > 16 * 1024 * 1024
                || chunk.Data.Length is < 1 or > 4 * 1024 || chunk.NextOffset != content.Length + chunk.Data.Length
                || chunk.NextOffset > chunk.TotalBytes || request.Snapshot is { } expected && chunk.Snapshot != expected)
            {
                CliOutput.Write(options.Json, command, null, new("invalid_response", "History chunk identity or bounds are invalid."));
                return 4;
            }
            content.Write(chunk.Data);
            request = request with { Offset = chunk.NextOffset, Snapshot = chunk.Snapshot };
        }
        var bytes = content.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != chunk.Snapshot)
        {
            CliOutput.Write(options.Json, command, null, new("invalid_response", "History export digest does not match."));
            return 4;
        }
        // The destination is created only after a complete, unchanged read. Never replace a user file.
        try
        {
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write([0xef, 0xbb, 0xbf]); file.Write(bytes); file.Flush(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CliOutput.Write(options.Json, command, null, new("output_unavailable", "CSV could not be written; inspect the selected output path."));
            return 4;
        }
        return CliOutput.Result(options.Json, command, CliOutput.ObjectElement(("path", output), ("turnCount", chunk.TurnCount),
            ("observedCount", chunk.ObservedCount), ("snapshot", chunk.Snapshot), ("capturedAt", chunk.CapturedAt.ToString("O")),
            ("pendingCount", chunk.PendingCount)), $"Exported {chunk.TurnCount} observed retained turns to {output}.");
    }
}
