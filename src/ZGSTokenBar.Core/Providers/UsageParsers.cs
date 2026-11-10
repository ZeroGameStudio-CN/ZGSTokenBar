using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record UsageParseResult(
    IReadOnlyList<UsageWindow> Windows,
    UsageCredits? Credits,
    IReadOnlyList<UsageProductShare> Products,
    string? Plan,
    string? ErrorKey);

public static class UsageParsers
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
            used is null ? "usage_unknown" : null);
    }

    public static UsageParseResult FromClaude(ClaudeUsageData usage)
    {
        var windows = new List<UsageWindow>();
        AddIfPresent(windows, "5h", "5h", usage.FiveHourUsedPercent, usage.FiveHourResetsAt, TimeSpan.FromHours(5));
        AddIfPresent(windows, "7d", "7d", usage.WeekUsedPercent, usage.WeekResetsAt, TimeSpan.FromDays(7));
        AddIfPresent(windows, "fable", "Fable", usage.FableWeekUsedPercent, usage.FableWeekResetsAt, TimeSpan.FromDays(7));
        return new UsageParseResult(windows, null, [], null, windows.Count == 0 ? "usage_unknown" : null);
    }

    public static UsageParseResult FromCodex(CodexUsageData usage) => new(
        usage.Windows
            .Select(window => new UsageWindow(
                window.Label,
                window.Label,
                window.UsedPercent,
                window.ResetsAt,
                window.Duration > TimeSpan.Zero ? window.Duration : null))
            .ToArray(),
        null,
        [],
        usage.Plan,
        usage.Windows.Count == 0 ? "usage_unknown" : null);

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
        return new UsageParseResult(windows, credits, [], membership, windows.Count == 0 ? "usage_unknown" : null);
    }

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
        return new UsageParseResult(windows, null, [], String(body, "copilot_plan", "plan_type"), null);
    }

    public static UsageParseResult? ParseDeepSeek(string? json)
    {
        var root = ReadObject(json);
        if (root is not { } body) return null;
        var infos = body.ArrayProperty("balance_infos");
        if (infos is not { } balances) return null;
        UsageCredits? selected = null;
        var rank = 0;
        foreach (var item in balances.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            var currency = String(item, "currency");
            var total = Decimal(item, "total_balance");
            var topped = Decimal(item, "topped_up_balance");
            var granted = Decimal(item, "granted_balance");
            if (currency is null || total is null || topped is null || granted is null) return null;
            if (topped < 0 || granted < 0 || total != topped + granted) return null;
            var preference = currency.ToUpperInvariant() switch
            {
                "CNY" => 3,
                "USD" => 2,
                _ => 1,
            };
            if (preference < rank) continue;
            rank = preference;
            selected = new UsageCredits(currency.ToUpperInvariant(), total, null, null);
        }

        if (selected is null) return null;
        var available = !body.TryGetProperty("is_available", out var flag) || flag.ValueKind != JsonValueKind.False;
        return new UsageParseResult([], selected, [], null, available ? null : "balance_unavailable");
    }

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

    public static UsageParseResult? ParseXai(string? json)
    {
        var root = ReadObject(json);
        if (root is not { } body) return null;
        decimal? balance = null;
        var currency = String(body, "currency", "currency_code") ?? "USD";
        if (Number(body, "prepaid_balance_cents", "balance_cents") is { } cents && cents >= 0)
        {
            balance = (decimal)cents / 100m;
        }
        else if (DollarCent(body, "prepaidBalance", "prepaid_balance") is { } centBalance)
        {
            balance = centBalance;
        }
        else if (Decimal(body, "balance", "amount", "prepaid_balance") is { } amount && amount >= 0)
        {
            balance = amount;
        }

        if (body.ObjectProperty("balance") is { } nested && balance is null)
        {
            balance = Decimal(nested, "amount", "value");
            currency = String(nested, "currency", "currency_code") ?? currency;
        }

        if (balance is null) return null;
        return new UsageParseResult([], new UsageCredits(currency.ToUpperInvariant(), balance, null, null), [], null, null);
    }

    public static UsageParseResult? ParseOpenRouter(string? json)
    {
        var root = ReadObject(json);
        if (root is not { } body) return null;
        var data = body.ObjectProperty("data") ?? body;
        var credits = Decimal(data, "total_credits");
        var usage = Decimal(data, "total_usage");
        if (credits is null && usage is null) return null;
        var remaining = credits is { } total && usage is { } spent ? total - spent : credits;
        if (remaining < 0) return null;
        return new UsageParseResult(
            [],
            new UsageCredits("USD", remaining, usage, credits),
            [],
            null,
            null);
    }

    public static UsageParseResult? ParseAntigravity(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var trimmed = output.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return ParseAntigravityJson(trimmed);
        }

        var windows = trimmed
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 3 && parts[1].Contains("Weekly Limit Remaining", StringComparison.Ordinal))
            .Select(parts =>
            {
                var remaining = ParseLoosePercent(parts[2]);
                return remaining is null
                    ? null
                    : new UsageWindow(parts[0], parts[0], JsonSupport.ClampPercent(100 - remaining.Value), Time(parts.Length > 3 ? parts[3] : null), TimeSpan.FromDays(7));
            })
            .OfType<UsageWindow>()
            .Take(8)
            .ToArray();
        return windows.Length == 0 ? null : new UsageParseResult(windows, null, [], null, null);
    }

    public static string? GrokRemainingText(string? json)
    {
        var parsed = ParseGrok(json);
        var remaining = UsageFormatting.Remaining(parsed?.Windows.FirstOrDefault());
        return remaining is null ? null : UsageFormatting.Percent(remaining);
    }

    public static string[] AntigravityRows(string? output) =>
        (ParseAntigravity(output)?.Windows ?? [])
            .Take(2)
            .Select(window => $"{window.Label} {UsageFormatting.Percent(UsageFormatting.Remaining(window))}")
            .ToArray();

    private static UsageParseResult? ParseAntigravityJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Array
                ? root
                : root.ArrayProperty("buckets")
                    ?? root.ArrayProperty("quotaSummaries")
                    ?? root.ArrayProperty("models")
                    ?? root.ArrayProperty("quotas");
            if (array is not { } items) return null;
            var windows = new List<UsageWindow>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                var label = String(item, "model", "displayName", "label", "name");
                if (label is null) return null;
                double? used = null;
                if (Number(item, "remainingPercent", "remaining_percent") is { } remainingPercent)
                {
                    used = JsonSupport.ClampPercent(100 - remainingPercent);
                }
                else if (Number(item, "remainingFraction", "remaining_fraction") is { } fraction)
                {
                    used = JsonSupport.ClampPercent(100 - NormalizePercent(fraction));
                }
                else if (Number(item, "usedPercent", "used_percent") is { } usedPercent)
                {
                    used = NormalizePercent(usedPercent);
                }

                windows.Add(new UsageWindow(
                    label,
                    label,
                    used,
                    Time(item, "resetTime", "reset_time", "resetsAt"),
                    TimeSpan.FromDays(7)));
            }

            return windows.Count == 0 ? null : new UsageParseResult(windows, null, [], null, null);
        }
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
            null);
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

    private static void AddIfPresent(
        List<UsageWindow> windows,
        string id,
        string label,
        double? used,
        DateTimeOffset? resetsAt,
        TimeSpan duration)
    {
        if (used is null && resetsAt is null) return;
        windows.Add(new UsageWindow(id, label, used, resetsAt, duration));
    }

    private static void AddPercent(
        List<UsageWindow> windows,
        string id,
        string label,
        double? percent,
        DateTimeOffset? reset,
        TimeSpan? duration)
    {
        if (percent is not { } value || value is < 0 or > 100) return;
        windows.Add(new UsageWindow(id, label, value, reset, duration));
    }

    private static void AddRatio(
        List<UsageWindow> windows,
        string id,
        string label,
        JsonElement node,
        DateTimeOffset? reset,
        TimeSpan? duration)
    {
        var used = Number(node, "used");
        var limit = Number(node, "limit");
        if (used is not >= 0 || limit is not > 0) return;
        windows.Add(new UsageWindow(id, label, JsonSupport.ClampPercent(used.Value / limit.Value * 100), reset, duration));
    }

    private static string WindowLabel(TimeSpan? duration)
    {
        var days = duration?.TotalDays ?? 0;
        if (days is >= 6 and <= 8) return "Weekly";
        if (days is >= 27 and <= 32) return "Monthly";
        return "Credits";
    }

    private static string ProductLabel(string product) => product switch
    {
        "GrokBuild" => "Grok Build",
        "GrokChat" => "Grok Chat",
        "GrokImagine" => "Grok Imagine",
        "GrokAppBuilder" => "Grok App Builder",
        _ => product,
    };

    private static string CopilotLabel(string name) => name switch
    {
        "premium_interactions" => "Premium",
        "chat" => "Chat",
        "completions" => "Completions",
        _ => name,
    };

    private static string OpenCodeLabel(string name) => name.ToLowerInvariant() switch
    {
        "rolling" => "Rolling",
        "weekly" => "Weekly",
        "monthly" => "Monthly",
        _ => name,
    };

    private static double NormalizePercent(double value) =>
        JsonSupport.ClampPercent(value is > 0 and <= 1 ? value * 100 : value);

    private static double? ParseLoosePercent(string text)
    {
        var cleaned = text.Trim().TrimEnd('%');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static JsonElement? ReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? Number(JsonElement? element, params string[] names)
    {
        if (element is not { } node) return null;
        foreach (var name in names)
        {
            if (!node.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static decimal? Decimal(JsonElement? element, params string[] names)
    {
        if (element is not { } node) return null;
        foreach (var name in names)
        {
            if (!node.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? String(JsonElement? element, params string[] names) =>
        element is { } node ? node.StringProperty(names) : null;

    private static DateTimeOffset? Time(JsonElement? element, params string[] names)
    {
        if (element is not { } node) return null;
        foreach (var name in names)
        {
            if (!node.TryGetProperty(name, out var value)) continue;
            var parsed = Time(value);
            if (parsed is not null) return parsed;
        }

        return null;
    }

    private static DateTimeOffset? Time(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? Time(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return Time(value.GetString());
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var timestamp))
        {
            try
            {
                return timestamp > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                    : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static decimal? RawCent(JsonElement? element, string name)
    {
        if (element is not { } node || !node.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var direct)) return direct;
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (!value.TryGetProperty("val", out var inner) || inner.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return 0;
        }

        return inner.ValueKind == JsonValueKind.Number && inner.TryGetDecimal(out var cents) ? cents : null;
    }

    private static decimal? DollarCent(JsonElement? element, params string[] names)
    {
        foreach (var name in names)
        {
            var raw = RawCent(element, name);
            if (raw is { } cents)
            {
                return element is { } node
                    && node.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.Object
                    ? cents / 100m
                    : cents;
            }
        }

        return null;
    }

    private static decimal? Cents(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number >= 0)
        {
            return decimal.Truncate(number) == number ? number / 100m : number;
        }

        return null;
    }
}
