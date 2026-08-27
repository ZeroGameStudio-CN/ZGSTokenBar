using System.Runtime.InteropServices;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record AiGatewayConnection(string Endpoint, string Token);

public interface IAiGatewayConnectionStore
{
    AiGatewayConnection? Read();
    void Write(AiGatewayConnection connection);
    void Delete();
}

public static class AiGatewayEndpoint
{
    public static bool TryNormalize(string? value, out string endpoint)
    {
        endpoint = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath is not ("" or "/")
            || uri.Host.Length == 0)
        {
            return false;
        }

        var isHttps = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopback = uri.IsLoopback
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isLoopback) return false;

        var builder = new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        endpoint = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    public static string Mask(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : "(invalid endpoint)";
    }
}

public sealed class AiGatewayConnectionStore : IAiGatewayConnectionStore
{
    public const string TargetName = "ZGSTokenBar:ai-gateway-balance";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public AiGatewayConnection? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(TargetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            _ = Marshal.GetLastWin32Error();
            return null;
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (native.CredentialBlob == nint.Zero || native.CredentialBlobSize is 0 or > 64 * 1024)
            {
                return null;
            }

            var bytes = new byte[checked((int)native.CredentialBlobSize)];
            Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != 1
                || !root.TryGetProperty("endpoint", out var endpointValue)
                || endpointValue.ValueKind != JsonValueKind.String
                || !AiGatewayEndpoint.TryNormalize(endpointValue.GetString(), out var endpoint)
                || !root.TryGetProperty("token", out var tokenValue)
                || tokenValue.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var token = tokenValue.GetString();
            if (string.IsNullOrWhiteSpace(token)
                || token.Length > 4096
                || token.Contains('\r')
                || token.Contains('\n'))
            {
                return null;
            }

            return new AiGatewayConnection(endpoint, token);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(AiGatewayConnection connection)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("AI Gateway credentials require Windows Credential Manager.");
        if (!AiGatewayEndpoint.TryNormalize(connection.Endpoint, out var endpoint))
        {
            throw new ArgumentException("The gateway endpoint must use HTTPS, except for loopback development.", nameof(connection));
        }
        if (string.IsNullOrWhiteSpace(connection.Token)
            || connection.Token.Length > 4096
            || connection.Token.Contains('\r')
            || connection.Token.Contains('\n'))
        {
            throw new ArgumentException("The AI Gateway observer token is invalid.", nameof(connection));
        }

        using var payloadStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payloadStream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("endpoint", endpoint);
            writer.WriteString("token", connection.Token);
            writer.WriteEndObject();
        }
        var payload = payloadStream.ToArray();
        var target = Marshal.StringToCoTaskMemUni(TargetName);
        var username = Marshal.StringToCoTaskMemUni("ZGSTokenBar");
        var blob = Marshal.AllocCoTaskMem(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, blob, payload.Length);
            var native = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)payload.Length,
                Persist = CredentialPersistLocalMachine,
                UserName = username,
            };
            if (!CredWrite(ref native, 0))
            {
                throw new InvalidOperationException($"Windows Credential Manager write failed ({Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(username);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!CredDelete(TargetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new InvalidOperationException($"Windows Credential Manager delete failed ({error}).");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out nint credential);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern bool CredFree(nint buffer);
}
