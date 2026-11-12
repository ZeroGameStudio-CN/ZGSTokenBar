using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using ZGSTokenBar.Builtins;
using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;
using ZGSTokenBar.Transport.NamedPipe;

namespace ZGSTokenBar.App;

/// <summary>Restores the settings window to its saved, still-visible location.</summary>
internal static class SettingsWindowPlacement
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    internal static void Restore(Form dialog, Rectangle? fallbackWorkingArea = null)
    {
        if (dialog.WindowState == FormWindowState.Minimized)
        {
            dialog.WindowState = FormWindowState.Normal;
        }
        var workingAreas = Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
        var fallback = fallbackWorkingArea ?? Screen.FromControl(dialog).WorkingArea;
        dialog.Location = RestoredLocation(dialog.Bounds, workingAreas, fallback);
        if (!dialog.Visible) dialog.Show();
        if (dialog.IsHandleCreated)
        {
            SetWindowPos(
                dialog.Handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
        dialog.BringToFront();
        dialog.Activate();
    }

    internal static Point RestoredLocation(
        Rectangle bounds,
        IReadOnlyList<Rectangle> workingAreas,
        Rectangle fallbackWorkingArea)
    {
        if (workingAreas.Any(area => Rectangle.Intersect(area, bounds) is { Width: > 0, Height: > 0 }))
        {
            return bounds.Location;
        }
        return new Point(
            fallbackWorkingArea.Left + Math.Max(0, (fallbackWorkingArea.Width - bounds.Width) / 2),
            fallbackWorkingArea.Top + Math.Max(0, (fallbackWorkingArea.Height - bounds.Height) / 2));
    }
}
