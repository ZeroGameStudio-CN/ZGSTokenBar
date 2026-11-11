using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ZGSTokenBar.Core;

public sealed class UsageHttpRequest
{
    public UsageHttpRequest(string method, Uri url, IReadOnlyDictionary<string, string> headers, string? body = null, string? contentType = null)
    {
        Method = method;
        Url = url;
        Headers = headers;
        Body = body;
        ContentType = contentType;
    }

    public string Method { get; }
    public Uri Url { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public string? Body { get; }
    public string? ContentType { get; }

    public override string ToString() => Method + " " + Url.GetLeftPart(UriPartial.Path);
}

public sealed record UsageHttpResult(int Status, string? Body, string? ErrorKey)
{
    public static UsageHttpResult Blocked() => new(0, null, "endpoint_blocked");
}

public interface IUsageTransport
{
    Task<UsageHttpResult> SendAsync(UsageHttpRequest request, CancellationToken cancellationToken);
}

public static class UsageEndpoints
{
    public static bool IsAllowed(string method, Uri url)
    {
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (url.UserInfo.Length > 0 || url.HostNameType == UriHostNameType.Unknown) return false;
        var host = url.IdnHost;
        var path = url.AbsolutePath;
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            if (Host(host, "cli-chat-proxy.grok.com")) return path == "/v1/billing";
            if (Host(host, "cursor.com")) return path == "/api/usage-summary";
            if (Host(host, "api.github.com")) return path == "/copilot_internal/user";
            if (Host(host, "api.deepseek.com")) return path == "/user/balance";
            if (Host(host, "opencode.ai")) return path == "/zen/go/v1/usage";
            if (Host(host, "openrouter.ai")) return path == "/api/v1/credits";
            if (Host(host, "management-api.x.ai"))
            {
                return path.StartsWith("/v1/billing/teams/", StringComparison.Ordinal)
                    && path.EndsWith("/prepaid/balance", StringComparison.Ordinal)
                    && path.Count(character => character == '/') == 6;
            }

            return false;
        }

        // The Grok CLI's OIDC refresh grant (see GrokTokenRefresher).
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && Host(host, "auth.x.ai"))
        {
            return path == "/oauth2/token";
        }

        return string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && (Host(host, "cloudcode-pa.googleapis.com") || Host(host, "daily-cloudcode-pa.googleapis.com"))
            && path == "/v1internal:retrieveUserQuotaSummary";
    }

    private static bool Host(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}

public sealed class HttpUsageTransport(HttpClient httpClient) : IUsageTransport
{
    public const int MaximumBodyBytes = 262_144;

    public async Task<UsageHttpResult> SendAsync(UsageHttpRequest request, CancellationToken cancellationToken)
    {
        if (!UsageEndpoints.IsAllowed(request.Method, request.Url)) return UsageHttpResult.Blocked();
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        foreach (var header in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType ?? "application/json");
        }

        try
        {
            using var response = await httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400) return UsageHttpResult.Blocked();
            var body = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            return body is null
                ? new UsageHttpResult((int)response.StatusCode, null, "payload_too_large")
                : new UsageHttpResult((int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UsageHttpResult(0, null, "unavailable");
        }
        catch (HttpRequestException)
        {
            return new UsageHttpResult(0, null, "unavailable");
        }
    }

    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (buffer.Length < MaximumBodyBytes)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                var room = MaximumBodyBytes - (int)buffer.Length;
                buffer.Write(chunk, 0, Math.Min(read, room));
                if (read > room) return null;
            }

            return null;
        }
    }
}

public sealed record UsageLoadRequest(
    DateTimeOffset Now,
    UsagePaths Paths,
    Func<string, bool> IsEnabled,
    IUsageTransport Transport,
    IUsageEnvironment Environment,
    bool SkipPrimaryClaude);

public static class UsageProviderEngine
{
    public static async Task<UsageBoard> LoadAsync(UsageLoadRequest request, CancellationToken cancellationToken)
    {
        var providers = new List<UsageProviderSnapshot>();
        foreach (var definition in ProviderCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.IsEnabled(definition.Id) || definition.FetcherKey == "none") continue;
            var accounts = new List<UsageAccountSnapshot>();
            foreach (var credential in UsageAccountDirectory.Discover(
                definition,
                request.Paths,
                request.Environment,
                request.SkipPrimaryClaude))
            {
                cancellationToken.ThrowIfCancellationRequested();
                accounts.Add(await ReadAccountAsync(definition, credential, request, cancellationToken).ConfigureAwait(false));
            }

            if (definition.FetcherKey == "grok" && accounts.Count > 0)
            {
                var home = request.Environment.Get("GROK_HOME");
                var ledger = UsageLedgerReader.ReadGrok(
                    string.IsNullOrWhiteSpace(home) ? Path.Combine(request.Paths.Home, ".grok") : home,
                    request.Now);
                if (ledger is not null)
                {
                    accounts[0] = accounts[0] with { Ledger = ledger };
                }
            }

            var normalized = UsageAccounts.Normalize(accounts);
            if (normalized.Count == 0) continue;
            providers.Add(new UsageProviderSnapshot(
                definition.Id,
                definition.DisplayName,
                definition.Accent,
                definition.Order,
                normalized));
        }

        return new UsageBoard(providers);
    }

    public static UsageBoard Merge(UsageBoard fetched, IReadOnlyList<UsageProviderSnapshot>? external)
    {
        var map = fetched.Providers.ToDictionary(provider => provider.ProviderId, StringComparer.Ordinal);
        foreach (var snapshot in external ?? [])
        {
            if (ProviderCatalog.Find(snapshot.ProviderId) is null || snapshot.Accounts.Count == 0) continue;
            map[snapshot.ProviderId] = map.TryGetValue(snapshot.ProviderId, out var existing)
                ? existing with { Accounts = UsageAccounts.Normalize(existing.Accounts.Concat(snapshot.Accounts)) }
                : snapshot with { Accounts = UsageAccounts.Normalize(snapshot.Accounts) };
        }

        return new UsageBoard(ProviderCatalog.All
            .Select(definition => map.TryGetValue(definition.Id, out var snapshot) ? snapshot : null)
            .OfType<UsageProviderSnapshot>()
            .ToArray());
    }

    private static async Task<UsageAccountSnapshot> ReadAccountAsync(
        ProviderDefinition definition,
        UsageCredential credential,
        UsageLoadRequest request,
        CancellationToken cancellationToken)
    {
        // An expired Grok CLI access token is renewed with the CLI's own refresh token (and written
        // back for the CLI), so a signed-in CLI never shows "sign-in expired" just for being idle.
        if (credential.Secret is null
            && definition.FetcherKey == "grok"
            && credential.Fields.TryGetValue(GrokTokenRefresher.PathField, out var authPath)
            && credential.Fields.TryGetValue(GrokTokenRefresher.EntryField, out var authEntry)
            && await GrokTokenRefresher.RefreshAsync(authPath, authEntry, request.Transport, request.Now, cancellationToken)
                .ConfigureAwait(false) is { } renewed)
        {
            var fields = credential.Fields
                .Where(field => field.Key is not GrokTokenRefresher.PathField and not GrokTokenRefresher.EntryField)
                .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
            credential = new UsageCredential(credential.AccountId, credential.Label, credential.SourceKind, renewed, fields);
        }

        if (credential.Secret is null)
        {
            return Account(credential, null, UsageFreshness.MissingCredentials, request.Now, credential.UnavailableReason ?? "missing_credentials");
        }

        var http = Build(definition.FetcherKey, credential);
        if (http is null)
        {
            return Account(credential, null, UsageFreshness.Unavailable, request.Now, "endpoint_blocked");
        }

        var result = await request.Transport.SendAsync(http, cancellationToken).ConfigureAwait(false);
        if (result.ErrorKey is not null || result.Status != (int)HttpStatusCode.OK)
        {
            return Account(credential, null, Freshness(result), request.Now, result.ErrorKey ?? StatusKey(result.Status));
        }

        var parsed = Parse(definition.FetcherKey, result.Body);
        return parsed is null
            ? Account(credential, null, UsageFreshness.Unavailable, request.Now, "unavailable")
            : Account(
                credential,
                parsed,
                parsed.ErrorKey is null ? UsageFreshness.Current : UsageFreshness.Unavailable,
                request.Now,
                parsed.ErrorKey);
    }

    private static UsageAccountSnapshot Account(
        UsageCredential credential,
        UsageParseResult? parsed,
        UsageFreshness freshness,
        DateTimeOffset now,
        string? errorKey)
    {
        var plan = parsed?.Plan;
        if (string.IsNullOrWhiteSpace(plan)
            && credential.Fields.TryGetValue("plan", out var stored)
            && stored.Length > 0)
        {
            plan = stored;
        }

        return new UsageAccountSnapshot(
            credential.AccountId,
            credential.Label,
            plan,
            credential.SourceKind,
            parsed?.Windows ?? [],
            parsed?.Credits,
            null,
            parsed?.Products ?? [],
            freshness,
            now,
            errorKey);
    }

    private static UsageFreshness Freshness(UsageHttpResult result) => result.ErrorKey switch
    {
        "rate_limited" => UsageFreshness.Unavailable,
        "credential_rejected" => UsageFreshness.MissingCredentials,
        _ => result.Status is 401 or 403 ? UsageFreshness.MissingCredentials : UsageFreshness.Unavailable,
    };

    private static string StatusKey(int status) => status switch
    {
        401 or 403 => "credential_rejected",
        429 => "rate_limited",
        _ => "http_error",
    };

    private static UsageParseResult? Parse(string fetcher, string? json) => fetcher switch
    {
        "grok" => UsageParsers.ParseGrok(json),
        "cursor" => UsageParsers.ParseCursor(json),
        "copilot" => UsageParsers.ParseCopilot(json),
        "deepseek" => UsageParsers.ParseDeepSeek(json),
        "opencode" => UsageParsers.ParseOpenCode(json),
        "xai" => UsageParsers.ParseXai(json),
        "openrouter" => UsageParsers.ParseOpenRouter(json),
        "antigravity" => UsageParsers.ParseAntigravity(json),
        "claude-extra" => ParseClaude(json),
        _ => null,
    };

    private static UsageParseResult? ParseClaude(string? json)
    {
        try
        {
            return json is null ? null : UsageParsers.FromClaude(ClaudeUsageParser.Parse(json));
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static UsageHttpRequest? Build(string fetcher, UsageCredential credential)
    {
        var bearer = "Bearer " + credential.Secret;
        return fetcher switch
        {
            "grok" => Get(
                "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = bearer,
                    ["Accept"] = "application/json",
                    ["x-xai-token-auth"] = "xai-grok-cli",
                }),
            "cursor" => Get(
                "https://cursor.com/api/usage-summary",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Cookie"] = "WorkosCursorSessionToken=" + Uri.EscapeDataString(
                        (credential.Fields.TryGetValue("user", out var user) ? user : credential.AccountId)
                        + "::"
                        + credential.Secret),
                    ["Accept"] = "application/json",
                }),
            "copilot" => Get(
                "https://api.github.com/copilot_internal/user",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = bearer,
                    ["Accept"] = "application/json",
                    ["User-Agent"] = "GitHubCopilotChat/0.26.7",
                    ["Editor-Version"] = "vscode/1.96.2",
                    ["Editor-Plugin-Version"] = "copilot-chat/0.26.7",
                }),
            "deepseek" => Get(
                "https://api.deepseek.com/user/balance",
                Bearer(bearer)),
            "opencode" => Get(
                "https://opencode.ai/zen/go/v1/usage",
                Bearer(bearer)),
            "openrouter" => Get(
                "https://openrouter.ai/api/v1/credits",
                Bearer(bearer)),
            "xai" => credential.Fields.TryGetValue("team", out var team)
                ? Get("https://management-api.x.ai/v1/billing/teams/" + team + "/prepaid/balance", Bearer(bearer))
                : null,
            "antigravity" => new UsageHttpRequest(
                "POST",
                new Uri("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"),
                Bearer(bearer),
                "{}"),
            "claude-extra" => Get(
                "https://api.anthropic.com/api/oauth/usage",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = bearer,
                    ["Accept"] = "application/json",
                    ["anthropic-version"] = "2023-06-01",
                    ["anthropic-beta"] = "oauth-2025-04-20",
                    ["User-Agent"] = "claude-code/1.0",
                }),
            _ => null,
        };
    }

    private static Dictionary<string, string> Bearer(string bearer) => new(StringComparer.Ordinal)
    {
        ["Authorization"] = bearer,
        ["Accept"] = "application/json",
    };

    private static UsageHttpRequest Get(string url, IReadOnlyDictionary<string, string> headers) =>
        new("GET", new Uri(url), headers);
}

public static class UsageProbes
{
    public static bool GrokPresent(UsagePaths paths, IUsageEnvironment environment)
    {
        var home = environment.Get("GROK_HOME");
        var root = string.IsNullOrWhiteSpace(home) ? Path.Combine(paths.Home, ".grok") : home;
        return File.Exists(Path.Combine(root, "auth.json"))
            || File.Exists(Path.Combine(root, "bin", "grok.exe"))
            || File.Exists(Path.Combine(root, "bin", "grok"));
    }

    public static string? GrokExecutable(UsagePaths paths, IUsageEnvironment environment)
    {
        var home = environment.Get("GROK_HOME");
        var root = string.IsNullOrWhiteSpace(home) ? Path.Combine(paths.Home, ".grok") : home;
        foreach (var name in new[] { "grok.exe", "grok" })
        {
            var path = Path.Combine(root, "bin", name);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    public static bool AntigravityPresent(UsagePaths paths) =>
        AntigravityExecutable(paths) is not null
        || File.Exists(Path.Combine(paths.Home, ".gemini", "antigravity-cli", "antigravity-oauth-token"));

    public static string? AntigravityExecutable(UsagePaths paths)
    {
        var path = Path.Combine(paths.LocalApplicationData, "agy", "bin", "agy.exe");
        return File.Exists(path) ? path : null;
    }
}
