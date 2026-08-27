using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ZGSTokenBar.App;

internal sealed record DisplayTopologySource(
    string GdiName,
    string Identity,
    bool Primary,
    Rectangle Bounds,
    Rectangle WorkingArea);

internal sealed record DisplayTopologyScreen(
    string GdiName,
    string MonitorKey,
    bool Primary,
    Rectangle Bounds,
    Rectangle WorkingArea);

internal sealed class DisplayTopologySnapshot
{
    public DisplayTopologySnapshot(
        string key,
        string sessionClass,
        IReadOnlyList<DisplayTopologyScreen> screens)
    {
        Key = key;
        SessionClass = sessionClass;
        Screens = screens;
        IdentitySignature = key + "|" + string.Join(
            ';',
            screens
                .OrderBy(screen => screen.GdiName, StringComparer.OrdinalIgnoreCase)
                .Select(screen => NormalizeGdiName(screen.GdiName) + "=" + screen.MonitorKey));
    }

    public string Key { get; }
    public string SessionClass { get; }
    public IReadOnlyList<DisplayTopologyScreen> Screens { get; }
    public string IdentitySignature { get; }
    public DisplayTopologyScreen Primary => Screens.FirstOrDefault(screen => screen.Primary) ?? Screens[0];

    public DisplayTopologyScreen? FindByGdiName(string? gdiName) => string.IsNullOrWhiteSpace(gdiName)
        ? null
        : Screens.FirstOrDefault(screen => string.Equals(
            screen.GdiName,
            gdiName,
            StringComparison.OrdinalIgnoreCase));

    public DisplayTopologyScreen? FindByMonitorKey(string? monitorKey) => string.IsNullOrWhiteSpace(monitorKey)
        ? null
        : Screens.FirstOrDefault(screen => string.Equals(
            screen.MonitorKey,
            monitorKey,
            StringComparison.Ordinal));

    public DisplayTopologyScreen? ScreenForWindow(Rectangle bounds)
    {
        var best = Screens
            .Select(screen => (Screen: screen, Area: IntersectionArea(screen.Bounds, bounds)))
            .Where(candidate => candidate.Area > 0)
            .OrderByDescending(candidate => candidate.Area)
            .ThenByDescending(candidate => candidate.Screen.Primary)
            .FirstOrDefault();
        return best.Area > 0 ? best.Screen : null;
    }

    internal static string NormalizeGdiName(string value) => value.Trim().ToUpperInvariant();

    private static long IntersectionArea(Rectangle left, Rectangle right)
    {
        var intersection = Rectangle.Intersect(left, right);
        return intersection.IsEmpty ? 0 : (long)intersection.Width * intersection.Height;
    }
}

internal static class DisplayTopology
{
    private const uint QueryOnlyActivePaths = 0x00000002;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const uint GetSourceName = 1;
    private const uint GetTargetName = 2;
    private const int WtsCurrentSession = -1;
    private const int WtsClientProtocolType = 16;
    private const int MaximumBufferAttempts = 3;

    public static DisplayTopologySnapshot? Capture()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return null;

        var sessionClass = ReadSessionClass();
        var physicalIdentitiesAvailable = TryReadPhysicalIdentities(out var physicalIdentities)
            && screens.All(screen => physicalIdentities.ContainsKey(NormalizeGdiName(screen.DeviceName)));
        var sources = screens.Select(screen =>
        {
            var gdiName = NormalizeGdiName(screen.DeviceName);
            var identity = physicalIdentitiesAvailable
                ? "path:" + physicalIdentities[gdiName]
                : "gdi:" + sessionClass + "|" + gdiName;
            return new DisplayTopologySource(
                screen.DeviceName,
                identity,
                screen.Primary,
                screen.Bounds,
                screen.WorkingArea);
        });
        return CreateSnapshot(sessionClass, sources);
    }

    internal static DisplayTopologySnapshot CreateSnapshot(
        string sessionClass,
        IEnumerable<DisplayTopologySource> sources)
    {
        var normalizedSession = string.IsNullOrWhiteSpace(sessionClass)
            ? "unknown"
            : sessionClass.Trim().ToLowerInvariant();
        var screens = sources
            .Select(source => new DisplayTopologyScreen(
                source.GdiName,
                HashKey("monitor-v1:", normalizedSession + "|" + source.Identity),
                source.Primary,
                source.Bounds,
                source.WorkingArea))
            .OrderBy(screen => screen.GdiName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (screens.Length == 0) throw new ArgumentException("A display topology requires at least one screen.", nameof(sources));

        var canonical = "v1|" + normalizedSession + "|views=" + screens.Length + "|"
            + string.Join('|', screens.Select(screen => screen.MonitorKey).Order(StringComparer.Ordinal));
        return new DisplayTopologySnapshot(
            HashKey("topology-v1:", canonical),
            normalizedSession,
            screens);
    }

    private static bool TryReadPhysicalIdentities(out Dictionary<string, string> identities)
    {
        identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < MaximumBufferAttempts; attempt++)
        {
            var error = GetDisplayConfigBufferSizes(QueryOnlyActivePaths, out var pathCount, out var modeCount);
            if (error != ErrorSuccess || pathCount == 0) return false;

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[Math.Max(1, modeCount)];
            var actualPathCount = pathCount;
            var actualModeCount = modeCount;
            error = QueryDisplayConfig(
                QueryOnlyActivePaths,
                ref actualPathCount,
                paths,
                ref actualModeCount,
                modes,
                nint.Zero);
            if (error == ErrorInsufficientBuffer) continue;
            if (error != ErrorSuccess) return false;

            var targetPaths = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths.Take((int)actualPathCount))
            {
                if (!TryReadSourceName(path.SourceInfo, out var gdiName)
                    || !TryReadTargetPath(path.TargetInfo, out var targetPath))
                {
                    return false;
                }

                var normalizedGdi = NormalizeGdiName(gdiName);
                if (!targetPaths.TryGetValue(normalizedGdi, out var pathsForSource))
                {
                    pathsForSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    targetPaths[normalizedGdi] = pathsForSource;
                }
                pathsForSource.Add(targetPath.Trim().ToUpperInvariant());
            }

            foreach (var entry in targetPaths)
            {
                identities[entry.Key] = string.Join('\0', entry.Value.Order(StringComparer.OrdinalIgnoreCase));
            }
            return identities.Count > 0;
        }
        return false;
    }

    private static bool TryReadSourceName(DisplayConfigPathSourceInfo source, out string gdiName)
    {
        var request = new DisplayConfigSourceDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetSourceName,
                Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>(),
                AdapterId = source.AdapterId,
                Id = source.Id,
            },
        };
        var error = DisplayConfigGetDeviceInfo(ref request);
        gdiName = request.ViewGdiDeviceName ?? string.Empty;
        return error == ErrorSuccess && !string.IsNullOrWhiteSpace(gdiName);
    }

    private static bool TryReadTargetPath(DisplayConfigPathTargetInfo target, out string targetPath)
    {
        var request = new DisplayConfigTargetDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = GetTargetName,
                Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>(),
                AdapterId = target.AdapterId,
                Id = target.Id,
            },
        };
        var error = DisplayConfigGetDeviceInfo(ref request);
        targetPath = request.MonitorDevicePath ?? string.Empty;
        return error == ErrorSuccess && !string.IsNullOrWhiteSpace(targetPath);
    }

    private static string ReadSessionClass()
    {
        if (!WTSQuerySessionInformation(
                nint.Zero,
                WtsCurrentSession,
                WtsClientProtocolType,
                out var buffer,
                out var bytes)
            || buffer == nint.Zero
            || bytes < sizeof(ushort))
        {
            if (buffer != nint.Zero) WTSFreeMemory(buffer);
            return "unknown";
        }

        try
        {
            return Marshal.ReadInt16(buffer) switch
            {
                0 => "console",
                2 => "rdp",
                _ => "unknown",
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string HashKey(string prefix, string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return prefix + Convert.ToHexStringLower(digest);
    }

    private static string NormalizeGdiName(string value) => DisplayTopologySnapshot.NormalizeGdiName(value);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint pathCount,
        out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint pathCount,
        [Out] DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        [Out] DisplayConfigModeInfo[] modes,
        nint currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName request);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        nint server,
        int sessionId,
        int infoClass,
        out nint buffer,
        out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public fixed byte ModeInfo[48];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? MonitorDevicePath;
    }
}
