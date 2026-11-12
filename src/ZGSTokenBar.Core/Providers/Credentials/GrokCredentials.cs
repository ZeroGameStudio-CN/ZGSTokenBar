using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>Grok: ~/.grok auth and plan files (metadata only; tokens never logged).</summary>
public static partial class UsageAccountDirectory
{
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
}
