using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>Claude: additional OAuth credential files beyond the primary account.</summary>
public static partial class UsageAccountDirectory
{
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
}
