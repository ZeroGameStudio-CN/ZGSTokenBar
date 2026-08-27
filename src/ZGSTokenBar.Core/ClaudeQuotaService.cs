using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZGSTokenBar.Core;

public sealed class ClaudeQuotaService
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumRateLimitBackoff = TimeSpan.FromDays(1);
    private readonly HttpClient _httpClient;
    private DateTimeOffset? _retryAt;
    private string? _rateLimitedAccessToken;

    public ClaudeQuotaService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ProviderResult> FetchAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        bool allowOAuthRefresh = false)
    {
        var credential = ReadCredential();
        if (credential is null)
        {
            return Missing(
                "Claude OAuth credentials were not found.",
                ProviderHealthCode.MissingCredentials);
        }

        var refreshAllowed = settings.AutoRefreshClaudeOAuth || allowOAuthRefresh;
        var refreshAttempted = false;
        if (refreshAllowed
            && credential.ExpiresAt is { } expiresAt
            && expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1)
            && !string.IsNullOrWhiteSpace(credential.RefreshToken))
        {
            refreshAttempted = true;
            credential = await RefreshAsync(credential, cancellationToken) ?? credential;
        }

        if (!string.Equals(_rateLimitedAccessToken, credential.AccessToken, StringComparison.Ordinal))
        {
            ClearRateLimit();
        }

        var now = DateTimeOffset.UtcNow;
        if (_retryAt is { } retryAt && retryAt > now)
        {
            return Missing(
                RateLimitDetail(retryAt, now),
                ProviderHealthCode.RateLimited,
                retryAt: retryAt);
        }
        ClearRateLimit();

        try
        {
            var response = await GetUsageAsync(credential.AccessToken, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized
                && refreshAllowed
                && !refreshAttempted
                && !string.IsNullOrWhiteSpace(credential.RefreshToken))
            {
                var refreshed = await RefreshAsync(credential, cancellationToken);
                if (refreshed is not null)
                {
                    credential = refreshed;
                    response.Dispose();
                    response = await GetUsageAsync(credential.AccessToken, cancellationToken);
                }
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _retryAt = ResolveRateLimitRetryAt(response, DateTimeOffset.UtcNow);
                    _rateLimitedAccessToken = credential.AccessToken;
                    return Missing(
                        RateLimitDetail(_retryAt.Value, DateTimeOffset.UtcNow),
                        ProviderHealthCode.RateLimited,
                        retryAt: _retryAt);
                }

                ClearRateLimit();
                if (!response.IsSuccessStatusCode)
                {
                    return response.StatusCode == HttpStatusCode.Unauthorized
                        ? Missing(refreshAllowed
                            ? "Claude OAuth refresh failed. Run `claude /login` to re-authenticate."
                            : "Claude OAuth expired. Run Claude Code or enable OAuth auto-refresh.",
                            refreshAllowed
                                ? ProviderHealthCode.OAuthRefreshFailed
                                : ProviderHealthCode.OAuthExpired)
                        : Missing(
                            $"Claude API returned HTTP {(int)response.StatusCode}.",
                            ProviderHealthCode.HttpError,
                            (int)response.StatusCode);
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var usage = ClaudeUsageParser.Parse(json);
                var windows = new List<QuotaWindow>
                {
                    new("5h", usage.FiveHourUsedPercent, usage.FiveHourResetsAt, TimeSpan.FromHours(5)),
                    new("1w", usage.WeekUsedPercent, usage.WeekResetsAt, TimeSpan.FromDays(7)),
                };
                if (usage.FableWeekUsedPercent is not null || usage.FableWeekResetsAt is not null)
                {
                    windows.Add(new QuotaWindow(
                        "Fable",
                        usage.FableWeekUsedPercent,
                        usage.FableWeekResetsAt,
                        TimeSpan.FromDays(7)));
                }
                var card = new QuotaCard(
                    "claude.account",
                    ProviderKind.Claude,
                    "Claude",
                    credential.Plan,
                    "#d97757",
                    true,
                    windows);
                return new ProviderResult(
                    ProviderKind.Claude,
                    [card],
                    new ProviderHealth(
                        ProviderKind.Claude,
                        true,
                        "Claude quota is current.",
                        ProviderHealthCode.Current));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Missing("Claude API request timed out.", ProviderHealthCode.Timeout);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            return Missing(
                $"Claude quota unavailable: {exception.Message}",
                ProviderHealthCode.Unavailable);
        }
    }

    private async Task<HttpResponseMessage> GetUsageAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("claude-code/1.0");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    private static ClaudeCredential? ReadCredential()
    {
        var configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        var path = Path.Combine(configDirectory, ".credentials.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var oauth = document.RootElement.ObjectProperty("claudeAiOauth");
            if (oauth is null) return null;
            var accessToken = oauth.Value.StringProperty("accessToken");
            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            var tier = oauth.Value.StringProperty("rateLimitTier");
            var subscription = oauth.Value.StringProperty("subscriptionType");
            DateTimeOffset? expiresAt = null;
            if (oauth.Value.TryGetProperty("expiresAt", out var expiry)
                && expiry.TryGetInt64(out var expiresAtMilliseconds))
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMilliseconds);
            }
            return new ClaudeCredential(
                path,
                accessToken,
                oauth.Value.StringProperty("refreshToken"),
                PlanLabel(tier, subscription),
                expiresAt);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ClaudeCredential?> RefreshAsync(ClaudeCredential credential, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = credential.RefreshToken!,
                    ["client_id"] = ClientId,
                }),
            };
            request.Headers.UserAgent.ParseAdd("claude-code/1.0");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var refreshed = JsonNode.Parse(json)?.AsObject();
            var accessToken = refreshed?["access_token"]?.GetValue<string>();
            double? expiresIn = null;
            if (refreshed?["expires_in"] is JsonValue expiresValue
                && expiresValue.TryGetValue<double>(out var parsedExpiresIn))
            {
                expiresIn = parsedExpiresIn;
            }
            if (string.IsNullOrWhiteSpace(accessToken) || expiresIn is not > 0) return null;

            var currentContents = File.ReadAllText(credential.Path);
            var root = JsonNode.Parse(currentContents)?.AsObject();
            var oauth = root?["claudeAiOauth"]?.AsObject();
            if (root is null || oauth is null) return null;
            var currentAccessToken = oauth["accessToken"]?.GetValue<string>();
            if (!string.Equals(currentAccessToken, credential.AccessToken, StringComparison.Ordinal))
            {
                return ReadCredential();
            }
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value);
            oauth["accessToken"] = accessToken;
            oauth["expiresAt"] = expiresAt.ToUnixTimeMilliseconds();
            var rotatingRefreshToken = refreshed?["refresh_token"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(rotatingRefreshToken)) oauth["refreshToken"] = rotatingRefreshToken;
            if (!CredentialSupport.AtomicWrite(
                credential.Path,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                currentContents))
            {
                return ReadCredential();
            }
            return credential with
            {
                AccessToken = accessToken,
                RefreshToken = rotatingRefreshToken ?? credential.RefreshToken,
                ExpiresAt = expiresAt,
            };
        }
        catch
        {
            return null;
        }
    }

    private static ProviderResult Missing(
        string detail,
        ProviderHealthCode code,
        int? httpStatus = null,
        DateTimeOffset? retryAt = null)
    {
        var card = new QuotaCard(
            "claude.account",
            ProviderKind.Claude,
            "Claude",
            null,
            "#d97757",
            true,
            [
                new QuotaWindow("5h", null, null, TimeSpan.FromHours(5)),
                new QuotaWindow("1w", null, null, TimeSpan.FromDays(7)),
            ]);
        return new ProviderResult(
            ProviderKind.Claude,
            [card],
            new ProviderHealth(
                ProviderKind.Claude,
                false,
                detail,
                code,
                httpStatus,
                retryAt));
    }

    private static string? PlanLabel(string? tier, string? subscription)
    {
        var normalizedTier = tier?.ToLowerInvariant() ?? string.Empty;
        var normalizedSubscription = subscription?.ToLowerInvariant() ?? string.Empty;
        if (normalizedTier.Contains("max_20") || normalizedTier.Contains("20x")) return "Max 20x";
        if (normalizedTier.Contains("max_5") || normalizedTier.Contains("5x")) return "Max 5x";
        if (normalizedTier.Contains("max") || normalizedSubscription == "max") return "Max";
        if (normalizedTier.Contains("pro") || normalizedSubscription == "pro") return "Pro";
        if (normalizedSubscription == "free") return "Free";
        return string.IsNullOrWhiteSpace(subscription) ? null : subscription;
    }

    internal static DateTimeOffset ResolveRateLimitRetryAt(
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - now : DefaultRateLimitBackoff);
        delay = TimeSpan.FromMilliseconds(Math.Clamp(
            delay.TotalMilliseconds,
            0,
            MaximumRateLimitBackoff.TotalMilliseconds));
        return now + delay;
    }

    private static string RateLimitDetail(DateTimeOffset retryAt, DateTimeOffset now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((retryAt - now).TotalMinutes));
        return $"Claude API is rate limited. Retry in {minutes}m (after {retryAt.ToLocalTime():HH:mm}).";
    }

    private void ClearRateLimit()
    {
        _retryAt = null;
        _rateLimitedAccessToken = null;
    }

    private sealed record ClaudeCredential(
        string Path,
        string AccessToken,
        string? RefreshToken,
        string? Plan,
        DateTimeOffset? ExpiresAt);
}
