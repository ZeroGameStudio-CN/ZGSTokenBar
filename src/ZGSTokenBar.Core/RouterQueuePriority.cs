namespace ZGSTokenBar.Core;

/// <summary>
/// Compact label for Router's admission priority. Router still owns queue order;
/// this only maps <c>queuePriority</c> / <c>queuePriorityReason</c> for display.
/// </summary>
public static class RouterQueuePriority
{
    public readonly record struct Display(string Label, string Tone, string ToolTip);

    public static Display Describe(string? priority, string? reason)
    {
        var token = priority?.Trim() ?? "";
        if (token.Length == 0) return new("—", "missing", "");

        var (label, tone, full) = token.ToLowerInvariant() switch
        {
            "high" => ("高", "high", "高优先级"),
            "normal" => ("普通", "normal", "普通优先级"),
            "low" => ("低", "low", "低优先级"),
            _ when IsCompactToken(token) => (token, "unknown", token),
            _ => ("—", "missing", ""),
        };
        var detail = reason?.Trim() ?? "";
        var tip = detail.Length == 0 ? full : full + " · " + detail;
        return new(label, tone, tip);
    }

    private static bool IsCompactToken(string token) =>
        token.Length <= 12
        && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
