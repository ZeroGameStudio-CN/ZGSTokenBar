using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed class UsageCredential
{
    public UsageCredential(
        string accountId,
        string label,
        string sourceKind,
        string? secret,
        IReadOnlyDictionary<string, string>? fields = null,
        string? unavailableReason = null)
    {
        AccountId = Trim(accountId, 80) ?? "account";
        Label = Trim(label, 80) ?? AccountId;
        SourceKind = sourceKind;
        Secret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        Fields = fields ?? new Dictionary<string, string>(StringComparer.Ordinal);
        UnavailableReason = unavailableReason;
    }

    public string AccountId { get; }
    public string Label { get; }
    public string SourceKind { get; }
    public string? Secret { get; }
    public IReadOnlyDictionary<string, string> Fields { get; }
    public string? UnavailableReason { get; }

    public override string ToString() => SourceKind + ":" + AccountId;

    private static string? Trim(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var filtered = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (filtered.Length == 0) return null;
        return filtered.Length <= maximum ? filtered : filtered[..maximum];
    }
}

public interface IUsageEnvironment
{
    string? Get(string name);
}

public sealed class ProcessUsageEnvironment : IUsageEnvironment
{
    public static ProcessUsageEnvironment Instance { get; } = new();

    public string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

public sealed class MapUsageEnvironment(IReadOnlyDictionary<string, string> values) : IUsageEnvironment
{
    public string? Get(string name) => values.TryGetValue(name, out var value) ? value : null;
}

public sealed record UsagePaths(string Home, string ApplicationData, string LocalApplicationData)
{
    public static UsagePaths Current() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}

public static class UsageAccountDirectory
{
    public static IReadOnlyList<UsageCredential> Discover(
        ProviderDefinition provider,
        UsagePaths paths,
        IUsageEnvironment environment,
        bool skipPrimaryClaude)
    {
        var discovered = UsageProviderRegistry.Find(provider.Id)?.Detect(new UsageDetectContext(paths, environment, skipPrimaryClaude)) ?? [];
        return discovered
            .Concat(Declared(provider.Id, paths))
            .GroupBy(account => account.AccountId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(UsageAccounts.MaximumAccounts)
            .ToArray();
    }

    internal static IReadOnlyList<UsageCredential> DetectGrok(UsagePaths paths, IUsageEnvironment environment)
    {
        var accounts = new List<UsageCredential>();
        var home = environment.Get("GROK_HOME");
        var root = string.IsNullOrWhiteSpace(home) ? Path.Combine(paths.Home, ".grok") : home;
        var fromAuth = ReadGrokAuth(Path.Combine(root, "auth.json"));
        // auth.json only carries the sign-in type (auth_mode, e.g. "OIDC"), never the plan.
        // The Grok CLI caches the plan's display name in settings_cache.json (no secrets);
        // it describes the one signed-in identity, so only attach it when that is unambiguous.
        if (fromAuth.Count == 1 && ReadGrokPlan(Path.Combine(root, "settings_cache.json")) is { } plan)
        {
            var only = fromAuth[0];
            var fields = new Dictionary<string, string>(only.Fields, StringComparer.Ordinal) { ["plan"] = plan };
            fromAuth = [new UsageCredential(only.AccountId, only.Label, only.SourceKind, only.Secret, fields, only.UnavailableReason)];
        }

        accounts.AddRange(fromAuth);
        if (environment.Get("GROK_OAUTH_TOKEN") is { Length: > 0 } token)
        {
            accounts.Add(new UsageCredential("grok-env", "Grok", "environment", token));
        }

        return accounts;
    }

    /// <summary>
    /// Reads the plan display name ("SuperGrok Heavy", …) from the Grok CLI's settings cache.
    /// The file is <c>{ payload: "&lt;json&gt;", signature: [...] }</c>; the plan sits at
    /// <c>payload.settings.subscription_tier_display</c> (or <c>subscription_tier</c>).
    /// </summary>
    internal static string? ReadGrokPlan(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > 1024 * 1024) return null;
            using var outer = JsonDocument.Parse(File.ReadAllBytes(path));
            if (outer.RootElement.ValueKind != JsonValueKind.Object) return null;
            JsonDocument? owned = null;
            try
            {
                var payload = outer.RootElement.TryGetProperty("payload", out var raw) ? raw : outer.RootElement;
                if (payload.ValueKind == JsonValueKind.String)
                {
                    owned = JsonDocument.Parse(payload.GetString() ?? "");
                    payload = owned.RootElement;
                }

                if (payload.ValueKind != JsonValueKind.Object) return null;
                var settings = payload.TryGetProperty("settings", out var inner) && inner.ValueKind == JsonValueKind.Object
                    ? inner
                    : payload;
                var plan = StringOf(settings, "subscription_tier_display", "subscription_tier");
                return plan is { Length: > 0 and <= 48 } ? plan : null;
            }
            finally
            {
                owned?.Dispose();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<UsageCredential> ReadGrokAuth(string path)
    {
        var root = Read(path);
        if (root is not { } document) return [];
        using (document)
        {
            var node = document.RootElement;
            if (node.ValueKind != JsonValueKind.Object) return [];
            if (StringOf(node, "key", "access_token") is { } single)
            {
                return [GrokAccount("grok", node, single, path, "")];
            }

            var accounts = new List<UsageCredential>();
            foreach (var property in node.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var secret = StringOf(property.Value, "key", "access_token");
                if (secret is null) continue;
                accounts.Add(GrokAccount(property.Name, property.Value, secret, path, property.Name));
            }

            return accounts;
        }
    }

    private static UsageCredential GrokAccount(string name, JsonElement node, string secret, string path, string entry)
    {
        var email = StringOf(node, "email");
        var expired = IsExpired(node);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_mode"] = StringOf(node, "auth_mode") ?? "",
        };
        // Only the file location travels with the credential; the refresh token stays in the file.
        if (expired && GrokTokenRefresher.IsRefreshable(node))
        {
            fields[GrokTokenRefresher.PathField] = path;
            fields[GrokTokenRefresher.EntryField] = entry;
        }
        return new UsageCredential(
            email ?? name,
            email ?? "Grok",
            "grok-auth",
            expired ? null : secret,
            fields,
            expired ? "credential_expired" : null);
    }

    internal static IReadOnlyList<UsageCredential> DetectClaude(UsagePaths paths, bool skipPrimary)
    {
        var root = Path.Combine(paths.Home, ".claude");
        var accounts = new List<UsageCredential>();
        var primary = Path.Combine(root, ".credentials.json");
        if (!skipPrimary && ReadClaude(primary, "claude-primary") is { } main) accounts.Add(main);
        var extra = Path.Combine(root, "accounts");
        if (!Directory.Exists(extra)) return accounts;
        foreach (var file in Directory.EnumerateFiles(extra, "*.json").Take(UsageAccounts.MaximumAccounts))
        {
            if (ReadClaude(file, Path.GetFileNameWithoutExtension(file)) is { } account) accounts.Add(account);
        }

        return accounts;
    }

    private static UsageCredential? ReadClaude(string path, string fallbackId)
    {
        var document = Read(path);
        if (document is null) return null;
        using (document)
        {
            var oauth = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.ObjectProperty("claudeAiOauth") ?? document.RootElement
                : (JsonElement?)null;
            var token = oauth is { } node ? StringOf(node, "accessToken", "access_token") : null;
            if (token is null) return null;
            var email = oauth is { } value ? StringOf(value, "email") : null;
            return new UsageCredential(email ?? fallbackId, email ?? fallbackId, "claude-oauth", token);
        }
    }

    internal static IReadOnlyList<UsageCredential> DetectCursor(UsagePaths paths)
    {
        var accounts = new List<UsageCredential>();
        foreach (var candidate in new[]
        {
            Path.Combine(paths.ApplicationData, "Cursor", "User", "globalStorage", "state.vscdb"),
            Path.Combine(paths.Home, ".cursor", "auth.json"),
        })
        {
            var token = candidate.EndsWith(".vscdb", StringComparison.OrdinalIgnoreCase)
                ? ProbeSqliteText(candidate, "cursorAuth/accessToken")
                : ReadTokenFile(candidate, "accessToken", "access_token");
            if (token is null) continue;
            var user = JwtSubject(token) ?? "cursor-local";
            accounts.Add(new UsageCredential(user, "Cursor", "cursor-session", token, new Dictionary<string, string>
            {
                ["user"] = user,
            }));
            break;
        }

        return accounts;
    }

    internal static IReadOnlyList<UsageCredential> DetectCopilot(UsagePaths paths)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(paths.ApplicationData, "GitHub CLI", "hosts.yml"),
            Path.Combine(paths.Home, ".config", "gh", "hosts.yml"),
        })
        {
            var parsed = ReadGitHubToken(candidate);
            if (parsed is { } account) return [account];
        }

        // OpenCode's "GitHub Copilot" sign-in stores the GitHub OAuth token from Copilot's own
        // device flow, which is exactly what copilot_internal/user accepts (newer gh CLIs keep
        // their token in the OS keyring, so hosts.yml often has none).
        foreach (var candidate in new[]
        {
            Path.Combine(paths.Home, ".local", "share", "opencode", "auth.json"),
            Path.Combine(paths.Home, ".config", "opencode", "auth.json"),
        })
        {
            if (ReadOpenCodeCopilot(candidate) is { } account) return [account];
        }

        return [];
    }

    internal static UsageCredential? ReadOpenCodeCopilot(string path)
    {
        var document = Read(path);
        if (document is null) return null;
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.ObjectProperty("github-copilot") is not { } node)
            {
                return null;
            }

            var token = new[] { StringOf(node, "refresh"), StringOf(node, "access") }
                .FirstOrDefault(value => value is not null
                    && (value.StartsWith("gho_", StringComparison.Ordinal) || value.StartsWith("ghu_", StringComparison.Ordinal)));
            return token is null ? null : new UsageCredential("opencode-copilot", "GitHub Copilot", "opencode-copilot", token);
        }
    }

    public static UsageCredential? ReadGitHubToken(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return null;
            text = ReadBounded(path, 65_536);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string? section = null;
        string? token = null;
        string? user = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!raw.StartsWith(' ') && !raw.StartsWith('\t') && line.EndsWith(':'))
            {
                section = line.TrimEnd(':');
                continue;
            }

            if (!string.Equals(section, "github.com", StringComparison.OrdinalIgnoreCase)) continue;
            var split = line.Split(':', 2);
            if (split.Length != 2) continue;
            var key = split[0].Trim();
            var value = split[1].Trim().Trim('"');
            if (string.Equals(key, "oauth_token", StringComparison.Ordinal)) token = value;
            if (string.Equals(key, "user", StringComparison.Ordinal)) user = value;
        }

        if (string.IsNullOrWhiteSpace(token)) return null;
        return new UsageCredential(user ?? "github", user ?? "GitHub", "github-cli", token);
    }

    internal static IReadOnlyList<UsageCredential> DetectOpenCode(UsagePaths paths)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(paths.Home, ".local", "share", "opencode", "auth.json"),
            Path.Combine(paths.Home, ".config", "opencode", "auth.json"),
        })
        {
            var document = Read(candidate);
            if (document is null) continue;
            using (document)
            {
                var root = document.RootElement;
                var key = FirstString(root, "key", "apiKey", "api_key")
                    ?? NestedString(root, "opencode", "key")
                    ?? NestedString(root, "zen", "key");
                if (key is null) continue;
                return [new UsageCredential("opencode", "OpenCode", "opencode-auth", key)];
            }
        }

        return [];
    }

    internal static IReadOnlyList<UsageCredential> DetectXai(UsagePaths paths, IUsageEnvironment environment)
    {
        var key = environment.Get("XAI_API_KEY");
        var team = environment.Get("XAI_TEAM_ID");
        var config = Read(Path.Combine(paths.Home, ".xai", "config.json"));
        if (config is not null)
        {
            using (config)
            {
                key ??= StringOf(config.RootElement, "apiKey", "management_key", "key");
                team ??= StringOf(config.RootElement, "teamId", "team_id");
            }
        }

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(team)) return [];
        if (!IsToken(team)) return [];
        return [new UsageCredential(team, "xAI", "xai-management", key, new Dictionary<string, string>
        {
            ["team"] = team,
        })];
    }

    internal static IReadOnlyList<UsageCredential> DetectAntigravity(UsagePaths paths)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(paths.Home, ".gemini", "antigravity-cli", "antigravity-oauth-token"),
            Path.Combine(paths.Home, ".gemini", "jetski-standalone-oauth-token"),
        })
        {
            if (ReadAntigravityToken(candidate) is { } credential) return [credential];
        }

        return [];
    }

    /// <summary>
    /// agy keeps <c>{ token: { access_token, refresh_token, expiry }, auth_method }</c> (older builds
    /// wrote the token fields at the root). The Google access token lives about an hour and agy
    /// refreshes it in memory only, so an expired file token is reported as such and the
    /// <c>agy /usage</c> fallback supplies the reading instead.
    /// </summary>
    internal static UsageCredential? ReadAntigravityToken(string path)
    {
        var document = Read(path);
        if (document is null) return null;
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var node = root.ObjectProperty("token") ?? root;
            var token = StringOf(node, "access_token", "accessToken");
            if (token is null) return null;
            var expired = node.TryGetProperty("expiry", out var expiry)
                && expiry.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                && at <= DateTimeOffset.UtcNow.AddMinutes(1)
                || IsExpired(node);
            return new UsageCredential(
                "antigravity",
                "Antigravity",
                "google-oauth",
                expired ? null : token,
                null,
                expired ? "credential_expired" : null);
        }
    }

    internal static IReadOnlyList<UsageCredential> DetectKeyed(
        IUsageEnvironment environment,
        string variable,
        string source,
        string label)
    {
        var secret = environment.Get(variable);
        if (string.IsNullOrWhiteSpace(secret)) return [];
        return [new UsageCredential(source, label, source, secret)];
    }

    private static IReadOnlyList<UsageCredential> Declared(string providerId, UsagePaths paths)
    {
        var directory = Path.Combine(paths.Home, ".zgstokenbar", "providers", providerId);
        var index = Read(Path.Combine(directory, "accounts.json"));
        if (index is null) return [];
        using (index)
        {
            var accounts = index.RootElement.ArrayProperty("accounts");
            if (accounts is not { } listed) return [];
            var parsed = new List<UsageCredential>();
            foreach (var item in listed.EnumerateArray())
            {
                if (parsed.Count >= UsageAccounts.MaximumAccounts) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var file = StringOf(item, "credentialFile");
                if (!IsRelativeFile(file)) continue;
                var id = StringOf(item, "id") ?? Path.GetFileNameWithoutExtension(file);
                var label = StringOf(item, "label") ?? id;
                var credential = Read(Path.Combine(directory, file!));
                if (credential is null) continue;
                using (credential)
                {
                    var node = credential.RootElement;
                    var secret = StringOf(node, "key", "accessToken", "access_token", "token", "apiKey", "api_key");
                    if (secret is null) continue;
                    var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                    var team = StringOf(node, "teamId", "team_id");
                    var user = StringOf(node, "userId", "user");
                    if (team is not null && IsToken(team)) fields["team"] = team;
                    if (user is not null) fields["user"] = user;
                    parsed.Add(new UsageCredential(id!, label!, "accounts-file", secret, fields));
                }
            }

            return parsed;
        }
    }

    public static string? ProbeSqliteText(string path, string key)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return null;
            var length = new FileInfo(path).Length;
            if (length <= 0 || length > 33_554_432) return null;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var keyBytes = Encoding.UTF8.GetBytes(key);
        var origin = IndexOf(bytes, keyBytes);
        if (origin < 0) return null;
        var window = bytes.AsSpan(origin, Math.Min(4096, bytes.Length - origin));
        var marker = "eyJ"u8;
        var tokenAt = IndexOf(window, marker);
        if (tokenAt < 0) return null;
        var end = tokenAt;
        while (end < window.Length && IsJwtChar(window[end])) end++;
        var token = Encoding.ASCII.GetString(window[tokenAt..end]);
        return token.Count(character => character == '.') >= 2 ? token : null;
    }

    public static string? JwtSubject(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload,
            };
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var subject = document.RootElement.StringProperty("sub");
            if (string.IsNullOrWhiteSpace(subject)) return null;
            var separator = subject.LastIndexOf('|');
            return separator >= 0 && separator < subject.Length - 1 ? subject[(separator + 1)..] : subject;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadTokenFile(string path, params string[] names)
    {
        var document = Read(path);
        if (document is null) return null;
        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? StringOf(document.RootElement, names)
                : null;
        }
    }

    private static bool IsExpired(JsonElement node)
    {
        if (!node.TryGetProperty("expires_at", out var value) && !node.TryGetProperty("expiresAt", out value))
        {
            return false;
        }

        try
        {
            DateTimeOffset? expiry = value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix > 10_000_000_000 ? unix / 1000 : unix)
                    : null;
            return expiry is { } at && at <= DateTimeOffset.UtcNow.AddMinutes(1);
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static JsonDocument? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = ReadBounded(path, 262_144);
            return JsonDocument.Parse(json);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(stream.Length, maximum);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return Encoding.UTF8.GetString(buffer);
    }

    private static string? StringOf(JsonElement element, params string[] names) => element.StringProperty(names);

    private static string? FirstString(JsonElement element, params string[] names) => StringOf(element, names);

    private static string? NestedString(JsonElement element, string objectName, string property)
    {
        var nested = element.ObjectProperty(objectName);
        return nested is { } node ? StringOf(node, property) : null;
    }

    private static bool IsRelativeFile(string? file) =>
        !string.IsNullOrWhiteSpace(file)
        && file.IndexOfAny(['/', '\\', ':']) < 0
        && !file.Contains("..", StringComparison.Ordinal);

    private static bool IsToken(string value) =>
        value.Length is > 0 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsJwtChar(byte value) =>
        value is (>= (byte)'A' and <= (byte)'Z')
            or (>= (byte)'a' and <= (byte)'z')
            or (>= (byte)'0' and <= (byte)'9')
            or (byte)'-' or (byte)'_' or (byte)'.';

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.Slice(index, needle.Length).SequenceEqual(needle)) return index;
        }

        return -1;
    }
}

