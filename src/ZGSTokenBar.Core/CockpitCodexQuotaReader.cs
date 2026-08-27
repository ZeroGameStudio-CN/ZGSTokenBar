using System.Security.Cryptography;
using System.Text.Json;

namespace ZGSTokenBar.Core;

internal sealed record CockpitCodexQuotaAccount(
    string Key,
    string? AccountId,
    string? Email,
    string? Plan,
    bool Active,
    string? AccessToken,
    CodexUsageData? Usage,
    DateTimeOffset? CapturedAt)
{
    public string? ApiProviderName { get; init; }
    public bool IsApiService => string.Equals(Plan, "api_key", StringComparison.Ordinal);
}

internal static class CockpitCodexQuotaReader
{
    internal static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromMinutes(5);
    private const int AuthenticationTagBytes = 16;
    private const int MaximumAccountFiles = 100;
    private const long MaximumJsonBytes = 1024 * 1024;

    public static IReadOnlyList<CockpitCodexQuotaAccount> Read(string home, DateTimeOffset now)
    {
        byte[]? key = null;
        try
        {
            var (selectedAccounts, currentId, activeAccountIds) = ReadSelectedAccountIds(home, now);
            if (selectedAccounts.Count == 0) return [];

            key = ReadKey(Path.Combine(home, "secure-account-storage.key"));
            if (key is null) return [];

            var accountsDirectory = Path.Combine(home, "codex_accounts");
            if (!Directory.Exists(accountsDirectory)) return [];
            var accounts = new List<CockpitCodexQuotaAccount>();
            foreach (var path in Directory
                         .EnumerateFiles(accountsDirectory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.Ordinal)
                         .Take(MaximumAccountFiles))
            {
                var account = ReadAccount(path, key, selectedAccounts, currentId, activeAccountIds, now);
                if (account is not null) accounts.Add(account);
            }

            return accounts
                .GroupBy(account => account.Key, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(account => account.CapturedAt).First())
                .OrderByDescending(account => account.Active)
                .ThenBy(account => account.Key, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static (
        IReadOnlyDictionary<string, SelectedAccount> SelectedAccounts,
        string? CurrentId,
        IReadOnlySet<string>? ActiveAccountIds)
        ReadSelectedAccountIds(string home, DateTimeOffset now)
    {
        var selectedAccounts = new Dictionary<string, SelectedAccount>(StringComparer.Ordinal);
        var invalidIds = new HashSet<string>(StringComparer.Ordinal);
        string? currentId = null;
        var activeAccountIds = CockpitCodexInstanceActivity.ReadActiveAccountIds(home);
        using (var index = ReadDocument(Path.Combine(home, "codex_accounts.json")))
        {
            currentId = index?.RootElement.StringProperty("current_account_id");
            if (index?.RootElement.TryGetProperty("accounts", out var items) == true
                && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var accountId = item.StringProperty("id");
                    var plan = CodexPlanNormalization.Normalize(item.StringProperty("plan_type"));
                    var subscription = SubscriptionActiveUntil(item);
                    if (string.IsNullOrWhiteSpace(accountId)
                        || plan is null
                        || !SubscriptionIsUsable(plan, subscription, now)
                        || IsInactive(item))
                    {
                        continue;
                    }

                    if (invalidIds.Contains(accountId)) continue;
                    var candidate = new SelectedAccount(plan, subscription);
                    if (selectedAccounts.TryGetValue(accountId, out var existing)
                        && (existing.Plan != candidate.Plan
                            || existing.SubscriptionActiveUntil != candidate.SubscriptionActiveUntil))
                    {
                        selectedAccounts.Remove(accountId);
                        invalidIds.Add(accountId);
                        continue;
                    }

                    selectedAccounts[accountId] = candidate;
                }
            }
        }

        return (selectedAccounts, currentId, activeAccountIds);
    }

    private static CockpitCodexQuotaAccount? ReadAccount(
        string path,
        byte[] key,
        IReadOnlyDictionary<string, SelectedAccount> selectedAccounts,
        string? currentId,
        IReadOnlySet<string>? activeAccountIds,
        DateTimeOffset now)
    {
        byte[]? encrypted = null;
        byte[]? ciphertext = null;
        byte[]? tag = null;
        byte[]? plaintext = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumJsonBytes) return null;
            using var envelope = JsonDocument.Parse(File.ReadAllText(path));
            var root = envelope.RootElement;
            if (!string.Equals(root.StringProperty("kind"), "codex", StringComparison.Ordinal)
                || !string.Equals(root.StringProperty("algorithm"), "AES-256-GCM", StringComparison.Ordinal))
            {
                return null;
            }

            var nonceText = root.StringProperty("nonce");
            var ciphertextText = root.StringProperty("ciphertext");
            if (string.IsNullOrWhiteSpace(nonceText) || string.IsNullOrWhiteSpace(ciphertextText)) return null;
            var nonce = Convert.FromBase64String(nonceText);
            encrypted = Convert.FromBase64String(ciphertextText);
            if (nonce.Length != 12 || encrypted.Length <= AuthenticationTagBytes) return null;

            var ciphertextLength = encrypted.Length - AuthenticationTagBytes;
            ciphertext = encrypted.AsSpan(0, ciphertextLength).ToArray();
            tag = encrypted.AsSpan(ciphertextLength, AuthenticationTagBytes).ToArray();
            plaintext = new byte[ciphertextLength];
            using (var aes = new AesGcm(key, AuthenticationTagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            using var accountDocument = JsonDocument.Parse(plaintext.AsMemory());
            var account = accountDocument.RootElement;
            var id = account.StringProperty("id");
            if (string.IsNullOrWhiteSpace(id) || !selectedAccounts.TryGetValue(id, out var selected)) return null;

            var plan = CodexPlanNormalization.Normalize(account.StringProperty("plan_type"));
            if (plan is null || !string.Equals(plan, selected.Plan, StringComparison.Ordinal)) return null;
            if (!SubscriptionIsUsable(plan, SubscriptionActiveUntil(account), now)) return null;
            if (IsInactive(account)) return null;

            var active = activeAccountIds is null
                ? string.Equals(id, currentId, StringComparison.Ordinal)
                : activeAccountIds.Contains(id);
            if (string.Equals(plan, "api_key", StringComparison.Ordinal))
            {
                return new CockpitCodexQuotaAccount(
                    $"cockpit:{id}",
                    account.StringProperty("account_id") ?? id,
                    account.StringProperty("email"),
                    plan,
                    active,
                    null,
                    null,
                    null)
                {
                    ApiProviderName = account.StringProperty("api_provider_name"),
                };
            }

            var accountId = account.StringProperty("account_id");
            if (string.IsNullOrWhiteSpace(accountId)) return null;
            var tokens = account.ObjectProperty("tokens");
            var accessToken = tokens?.StringProperty("access_token");
            var idToken = tokens?.StringProperty("id_token");
            var refreshToken = tokens?.StringProperty("refresh_token");
            if (string.IsNullOrWhiteSpace(accessToken)
                || string.IsNullOrWhiteSpace(idToken)
                || string.IsNullOrWhiteSpace(refreshToken))
            {
                return null;
            }

            var capturedAt = UnixTime(account.NumberProperty("usage_updated_at"));
            CodexUsageData? usage = null;
            JsonElement? rawData = null;
            if (capturedAt is { } snapshotAt
                && snapshotAt >= now - MaxSnapshotAge
                && snapshotAt <= now + FutureClockTolerance)
            {
                rawData = account.ObjectProperty("quota")?.ObjectProperty("raw_data");
                if (rawData?.ValueKind == JsonValueKind.Object)
                {
                    var parsed = CodexUsageParser.Parse(rawData.Value.GetRawText());
                    var usageAccountId = rawData.Value.StringProperty("account_id");
                    var usagePlan = CodexPlanNormalization.Normalize(parsed.Plan);
                    if (parsed.Windows.Any(window => window.UsedPercent is not null)
                        && string.Equals(usageAccountId, accountId, StringComparison.Ordinal)
                        && (parsed.Plan is null
                            || usagePlan is not null
                                && string.Equals(usagePlan, plan, StringComparison.Ordinal)))
                    {
                        usage = parsed with { Plan = usagePlan };
                    }
                }
            }
            if (usage is null && string.IsNullOrWhiteSpace(accessToken)) return null;

            return new CockpitCodexQuotaAccount(
                $"cockpit:{id}",
                accountId,
                account.StringProperty("email") ?? rawData?.StringProperty("email"),
                plan,
                active,
                accessToken,
                usage,
                usage is null ? null : capturedAt);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
            if (tag is not null) CryptographicOperations.ZeroMemory(tag);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[]? ReadKey(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > 256) return null;
        var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        return null;
    }

    private static JsonDocument? ReadDocument(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return !file.Exists || file.Length is <= 0 or > MaximumJsonBytes
                ? null
                : JsonDocument.Parse(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? UnixTime(double? value)
    {
        if (value is null || !double.IsFinite(value.Value) || value <= 0) return null;
        try
        {
            var timestamp = (long)value.Value;
            return timestamp > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch
        {
            return null;
        }
    }

    private sealed record SelectedAccount(string Plan, DateTimeOffset? SubscriptionActiveUntil);

    private static bool SubscriptionIsUsable(
        string plan,
        DateTimeOffset? subscription,
        DateTimeOffset now) =>
        string.Equals(plan, "free", StringComparison.Ordinal)
            || string.Equals(plan, "api_key", StringComparison.Ordinal)
            ? true
            : subscription is { } value && value > now;

    private static DateTimeOffset? SubscriptionActiveUntil(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("subscription_active_until", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (DateTimeOffset.TryParse(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed;
            }

            if (double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var numericTimestamp))
            {
                return UnixTime(numericTimestamp);
            }
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var timestamp)
            ? UnixTime(timestamp)
            : null;
    }

    private static bool IsInactive(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return true;
        if (element.TryGetProperty("disabled", out var disabled)
            && disabled.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (element.TryGetProperty("revoked", out var revoked)
            && revoked.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (element.TryGetProperty("active", out var active)
            && active.ValueKind == JsonValueKind.False)
        {
            return true;
        }

        var status = element.StringProperty("status");
        return status is not null
            && status.Trim().ToLowerInvariant() is "disabled" or "inactive" or "revoked" or "expired";
    }
}
