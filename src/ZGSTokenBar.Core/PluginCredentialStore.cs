using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Core;

public sealed class PluginCredentialStore : IPluginCredentialBroker
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public ValueTask<string?> ResolveAsync(
        string pluginId,
        string slot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(pluginId, slot));
    }

    public string? Read(string pluginId, string slot)
    {
        var target = Target(pluginId, slot);
        if (!OperatingSystem.IsWindows()) return null;
        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            return null;
        }
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (native.CredentialBlob == nint.Zero
                || native.CredentialBlobSize is 0 or > 32 * 1024)
            {
                return null;
            }
            var bytes = new byte[checked((int)native.CredentialBlobSize)];
            try
            {
                Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
                var value = Encoding.UTF8.GetString(bytes);
                return string.IsNullOrEmpty(value) ? null : value;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(string pluginId, string slot, string value)
    {
        var targetName = Target(pluginId, slot);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Plugin credentials require Windows Credential Manager.");
        }
        var payload = Encoding.UTF8.GetBytes(value);
        if (payload.Length is <= 0 or > 32 * 1024
            || value.Contains('\0'))
        {
            throw new ArgumentException("Plugin credential is invalid.", nameof(value));
        }
        var target = Marshal.StringToCoTaskMemUni(targetName);
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
                throw new InvalidOperationException(
                    $"Windows Credential Manager write failed ({Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(username);
            Marshal.FreeCoTaskMem(blob);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public void Delete(string pluginId, string slot)
    {
        var target = Target(pluginId, slot);
        if (!OperatingSystem.IsWindows()) return;
        if (!CredDelete(target, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new InvalidOperationException(
                    $"Windows Credential Manager delete failed ({error}).");
            }
        }
    }

    private static string Target(string pluginId, string slot)
    {
        if (!PluginValidation.IsStableId(pluginId)
            || !PluginValidation.IsStableId(slot))
        {
            throw new ArgumentException("Plugin credential identity is invalid.");
        }
        return $"ZGSTokenBar:plugin:{pluginId}:{slot}";
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
    private static extern bool CredRead(
        string target,
        uint type,
        uint reservedFlag,
        out nint credential);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(nint buffer);
}
