using System.Runtime.InteropServices;

namespace ZGSTokenBar.App;

internal static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;
    private const int DwmsbtMainWindow = 2;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    private const int WcaAccentPolicy = 19;
    private const int AccentEnableBlur = 3;
    private const int AccentEnableAcrylic = 4;

    public static bool TryApply(IntPtr handle, bool light, bool transient)
    {
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;
        try
        {
            var dark = light ? 0 : 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var corner = DwmwcpRound;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            var border = DwmwaColorNone;
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
            var backdrop = transient ? DwmsbtTransientWindow : DwmsbtMainWindow;
            if (DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0)
            {
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                _ = DwmExtendFrameIntoClientArea(handle, ref margins);
                return true;
            }

            return TryAccent(handle, light);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryAccent(IntPtr handle, bool light)
    {
        var accent = new AccentPolicy
        {
            AccentState = AccentEnableAcrylic,
            GradientColor = light ? 0xB0F6F4F4u : 0xCC1C1612u,
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var payload = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = data,
                SizeOfData = size,
            };
            if (SetWindowCompositionAttribute(handle, ref payload) != 0) return true;
            accent.AccentState = AccentEnableBlur;
            Marshal.StructureToPtr(accent, data, false);
            return SetWindowCompositionAttribute(handle, ref payload) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr window, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
