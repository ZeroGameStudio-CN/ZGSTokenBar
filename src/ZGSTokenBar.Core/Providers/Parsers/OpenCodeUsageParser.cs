using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>OpenCode: per-window usage.</summary>
public static partial class UsageParsers
{
    public static UsageParseResult? ParseOpenCode(string? json)
    {
        var root = ReadObject(json);
        if (root is not { } body) return null;
        var source = body.ArrayProperty("windows") ?? body.ArrayProperty("usage");
        var windows = new List<UsageWindow>();
        if (source is { } listed)
        {
            foreach (var item in listed.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                var id = String(item, "id", "name") ?? "window";
                var percent = Number(item, "percent", "usedPercent", "percentUsed");
                var remaining = Number(item, "percentRemaining", "remainingPercent");
                double? used = percent is { } direct
                    ? JsonSupport.ClampPercent(direct)
                    : remaining is { } left ? JsonSupport.ClampPercent(100 - left) : null;
                windows.Add(new UsageWindow(
                    id,
                    OpenCodeLabel(id),
                    used,
                    Time(item, "resetsAt", "resets_at", "reset_at"),
                    null));
            }
        }
        else
        {
            foreach (var name in new[] { "rolling", "weekly", "monthly" })
            {
                if (body.ObjectProperty(name) is not { } item) continue;
                var percent = Number(item, "percent", "usedPercent");
                windows.Add(new UsageWindow(
                    name,
                    OpenCodeLabel(name),
                    percent is { } value ? JsonSupport.ClampPercent(value) : null,
                    Time(item, "resetsAt", "resets_at"),
                    null));
            }
        }

        if (windows.Count == 0) return null;
        return new UsageParseResult(windows, null, [], String(body, "plan"), windows.All(window => window.UsedPercent is null) ? "usage_unknown" : null);
    }

    private static string OpenCodeLabel(string name) => name.ToLowerInvariant() switch
    {
        "rolling" => "Rolling",
        "weekly" => "Weekly",
        "monthly" => "Monthly",
        _ => name,
    };
}
