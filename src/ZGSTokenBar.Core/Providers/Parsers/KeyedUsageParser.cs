using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>API-key providers (DeepSeek, xAI, OpenRouter): balances and credits.</summary>
public static partial class UsageParsers
{
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
}
