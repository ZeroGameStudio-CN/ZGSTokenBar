using System.Globalization;
using System.Text;
using System.Security.Cryptography;

namespace ZGSTokenBar.Core;

public enum CodexHistoryPeriod { Retained, Today, Days7, Days30 }

public sealed record CodexConversationQuery(
    CodexHistoryPeriod Period = CodexHistoryPeriod.Retained,
    string? Model = null, string? Project = null, string Search = "",
    int SortColumn = -1, bool Descending = true);

public sealed record CodexConversationSummary(int Count, int TerminalCount, int OutputCoverage,
    long OutputTokens, decimal? Cost, bool PartialCost, int SpeedSamples, double? AverageTokensPerSecond)
{ public bool PartialOutput { get; init; } }

public sealed record CodexConversationView(CodexConversationQuery Query,
    IReadOnlyList<CodexConversationTurn> Rows, int ObservedCount, CodexConversationSummary Summary)
{
    public const int MaximumCsvCharacters = 4 * 1024 * 1024;
    public const int CsvChunkBytes = 4 * 1024;
    public static CodexConversationView Create(CodexTokenUsageSummary? usage, CodexConversationQuery query,
        DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var all = CodexConversationDisplay.Select(usage);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var start = query.Period switch
        {
            CodexHistoryPeriod.Today => today,
            CodexHistoryPeriod.Days7 => today.AddDays(-6),
            CodexHistoryPeriod.Days30 => today.AddDays(-29),
            _ => DateOnly.MinValue,
        };
        var search = query.Search.Trim();
        var rows = all.Where(turn =>
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(turn.StartedAt, timeZone).DateTime);
            return (query.Period == CodexHistoryPeriod.Retained || date >= start && date <= today)
                && (query.Model is null || string.Equals(turn.Model, query.Model, StringComparison.Ordinal))
                && (query.Project is null || string.Equals(turn.ProjectName, query.Project, StringComparison.Ordinal))
                && (search.Length == 0 || new[] { turn.Title, turn.ProjectName, turn.Model, turn.Effort,
                    turn.ServiceTier, turn.SessionId, turn.TurnId }.Any(value =>
                        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true));
        }).ToArray();
        Array.Sort(rows, (left, right) => Compare(left, right, query));
        var measured = rows.Where(turn => turn.Completed && !turn.Interrupted && !turn.Stale
            && turn.UsageComplete && turn.OutputTokens > 0 && double.IsFinite(turn.ElapsedSeconds)
            && turn.ElapsedSeconds > 0 && turn.ObservedAt <= now.AddMinutes(5)).ToArray();
        var priced = rows.Where(turn => turn.ApiEquivalentUsd is >= 0).ToArray();
        var output = rows.Where(turn => turn.UsageComplete || turn.OutputTokens > 0).ToArray();
        return new(query, rows, all.Count, new(rows.Length, rows.Count(turn => turn.IsTerminal),
            output.Length, output.Sum(turn => Math.Max(0, turn.OutputTokens)),
            priced.Length == 0 ? null : priced.Sum(turn => turn.ApiEquivalentUsd!.Value),
            priced.Length != rows.Length || priced.Any(turn => turn.CostIsPartial), measured.Length,
            measured.Length == 0 ? null : measured.Sum(turn => (double)turn.OutputTokens) / measured.Sum(turn => turn.ElapsedSeconds))
            { PartialOutput = output.Length != rows.Length || output.Any(turn => !turn.UsageComplete) });
    }

    private static int Compare(CodexConversationTurn left, CodexConversationTurn right, CodexConversationQuery query)
    {
        IComparable? Key(CodexConversationTurn turn) => query.SortColumn switch
        {
            0 => turn.Title ?? turn.ProjectName ?? turn.SessionId,
            1 => turn.State, 2 => turn.StartedAt, 3 => turn.NewInputTokens,
            4 => turn.UsageComplete || turn.OutputTokens > 0 ? turn.OutputTokens : null,
            5 => turn.CachedInputTokens, 6 => Finite(turn.CacheHitPercent),
            7 => turn.ApiEquivalentUsd, 8 => Finite(turn.OutputTokensPerSecond),
            9 => Finite(turn.FirstTokenSeconds), _ => turn.ObservedAt,
        };
        var a = Key(left); var b = Key(right);
        if (a is null || b is null) return a is null ? b is null ? Tie(left, right) : 1 : -1;
        var result = a is string text && b is string other
            ? StringComparer.OrdinalIgnoreCase.Compare(text, other) : a.CompareTo(b);
        return result == 0 ? Tie(left, right) : query.Descending ? -Math.Sign(result) : Math.Sign(result);
    }

    private static double? Finite(double? number) => number is { } value && double.IsFinite(value) ? value : null;
    private static int Tie(CodexConversationTurn left, CodexConversationTurn right)
    {
        var result = right.ObservedAt.CompareTo(left.ObservedAt);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(left.SessionId, right.SessionId);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.TurnId, right.TurnId);
    }

    public string Csv()
    {
        var csv = new StringBuilder("session_id,turn_id,title,project,configured_model,effort,service_tier,state,started_at,observed_at,new_input_tokens,output_tokens,cached_input_tokens,cache_hit_percent,api_equivalent_usd,cost_partial,whole_turn_seconds,whole_turn_avg_tok_s,recorded_first_token_seconds,usage_complete,interrupted,stale,recorded_response_models,routed_from_model,routed_to_model,routing_reason\r\n");
        foreach (var turn in Rows)
        {
            object?[] values = [turn.SessionId, turn.TurnId, turn.Title, turn.ProjectName, turn.Model,
                turn.Effort, turn.ServiceTier, turn.State, turn.StartedAt.ToString("O"), turn.ObservedAt.ToString("O"),
                turn.NewInputTokens, turn.UsageComplete || turn.OutputTokens > 0 ? turn.OutputTokens : null,
                turn.CachedInputTokens, Finite(turn.CacheHitPercent), turn.ApiEquivalentUsd, turn.CostIsPartial,
                Finite(turn.ElapsedSeconds), Finite(turn.OutputTokensPerSecond), Finite(turn.FirstTokenSeconds),
                turn.UsageComplete, turn.Interrupted, turn.Stale, turn.ResponseModels, turn.RoutedFromModel, turn.RoutedToModel, turn.RoutingReason];
            csv.AppendJoin(',', values.Select(value =>
            {
                var text = value is IFormattable number ? number.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
                if (text.Length > MaximumCsvCharacters || csv.Length + text.Length > MaximumCsvCharacters)
                    throw new InvalidOperationException("The CSV exceeds its export limit; narrow the history filters.");
                // Text cells are untrusted display metadata, never spreadsheet formulas.
                if (value is string && text.TrimStart() is { Length: > 0 } trimmed && "=+-@".Contains(trimmed[0])) text = "'" + text;
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }));
            csv.Append("\r\n");
            if (csv.Length > MaximumCsvCharacters)
                throw new InvalidOperationException("The CSV exceeds its export limit; narrow the history filters.");
        }
        return csv.ToString();
    }

    public CodexConversationCsvChunk ReadCsvChunk(int offset, string? expectedSnapshot)
    {
        var bytes = Encoding.UTF8.GetBytes(Csv());
        var snapshot = Convert.ToHexString(SHA256.HashData(bytes));
        if (expectedSnapshot is not null && !string.Equals(snapshot, expectedSnapshot, StringComparison.Ordinal))
            throw new CodexHistoryChangedException();
        if (offset < 0 || offset > bytes.Length || offset > 0 && expectedSnapshot is null)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var length = Math.Min(CsvChunkBytes, bytes.Length - offset);
        return new(snapshot, offset + length, bytes.Length, bytes.AsSpan(offset, length).ToArray());
    }
}

public sealed record CodexConversationCsvChunk(string Snapshot, int NextOffset, int TotalBytes, byte[] Data);
public sealed class CodexHistoryChangedException : IOException
{ public CodexHistoryChangedException() : base("Retained history changed during export; start a new export.") { } }
