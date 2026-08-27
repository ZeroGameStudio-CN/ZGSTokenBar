using System.Net;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

internal static class CredentialSupport
{
    public static JsonDocument? JwtPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return JsonDocument.Parse(Convert.FromBase64String(payload));
        }
        catch
        {
            return null;
        }
    }

    public static DateTimeOffset? JwtExpiry(string? token)
    {
        using var payload = JwtPayload(token);
        if (payload is null) return null;
        var exp = payload.RootElement.NumberProperty("exp");
        if (exp is null) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)exp.Value);
        }
        catch
        {
            return null;
        }
    }

    public static string? JwtString(string? token, params string[] names)
    {
        using var payload = JwtPayload(token);
        return payload?.RootElement.StringProperty(names);
    }

    public static string? JwtNestedString(string? token, string objectName, params string[] names)
    {
        using var payload = JwtPayload(token);
        return payload?.RootElement.ObjectProperty(objectName)?.StringProperty(names);
    }

    public static string? JwtAudience(string? token)
    {
        using var payload = JwtPayload(token);
        if (payload is null || !payload.RootElement.TryGetProperty("aud", out var audience)) return null;
        if (audience.ValueKind == JsonValueKind.String) return audience.GetString();
        if (audience.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in audience.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) return item.GetString();
            }
        }

        return null;
    }

    public static bool AtomicWrite(string path, string contents, string? expectedContents = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException("Atomic write requires a parent directory.");
        Directory.CreateDirectory(directory);
        using var writeLock = AcquireWriteLock(Path.Combine(directory, $".{Path.GetFileName(path)}.wmt.lock"));
        if (writeLock is null) return false;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
                {
                    writer.Write(contents);
                    writer.Flush();
                }
                stream.Flush(true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            if (expectedContents is not null)
            {
                string currentContents;
                try
                {
                    currentContents = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (IOException)
                {
                    return false;
                }
                if (!string.Equals(currentContents, expectedContents, StringComparison.Ordinal)) return false;
            }

            File.Move(temporaryPath, path, true);
            return true;
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static FileStream? AcquireWriteLock(string lockPath)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                return stream;
            }
            catch (IOException) when (attempt < 39)
            {
                Thread.Sleep(25);
            }
        }
        return null;
    }
}

internal static class QuotaHttp
{
    public static HttpClient Create()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
            MaxResponseContentBufferSize = 256 * 1024,
        };
    }

    public static HttpClient CreateAiGateway()
    {
        return new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 2,
            Proxy = new PrivateObserverProxy(HttpClient.DefaultProxy),
            UseProxy = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 32 * 1024,
        };
    }
}

internal sealed class PrivateObserverProxy(IWebProxy fallback) : IWebProxy
{
    private readonly IWebProxy _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    public ICredentials? Credentials
    {
        get => _fallback.Credentials;
        set => _fallback.Credentials = value;
    }

    public Uri GetProxy(Uri destination) =>
        IsPrivateObserverEndpoint(destination) ? destination : _fallback.GetProxy(destination) ?? destination;

    public bool IsBypassed(Uri host) =>
        IsPrivateObserverEndpoint(host) || _fallback.IsBypassed(host);

    internal static bool IsPrivateObserverEndpoint(Uri destination)
    {
        if (destination.IsLoopback
            || destination.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
