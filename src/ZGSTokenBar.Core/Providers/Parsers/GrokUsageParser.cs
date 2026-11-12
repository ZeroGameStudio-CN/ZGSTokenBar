using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>Grok (grok.com / CLI auth): plan quota, legacy remaining and RPC billing payloads.</summary>
public static partial class UsageParsers
{
    public static UsageParseResult? ParseGrok(string? json)
    {
        var root = ReadObject(json);
        if (root is not { } body) return null;
        var payload = body.ObjectProperty("result") ?? body.ObjectProperty("data") ?? body;
        var config = payload.ObjectProperty("config") ?? payload;

        if (TryLegacyRemaining(payload, out var legacy)) return legacy;

        double? used = null;
        var fromWire = false;
        var wire = Number(config, "creditUsagePercent") ?? Number(payload, "creditUsagePercent");
        if (wire is { } wirePercent && wirePercent is >= 0 and <= 100)
        {
            used = wirePercent;
            fromWire = true;
        }
        else
        {
            var demandUsed = RawCent(config, "onDemandUsed") ?? RawCent(payload, "onDemandUsed");
            var demandCap = RawCent(config, "onDemandCap") ?? RawCent(payload, "onDemandCap");
            if (demandUsed is >= 0 && demandCap is > 0)
            {
                used = JsonSupport.ClampPercent((double)(demandUsed.Value / demandCap.Value * 100m));
            }
            else if (TryLegacyRpc(payload, out var rpc))
            {
                return rpc;
            }
        }

        var period = config.ObjectProperty("currentPeriod") ?? payload.ObjectProperty("currentPeriod");
        var start = Time(period, "start") ?? Time(config, "billingPeriodStart") ?? Time(payload, "billingPeriodStart");
        var end = Time(period, "end")
            ?? Time(config, "billingPeriodEnd", "currentPeriodEnd")
            ?? Time(payload, "billingPeriodEnd", "currentPeriodEnd");
        TimeSpan? duration = start is { } periodStart && end is { } periodEnd && periodEnd > periodStart
            ? periodEnd - periodStart
            : null;
        var prepaid = DollarCent(config, "prepaidBalance") ?? DollarCent(payload, "prepaidBalance");
        var onDemandUsed = DollarCent(config, "onDemandUsed") ?? DollarCent(payload, "onDemandUsed");
        var onDemandCap = DollarCent(config, "onDemandCap") ?? DollarCent(payload, "onDemandCap");
        UsageCredits? credits = prepaid is not null || onDemandUsed is not null || onDemandCap is not null
            ? new UsageCredits("USD", prepaid, onDemandUsed, onDemandCap)
            : null;
        var products = fromWire && used is { } wireUsed
            ? ProductShares(config.ArrayProperty("productUsage") ?? payload.ArrayProperty("productUsage"), wireUsed)
            : [];
        var plan = String(config, "subscriptionTier", "subscription_tier_display")
            ?? String(payload, "subscriptionTier", "subscription_tier_display");
        if (used is null && end is null && credits is null) return null;
        var windows = new List<UsageWindow>();
        if (used is not null || end is not null)
        {
            windows.Add(new UsageWindow("credits", WindowLabel(duration), used, end, duration));
        }

        return new UsageParseResult(
            windows,
            credits,
            products,
            plan,
            used is null ? "usage_unknown" : null)
        {
            Period = start is not null || end is not null
                ? new UsageSubscriptionPeriod(start, end, "billing", period is not null ? "currentPeriod.start/end" : "billingPeriodStart/End")
                : null,
        };
    }

    public static string? GrokRemainingText(string? json)
    {
        var parsed = ParseGrok(json);
        var remaining = UsageFormatting.Remaining(parsed?.Windows.FirstOrDefault());
        return remaining is null ? null : UsageFormatting.Percent(remaining);
    }

    private static bool TryLegacyRemaining(JsonElement payload, out UsageParseResult? result)
    {
        result = null;
        if (Number(payload, "remainingPercent") is not { } remaining || remaining is < 0 or > 100) return false;
        if (payload.ObjectProperty("config") is not null) return false;
        result = new UsageParseResult(
            [new UsageWindow("credits", "Credits", JsonSupport.ClampPercent(100 - remaining), Time(payload, "resetsAt", "resetAt"), null)],
            null,
            [],
            null,
            null);
        return true;
    }

    private static bool TryLegacyRpc(JsonElement payload, out UsageParseResult? result)
    {
        result = null;
        var limit = RawCent(payload, "monthlyLimit");
        var used = RawCent(payload.ObjectProperty("usage"), "totalUsed");
        if (limit is not > 0 || used is not >= 0) return false;
        var cycle = payload.ObjectProperty("billingCycle");
        var end = Time(cycle, "billingPeriodEnd");
        var start = Time(cycle, "billingPeriodStart");
        TimeSpan? duration = start is { } periodStart && end is { } periodEnd && periodEnd > periodStart
            ? periodEnd - periodStart
            : null;
        result = new UsageParseResult(
            [new UsageWindow("credits", WindowLabel(duration), JsonSupport.ClampPercent((double)(used.Value / limit.Value * 100m)), end, duration)],
            null,
            [],
            null,
            null)
        {
            Period = start is not null || end is not null
                ? new UsageSubscriptionPeriod(start, end, "billing", "billingCycle.billingPeriodStart/End")
                : null,
        };
        return true;
    }

    private static IReadOnlyList<UsageProductShare> ProductShares(JsonElement? array, double usedPercent)
    {
        if (array is not { ValueKind: JsonValueKind.Array }) return [];
        var shares = new List<UsageProductShare>();
        foreach (var item in array.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return [];
            var name = String(item, "product", "name");
            if (name is null || Number(item, "usagePercent", "percent") is not { } percent || percent is < 0 or > 100) return [];
            shares.Add(new UsageProductShare(ProductLabel(name), percent));
        }

        var sum = shares.Sum(share => share.Percent);
        if (Math.Abs(sum - usedPercent) > 1) return [];
        return shares.Where(share => share.Percent > 0).OrderByDescending(share => share.Percent).ToArray();
    }

    private static string ProductLabel(string product) => product switch
    {
        "GrokBuild" => "Grok Build",
        "GrokChat" => "Grok Chat",
        "GrokImagine" => "Grok Imagine",
        "GrokAppBuilder" => "Grok App Builder",
        _ => product,
    };
}
