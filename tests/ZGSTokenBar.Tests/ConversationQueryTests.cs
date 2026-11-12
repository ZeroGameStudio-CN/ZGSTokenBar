using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using Microsoft.VisualBasic.FileIO;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;
using System.Text;
using System.Text.Json;

internal static class ConversationQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 1, 0, 0, TimeSpan.Zero);
    private static CodexConversationTurn Turn(string id, DateTimeOffset at, long output, double seconds) =>
        new("session-" + id, id, "model-a", "high", at, at.AddSeconds(seconds), output, seconds, true, true)
        { Title = "Chat " + id, ProjectName = "Alpha", ServiceTier = "standard", InputTokens = 100, CachedInputTokens = 40 };
    private static CodexTokenUsageSummary Usage(params CodexConversationTurn[] turns) =>
        new(123, 456, 3, Now) { ConversationHistory = turns };

    internal static void Run()
    {
        ConversationPresentationCacheTests.Run();
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixture +08", TimeSpan.FromHours(8), "fixture", "fixture");
        var today = Turn("today", new(2026, 10, 28, 16, 0, 0, TimeSpan.Zero), 100, 10);
        var yesterday = Turn("yesterday", today.StartedAt.AddSeconds(-1), 900, 30) with { Model = "model-b", ProjectName = "Beta" };
        var lastWeek = Turn("week", today.StartedAt.AddDays(-6), 50, 10);
        var old = Turn("old", today.StartedAt.AddDays(-31), 10, 10);
        var usage = Usage(today, yesterday, lastWeek, old) with { RecentTurns = [today with { ObservedAt = today.ObservedAt.AddSeconds(1) }] };
        Check(CodexConversationView.Create(usage, new(), Now, zone).Rows.Count == 4, "Retained and recent copies deduplicate by session and turn.");
        Check(CodexConversationView.Create(usage, new(CodexHistoryPeriod.Today), Now, zone).Rows.Single().TurnId == "today", "Today uses the local start date, including the midnight boundary.");
        Check(CodexConversationView.Create(usage, new(CodexHistoryPeriod.Days7), Now, zone).Rows.Count == 3, "Seven days include today and six preceding local calendar days.");
        Check(CodexConversationView.Create(usage, new(CodexHistoryPeriod.Days30), Now, zone).Rows.Count == 3, "Thirty-day selection excludes older retained rows.");
        Check(CodexConversationView.Create(usage, new(Model: "model-b", Project: "Beta", Search: "YESTERDAY"), Now, zone).Rows.Single().TurnId == "yesterday", "Model/project filters intersect with case-insensitive metadata search.");
        Check(CodexConversationView.Create(usage, new(Search: "missing"), Now, zone).Rows.Count == 0, "An empty search result is not replaced with unfiltered data.");
        Check(usage.LocalTokens == 456 && usage.ConversationHistory.Count == 4, "Filtering never rewrites lifetime accounting or stored history.");

        var running = today with { TurnId = "running", Completed = false, OutputTokens = 40 };
        var interrupted = today with { TurnId = "interrupted", Completed = false, Interrupted = true, OutputTokens = 200 };
        var unknown = today with { TurnId = "unknown", UsageComplete = false, OutputTokens = 0, ElapsedSeconds = double.NaN };
        var measured = Usage(today with { ApiEquivalentUsd = 1m }, yesterday, running, interrupted, unknown);
        var summary = CodexConversationView.Create(measured, new(), Now).Summary;
        Check(summary.SpeedSamples == 2 && summary.AverageTokensPerSecond == 25,
            "Speed is total completed output over total whole-turn time, excluding running, interrupted and invalid samples.");
        Check(summary.Cost == 1m && summary.PartialCost && summary.PartialOutput && summary.OutputCoverage == 4,
            "Partial pricing and missing output coverage stay explicit.");
        var absent = CodexConversationView.Create(Usage(unknown), new(), Now).Summary;
        Check(absent.Cost is null && absent.AverageTokensPerSecond is null && absent.OutputCoverage == 0,
            "Unavailable measurements remain unavailable rather than invented zeros.");
        foreach (var descending in new[] { true, false })
        {
            var sorted = CodexConversationView.Create(measured, new(SortColumn: 8, Descending: descending), Now).Rows;
            Check(sorted[^1].TurnId == "unknown", "Missing numeric values stay last in either sort direction.");
        }
        var outputSort = CodexConversationView.Create(measured, new(SortColumn: 4), Now).Rows;
        Check(outputSort[0].TurnId == "yesterday", "Column sorting compares numbers rather than their display strings.");
        var allSpeeds = CodexConversationSpeeds.BuildRows([today, yesterday, lastWeek], Now);
        Check(allSpeeds.Sum(row => row.SampleCount) == 3 && CodexConversationSpeeds.Build(Usage(today, yesterday, lastWeek), Now).Sum(row => row.SampleCount) == 2,
            "Filtered history analytics can cover older retained turns while the existing 24-hour API keeps its window.");

        var unsafeTitle = " \t=SUM(A1,A2),\"quoted\"\r\nnext";
        var csvView = CodexConversationView.Create(Usage(today with { Title = unsafeTitle, ApiEquivalentUsd = .125m, CostIsPartial = true }), new(), Now);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var parser = new TextFieldParser(new StringReader(csvView.Csv())) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
            parser.SetDelimiters(",");
            var header = parser.ReadFields()!;
            var row = parser.ReadFields()!;
            Check(header.Length == row.Length && row[2] == "'" + unsafeTitle && row[14] == "0.125",
                "CSV preserves quotes/newlines, uses invariant numbers and neutralizes spreadsheet formulas in metadata.");
            Check(parser.EndOfData, "CSV exports each selected turn exactly once.");
        }
        finally { CultureInfo.CurrentCulture = previous; }

        var many = CodexConversationView.Create(Usage(Enumerable.Range(0, 500).Select(index =>
            Turn(index.ToString(), Now.AddMinutes(-index - 1), 100, 10) with { Title = new string('中', 60) + index }).ToArray()), new(), Now);
        using var assembled = new MemoryStream();
        string? version = null;
        while (true)
        {
            var chunk = many.ReadCsvChunk((int)assembled.Length, version);
            version ??= chunk.Snapshot;
            var dto = new CodexHistoryExportChunk(chunk.Snapshot, chunk.NextOffset, chunk.TotalBytes, chunk.Data, 500, 500, Now, 0);
            var payload = JsonSerializer.SerializeToElement(dto, ApiJsonContext.Default.CodexHistoryExportChunk);
            var frame = JsonSerializer.SerializeToUtf8Bytes(new ApiResponseEnvelope(1, "fixture", true, payload, null), ApiJsonContext.Default.ApiResponseEnvelope);
            Check(frame.Length < ZgsHostApi.MaximumFrameBytes, "Every Unicode CSV chunk remains inside the real named-pipe frame budget.");
            assembled.Write(chunk.Data);
            if (chunk.NextOffset == chunk.TotalBytes) break;
        }
        Check(Encoding.UTF8.GetString(assembled.ToArray()) == many.Csv(), "Chunked export preserves the entire CSV, including Unicode across byte boundaries.");
        var escapeHeavy = new CodexHistoryExportChunk(new string('A', 64), 4095, 4095,
            Convert.FromBase64String(new string('+', 5460)), 500, 512, Now, 0);
        var escapedPayload = JsonSerializer.SerializeToElement(escapeHeavy, ApiJsonContext.Default.CodexHistoryExportChunk);
        var escapedFrame = JsonSerializer.SerializeToUtf8Bytes(new ApiResponseEnvelope(1, "fixture", true, escapedPayload, null), ApiJsonContext.Default.ApiResponseEnvelope);
        Check(escapedFrame.Length < ZgsHostApi.MaximumFrameBytes, "Even maximally escaped base64 data fits the real transport envelope.");
        var changed = CodexConversationView.Create(Usage(today), new(), Now);
        var rejected = false;
        try { changed.ReadCsvChunk(10, version); }
        catch (CodexHistoryChangedException) { rejected = true; }
        Check(rejected, "Changed cached data cannot be silently spliced into an earlier export.");
        var oversized = CodexConversationView.Create(Usage(today with { Title = new string('x', CodexConversationView.MaximumCsvCharacters + 1) }), new(), Now);
        rejected = false;
        try { oversized.Csv(); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Explicit exports have a finite representation budget rather than unbounded allocations.");

    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
