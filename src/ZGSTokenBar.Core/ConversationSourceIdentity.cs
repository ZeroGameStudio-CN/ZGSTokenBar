using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZGSTokenBar.Core;

internal static class ConversationSourceIdentity
{
    internal static string? Read(FileStream stream)
    {
        if (!OperatingSystem.IsWindows() || !GetFileInformationByHandle(stream.SafeFileHandle, out var info)) return null;
        return $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}:{info.CreationHigh:x8}{info.CreationLow:x8}";
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
