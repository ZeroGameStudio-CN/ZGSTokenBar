using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZGSTokenBar.Core;

public sealed class CodexQuotaService
{
    private const string DefaultBaseUrl = "https://chatgpt.com/backend-api";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string DefaultClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private readonly HttpClient _httpClient;
    private readonly string _cockpitHome;

    public CodexQuotaService(HttpClient httpClient)
        : this(httpClient, DefaultCockpitHome())
    {
    }

    internal CodexQuotaService(HttpClient httpClient, string cockpitHome)
    {
        _httpClient = httpClient;
        _cockpitHome = cockpitHome;
    }

    public async Task<ProviderResult> FetchAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var nativeCredentials = ReadCredentials();
        var cockpitAccounts = CockpitCodexQuotaReader.Read(_cockpitHome, now);
        var apiServices = cockpitAccounts
            .Where(account => account.IsApiService && account.Active)
            .ToArray();
        var cockpitCredentials = cockpitAccounts
            .Where(account => !string.IsNullOrWhiteSpace(account.AccessToken))
            .Select(account => new CodexCredential(
                "",
                account.Key,
                "Codex",
                account.Plan,
                account.Active,
                account.AccessToken!,
                null,
                account.AccountId,
                account.Email,
                DefaultClientId,
                CredentialSupport.JwtExpiry(account.AccessToken)))
            .ToArray();
        var credentials = MergeLiveCredentials(nativeCredentials, cockpitCredentials);
        if (credentials.Count == 0 && cockpitAccounts.Count == 0)
        {
            return Missing(
                "Codex OAuth credentials were not found.",
                ProviderHealthCode.MissingCredentials);
        }

        var usageUrl = ResolveUsageUrl();
        AccountResult[] liveAccounts;
        if (credentials.Count == 0)
        {
            liveAccounts = [];
        }
        else if (usageUrl is null)
        {
            liveAccounts = credentials
                .Select((credential, index) => new AccountResult(
                    credential,
                    index,
                    null,
                    "The configured Codex usage endpoint is not allowed.",
                    ProviderHealthCode.EndpointBlocked,
                    null))
                .ToArray();
        }
        else
        {
            var tasks = credentials.Select((credential, index) =>
                FetchAccountAsync(credential, index, usageUrl, cancellationToken));
            liveAccounts = await Task.WhenAll(tasks);
        }

        var cachedAccounts = cockpitAccounts
            .Where(account => account.Usage is not null)
            .Select((account, index) => new AccountResult(
                new CodexCredential(
                    "",
                    account.Key,
                    "Codex",
                    account.Plan,
                    account.Active,
                    "",
                    null,
                    account.AccountId,
                    account.Email,
                    DefaultClientId,
                    null),
                credentials.Count + index,
                account.Usage!,
                null,
                ProviderHealthCode.Cached,
                null,
                false,
                account.CapturedAt))
            .ToArray();
        var accounts = liveAccounts.Concat(cachedAccounts).ToArray();
        var visibleCandidates = accounts.Where(account => account.Credential.Active).ToArray();
        var visibleAccounts = DistinctVisibleAccounts(visibleCandidates);
        var quotaCards = visibleAccounts
            .Select((account, visibleIndex) => ToCard(account, visibleAccounts.Length, visibleIndex))
            .ToArray();
        var cards = quotaCards
            .Concat(apiServices.Length == 0 ? [] : [ToServiceCard(apiServices)])
            .ToArray();

        var connected = visibleAccounts.Any(account => account.Usage is not null);
        if (!connected && apiServices.Length == 0 && quotaCards.Length == 0)
        {
            var preferred = accounts.FirstOrDefault(account => account.Credential.Active);
            cards = preferred is null
                ? []
                : [PlaceholderCard(preferred.Credential)];
        }

        var failure = accounts.FirstOrDefault(account => !string.IsNullOrWhiteSpace(account.Error));
        var hasLiveQuota = visibleAccounts.Any(account => account.Live && account.Usage is not null);
        var detail = connected
            ? hasLiveQuota ? "Codex quota is current." : "Codex quota is cached."
            : apiServices.Length > 0 ? "Codex API service is configured; quota is unavailable."
            : failure?.Error ?? "Codex quota is unavailable.";
        return new ProviderResult(
            ProviderKind.Codex,
            cards,
            new ProviderHealth(
                ProviderKind.Codex,
                connected,
                detail,
                connected
                    ? hasLiveQuota ? ProviderHealthCode.Current : ProviderHealthCode.Cached
                    : failure?.ErrorCode ?? ProviderHealthCode.Unavailable,
                failure?.HttpStatus))
        {
            CodexAccounts = BuildAccountQuotas(cockpitAccounts, accounts, now),
            CodexQuotaTokenCounters = visibleAccounts
                .Where(account => account.Live
                    && account.Usage is not null
                    && account.ProfileLifetimeTokens is { } lifetimeTokens
                    && lifetimeTokens >= 0)
                .Select(account => new CodexQuotaTokenCounter(
                    StableCardKey(account.Credential.Key),
                    account.CapturedAt ?? now,
                    account.ProfileLifetimeTokens!.Value,
                    account.ProfileRecentWeeklyAverageTokens))
                .GroupBy(counter => counter.CardKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray(),
            ReplaceCachedCards = visibleAccounts.Length == 0
                && accounts.All(account => !account.Credential.Active),
        };
    }

    private async Task<AccountResult> FetchAccountAsync(
        CodexCredential credential,
        int displayIndex,
        Uri usageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!RefreshDisabled()
                && credential.ExpiresAt is { } expiry
                && expiry <= DateTimeOffset.UtcNow.AddSeconds(60)
                && !string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                credential = await RefreshAsync(credential, cancellationToken) ?? credential;
            }

            var response = await GetUsageAsync(credential, usageUrl, cancellationToken);
            if (!RefreshDisabled()
                && response.StatusCode == HttpStatusCode.Unauthorized
                && !string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                var refreshed = await RefreshAsync(credential, cancellationToken);
                if (refreshed is not null)
                {
                    credential = refreshed;
                    response.Dispose();
                    response = await GetUsageAsync(credential, usageUrl, cancellationToken);
                }
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return new AccountResult(
                        credential,
                        displayIndex,
                        null,
                        $"Codex API returned HTTP {(int)response.StatusCode}.",
                        response.StatusCode == HttpStatusCode.Unauthorized
                            ? ProviderHealthCode.OAuthRefreshFailed
                            : ProviderHealthCode.HttpError,
                        (int)response.StatusCode);
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var usage = CodexUsageParser.Parse(json);
                var profileTokens = await TryFetchProfileTokenStatsAsync(
                    credential,
                    usageUrl,
                    cancellationToken);
                var capturedAt = DateTimeOffset.UtcNow;
                return new AccountResult(
                    credential,
                    displayIndex,
                    usage,
                    null,
                    ProviderHealthCode.Current,
                    null,
                    true,
                    capturedAt,
                    profileTokens?.LifetimeTokens,
                    profileTokens?.RecentWeeklyAverageTokens);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AccountResult(
                credential,
                displayIndex,
                null,
                "Codex API request timed out.",
                ProviderHealthCode.Timeout,
                null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            return new AccountResult(
                credential,
                displayIndex,
                null,
                $"Codex quota unavailable: {exception.Message}",
                ProviderHealthCode.Unavailable,
                null);
        }
    }

    private async Task<ProfileTokenStats?> TryFetchProfileTokenStatsAsync(
        CodexCredential credential,
        Uri usageUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendGetAsync(
                credential,
                ProfileUrl(usageUrl),
                cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseProfileTokenStats(json, DateOnly.FromDateTime(DateTime.UtcNow));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private Task<HttpResponseMessage> GetUsageAsync(
        CodexCredential credential,
        Uri usageUrl,
        CancellationToken cancellationToken) =>
        SendGetAsync(credential, usageUrl, cancellationToken);

    private async Task<HttpResponseMessage> SendGetAsync(
        CodexCredential credential,
        Uri url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.UserAgent.ParseAdd("codex-cli");
        if (!string.IsNullOrWhiteSpace(credential.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        }

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    private static Uri ProfileUrl(Uri usageUrl)
    {
        const string usageSuffix = "/usage";
        var absolute = usageUrl.AbsoluteUri;
        return absolute.EndsWith(usageSuffix, StringComparison.Ordinal)
            ? new Uri(absolute[..^usageSuffix.Length] + "/profiles/me")
            : new Uri(usageUrl, "profiles/me");
    }

    private static ProfileTokenStats? ParseProfileTokenStats(string json, DateOnly today)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var stats = root.ObjectProperty("stats");
        if (stats is null
            || stats.Value.ValueKind != JsonValueKind.Object
            || !stats.Value.TryGetProperty("lifetime_tokens", out var lifetimeTokens)
            || lifetimeTokens.ValueKind != JsonValueKind.Number
            || !lifetimeTokens.TryGetInt64(out var parsed)
            || parsed < 0)
        {
            return null;
        }

        var recentWeeklyAverageTokens = stats.Value.TryGetProperty(
                "daily_usage_buckets",
                out var dailyUsageBuckets)
            ? ParseRecentWeeklyAverageTokens(dailyUsageBuckets, today)
            : null;
        return new ProfileTokenStats(parsed, recentWeeklyAverageTokens);
    }

    private static long? ParseRecentWeeklyAverageTokens(JsonElement dailyUsageBuckets, DateOnly today)
    {
        if (dailyUsageBuckets.ValueKind != JsonValueKind.Array) return null;

        var dates = new HashSet<DateOnly>();
        long sum = 0;
        var firstDate = today.DayNumber < 27
            ? (DateOnly?)null
            : DateOnly.FromDayNumber(today.DayNumber - 27);
        foreach (var bucket in dailyUsageBuckets.EnumerateArray())
        {
            if (bucket.ValueKind != JsonValueKind.Object) return null;

            DateOnly? date = null;
            long? tokens = null;
            foreach (var property in bucket.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "start_date":
                        if (date is not null
                            || property.Value.ValueKind != JsonValueKind.String
                            || !TryParseDailyUsageDate(property.Value.GetString(), out var parsedDate))
                        {
                            return null;
                        }

                        date = parsedDate;
                        break;
                    case "tokens":
                        if (tokens is not null
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out var parsedTokens)
                            || parsedTokens < 0)
                        {
                            return null;
                        }

                        tokens = parsedTokens;
                        break;
                }
            }

            if (date is null || tokens is null) return null;
            if (!dates.Add(date.Value)) return null;
            if (firstDate is { } startDate
                && date.Value >= startDate
                && date.Value <= today)
            {
                try
                {
                    sum = checked(sum + tokens.Value);
                }
                catch (OverflowException)
                {
                    return null;
                }
            }
        }

        return (long)decimal.Round(sum / 4m, 0, MidpointRounding.AwayFromZero);
    }

    private static bool TryParseDailyUsageDate(string? value, out DateOnly date)
    {
        if (value is null
            || value.Length != 10
            || value[4] != '-'
            || value[7] != '-'
            || !DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
        {
            date = default;
            return false;
        }

        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == value;
    }

    private async Task<CodexCredential?> RefreshAsync(CodexCredential credential, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credential.RefreshToken!,
                    ["client_id"] = credential.ClientId,
                }),
            };
            request.Headers.UserAgent.ParseAdd("codex-cli");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var refreshed = JsonNode.Parse(json)?.AsObject();
            var accessToken = refreshed?["access_token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(accessToken)) return null;

            var currentContents = File.ReadAllText(credential.Path);
            var root = JsonNode.Parse(currentContents)?.AsObject();
            var tokens = root?["tokens"]?.AsObject();
            if (root is null || tokens is null) return null;
            var currentAccessToken = tokens["access_token"]?.GetValue<string>();
            if (!string.Equals(currentAccessToken, credential.AccessToken, StringComparison.Ordinal))
            {
                return ReadCredential(
                    credential.Path,
                    credential.Key,
                    credential.Label,
                    credential.Plan,
                    credential.AccountId,
                    credential.Email,
                    credential.Active);
            }
            tokens["access_token"] = accessToken;
            var rotatingRefreshToken = refreshed?["refresh_token"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(rotatingRefreshToken)) tokens["refresh_token"] = rotatingRefreshToken;
            var idToken = refreshed?["id_token"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(idToken)) tokens["id_token"] = idToken;
            root["last_refresh"] = DateTimeOffset.UtcNow.ToString("O");
            if (!CredentialSupport.AtomicWrite(
                credential.Path,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                currentContents))
            {
                return ReadCredential(
                    credential.Path,
                    credential.Key,
                    credential.Label,
                    credential.Plan,
                    credential.AccountId,
                    credential.Email,
                    credential.Active);
            }

            return credential with
            {
                AccessToken = accessToken,
                RefreshToken = rotatingRefreshToken ?? credential.RefreshToken,
                ExpiresAt = CredentialSupport.JwtExpiry(accessToken),
            };
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<CodexCredential> ReadCredentials()
    {
        var home = CodexHome();
        var rootCredential = ReadCredential(
            Path.Combine(home, "auth.json"),
            "active",
            "Codex",
            null,
            null,
            null,
            true);
        var registryPath = Path.Combine(home, "accounts", "registry.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            {
                return rootCredential is null ? [] : [rootCredential];
            }

            var activeKey = root.StringProperty("active_account_key");
            var credentials = new List<CodexCredential>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            var rootMatched = false;
            foreach (var account in accounts.EnumerateArray())
            {
                var key = account.StringProperty("account_key");
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
                var label = account.StringProperty("alias", "account_name", "email") ?? $"Codex {index + 1}";
                var plan = account.StringProperty("plan");
                var accountId = account.StringProperty("chatgpt_account_id");
                var accountEmail = account.StringProperty("email");
                var matchesRoot = rootCredential is not null
                    && !rootMatched
                    && ((!string.IsNullOrWhiteSpace(accountId)
                            && string.Equals(rootCredential.AccountId, accountId, StringComparison.Ordinal))
                        || (!string.IsNullOrWhiteSpace(accountEmail)
                            && string.Equals(rootCredential.Email, accountEmail, StringComparison.OrdinalIgnoreCase)));
                var active = rootCredential is null
                    ? string.Equals(activeKey, key, StringComparison.Ordinal)
                    : matchesRoot;
                CodexCredential? credential = null;
                if (matchesRoot)
                {
                    credential = rootCredential! with
                    {
                        Key = key,
                        Label = label,
                        Plan = plan,
                        Active = true,
                        AccountId = rootCredential.AccountId ?? accountId,
                        Email = rootCredential.Email ?? accountEmail,
                    };
                    rootMatched = true;
                }
                else
                {
                    var authName = Base64Url(key) + ".auth.json";
                    credential = ReadCredential(
                        Path.Combine(home, "accounts", authName),
                        key,
                        label,
                        plan,
                        accountId,
                        accountEmail,
                        active);
                }

                if (credential is not null) credentials.Add(credential);
                index++;
            }

            if (rootCredential is not null && !rootMatched) credentials.Insert(0, rootCredential);
            return credentials.Count > 0
                ? credentials
                : rootCredential is null ? [] : [rootCredential];
        }
        catch
        {
            return rootCredential is null ? [] : [rootCredential];
        }
    }

    private static CodexCredential? ReadCredential(
        string path,
        string key,
        string label,
        string? plan,
        string? fallbackAccountId,
        string? fallbackEmail,
        bool active)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var tokens = root.ObjectProperty("tokens");
            if (tokens is null) return null;
            var accessToken = tokens.Value.StringProperty("access_token");
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            var idToken = tokens.Value.StringProperty("id_token");
            var accountId = tokens.Value.StringProperty("account_id")
                ?? root.StringProperty("account_id")
                ?? OpenAiAccountId(idToken)
                ?? OpenAiAccountId(accessToken)
                ?? fallbackAccountId;
            var email = tokens.Value.StringProperty("email")
                ?? root.StringProperty("email")
                ?? CredentialSupport.JwtString(idToken, "email")
                ?? CredentialSupport.JwtString(accessToken, "email")
                ?? fallbackEmail;
            var clientId = CredentialSupport.JwtAudience(idToken);
            if (string.IsNullOrWhiteSpace(clientId) || !clientId.StartsWith("app_", StringComparison.Ordinal))
            {
                clientId = DefaultClientId;
            }

            return new CodexCredential(
                path,
                key,
                label,
                plan,
                active,
                accessToken,
                tokens.Value.StringProperty("refresh_token"),
                accountId,
                email,
                clientId,
                CredentialSupport.JwtExpiry(accessToken));
        }
        catch
        {
            return null;
        }
    }

    private static string? OpenAiAccountId(string? token) =>
        CredentialSupport.JwtNestedString(token, "https://api.openai.com/auth", "chatgpt_account_id")
        ?? CredentialSupport.JwtString(
            token,
            "chatgpt_account_id",
            "https://api.openai.com/auth.chatgpt_account_id");

    private static Uri? ResolveUsageUrl()
    {
        var baseUrl = DefaultBaseUrl;
        var configPath = Path.Combine(CodexHome(), "config.toml");
        try
        {
            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Split('#', 2)[0].Trim();
                if (!line.StartsWith("chatgpt_base_url", StringComparison.Ordinal) || !line.Contains('=')) continue;
                baseUrl = line[(line.IndexOf('=') + 1)..].Trim().Trim('"', '\'');
                break;
            }
        }
        catch
        {
            // The official endpoint remains the safe default.
        }

        if (!Uri.TryCreate(baseUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || baseUri.Host is not ("chatgpt.com" or "chat.openai.com"))
        {
            return null;
        }

        var normalized = baseUri.AbsoluteUri.TrimEnd('/');
        if (!normalized.Contains("/backend-api", StringComparison.Ordinal)) normalized += "/backend-api";
        if (!normalized.EndsWith("/wham/usage", StringComparison.Ordinal)) normalized += "/wham/usage";
        return new Uri(normalized);
    }

    internal static string CodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Path.GetFullPath(configured);
    }

    private static string DefaultCockpitHome() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".antigravity_cockpit");

    private static bool RefreshDisabled() =>
        string.Equals(Environment.GetEnvironmentVariable("ZTB_DISABLE_REFRESH"), "1", StringComparison.Ordinal);

    private static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    internal static string StableCardKey(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"codex.{Convert.ToHexString(digest)[..10].ToLowerInvariant()}";
    }

    private static IReadOnlyList<CodexCredential> MergeLiveCredentials(
        IReadOnlyList<CodexCredential> nativeCredentials,
        IReadOnlyList<CodexCredential> cockpitCredentials)
    {
        var merged = nativeCredentials.ToList();
        foreach (var credential in cockpitCredentials)
        {
            var duplicateIndex = merged.FindIndex(existing =>
                string.Equals(existing.AccessToken, credential.AccessToken, StringComparison.Ordinal)
                || SameIdentity(existing, credential));
            if (duplicateIndex < 0)
            {
                merged.Add(credential);
            }
            else if (credential.Active && !merged[duplicateIndex].Active)
            {
                merged[duplicateIndex] = credential;
            }
            else if (credential.Active && !string.IsNullOrWhiteSpace(credential.Plan))
            {
                merged[duplicateIndex] = merged[duplicateIndex] with { Plan = credential.Plan };
            }
        }
        return merged;
    }

    private static IReadOnlyList<CodexAccountQuota> BuildAccountQuotas(
        IReadOnlyList<CockpitCodexQuotaAccount> cockpitAccounts,
        IReadOnlyList<AccountResult> accounts,
        DateTimeOffset now)
    {
        return cockpitAccounts
            .Where(account => !account.IsApiService && !string.IsNullOrWhiteSpace(account.AccountId))
            .Select(account =>
            {
                var result = accounts
                    .Where(candidate =>
                        string.Equals(candidate.Credential.Key, account.Key, StringComparison.Ordinal)
                        || !string.IsNullOrWhiteSpace(account.AccessToken)
                            && string.Equals(
                                candidate.Credential.AccessToken,
                                account.AccessToken,
                                StringComparison.Ordinal)
                        || SameAccountMetadata(candidate.Credential, account))
                    .OrderByDescending(candidate => candidate.Usage is not null)
                    .ThenByDescending(candidate => candidate.Live)
                    .FirstOrDefault();
                var usage = result?.Usage ?? account.Usage;
                return new CodexAccountQuota(
                    account.Key["cockpit:".Length..],
                    usage?.Windows ?? [],
                    usage is null
                        ? account.CapturedAt
                        : result?.CapturedAt ?? now,
                    result?.Error)
                {
                    CardKey = result is null
                        ? null
                        : StableCardKey(result.Credential.Key),
                };
            })
            .ToArray();
    }

    private static bool SameAccountMetadata(
        CodexCredential credential,
        CockpitCodexQuotaAccount account)
    {
        if (!string.IsNullOrWhiteSpace(credential.AccountId)
            && !string.IsNullOrWhiteSpace(account.AccountId))
        {
            return string.Equals(credential.AccountId, account.AccountId, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(credential.Email)
            && !string.IsNullOrWhiteSpace(account.Email)
            && string.Equals(credential.Email, account.Email, StringComparison.OrdinalIgnoreCase);
    }

    private static AccountResult[] DistinctVisibleAccounts(IEnumerable<AccountResult> accounts)
    {
        var distinct = new List<AccountResult>();
        foreach (var account in accounts
                     .OrderByDescending(account => account.Usage is not null)
                     .ThenByDescending(account => account.Live)
                     .ThenByDescending(account => account.Credential.Active)
                     .ThenBy(account => account.DisplayIndex))
        {
            if (distinct.Any(existing => SameIdentity(existing.Credential, account.Credential))) continue;
            distinct.Add(account);
        }

        return distinct
            .OrderByDescending(account => account.Credential.Active)
            .ThenBy(account => account.DisplayIndex)
            .ToArray();
    }

    private static bool SameIdentity(CodexCredential left, CodexCredential right)
    {
        if (!string.IsNullOrWhiteSpace(left.AccountId)
            && !string.IsNullOrWhiteSpace(right.AccountId))
        {
            return string.Equals(left.AccountId, right.AccountId, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(left.Email)
            && !string.IsNullOrWhiteSpace(right.Email))
        {
            return string.Equals(left.Email, right.Email, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.Key, right.Key, StringComparison.Ordinal);
    }

    private static QuotaCard ToCard(AccountResult account, int visibleAccountCount, int visibleIndex)
    {
        if (account.Usage is null)
        {
            return PlaceholderCard(account.Credential, visibleAccountCount, visibleIndex);
        }

        var usage = account.Usage;
        var card = new QuotaCard(
            StableCardKey(account.Credential.Key),
            ProviderKind.Codex,
            CodexDisplayFormatting.AccountLabel(visibleAccountCount, visibleIndex),
            account.Credential.Plan ?? usage.Plan ?? (account.Credential.Active ? "Active" : null),
            "#10a37f",
            account.Credential.Active,
            usage.Windows)
        {
            AccountHint = CodexAccountFormatting.MaskEmail(account.Credential.Email),
        };
        return account.CapturedAt is { } capturedAt
            ? card with { CapturedAt = capturedAt }
            : card;
    }

    private static string? ApiServiceDisplayName(
        IReadOnlyList<CockpitCodexQuotaAccount> services)
    {
        if (services.Count != 1 || string.IsNullOrWhiteSpace(services[0].ApiProviderName)) return null;
        var name = string.Join(
            " ",
            services[0].ApiProviderName!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length <= 64 ? name : name[..64];
    }

    private static QuotaCard ToServiceCard(
        IReadOnlyList<CockpitCodexQuotaAccount> services) => new(
        StableCardKey(string.Join('\0', services.Select(service => service.Key).Order(StringComparer.Ordinal))),
        ProviderKind.Codex,
        CodexDisplayFormatting.ApiServiceLabel(services.Count),
        "API key",
        "#64748b",
        true,
        [new QuotaWindow("API", null, null, TimeSpan.Zero)])
    {
        IsService = true,
        ServiceCount = services.Count,
        ServiceDisplayName = ApiServiceDisplayName(services),
    };

    private static QuotaCard PlaceholderCard(
        CodexCredential credential,
        int accountCount = 1,
        int displayIndex = 0) => new(
        StableCardKey(credential.Key),
        ProviderKind.Codex,
        CodexDisplayFormatting.AccountLabel(accountCount, displayIndex),
        credential.Plan,
        "#10a37f",
        credential.Active,
        [
            new QuotaWindow("5h", null, null, TimeSpan.FromHours(5)),
            new QuotaWindow("1w", null, null, TimeSpan.FromDays(7)),
        ])
        {
            AccountHint = CodexAccountFormatting.MaskEmail(credential.Email),
        };

    private static ProviderResult Missing(string detail, ProviderHealthCode code) => new(
        ProviderKind.Codex,
        [PlaceholderCard(new CodexCredential("", "codex", "Codex", null, true, "", null, null, null, DefaultClientId, null))],
        new ProviderHealth(ProviderKind.Codex, false, detail, code));

    private sealed record CodexCredential(
        string Path,
        string Key,
        string Label,
        string? Plan,
        bool Active,
        string AccessToken,
        string? RefreshToken,
        string? AccountId,
        string? Email,
        string ClientId,
        DateTimeOffset? ExpiresAt);

    private sealed record ProfileTokenStats(
        long LifetimeTokens,
        long? RecentWeeklyAverageTokens);

    private sealed record AccountResult(
        CodexCredential Credential,
        int DisplayIndex,
        CodexUsageData? Usage,
        string? Error,
        ProviderHealthCode ErrorCode,
        int? HttpStatus,
        bool Live = true,
        DateTimeOffset? CapturedAt = null,
        long? ProfileLifetimeTokens = null,
        long? ProfileRecentWeeklyAverageTokens = null);
}
