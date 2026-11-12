using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>GitHub Copilot: quota_snapshots per feature.</summary>
public static partial class UsageParsers
{
    public static UsageParseResult? ParseCopilot(string? json)
    {
        var root = ReadObject(json);
        if (root is not { } body) return null;
        var snapshots = body.ObjectProperty("quota_snapshots");
        if (snapshots is not { } quotas) return null;
        var reset = Time(body, "quota_reset_date");
        var windows = new List<UsageWindow>();
        foreach (var property in quotas.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) return null;
            var unlimited = property.Value.TryGetProperty("unlimited", out var flag)
                && flag.ValueKind == JsonValueKind.True;
            var remaining = Number(property.Value, "percent_remaining");
            double? used = remaining is { } value && value is >= 0 and <= 100
                ? JsonSupport.ClampPercent(100 - value)
                : null;
            if (!unlimited && used is null) return null;
            windows.Add(new UsageWindow(
                property.Name,
                CopilotLabel(property.Name),
                unlimited ? null : used,
                reset,
                TimeSpan.FromDays(30),
                unlimited));
        }

        if (windows.Count == 0) return null;
        return new UsageParseResult(windows, null, [], String(body, "copilot_plan", "plan_type"), null)
        {
            Period = reset is null ? null : new UsageSubscriptionPeriod(null, reset, "quota-reset", "quota_reset_date"),
        };
    }

    private static string CopilotLabel(string name) => name switch
    {
        "premium_interactions" => "Premium",
        "chat" => "Chat",
        "completions" => "Completions",
        _ => name,
    };
}
