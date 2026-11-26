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
        var primaryEmail = skipPrimary
            ? null
            : ClaudeQuotaService.ReadAccountEmail(root, allowUserProfileFallback: false)
                ?? ClaudeQuotaService.ReadAccountEmail(paths.Home, allowUserProfileFallback: false);
        if (!skipPrimary && ReadClaude(primary, "claude-primary", primaryEmail) is { } main) accounts.Add(main);
        var extra = Path.Combine(root, "accounts");
        if (!Directory.Exists(extra)) return accounts;
        foreach (var file in Directory.EnumerateFiles(extra, "*.json").Take(UsageAccounts.MaximumAccounts))
        {
            if (ReadClaude(file, Path.GetFileNameWithoutExtension(file)) is { } account) accounts.Add(account);
        }

        return accounts;
    }

    private static UsageCredential? ReadClaude(string path, string fallbackId, string? fallbackEmail = null)
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
            var rawEmail = oauth is { } value ? StringOf(value, "email") : null;
            var email = IsValidEmail(rawEmail) ? rawEmail!.Trim() : fallbackEmail;
            return new UsageCredential(email ?? fallbackId, email ?? fallbackId, "claude-oauth", token);
        }
    }
}
