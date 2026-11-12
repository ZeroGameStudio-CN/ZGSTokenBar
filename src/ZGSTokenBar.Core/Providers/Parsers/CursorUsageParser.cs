using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>Cursor: dashboard usage-summary (plan percent, included/on-demand ratios).</summary>
public static partial class UsageParsers
{
    public static UsageParseResult? ParseCursor(string? json)
    {
        var root = ReadObject(json);
        if (root is not { } body) return null;
        var reset = Time(body, "billingCycleEnd");
        var start = Time(body, "billingCycleStart");
        TimeSpan? duration = start is { } periodStart && reset is { } periodEnd && periodEnd > periodStart
            ? periodEnd - periodStart
            : null;
        var individual = body.ObjectProperty("individualUsage");
        var plan = individual?.ObjectProperty("plan");
        var windows = new List<UsageWindow>();
        if (plan is { } planNode)
        {
            AddPercent(windows, "auto", "Auto", Number(planNode, "autoPercentUsed"), reset, duration);
            AddPercent(windows, "api", "API", Number(planNode, "apiPercentUsed"), reset, duration);
            if (windows.Count == 0)
            {
                AddPercent(windows, "included", "Included", Number(planNode, "totalPercentUsed"), reset, duration);
            }

            if (windows.Count == 0)
            {
                AddRatio(windows, "included", "Included", planNode, reset, duration);
            }
        }

        if (individual?.ObjectProperty("overall") is { } overall)
        {
            AddRatio(windows, "overall", "Overall", overall, reset, duration);
        }

        if (body.ObjectProperty("teamUsage")?.ObjectProperty("pooled") is { } pooled)
        {
            AddRatio(windows, "pool", "Pool", pooled, reset, duration);
        }

        var onDemand = individual?.ObjectProperty("onDemand") ?? body.ObjectProperty("teamUsage")?.ObjectProperty("onDemand");
        var credits = onDemand is null ? null : new UsageCredits(
            "USD",
            null,
            Cents(onDemand.Value, "used"),
            Cents(onDemand.Value, "limit"));
        var membership = String(body, "membershipType");
        if (windows.Count == 0 && credits?.OnDemandUsed is null && credits?.OnDemandCap is null) return null;
        return new UsageParseResult(windows, credits, [], membership, windows.Count == 0 ? "usage_unknown" : null)
        {
            Period = start is not null || reset is not null
                ? new UsageSubscriptionPeriod(start, reset, "billing", "billingCycleStart/End")
                : null,
        };
    }
}
