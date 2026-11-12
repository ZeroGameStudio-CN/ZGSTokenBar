using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>API-key providers (xAI and the declared keyed providers) from environment or files.</summary>
public static partial class UsageAccountDirectory
{
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
}
