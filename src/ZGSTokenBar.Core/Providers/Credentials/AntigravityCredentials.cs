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
    /// The Google account agy and the Antigravity app are signed in with. Prefers the freshest
    /// identity between the CLI's OAuth <c>id_token</c> email claim and the app's remembered
    /// <c>agy.onboarding.lastLoginUsername</c> in <c>%APPDATA%\Antigravity\app_storage.json</c>.
    /// </summary>
    public static string? AntigravitySignedInAccount(UsagePaths paths)
    {
        (string Email, DateTime LastWriteUtc)? tokenCandidate = null;
        foreach (var path in new[]
        {
            Path.Combine(paths.Home, ".gemini", "antigravity-cli", "antigravity-oauth-token"),
            Path.Combine(paths.Home, ".gemini", "jetski-standalone-oauth-token"),
        })
        {
            var document = Read(path);
            if (document is null) continue;
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var idToken = StringOf(root, "id_token", "idToken")
                    ?? (root.ObjectProperty("token") is { } nested ? StringOf(nested, "id_token", "idToken") : null);
                var email = CredentialSupport.JwtString(idToken, "email")?.Trim();
                if (!IsValidEmail(email)) continue;
                var stamp = SafeLastWriteUtc(path);
                if (tokenCandidate is null || stamp > tokenCandidate.Value.LastWriteUtc)
                {
                    tokenCandidate = (email!, stamp);
                }
            }
        }

        (string Email, DateTime LastWriteUtc)? appStorageCandidate = null;
        var appStoragePath = Path.Combine(paths.ApplicationData, "Antigravity", "app_storage.json");
        var appDocument = Read(appStoragePath);
        if (appDocument is not null)
        {
            using (appDocument)
            {
                if (appDocument.RootElement.ValueKind == JsonValueKind.Object
                    && appDocument.RootElement.TryGetProperty("agy.onboarding.lastLoginUsername", out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    var email = value.GetString()?.Trim();
                    // The app stores some values JSON-encoded a second time ("\"name@example.com\"").
                    if (email is { Length: > 1 } && email[0] == '"' && email[^1] == '"') email = email[1..^1].Trim();
                    if (IsValidEmail(email))
                    {
                        appStorageCandidate = (email!, SafeLastWriteUtc(appStoragePath));
                    }
                }
            }
        }

        if (tokenCandidate is { } tok && appStorageCandidate is { } app)
        {
            return app.LastWriteUtc > tok.LastWriteUtc ? app.Email : tok.Email;
        }

        return tokenCandidate?.Email ?? appStorageCandidate?.Email;
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return new FileInfo(path).LastWriteTimeUtc; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>
    /// agy keeps <c>{ token: { access_token, refresh_token, expiry }, auth_method }</c> (older builds
    /// wrote the token fields at the root). This file is the legacy fallback. An expired
    /// file token is reported as unavailable without trying to refresh it here.
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
