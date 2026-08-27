using System.Globalization;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record CodexAccountInfo(
    string AccountId,
    string? Email,
    string? Plan,
    bool Active,
    int AccountCount = 1);

public static class CockpitCodexAccountDirectory
{
    private const long MaximumJsonBytes = 1024 * 1024;

    public static IReadOnlyList<CodexAccountInfo> Read() => Read(DefaultHome());

    internal static IReadOnlyList<CodexAccountInfo> Read(string home)
    {
        try
        {
            var path = Path.Combine(home, "codex_accounts.json");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumJsonBytes) return [];

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var currentId = root.StringProperty("current_account_id");
            var activeAccountIds = CockpitCodexInstanceActivity.ReadActiveAccountIds(home);
            if (!root.TryGetProperty("accounts", out var accounts)
                || accounts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<(CodexAccountInfo Account, int Index, DateTimeOffset? LastUsed)>();
            var index = 0;
            foreach (var item in accounts.EnumerateArray())
            {
                var accountId = item.StringProperty("id");
                if (string.IsNullOrWhiteSpace(accountId)) continue;

                result.Add((
                    new CodexAccountInfo(
                        accountId,
                        item.StringProperty("email"),
                        item.StringProperty("plan_type"),
                        activeAccountIds is null
                            ? string.Equals(accountId, currentId, StringComparison.Ordinal)
                            : activeAccountIds.Contains(accountId)),
                    index,
                    item.TryGetProperty("last_used", out var lastUsed)
                        ? ParseTime(lastUsed)
                        : null));
                index++;
            }

            var sorted = result
                .OrderBy(item => PlanSortRank(item.Account.Plan))
                .ThenByDescending(item => item.Account.Active)
                .ThenByDescending(item => item.LastUsed)
                .ThenBy(item => item.Index)
                .Select(item => item.Account)
                .ToArray();
            var apiServices = sorted
                .Where(account => IsApiService(account) && account.Active)
                .ToArray();
            if (apiServices.Length == 0)
            {
                return sorted
                    .Where(account => !IsApiService(account))
                    .ToArray();
            }

            var firstApiIndex = Array.FindIndex(
                sorted,
                IsApiService);
            var aggregate = new CodexAccountInfo(
                "cockpit:api-services",
                null,
                "api_key",
                apiServices.Any(account => account.Active),
                apiServices.Length);
            return sorted
                .Take(firstApiIndex)
                .Concat([aggregate])
                .Concat(sorted.Skip(firstApiIndex).Where(account => !IsApiService(account)))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static DateTimeOffset? ParseTime(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var unix)) return null;
        try
        {
            return unix > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                : DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        catch
        {
            return null;
        }
    }

    private static string DefaultHome() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".antigravity_cockpit");

    private static bool IsApiService(CodexAccountInfo account) =>
        string.Equals(
            CodexPlanNormalization.Normalize(account.Plan),
            "api_key",
            StringComparison.Ordinal);

    private static int PlanSortRank(string? plan) => CodexPlanNormalization.Normalize(plan) switch
    {
        "pro" => 0,
        "plus" => 1,
        "api_key" => 2,
        "free" => 3,
        _ => 4,
    };
}

internal static class CodexPlanNormalization
{
    internal static string? Normalize(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return null;

        var compact = string.Concat(plan.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return compact switch
        {
            "pro" or "chatgptpro" => "pro",
            "plus" or "chatgptplus" => "plus",
            "free" or "chatgptfree" => "free",
            "apikey" => "api_key",
            _ => null,
        };
    }
}

public static class CodexAccountFormatting
{
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "Codex account";
        var separator = email.IndexOf('@');
        if (separator <= 0 || separator == email.Length - 1) return "Codex account";

        var local = email[..separator];
        var domain = email[(separator + 1)..];
        var maskedLocal = local.Length switch
        {
            1 => "*",
            2 => $"{local[0]}*",
            _ => $"{local[0]}***{local[^1]}",
        };
        return $"{maskedLocal}@{domain}";
    }

    public static string PlanLabel(string? plan)
    {
        var normalized = CodexPlanNormalization.Normalize(plan);
        return normalized switch
        {
            "api_key" => "API key",
            "free" => "free",
            "plus" => "plus",
            "pro" => "pro",
            _ => string.IsNullOrWhiteSpace(plan) ? "unknown" : plan.Trim(),
        };
    }
}
