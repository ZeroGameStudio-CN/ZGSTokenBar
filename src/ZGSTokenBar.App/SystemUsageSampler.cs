using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ZGSTokenBar.App;

internal sealed record SystemProcessUsage(
    string Name,
    int ProcessCount,
    double? CpuPercent,
    ulong PrivateWorkingSetBytes,
    double? GpuPercent);

internal sealed record SystemUsageSnapshot(
    double? CpuPercent,
    ulong? MemoryUsedBytes,
    ulong? MemoryAvailableBytes,
    ulong? MemoryTotalBytes,
    double? GpuPercent,
    string? GpuEngine,
    int GpuProcessCount,
    int LogicalProcessorCount,
    DateTimeOffset CapturedAt)
{
    public double? MemoryPercent => SystemUsageMath.Percent(MemoryUsedBytes, MemoryTotalBytes);
    public double? DiskActivePercent { get; init; }
    public double? DiskReadBytesPerSecond { get; init; }
    public double? DiskWriteBytesPerSecond { get; init; }
    public IReadOnlyList<SystemProcessUsage> TopProcesses { get; init; } = [];
}

internal readonly record struct CpuUsageTimes(ulong Idle, ulong Kernel, ulong User);
internal readonly record struct DiskUsage(
    double? ActivePercent,
    double? ReadBytesPerSecond,
    double? WriteBytesPerSecond);
internal readonly record struct GpuCounterSample(string InstanceName, double Value);
internal readonly record struct GpuUsage(
    double? Percent,
    string? Engine,
    int ProcessCount,
    IReadOnlyDictionary<ProcessGpuEngineKey, double>? ProcessEngines = null);
internal readonly record struct ProcessCounterSample(
    int ProcessId,
    string Name,
    long CpuTicks,
    ulong PrivateWorkingSetBytes);
internal readonly record struct ProcessCpuBaseline(string Name, long CpuTicks);
internal readonly record struct GpuEngineKey(ulong LuidHigh, ulong LuidLow, int Physical, int Engine);
internal readonly record struct ProcessGpuEngineKey(int ProcessId, GpuEngineKey Engine);

internal enum GpuEngineKind
{
    Unknown,
    ThreeD,
    Copy,
    Compute,
    VideoDecode,
    VideoEncode,
    VideoProcessing,
    Security,
    Cuda,
}

internal static class SystemUsageMath
{
    public static double? CpuPercent(CpuUsageTimes previous, CpuUsageTimes current)
    {
        if (current.Idle < previous.Idle
            || current.Kernel < previous.Kernel
            || current.User < previous.User)
        {
            return null;
        }

        var idle = current.Idle - previous.Idle;
        var kernel = current.Kernel - previous.Kernel;
        var user = current.User - previous.User;
        var total = kernel + user;
        if (total == 0 || idle > total) return null;
        return Math.Clamp((total - idle) * 100d / total, 0, 100);
    }

    public static double? Percent(ulong? used, ulong? total)
    {
        if (used is null || total is null || total == 0) return null;
        return Math.Clamp(used.Value * 100d / total.Value, 0, 100);
    }

    public static double? ProcessCpuPercent(
        long previousTicks,
        long currentTicks,
        TimeSpan elapsed,
        int logicalProcessors)
    {
        if (currentTicks < previousTicks || elapsed <= TimeSpan.Zero || logicalProcessors <= 0)
        {
            return null;
        }

        var processSeconds = (currentTicks - previousTicks) / (double)TimeSpan.TicksPerSecond;
        return Math.Clamp(processSeconds / elapsed.TotalSeconds / logicalProcessors * 100, 0, 100);
    }

    public static IReadOnlyList<SystemProcessUsage> AggregateProcesses(
        IEnumerable<ProcessCounterSample> samples,
        IReadOnlyDictionary<int, ProcessCpuBaseline> previous,
        TimeSpan elapsed,
        int logicalProcessors,
        ulong? totalMemory,
        IReadOnlyDictionary<ProcessGpuEngineKey, double>? gpuProcessEngines,
        int limit = 5,
        ProcessAggregationWorkspace? workspace = null)
    {
        workspace?.Clear();
        var groups = workspace?.Groups
            ?? new Dictionary<string, ProcessGroup>(StringComparer.OrdinalIgnoreCase);
        var processGroups = gpuProcessEngines is null
            ? null
            : workspace?.ProcessGroups ?? new Dictionary<int, ProcessGroup>();
        var groupEngines = gpuProcessEngines is null
            ? null
            : workspace?.GroupEngines ?? new Dictionary<GroupGpuEngineKey, double>();
        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Name)) continue;
            if (!groups.TryGetValue(sample.Name, out var group))
            {
                group = new ProcessGroup(sample.Name);
                groups.Add(sample.Name, group);
            }

            group.ProcessCount++;
            group.PrivateWorkingSetBytes = AddSaturated(
                group.PrivateWorkingSetBytes,
                sample.PrivateWorkingSetBytes);
            if (previous.TryGetValue(sample.ProcessId, out var baseline)
                && string.Equals(baseline.Name, sample.Name, StringComparison.OrdinalIgnoreCase)
                && ProcessCpuPercent(
                    baseline.CpuTicks,
                    sample.CpuTicks,
                    elapsed,
                    logicalProcessors) is { } cpu)
            {
                group.CpuAvailable = true;
                group.CpuPercent += cpu;
            }

            if (processGroups is not null) processGroups[sample.ProcessId] = group;
        }

        if (gpuProcessEngines is not null && processGroups is not null && groupEngines is not null)
        {
            foreach (var group in groups.Values) group.GpuAvailable = true;
            foreach (var pair in gpuProcessEngines)
            {
                if (processGroups.TryGetValue(pair.Key.ProcessId, out var group))
                {
                    if (!double.IsFinite(pair.Value) || pair.Value < 0) continue;
                    var key = new GroupGpuEngineKey(group, pair.Key.Engine);
                    var total = groupEngines.GetValueOrDefault(key)
                        + Math.Clamp(pair.Value, 0, 100);
                    groupEngines[key] = total;
                    if (total > group.BusiestGpuPercent) group.BusiestGpuPercent = total;
                }
            }
        }

        return groups.Values
            .Select(group => new SystemProcessUsage(
                group.Name,
                group.ProcessCount,
                group.CpuAvailable ? Math.Clamp(group.CpuPercent, 0, 100) : null,
                group.PrivateWorkingSetBytes,
                group.GpuAvailable ? Math.Clamp(group.BusiestGpuPercent, 0, 100) : null))
            .OrderByDescending(process => ProcessPressure(process, totalMemory))
            .ThenByDescending(process => process.PrivateWorkingSetBytes)
            .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .ToArray();
    }

    public static GpuUsage AggregateGpu(IEnumerable<GpuCounterSample> samples)
    {
        var engines = new Dictionary<GpuEngineKey, (double Total, GpuEngineKind Kind)>();
        var activeProcesses = new HashSet<int>();
        var processEngines = new Dictionary<ProcessGpuEngineKey, double>();
        foreach (var sample in samples)
        {
            if (!double.IsFinite(sample.Value) || sample.Value < 0) continue;
            if (!TryParseGpuInstance(
                    sample.InstanceName.AsSpan(),
                    out var engineKey,
                    out var engineKind,
                    out var processId))
            {
                continue;
            }

            var value = Math.Clamp(sample.Value, 0, 100);
            var current = engines.GetValueOrDefault(engineKey);
            engines[engineKey] = (current.Total + value, engineKind);
            if (processId is not { } pid) continue;
            if (sample.Value >= .1) activeProcesses.Add(pid);
            var processKey = new ProcessGpuEngineKey(pid, engineKey);
            processEngines[processKey] = processEngines.GetValueOrDefault(processKey) + value;
        }

        if (engines.Count == 0) return new GpuUsage(null, null, 0);
        var busiest = engines
            .Select(pair => (
                pair.Key,
                Percent: Math.Clamp(pair.Value.Total, 0, 100),
                pair.Value.Kind))
            .OrderByDescending(engine => engine.Percent)
            .ThenBy(engine => engine.Key.LuidHigh)
            .ThenBy(engine => engine.Key.LuidLow)
            .ThenBy(engine => engine.Key.Physical)
            .ThenBy(engine => engine.Key.Engine)
            .First();
        return new GpuUsage(
            busiest.Percent,
            GpuEngineLabel(busiest.Kind),
            activeProcesses.Count,
            processEngines);
    }

    public static bool TryParseGpuInstance(
        ReadOnlySpan<char> instance,
        out GpuEngineKey engineKey,
        out GpuEngineKind engineKind,
        out int? processId)
    {
        engineKey = default;
        engineKind = GpuEngineKind.Unknown;
        processId = null;
        if (instance.IsEmpty) return false;

        var luid = instance.IndexOf("_luid_".AsSpan(), StringComparison.OrdinalIgnoreCase);
        var physical = luid < 0
            ? -1
            : IndexAfter(instance, "_phys_", luid + 6);
        var engine = physical < 0
            ? -1
            : IndexAfter(instance, "_eng_", physical + 6);
        var type = engine < 0
            ? -1
            : IndexAfter(instance, "_engtype_", engine + 5);
        if (luid < 0 || physical < 0 || engine < 0 || type < 0) return false;

        var luidValue = instance[(luid + 6)..physical];
        var luidSeparator = luidValue.IndexOf('_');
        if (luidSeparator <= 0
            || !TryParseHex(luidValue[..luidSeparator], out var luidHigh)
            || !TryParseHex(luidValue[(luidSeparator + 1)..], out var luidLow)
            || !int.TryParse(
                instance[(physical + 6)..engine],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var physicalIndex)
            || !int.TryParse(
                instance[(engine + 5)..type],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var engineIndex))
        {
            return false;
        }

        var typeValue = instance[(type + 9)..];
        var duplicateSuffix = typeValue.LastIndexOf('#');
        if (duplicateSuffix > 0
            && int.TryParse(
                typeValue[(duplicateSuffix + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _))
        {
            typeValue = typeValue[..duplicateSuffix];
        }
        engineKind = ParseEngineKind(typeValue);
        engineKey = new GpuEngineKey(luidHigh, luidLow, physicalIndex, engineIndex);

        if (instance.StartsWith("pid_".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            var processEnd = instance[4..].IndexOf('_');
            if (processEnd > 0
                && int.TryParse(
                    instance.Slice(4, processEnd),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pid)
                && pid >= 0)
            {
                processId = pid;
            }
        }
        return true;
    }

    public static string GpuEngineLabel(GpuEngineKind kind) => kind switch
    {
        GpuEngineKind.ThreeD => "3D",
        GpuEngineKind.Copy => "Copy",
        GpuEngineKind.Compute => "Compute",
        GpuEngineKind.VideoDecode => "Video Decode",
        GpuEngineKind.VideoEncode => "Video Encode",
        GpuEngineKind.VideoProcessing => "Video Processing",
        GpuEngineKind.Security => "Security",
        GpuEngineKind.Cuda => "CUDA",
        _ => "GPU",
    };

    private static double ProcessPressure(SystemProcessUsage process, ulong? totalMemory) =>
        Math.Max(
            process.CpuPercent ?? 0,
            Math.Max(
                Percent(process.PrivateWorkingSetBytes, totalMemory) ?? 0,
                process.GpuPercent ?? 0));

    private static ulong AddSaturated(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static int IndexAfter(ReadOnlySpan<char> value, string marker, int start)
    {
        if (start >= value.Length) return -1;
        var relative = value[start..].IndexOf(marker.AsSpan(), StringComparison.OrdinalIgnoreCase);
        return relative < 0 ? -1 : start + relative;
    }

    private static bool TryParseHex(ReadOnlySpan<char> value, out ulong result)
    {
        if (value.StartsWith("0x".AsSpan(), StringComparison.OrdinalIgnoreCase)) value = value[2..];
        return ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }

    private static GpuEngineKind ParseEngineKind(ReadOnlySpan<char> value)
    {
        if (value.Equals("3D".AsSpan(), StringComparison.OrdinalIgnoreCase)) return GpuEngineKind.ThreeD;
        if (value.Equals("Copy".AsSpan(), StringComparison.OrdinalIgnoreCase)) return GpuEngineKind.Copy;
        if (value.StartsWith("Compute".AsSpan(), StringComparison.OrdinalIgnoreCase)) return GpuEngineKind.Compute;
        if (value.Equals("VideoDecode".AsSpan(), StringComparison.OrdinalIgnoreCase)) return GpuEngineKind.VideoDecode;
        if (value.Equals("VideoEncode".AsSpan(), StringComparison.OrdinalIgnoreCase)) return GpuEngineKind.VideoEncode;
        if (value.Equals("VideoProcessing".AsSpan(), StringComparison.OrdinalIgnoreCase)) return GpuEngineKind.VideoProcessing;
        if (value.Equals("Security".AsSpan(), StringComparison.OrdinalIgnoreCase)) return GpuEngineKind.Security;
        if (value.Equals("Cuda".AsSpan(), StringComparison.OrdinalIgnoreCase)) return GpuEngineKind.Cuda;
        return GpuEngineKind.Unknown;
    }

    internal readonly record struct GroupGpuEngineKey(ProcessGroup Group, GpuEngineKey Engine);

    internal sealed class ProcessAggregationWorkspace
    {
        private readonly Dictionary<string, ProcessGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, ProcessGroup> _processGroups = [];
        private readonly Dictionary<GroupGpuEngineKey, double> _groupEngines = [];

        internal Dictionary<string, ProcessGroup> Groups => _groups;
        internal Dictionary<int, ProcessGroup> ProcessGroups => _processGroups;
        internal Dictionary<GroupGpuEngineKey, double> GroupEngines => _groupEngines;

        internal void Clear()
        {
            _groups.Clear();
            _processGroups.Clear();
            _groupEngines.Clear();
        }
    }

    internal sealed class ProcessGroup(string name)
    {
        public string Name { get; } = name;
        public int ProcessCount { get; set; }
        public double CpuPercent { get; set; }
        public bool CpuAvailable { get; set; }
        public ulong PrivateWorkingSetBytes { get; set; }
        public bool GpuAvailable { get; set; }
        public double BusiestGpuPercent { get; set; }
    }
}

internal sealed class SystemUsageSampler : IDisposable
{
    private readonly object _counterSync = new();
    private CpuUsageTimes? _previousCpu;
    private DiskUsageCounter? _disk;
    private GpuUsageCounter? _gpu;
    private readonly List<ProcessCounterSample> _processSamples = [];
    private Dictionary<int, ProcessCpuBaseline> _previousProcesses = [];
    private Dictionary<int, ProcessCpuBaseline> _currentProcesses = [];
    private readonly SystemUsageMath.ProcessAggregationWorkspace _processAggregation = new();
    private long? _previousProcessTimestamp;
    private nint _processBuffer;
    private int _processBufferCapacity;
    private bool _disposed;

    public SystemUsageSampler()
    {
        _ = InitializeDiskCounterAsync();
        _ = InitializeGpuCounterAsync();
    }

    public SystemUsageSnapshot Sample(bool includeProcesses = false)
    {
        var cpuTimes = ReadCpuTimes();
        var cpuPercent = cpuTimes is { } current && _previousCpu is { } previous
            ? SystemUsageMath.CpuPercent(previous, current)
            : null;
        if (cpuTimes is { }) _previousCpu = cpuTimes;

        ReadMemory(out var usedMemory, out var availableMemory, out var totalMemory);
        var (diskCounter, gpuCounter) = AvailableCounters();
        var disk = diskCounter?.Read() ?? new DiskUsage(null, null, null);
        var gpu = gpuCounter?.Read(includeProcesses) ?? new GpuUsage(null, null, 0);
        var topProcesses = includeProcesses
            ? ReadTopProcesses(totalMemory, gpu.ProcessEngines)
            : ResetProcessSampling();
        return new SystemUsageSnapshot(
            cpuPercent,
            usedMemory,
            availableMemory,
            totalMemory,
            gpu.Percent,
            gpu.Engine,
            gpu.ProcessCount,
            Environment.ProcessorCount,
            DateTimeOffset.UtcNow)
        {
            DiskActivePercent = disk.ActivePercent,
            DiskReadBytesPerSecond = disk.ReadBytesPerSecond,
            DiskWriteBytesPerSecond = disk.WriteBytesPerSecond,
            TopProcesses = topProcesses,
        };
    }

    private IReadOnlyList<SystemProcessUsage> ReadTopProcesses(
        ulong? totalMemory,
        IReadOnlyDictionary<ProcessGpuEngineKey, double>? gpuProcessEngines)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = _previousProcessTimestamp is { } previousTimestamp
            ? Stopwatch.GetElapsedTime(previousTimestamp, timestamp)
            : TimeSpan.Zero;
        _processSamples.Clear();
        _currentProcesses.Clear();

        ReadNativeProcesses();

        return CompleteProcessSample(timestamp, elapsed, totalMemory, gpuProcessEngines);
    }

    private unsafe void ReadNativeProcesses()
    {
        if (!TryGetProcessBuffer(out var bufferLength)) return;
        var offset = 0u;
        while (offset + (uint)sizeof(SystemProcessInformation) <= bufferLength)
        {
            var process = (SystemProcessInformation*)((byte*)_processBuffer + offset);
            if (TryReadNativeProcess(process, out var sample))
            {
                _processSamples.Add(sample);
                _currentProcesses[sample.ProcessId] = new ProcessCpuBaseline(sample.Name, sample.CpuTicks);
            }

            if (process->NextEntryOffset == 0) break;
            if (process->NextEntryOffset < (uint)sizeof(SystemProcessInformation)
                || process->NextEntryOffset > bufferLength - offset)
            {
                break;
            }
            offset += process->NextEntryOffset;
        }
    }

    private static unsafe bool TryReadNativeProcess(
        SystemProcessInformation* process,
        out ProcessCounterSample sample)
    {
        sample = default;
        var processId = process->UniqueProcessId.ToInt64();
        if (processId is <= 0 or > int.MaxValue
            || process->ImageName.Buffer == 0
            || process->ImageName.Length == 0
            || process->ImageName.Length > process->ImageName.MaximumLength
            || process->KernelTime < 0
            || process->UserTime < 0)
        {
            return false;
        }

        var name = ReadProcessName(process->ImageName);
        if (name.Length == 0) return false;
        var cpuTicks = process->KernelTime > long.MaxValue - process->UserTime
            ? long.MaxValue
            : process->KernelTime + process->UserTime;
        sample = new ProcessCounterSample(
            (int)processId,
            name,
            cpuTicks,
            process->WorkingSetPrivateSize > 0
                ? (ulong)process->WorkingSetPrivateSize
                : 0);
        return true;
    }

    private static unsafe string ReadProcessName(UnicodeString name)
    {
        var value = new ReadOnlySpan<char>((void*)name.Buffer, name.Length / sizeof(char));
        if (value.EndsWith(".exe".AsSpan(), StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return value.ToString();
    }

    private bool TryGetProcessBuffer(out uint bufferLength)
    {
        bufferLength = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = NtQuerySystemInformation(
                SystemProcessInformationClass,
                _processBuffer,
                (uint)_processBufferCapacity,
                out var requiredBytes);
            if (status == 0)
            {
                bufferLength = requiredBytes == 0
                    ? (uint)_processBufferCapacity
                    : Math.Min(requiredBytes, (uint)_processBufferCapacity);
                return _processBuffer != 0;
            }
            if (status != StatusInfoLengthMismatch
                || requiredBytes == 0
                || requiredBytes > MaximumProcessBufferBytes
                || !EnsureProcessBuffer(requiredBytes))
            {
                return false;
            }
        }
        return false;
    }

    private bool EnsureProcessBuffer(uint requiredBytes)
    {
        if (requiredBytes <= _processBufferCapacity) return true;
        var capacity = Math.Min(
            MaximumProcessBufferBytes,
            Math.Max(requiredBytes, requiredBytes + requiredBytes / 4));
        try
        {
            _processBuffer = _processBuffer == 0
                ? Marshal.AllocHGlobal(checked((int)capacity))
                : Marshal.ReAllocHGlobal(_processBuffer, checked((nint)capacity));
            _processBufferCapacity = checked((int)capacity);
            return _processBuffer != 0;
        }
        catch (OutOfMemoryException)
        {
            return false;
        }
    }

    private IReadOnlyList<SystemProcessUsage> CompleteProcessSample(
        long timestamp,
        TimeSpan elapsed,
        ulong? totalMemory,
        IReadOnlyDictionary<ProcessGpuEngineKey, double>? gpuProcessEngines)
    {
        var topProcesses = SystemUsageMath.AggregateProcesses(
            _processSamples,
            _previousProcesses,
            elapsed,
            Environment.ProcessorCount,
            totalMemory,
            gpuProcessEngines,
            workspace: _processAggregation);
        (_previousProcesses, _currentProcesses) = (_currentProcesses, _previousProcesses);
        _previousProcessTimestamp = timestamp;
        return topProcesses;
    }

    private IReadOnlyList<SystemProcessUsage> ResetProcessSampling()
    {
        _processSamples.Clear();
        _previousProcesses.Clear();
        _currentProcesses.Clear();
        _previousProcessTimestamp = null;
        return [];
    }

    private async Task InitializeDiskCounterAsync()
    {
        DiskUsageCounter? counter;
        try
        {
            counter = await Task.Run(DiskUsageCounter.TryCreate).ConfigureAwait(false);
        }
        catch
        {
            return;
        }
        InstallCounter(counter, isGpu: false);
    }

    private async Task InitializeGpuCounterAsync()
    {
        GpuUsageCounter? counter;
        try
        {
            counter = await Task.Run(GpuUsageCounter.TryCreate).ConfigureAwait(false);
        }
        catch
        {
            return;
        }
        InstallCounter(counter, isGpu: true);
    }

    private void InstallCounter(IDisposable? counter, bool isGpu)
    {
        if (counter is null) return;
        lock (_counterSync)
        {
            if (!_disposed)
            {
                if (isGpu) _gpu = (GpuUsageCounter)counter;
                else _disk = (DiskUsageCounter)counter;
                return;
            }
        }
        counter.Dispose();
    }

    private (DiskUsageCounter? Disk, GpuUsageCounter? Gpu) AvailableCounters()
    {
        lock (_counterSync) return (_disk, _gpu);
    }

    private static CpuUsageTimes? ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        return new CpuUsageTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static void ReadMemory(out ulong? used, out ulong? available, out ulong? total)
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
        {
            used = null;
            available = null;
            total = null;
            return;
        }

        total = status.TotalPhysical;
        available = Math.Min(status.AvailablePhysical, status.TotalPhysical);
        used = status.TotalPhysical - available.Value;
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    public void Dispose()
    {
        DiskUsageCounter? disk;
        GpuUsageCounter? gpu;
        lock (_counterSync)
        {
            if (_disposed) return;
            _disposed = true;
            disk = _disk;
            gpu = _gpu;
            _disk = null;
            _gpu = null;
        }
        disk?.Dispose();
        gpu?.Dispose();
        if (_processBuffer == 0) return;
        Marshal.FreeHGlobal(_processBuffer);
        _processBuffer = 0;
        _processBufferCapacity = 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private const int SystemProcessInformationClass = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint MaximumProcessBufferBytes = 16 * 1024 * 1024;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int informationClass,
        nint information,
        uint informationLength,
        out uint returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        public readonly uint Low;
        public readonly uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SystemProcessInformation
    {
        public readonly uint NextEntryOffset;
        public readonly uint NumberOfThreads;
        public readonly long WorkingSetPrivateSize;
        public readonly uint HardFaultCount;
        public readonly uint NumberOfThreadsHighWatermark;
        public readonly ulong CycleTime;
        public readonly long CreateTime;
        public readonly long UserTime;
        public readonly long KernelTime;
        public readonly UnicodeString ImageName;
        public readonly int BasePriority;
        public readonly nint UniqueProcessId;
        public readonly nint InheritedFromUniqueProcessId;
        public readonly uint HandleCount;
        public readonly uint SessionId;
        public readonly nuint UniqueProcessKey;
        public readonly nuint PeakVirtualSize;
        public readonly nuint VirtualSize;
        public readonly uint PageFaultCount;
        public readonly nuint PeakWorkingSetSize;
        public readonly nuint WorkingSetSize;
    }

    private sealed class DiskUsageCounter : IDisposable
    {
        private const uint PdhFormatDouble = 0x00000200;
        private const uint PdhValidData = 0x00000000;
        private const uint PdhNewData = 0x00000001;
        private nint _query;
        private nint _activeCounter;
        private nint _readCounter;
        private nint _writeCounter;

        private DiskUsageCounter(
            nint query,
            nint activeCounter,
            nint readCounter,
            nint writeCounter)
        {
            _query = query;
            _activeCounter = activeCounter;
            _readCounter = readCounter;
            _writeCounter = writeCounter;
            _ = PdhCollectQueryData(_query);
        }

        public static DiskUsageCounter? TryCreate()
        {
            if (PdhOpenQuery(null, 0, out var query) != 0) return null;

            var activeCounter = AddCounter(query, @"\PhysicalDisk(_Total)\% Disk Time");
            var readCounter = AddCounter(query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
            var writeCounter = AddCounter(query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
            if (activeCounter != 0 || readCounter != 0 || writeCounter != 0)
            {
                return new DiskUsageCounter(query, activeCounter, readCounter, writeCounter);
            }

            _ = PdhCloseQuery(query);
            return null;
        }

        public DiskUsage Read()
        {
            if (_query == 0 || PdhCollectQueryData(_query) != 0)
            {
                return new DiskUsage(null, null, null);
            }

            return new DiskUsage(
                ReadValue(_activeCounter, 0, 100),
                ReadValue(_readCounter, 0, double.MaxValue),
                ReadValue(_writeCounter, 0, double.MaxValue));
        }

        private static nint AddCounter(nint query, string path) =>
            PdhAddEnglishCounter(query, path, 0, out var counter) == 0 ? counter : 0;

        private static double? ReadValue(nint counter, double minimum, double maximum)
        {
            if (counter == 0
                || PdhGetFormattedCounterValue(
                    counter,
                    PdhFormatDouble,
                    out _,
                    out var value) != 0
                || value.Status is not (PdhValidData or PdhNewData)
                || !double.IsFinite(value.DoubleValue)
                || value.DoubleValue < minimum)
            {
                return null;
            }

            return Math.Clamp(value.DoubleValue, minimum, maximum);
        }

        public void Dispose()
        {
            if (_query == 0) return;
            _ = PdhCloseQuery(_query);
            _query = 0;
            _activeCounter = 0;
            _readCounter = 0;
            _writeCounter = 0;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
        private static extern uint PdhAddEnglishCounter(
            nint query,
            string counterPath,
            nint userData,
            out nint counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(nint query);

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(
            nint counter,
            uint format,
            out uint counterType,
            out PdhFormattedCounterValue value);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(nint query);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct PdhFormattedCounterValue
        {
            public readonly uint Status;
            public readonly double DoubleValue;
        }
    }

    private sealed class GpuUsageCounter : IDisposable
    {
        private const uint PdhFormatDouble = 0x00000200;
        private const uint PdhMoreData = 0x800007D2;
        private const uint PdhValidData = 0x00000000;
        private const uint PdhNewData = 0x00000001;
        private const uint MaximumBufferBytes = 16 * 1024 * 1024;
        private readonly Dictionary<GpuEngineKey, EngineTotal> _engines = [];
        private readonly HashSet<int> _activeProcesses = [];
        private readonly Dictionary<ProcessGpuEngineKey, double> _processEngines = [];
        private nint _query;
        private nint _counter;
        private nint _buffer;
        private int _bufferCapacity;

        private GpuUsageCounter(nint query, nint counter)
        {
            _query = query;
            _counter = counter;
            _ = PdhCollectQueryData(_query);
        }

        public static GpuUsageCounter? TryCreate()
        {
            if (PdhOpenQuery(null, 0, out var query) != 0) return null;
            if (PdhAddEnglishCounter(
                    query,
                    @"\GPU Engine(*)\Utilization Percentage",
                    0,
                    out var counter) == 0)
            {
                return new GpuUsageCounter(query, counter);
            }

            _ = PdhCloseQuery(query);
            return null;
        }

        public unsafe GpuUsage Read(bool includeProcesses)
        {
            _engines.Clear();
            _activeProcesses.Clear();
            _processEngines.Clear();
            if (_query == 0
                || _counter == 0
                || PdhCollectQueryData(_query) != 0
                || !TryGetFormattedArray(out var itemCount))
            {
                return new GpuUsage(null, null, 0);
            }

            var items = (PdhFormattedCounterValueItem*)_buffer;
            for (var index = 0; index < itemCount; index++)
            {
                var item = items[index];
                if (item.Name == 0
                    || item.Value.Status is not (PdhValidData or PdhNewData)
                    || !double.IsFinite(item.Value.DoubleValue)
                    || item.Value.DoubleValue < 0)
                {
                    continue;
                }

                var instance = NullTerminatedSpan((char*)item.Name);
                if (!SystemUsageMath.TryParseGpuInstance(
                        instance,
                        out var engineKey,
                        out var engineKind,
                        out var processId))
                {
                    continue;
                }

                var value = Math.Clamp(item.Value.DoubleValue, 0, 100);
                var current = _engines.GetValueOrDefault(engineKey);
                _engines[engineKey] = new EngineTotal(current.Percent + value, engineKind);
                if (processId is not { } pid) continue;
                if (item.Value.DoubleValue >= .1) _activeProcesses.Add(pid);
                if (!includeProcesses) continue;
                var processKey = new ProcessGpuEngineKey(pid, engineKey);
                _processEngines[processKey] = _processEngines.GetValueOrDefault(processKey) + value;
            }

            if (_engines.Count == 0) return new GpuUsage(null, null, 0);
            var busiestPercent = -1d;
            var busiestKind = GpuEngineKind.Unknown;
            foreach (var engine in _engines.Values)
            {
                var percent = Math.Clamp(engine.Percent, 0, 100);
                if (percent <= busiestPercent) continue;
                busiestPercent = percent;
                busiestKind = engine.Kind;
            }

            return new GpuUsage(
                busiestPercent,
                SystemUsageMath.GpuEngineLabel(busiestKind),
                _activeProcesses.Count,
                includeProcesses ? _processEngines : null);
        }

        private bool TryGetFormattedArray(out uint itemCount)
        {
            itemCount = 0;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var bufferSize = (uint)_bufferCapacity;
                var status = PdhGetFormattedCounterArray(
                    _counter,
                    PdhFormatDouble,
                    ref bufferSize,
                    ref itemCount,
                    _buffer);
                if (status == 0) return itemCount == 0 || _buffer != 0;
                if (status != PdhMoreData
                    || bufferSize == 0
                    || bufferSize > MaximumBufferBytes
                    || !EnsureBuffer(bufferSize))
                {
                    return false;
                }
            }
            return false;
        }

        private bool EnsureBuffer(uint requiredBytes)
        {
            if (requiredBytes <= _bufferCapacity) return true;
            try
            {
                _buffer = _buffer == 0
                    ? Marshal.AllocHGlobal(checked((int)requiredBytes))
                    : Marshal.ReAllocHGlobal(_buffer, checked((nint)requiredBytes));
                _bufferCapacity = checked((int)requiredBytes);
                return _buffer != 0;
            }
            catch (OutOfMemoryException)
            {
                return false;
            }
        }

        private static unsafe ReadOnlySpan<char> NullTerminatedSpan(char* value)
        {
            const int maximumLength = 32 * 1024;
            var length = 0;
            while (length < maximumLength && value[length] != '\0') length++;
            return new ReadOnlySpan<char>(value, length);
        }

        public void Dispose()
        {
            if (_buffer != 0)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = 0;
                _bufferCapacity = 0;
            }
            if (_query == 0) return;
            _ = PdhCloseQuery(_query);
            _query = 0;
            _counter = 0;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
        private static extern uint PdhAddEnglishCounter(
            nint query,
            string counterPath,
            nint userData,
            out nint counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(nint query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
        private static extern uint PdhGetFormattedCounterArray(
            nint counter,
            uint format,
            ref uint bufferSize,
            ref uint itemCount,
            nint itemBuffer);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(nint query);

        private readonly record struct EngineTotal(double Percent, GpuEngineKind Kind);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct PdhFormattedCounterValue
        {
            public readonly uint Status;
            public readonly double DoubleValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct PdhFormattedCounterValueItem
        {
            public readonly nint Name;
            public readonly PdhFormattedCounterValue Value;
        }
    }
}
