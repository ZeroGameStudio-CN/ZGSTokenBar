using System.Runtime.InteropServices;
using System.Text;

namespace ZGSTokenBar.App;

internal static class TaskbarPlacement
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";
    private const int MinimumVisibleThickness = 8;
    private const int Gap = 6;
    private const int DockThreshold = 32;
    private const int MonitorSwitchInset = 8;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private static readonly nint TopMostWindow = new(-1);
    private static PlacementTrack[]? _cachedTracks;
    private static Size _cachedTrackWindowSize;
    private static readonly HashSet<string> ShellSurfaceClasses = new(StringComparer.Ordinal)
    {
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "ControlCenterWindow",
    };

    public static bool TryGetTarget(Size windowSize, double? relativePosition, out Point location)
        => TryGetTarget(windowSize, relativePosition, null, out location, out _);

    public static bool TryGetTarget(
        Size windowSize,
        double? relativePosition,
        string? preferredMonitor,
        out Point location,
        out string? resolvedMonitor)
    {
        location = Point.Empty;
        resolvedMonitor = null;
        if (!TryGetTracks(windowSize, preferredMonitor, out var tracks)) return false;
        var track = SelectTrack(tracks, preferredMonitor, null);
        resolvedMonitor = track.MonitorName;
        location = track.LocationAt(relativePosition ?? 1);
        return true;
    }

    public static bool TryConstrain(
        Size windowSize,
        Point requestedLocation,
        out Point location,
        out double relativePosition)
        => TryConstrain(windowSize, requestedLocation, null, out location, out relativePosition, out _);

    public static bool TryConstrain(
        Size windowSize,
        Point requestedLocation,
        string? preferredMonitor,
        out Point location,
        out double relativePosition,
        out string? resolvedMonitor)
    {
        location = Point.Empty;
        relativePosition = 1;
        resolvedMonitor = null;
        if (!TryGetTracks(windowSize, preferredMonitor, out var tracks)) return false;
        var requestedCenter = new Point(
            requestedLocation.X + windowSize.Width / 2,
            requestedLocation.Y + windowSize.Height / 2);
        var track = SelectTrack(tracks, preferredMonitor, requestedCenter);
        resolvedMonitor = track.MonitorName;
        location = track.Constrain(requestedLocation);
        relativePosition = track.RelativePosition(location);
        return true;
    }

    public static bool TryGetDockTarget(
        Size windowSize,
        Point requestedLocation,
        Point cursorPosition,
        string? preferredMonitor,
        out Point location,
        out double relativePosition,
        out string? resolvedMonitor)
    {
        location = Point.Empty;
        relativePosition = 1;
        resolvedMonitor = null;
        if (!TryGetTracks(windowSize, null, out var tracks)) return false;

        var candidate = SelectDockTrack(tracks, cursorPosition, preferredMonitor);
        if (candidate is not { } dockTrack) return false;

        resolvedMonitor = dockTrack.MonitorName;
        location = dockTrack.Constrain(requestedLocation);
        relativePosition = dockTrack.RelativePosition(location);
        return true;
    }

    internal static PlacementTrack? SelectDockTrack(
        IReadOnlyList<PlacementTrack> tracks,
        Point cursorPosition,
        string? preferredMonitor)
    {
        return tracks
            .Select(track => (Track: track, Distance: DistanceToTaskbar(track.TaskbarBounds, cursorPosition)))
            .Where(candidate => candidate.Distance <= DockThreshold)
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => string.Equals(
                candidate.Track.MonitorName,
                preferredMonitor,
                StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(candidate => candidate.Track.Primary)
            .Select(candidate => candidate.Track)
            .Select(track => (PlacementTrack?)track)
            .FirstOrDefault();
    }

    internal static string MonitorNameAt(Point point) => MonitorNameAt(point, null);

    internal static string MonitorNameAt(Point point, string? currentMonitor)
    {
        var screen = Screen.FromPoint(point);
        if (string.IsNullOrWhiteSpace(currentMonitor)
            || string.Equals(screen.DeviceName, currentMonitor, StringComparison.OrdinalIgnoreCase))
        {
            return screen.DeviceName;
        }

        var inset = Math.Min(
            MonitorSwitchInset,
            Math.Min(screen.Bounds.Width / 4, screen.Bounds.Height / 4));
        var interior = screen.Bounds;
        interior.Inflate(-inset, -inset);
        return interior.Contains(point) ? screen.DeviceName : currentMonitor;
    }

    public static void InvalidateCache()
    {
        _cachedTracks = null;
        _cachedTrackWindowSize = Size.Empty;
    }

    internal static bool CanReuseCachedTracks(
        IReadOnlyList<PlacementTrack>? tracks,
        Size cachedWindowSize,
        Size requestedWindowSize,
        string? requiredMonitor)
    {
        if (tracks is not { Count: > 0 } || cachedWindowSize != requestedWindowSize) return false;
        return string.IsNullOrWhiteSpace(requiredMonitor)
            || tracks.Any(track => string.Equals(
                track.MonitorName,
                requiredMonitor,
                StringComparison.OrdinalIgnoreCase));
    }

    internal static PlacementTrack SelectTrack(
        IReadOnlyList<PlacementTrack> tracks,
        string? preferredMonitor,
        Point? requestedCenter)
    {
        if (!string.IsNullOrWhiteSpace(preferredMonitor))
        {
            foreach (var track in tracks)
            {
                if (string.Equals(track.MonitorName, preferredMonitor, StringComparison.OrdinalIgnoreCase))
                {
                    return track;
                }
            }
        }

        if (requestedCenter is { } center)
        {
            foreach (var track in tracks)
            {
                if (track.ScreenBounds.Contains(center)) return track;
            }
        }

        foreach (var track in tracks)
        {
            if (track.Primary) return track;
        }

        return tracks[0];
    }

    private static bool TryGetTracks(
        Size windowSize,
        string? requiredMonitor,
        out PlacementTrack[] tracks)
    {
        if (CanReuseCachedTracks(
                _cachedTracks,
                _cachedTrackWindowSize,
                windowSize,
                requiredMonitor))
        {
            tracks = _cachedTracks!;
            return true;
        }

        var discovered = new List<PlacementTrack>();
        EnumWindowsProc callback = (window, _) =>
        {
            var className = WindowClass(window);
            if (className is not (PrimaryTaskbarClass or SecondaryTaskbarClass)
                || !IsWindowVisible(window)
                || !GetWindowRect(window, out var taskbarBounds)
                || Math.Min(Math.Abs(taskbarBounds.Width), Math.Abs(taskbarBounds.Height)) < MinimumVisibleThickness)
            {
                return true;
            }

            var tray = FindWindowEx(window, 0, "TrayNotifyWnd", null);
            var trayBounds = default(NativeRect);
            var hasTray = tray != 0 && GetWindowRect(tray, out trayBounds);

            var screen = Screen.FromRectangle(taskbarBounds.Rectangle);
            if (discovered.Any(track => string.Equals(
                    track.MonitorName,
                    screen.DeviceName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (Math.Abs(taskbarBounds.Width) >= Math.Abs(taskbarBounds.Height))
            {
                var minimum = taskbarBounds.Left + Gap;
                var taskbarMaximum = Math.Max(minimum, taskbarBounds.Right - windowSize.Width - Gap);
                var maximum = hasTray
                    ? Math.Clamp(trayBounds.Left - windowSize.Width - Gap, minimum, taskbarMaximum)
                    : taskbarMaximum;
                discovered.Add(new PlacementTrack(
                    screen.DeviceName,
                    screen.Bounds,
                    className == PrimaryTaskbarClass,
                    true,
                    minimum,
                    maximum,
                    taskbarBounds.Top + (taskbarBounds.Height - windowSize.Height) / 2)
                {
                    TaskbarBounds = taskbarBounds.Rectangle,
                });
            }
            else
            {
                var minimum = taskbarBounds.Top + Gap;
                var taskbarMaximum = Math.Max(minimum, taskbarBounds.Bottom - windowSize.Height - Gap);
                var maximum = hasTray
                    ? Math.Clamp(trayBounds.Top - windowSize.Height - Gap, minimum, taskbarMaximum)
                    : taskbarMaximum;
                discovered.Add(new PlacementTrack(
                    screen.DeviceName,
                    screen.Bounds,
                    className == PrimaryTaskbarClass,
                    false,
                    minimum,
                    maximum,
                    taskbarBounds.Left + (taskbarBounds.Width - windowSize.Width) / 2)
                {
                    TaskbarBounds = taskbarBounds.Rectangle,
                });
            }

            return true;
        };

        _ = EnumWindows(callback, 0);
        tracks = discovered
            .OrderByDescending(track => track.Primary)
            .ThenBy(track => track.MonitorName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tracks.Length > 0)
        {
            _cachedTracks = tracks;
            _cachedTrackWindowSize = windowSize;
        }
        return tracks.Length > 0;
    }

    private static string WindowClass(nint window)
    {
        var className = new StringBuilder(128);
        _ = GetClassName(window, className, className.Capacity);
        return className.ToString();
    }

    private static int DistanceToTaskbar(Rectangle taskbarBounds, Point point)
    {
        if (taskbarBounds.IsEmpty) return int.MaxValue;
        var horizontal = point.X < taskbarBounds.Left
            ? taskbarBounds.Left - point.X
            : point.X > taskbarBounds.Right
                ? point.X - taskbarBounds.Right
                : 0;
        var vertical = point.Y < taskbarBounds.Top
            ? taskbarBounds.Top - point.Y
            : point.Y > taskbarBounds.Bottom
                ? point.Y - taskbarBounds.Bottom
                : 0;
        return Math.Max(horizontal, vertical);
    }

    public static bool ShowAt(nint window, Point location, Size size)
    {
        if (window == 0 || size.Width <= 0 || size.Height <= 0) return false;
        return SetWindowPos(
            window,
            TopMostWindow,
            location.X,
            location.Y,
            size.Width,
            size.Height,
            SetWindowPositionNoActivate);
    }

    public static bool MoveAt(nint window, Point location, Size size)
    {
        if (window == 0 || size.Width <= 0 || size.Height <= 0) return false;
        return SetWindowPos(
            window,
            0,
            location.X,
            location.Y,
            size.Width,
            size.Height,
            SetWindowPositionNoActivate | SetWindowPositionNoZOrder);
    }

    public static bool ShouldHideForFullscreen()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var foreground = GetForegroundWindow();
        if (foreground == 0 || foreground == taskbar) return false;

        var className = new StringBuilder(128);
        _ = GetClassName(foreground, className, className.Capacity);
        var foregroundClass = className.ToString();
        if (foregroundClass is "Progman" or "WorkerW" or "Shell_TrayWnd"
            || ShellSurfaceClasses.Contains(foregroundClass))
        {
            return false;
        }
        if (IsZoomed(foreground)) return false;
        if (!GetWindowRect(foreground, out var bounds)) return false;

        var screen = Screen.FromHandle(foreground).Bounds;
        return bounds.Left <= screen.Left + 1
            && bounds.Top <= screen.Top + 1
            && bounds.Right >= screen.Right - 1
            && bounds.Bottom >= screen.Bottom - 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximum);

    private delegate bool EnumWindowsProc(nint window, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    internal readonly record struct PlacementTrack(
        string MonitorName,
        Rectangle ScreenBounds,
        bool Primary,
        bool Horizontal,
        int Minimum,
        int Maximum,
        int Fixed)
    {
        public Rectangle TaskbarBounds { get; init; }

        public Point LocationAt(double relativePosition)
        {
            var normalized = double.IsFinite(relativePosition) ? Math.Clamp(relativePosition, 0, 1) : 1;
            var coordinate = Minimum + (int)Math.Round((Maximum - Minimum) * normalized);
            return Horizontal ? new Point(coordinate, Fixed) : new Point(Fixed, coordinate);
        }

        public Point Constrain(Point location)
        {
            var coordinate = Math.Clamp(Horizontal ? location.X : location.Y, Minimum, Maximum);
            return Horizontal ? new Point(coordinate, Fixed) : new Point(Fixed, coordinate);
        }

        public double RelativePosition(Point location)
        {
            if (Maximum <= Minimum) return 1;
            var coordinate = Horizontal ? location.X : location.Y;
            return Math.Clamp((coordinate - Minimum) / (double)(Maximum - Minimum), 0, 1);
        }
    }
}
