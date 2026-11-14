using System.Runtime.InteropServices;

namespace ZGSTokenBar.App;

internal static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwcpRound = 2;
    private const int DwmwcpDoNotRound = 1;
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

    /// <summary>
    /// Prepares a per-pixel-alpha (layered) window that paints its own opaque card: DWM rounding
    /// and border are turned off and no blur is requested.
    /// </summary>
    public static bool TryPrepareLayered(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;
        try
        {
            var corner = DwmwcpDoNotRound;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            var border = DwmwaColorNone;
            return DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int)) == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryAccent(IntPtr handle, bool light) =>
        TryAccent(handle, light, light ? 0xB0F6F4F4u : 0xCC1C1612u);

    private static bool TryAccent(IntPtr handle, bool light, uint gradient)
    {
        _ = light;
        var accent = new AccentPolicy
        {
            AccentState = AccentEnableAcrylic,
            GradientColor = gradient,
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

/// <summary>Pushes a 32bpp ARGB bitmap to a WS_EX_LAYERED window with per-pixel alpha.</summary>
internal static class LayeredWindow
{
    public const int ExStyle = 0x00080000;
    private const int GwlExStyle = -20;
    private const int UlwAlpha = 2;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;

    public static bool Update(IntPtr handle, Bitmap bitmap, bool resetStyle = false)
    {
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;
        if (resetStyle)
        {
            var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(exStyle & ~ExStyle));
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(exStyle | ExStyle));
        }
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var hbitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memory, hbitmap);
            var size = new NativeSize { Width = bitmap.Width, Height = bitmap.Height };
            var source = new NativePoint();
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };
            return UpdateLayeredWindow(handle, screen, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr window,
        IntPtr destination,
        IntPtr destinationPoint,
        ref NativeSize size,
        IntPtr source,
        ref NativePoint sourcePoint,
        int colorKey,
        ref BlendFunction blend,
        int flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }
}
