using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZGSTokenBar.Core;

internal static class ConversationSourceIdentity
{
    internal readonly record struct Snapshot(string Identity, long Length, long WriteTicks);

    internal static string? Read(FileStream stream) => ReadSnapshot(stream)?.Identity;

    internal static Snapshot? ReadSnapshot(FileStream stream)
    {
        if (!OperatingSystem.IsWindows() || !GetFileInformationByHandle(stream.SafeFileHandle, out var info)) return null;
        var identity = $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}:{info.CreationHigh:x8}{info.CreationLow:x8}";
        var length = ((long)info.SizeHigh << 32) | info.SizeLow;
        var writeTime = ((long)info.WriteHigh << 32) | info.WriteLow;
        return new Snapshot(identity, length, DateTime.FromFileTimeUtc(writeTime).Ticks);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
