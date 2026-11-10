using System.Globalization;

namespace ZGSTokenBar.Core;

public static class UsageFormatting
{
    public static double? Remaining(UsageWindow? window) => window switch
    {
        null => null,
        { Unlimited: true } => null,
        { UsedPercent: { } used } => Math.Clamp(100 - used, 0, 100),
        _ => null,
    };

    public static UsageWindow? Primary(UsageAccountSnapshot account) =>
        account.Windows.FirstOrDefault(window => window.UsedPercent is not null || window.Unlimited)
        ?? account.Windows.FirstOrDefault();

    public static string Overall(UsageBoard board, bool chinese)
    {
        string? name = null;
        double? tightest = null;
        foreach (var provider in board.Providers)
        {
            foreach (var account in provider.Accounts)
            {
                var remaining = Remaining(Primary(account));
                if (remaining is not { } value) continue;
                if (tightest is null || value < tightest)
                {
                    tightest = value;
                    name = provider.DisplayName;
                }
            }
        }

        if (tightest is null || name is null)
        {
            return chinese ? "暂无读数" : "No reading";
        }

        var providers = board.Providers.Count;
        return chinese
            ? $"最紧 {Percent(tightest)} · {name} · {providers} 个工具"
            : $"Tightest {Percent(tightest)} · {name} · {providers} tools";
    }

    public static string Percent(double? remaining) =>
        remaining is { } value
            ? value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : "—";

    public static string Compact(UsageProviderSnapshot provider, bool chinese)
    {
        if (provider.Accounts.Count == 0)
        {
            return chinese ? "未登录" : "Signed out";
        }

        var primaries = provider.Accounts.Select(Primary).ToArray();
        var remainings = primaries.Select(Remaining).OfType<double>().ToArray();
        var tightest = remainings.Length == 0 ? (double?)null : remainings.Min();
        string text;
        if (tightest is { } remaining)
        {
            text = Percent(remaining);
        }
        else if (provider.Accounts[0].Credits is { PrepaidBalance: { } balance } credits)
        {
            text = Money(credits.Currency, balance);
        }
        else
        {
            text = Error(provider.Accounts[0].ErrorKey, chinese);
        }
        if (provider.Accounts.Count == 1) return text;
        return chinese
            ? $"{text} · {provider.Accounts.Count} 个账号"
            : $"{text} · {provider.Accounts.Count} accounts";
    }

    public static string Error(string? errorKey, bool chinese) => errorKey switch
    {
        "credential_expired" => chinese ? "登录已过期" : "Sign-in expired",
        "missing_credentials" => chinese ? "未找到登录" : "No sign-in",
        "credential_rejected" => chinese ? "登录被拒绝" : "Sign-in rejected",
        "rate_limited" => chinese ? "请求过于频繁" : "Rate limited",
        "endpoint_blocked" => chinese ? "端点被拒绝" : "Endpoint blocked",
        "usage_unknown" => chinese ? "用量未知" : "Usage unknown",
        "balance_unavailable" => chinese ? "余额暂不可用" : "Balance unavailable",
        "payload_too_large" => chinese ? "响应过大" : "Response too large",
        "http_error" => chinese ? "服务暂时失败" : "Service error",
        _ => chinese ? "额度不可读" : "Quota unavailable",
    };

    public static string Reset(DateTimeOffset? resetsAt, DateTimeOffset now, bool chinese)
    {
        if (resetsAt is not { } at) return chinese ? "无重置时间" : "No reset";
        var remaining = at - now;
        if (remaining <= TimeSpan.Zero) return chinese ? "即将重置" : "Resetting";
        if (remaining.TotalDays >= 1)
        {
            return chinese
                ? $"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时"
                : $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        return chinese
            ? $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分"
            : $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
    }

    public static string Money(string? currency, decimal amount)
    {
        var text = amount.ToString("0.##", CultureInfo.InvariantCulture);
        return currency?.ToUpperInvariant() switch
        {
            "USD" => "$" + text,
            "CNY" => "¥" + text,
            "CENTS" => text + "¢",
            { Length: > 0 } code => code + " " + text,
            _ => text,
        };
    }

    public static string Tokens(long tokens)
    {
        var absolute = Math.Abs((double)tokens);
        if (absolute >= 1_000_000)
        {
            return (tokens / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        if (absolute >= 1_000)
        {
            return (tokens / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }

        return tokens.ToString(CultureInfo.InvariantCulture);
    }
}
