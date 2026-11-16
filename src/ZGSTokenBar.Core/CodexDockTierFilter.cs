namespace ZGSTokenBar.Core;

/// <summary>Display-only categories. This never changes account discovery or usage collection.</summary>
public static class CodexDockTierFilter
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { "free", "plus", "pro", "other" });

    public static string[] Normalize(IEnumerable<string>? selected)
    {
        if (selected is null) return All.ToArray();
        var values = selected.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return All.Where(values.Contains).ToArray();
    }

    public static string Category(string? plan) => CodexPlanNormalization.Normalize(plan) switch
    {
        "free" => "free",
        "plus" => "plus",
        "pro" => "pro",
        _ => "other",
    };
}
