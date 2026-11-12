using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>Antigravity: local sign-in token presence.</summary>
public static partial class UsageAccountDirectory
{
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
}
