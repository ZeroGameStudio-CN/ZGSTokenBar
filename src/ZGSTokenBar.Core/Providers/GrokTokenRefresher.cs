using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZGSTokenBar.Core;

/// <summary>
/// Renews an expired Grok CLI sign-in the same way the official CLI does: an OIDC
/// <c>refresh_token</c> grant against the issuer recorded in <c>~/.grok/auth.json</c>, then the
/// new tokens are written back into that file (all other fields kept) so the CLI and this app keep
/// sharing one session. The whole exchange runs under the CLI's own <c>auth.json.lock</c>, so a
/// rotated refresh token can never be lost between the two processes.
/// </summary>
public static class GrokTokenRefresher
{
    public const string Issuer = "https://auth.x.ai";
    public static readonly Uri TokenEndpoint = new("https://auth.x.ai/oauth2/token");
    internal const string PathField = "auth_path";
    internal const string EntryField = "auth_entry";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    // A refresh token the issuer rejected stays dead until the CLI signs in again and rewrites the
    // file, so remember the file state it was rejected in instead of re-asking every refresh.
    private static readonly Dictionary<string, (DateTime WrittenUtc, long Length)> Rejected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns a fresh access token, or null when the sign-in cannot be renewed silently.</summary>
    public static async Task<string?> RefreshAsync(
        string path,
        string entry,
        IUsageTransport transport,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cliLock = await AcquireCliLockAsync(path, cancellationToken).ConfigureAwait(false);
            if (cliLock is null) return null;
            var state = FileState(path);
            if (state is { } known && Rejected.TryGetValue(path, out var dead) && dead == known) return null;

            // Re-read inside the lock: the CLI may have renewed the session a moment ago.
            if (ReadDocument(path) is not { } document) return null;
            if (Node(document, entry) is not { } node) return null;
            if (Text(node, "key") is { } current && !Expired(node, now)) return current;
            if (Text(node, "refresh_token") is not { } refreshToken
                || Text(node, "oidc_client_id") is not { } clientId
                || !string.Equals(Text(node, "oidc_issuer")?.TrimEnd('/'), Issuer, StringComparison.Ordinal))
            {
                return null;
            }

            var body = "grant_type=refresh_token"
                + "&refresh_token=" + Uri.EscapeDataString(refreshToken)
                + "&client_id=" + Uri.EscapeDataString(clientId);
            var request = new UsageHttpRequest(
                "POST",
                TokenEndpoint,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Accept"] = "application/json" },
                body,
                "application/x-www-form-urlencoded");
            var result = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.ErrorKey is null && result.Status is 400 or 401 && state is { } rejectedState)
            {
                Rejected[path] = rejectedState;
            }

            if (result.ErrorKey is not null || result.Status != 200 || result.Body is null) return null;
            Rejected.Remove(path);

            string? accessToken;
            string? rotated;
            long? lifetime;
            try
            {
                using var response = JsonDocument.Parse(result.Body);
                var root = response.RootElement;
                accessToken = root.StringProperty("access_token");
                rotated = root.StringProperty("refresh_token");
                lifetime = root.TryGetProperty("expires_in", out var seconds) && seconds.TryGetInt64(out var value)
                    ? value
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(accessToken)) return null;
            var expiry = (lifetime is > 0 ? now.AddSeconds(lifetime.Value) : CredentialSupport.JwtExpiry(accessToken))
                ?? now.AddHours(1);
            node["key"] = accessToken;
            if (!string.IsNullOrWhiteSpace(rotated)) node["refresh_token"] = rotated;
            node["expires_at"] = expiry.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            try
            {
                var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                CredentialSupport.AtomicWriteLockedStream(path, stream => stream.Write(Encoding.UTF8.GetBytes(json)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The new token still works for this read; the CLI keeps its own copy of the old session.
            }

            return accessToken;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static bool IsRefreshable(JsonElement node) =>
        node.StringProperty("refresh_token") is { Length: > 0 }
        && node.StringProperty("oidc_client_id") is { Length: > 0 }
        && string.Equals(node.StringProperty("oidc_issuer")?.TrimEnd('/'), Issuer, StringComparison.Ordinal);

    private static async Task<FileStream?> AcquireCliLockAsync(string path, CancellationToken cancellationToken)
    {
        var lockPath = path + ".lock";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                try
                {
                    if (OperatingSystem.IsWindows()) stream.Lock(0, long.MaxValue);
                    return stream;
                }
                catch (IOException)
                {
                    stream.Dispose();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static (DateTime, long)? FileState(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonObject? ReadDocument(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 262_144) return null;
            return JsonNode.Parse(stream) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static JsonObject? Node(JsonObject document, string entry) =>
        entry.Length == 0 ? document : document[entry] as JsonObject;

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static bool Expired(JsonObject node, DateTimeOffset now) =>
        Text(node, "expires_at") is not { } raw
            ? false
            : !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                || at <= now.AddMinutes(1);
}
