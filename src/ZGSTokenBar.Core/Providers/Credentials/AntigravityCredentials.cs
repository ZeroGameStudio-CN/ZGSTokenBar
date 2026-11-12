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
            if (ReadAntigravityToken(candidate, AntigravitySignedInAccount(paths)) is { } credential) return [credential];
        }

        return [];
    }

    /// <summary>
    /// The Google account agy and the Antigravity app are signed in with. They remember it, as an
    /// identity only (no token), under <c>agy.onboarding.lastLoginUsername</c> in
    /// <c>%APPDATA%\Antigravity\app_storage.json</c>; agy's own /usage output carries no identity.
    /// </summary>
    public static string? AntigravitySignedInAccount(UsagePaths paths)
    {
        var document = Read(Path.Combine(paths.ApplicationData, "Antigravity", "app_storage.json"));
        if (document is null) return null;
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("agy.onboarding.lastLoginUsername", out var value)
                || value.ValueKind != JsonValueKind.String) return null;
            var email = value.GetString()?.Trim();
            // The app stores some values JSON-encoded a second time ("\"name@example.com\"").
            if (email is { Length: > 1 } && email[0] == '"' && email[^1] == '"') email = email[1..^1].Trim();
            return email is { Length: > 3 and <= 254 }
                && email.IndexOf('@') is > 0 and var at && at < email.Length - 1
                && !email.Any(char.IsWhiteSpace)
                    ? email
                    : null;
        }
    }

    /// <summary>
    /// agy keeps <c>{ token: { access_token, refresh_token, expiry }, auth_method }</c> (older builds
    /// wrote the token fields at the root). The Google access token lives about an hour and agy
    /// refreshes it in memory only, so an expired file token is reported as such and the
    /// <c>agy /usage</c> fallback supplies the reading instead.
    /// </summary>
    internal static UsageCredential? ReadAntigravityToken(string path, string? account = null)
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
                account ?? "Antigravity",
                "google-oauth",
                expired ? null : token,
                null,
                expired ? "credential_expired" : null);
        }
    }
}
