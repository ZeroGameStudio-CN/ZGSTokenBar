using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>GitHub Copilot: gh/OpenCode tokens.</summary>
public static partial class UsageAccountDirectory
{
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
}
