using System.Runtime.InteropServices;

namespace ZGSTokenBar.App;

/// <summary>
/// Win32 side of the optional "embed in taskbar" mode (TrafficMonitor-style): the bar window is
/// re-parented as a WS_CHILD of the primary <c>Shell_TrayWnd</c>. Every call here either touches
/// only our own window or uses a non-blocking / time-limited probe of Explorer, so a hung Explorer
/// can never stall the bar's UI thread inside this class.
/// </summary>
internal static class TaskbarEmbedding
{
    internal const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsVisible = 0x10000000L;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExNoParentNotify = 0x00000004L;
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoErrorOnExit = 0x0020;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint GaParent = 1;
    private static readonly nint Top = 0;

    /// <summary>Primary taskbar window, or 0.</summary>
    internal static nint FindPrimaryTaskbar()
    {
        var tray = FindWindow(PrimaryTaskbarClass, null);
        return tray != 0 && IsWindowVisible(tray) ? tray : 0;
    }

    /// <summary>True when Explorer answers a WM_NULL within <paramref name="timeoutMs"/> and is not flagged hung.</summary>
    internal static bool IsResponsive(nint window, uint timeoutMs = 250)
    {
        if (window == 0 || !IsWindow(window) || IsHungAppWindow(window)) return false;
        return SendMessageTimeout(window, WmNull, 0, 0, SmtoAbortIfHung | SmtoErrorOnExit, timeoutMs, out _) != 0;
    }

    /// <summary>Non-blocking hang probe for the watchdog (reads a kernel flag, sends nothing).</summary>
    internal static bool IsHung(nint window) => window != 0 && IsWindow(window) && IsHungAppWindow(window);

    internal static bool IsAlive(nint window) => window != 0 && IsWindow(window);

    internal static uint WindowThread(nint window, out uint processId) => GetWindowThreadProcessId(window, out processId);

    internal static bool IsDescendant(nint parent, nint window) => IsChildWindow(parent, window);

    internal static bool SameDpiAwareness(nint first, nint second)
    {
        try
        {
            var a = GetWindowDpiAwarenessContext(first);
            var b = GetWindowDpiAwarenessContext(second);
            return a != 0 && b != 0 && AreDpiAwarenessContextsEqual(a, b);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Re-parents <paramref name="child"/> into <paramref name="parent"/> as a WS_CHILD window.
    /// Returns the original style so <see cref="Detach"/> can restore it; null on failure (the
    /// window is left exactly as it was).
    /// </summary>
    internal static (long Style, long ExStyle)? Attach(nint child, nint parent, out string failure)
    {
        failure = "";
        if (child == 0 || !IsAlive(parent)) { failure = "taskbar-missing"; return null; }
        if (!IsResponsive(parent)) { failure = "taskbar-unresponsive"; return null; }
        if (!SameDpiAwareness(child, parent)) { failure = "dpi-awareness-mismatch"; return null; }
        var style = GetWindowLongPtr(child, GwlStyle).ToInt64();
        var exStyle = GetWindowLongPtr(child, GwlExStyle).ToInt64();
        SetWindowLongPtr(child, GwlStyle, (nint)((style | WsChild) & ~WsPopup));
        SetWindowLongPtr(child, GwlExStyle, (nint)(exStyle | WsExNoParentNotify));
        _ = SetParent(child, parent);
        if (ParentOf(child) != parent)
        {
            failure = "setparent-failed:" + Marshal.GetLastWin32Error();
            _ = SetParent(child, 0);
            SetWindowLongPtr(child, GwlStyle, (nint)style);
            SetWindowLongPtr(child, GwlExStyle, (nint)exStyle);
            return null;
        }
        return (style, exStyle);
    }

    /// <summary>Turns the window back into a top-level popup with its original styles.</summary>
    internal static void Detach(nint child, (long Style, long ExStyle) original)
    {
        if (child == 0 || !IsWindow(child)) return;
        var hasBounds = TryGetScreenBounds(child, out var bounds);
        var visibleStyle = GetWindowLongPtr(child, GwlStyle).ToInt64() & WsVisible;
        // WinForms may rebuild the styles without WS_CHILD while the native parent is still
        // Explorer. Always clear that parent; testing WS_CHILD leaves a clipped, misplaced bar.
        _ = SetParent(child, 0);
        SetWindowLongPtr(child, GwlStyle, (nint)((original.Style & ~WsVisible) | visibleStyle));
        SetWindowLongPtr(child, GwlExStyle, (nint)original.ExStyle);
        if (hasBounds)
        {
            // Reparenting changes the coordinate space and can remove native topmost state.
            // Restore both before the next drag/paint, without activating or showing the bar.
            var topmost = (original.ExStyle & WsExTopmost) != 0;
            _ = SetWindowPos(child, topmost ? new nint(-1) : Top,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SwpNoActivate | SwpFrameChanged | (topmost ? 0 : SwpNoZOrder));
        }
    }

    internal static bool TryGetScreenBounds(nint window, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (window == 0 || !GetWindowRect(window, out var native)) return false;
        bounds = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
        return true;
    }

    internal static bool IsTopmost(nint window) =>
        window != 0 && (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExTopmost) != 0;

    internal static bool IsChild(nint window) =>
        window != 0 && (GetWindowLongPtr(window, GwlStyle).ToInt64() & WsChild) != 0;

    internal static nint ParentOf(nint window) => window == 0 ? 0 : GetAncestor(window, GaParent);

    /// <summary>
    /// Positions an embedded child using screen coordinates: converts them to the parent's client
    /// space and keeps the child on top of Explorer's own XAML content. Returns false when the
    /// window is not an embedded child so callers fall back to their top-level path.
    /// </summary>
    internal static bool TryPlaceChild(nint window, Point screenLocation, Size size, bool keepZOrder)
    {
        if (!IsChild(window)) return false;
        var parent = ParentOf(window);
        if (parent == 0) return false;
        var point = new NativePoint { X = screenLocation.X, Y = screenLocation.Y };
        if (!ScreenToClient(parent, ref point)) return false;
        return SetWindowPos(window, Top, point.X, point.Y, size.Width, size.Height,
            SwpNoActivate | (keepZOrder ? SwpNoZOrder : 0));
    }

    /// <summary>The topmost window under a screen point (passive hit-test, no input is sent).</summary>
    internal static nint WindowAt(Point screenPoint) => WindowFromPoint(new NativePoint { X = screenPoint.X, Y = screenPoint.Y });

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "IsChild")]
    private static extern bool IsChildWindow(nint parent, nint window);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDpiAwarenessContext(nint window);

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint parent);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
}
