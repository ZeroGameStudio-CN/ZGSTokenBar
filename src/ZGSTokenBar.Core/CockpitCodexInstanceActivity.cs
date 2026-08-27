using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ZGSTokenBar.Core;

internal static class CockpitCodexInstanceActivity
{
    private const long MaximumJsonBytes = 1024 * 1024;
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);
    private static readonly HashSet<string> ActiveProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ChatGPT",
            "codex",
            "codex-cli",
        };

    internal static IReadOnlySet<string>? ReadActiveAccountIds(
        string home,
        Func<int, bool>? processIsActive = null,
        Func<IReadOnlyList<ProcessEntry>>? processSnapshot = null)
    {
        try
        {
            var path = Path.Combine(home, "codex_instances.json");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumJsonBytes) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var bindings = new List<(string? AccountId, int? ProcessId, bool IsDefault)>();
            if (root.TryGetProperty("instances", out var instances)
                && instances.ValueKind == JsonValueKind.Array)
            {
                foreach (var instance in instances.EnumerateArray())
                {
                    bindings.Add((
                        instance.StringProperty("bindAccountId"),
                        ProcessId(instance.StringProperty("lastPid"), instance),
                        false));
                }
            }

            if (root.TryGetProperty("defaultSettings", out var defaults)
                && defaults.ValueKind == JsonValueKind.Object)
            {
                bindings.Add((
                    defaults.StringProperty("bindAccountId"),
                    ProcessId(defaults.StringProperty("lastPid"), defaults),
                    true));
            }

            return SelectActiveAccountIds(
                bindings,
                processIsActive ?? IsActiveProcess,
                processSnapshot ?? ReadProcessSnapshot);
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<CockpitCodexRolloutSource> ReadRolloutSources() =>
        ReadRolloutSources(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".antigravity_cockpit"));

    internal static IReadOnlyList<CockpitCodexRolloutSource> ReadRolloutSources(string home)
    {
        try
        {
            var path = Path.Combine(home, "codex_instances.json");
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumJsonBytes) return [];

            var instancesRoot = Path.GetFullPath(Path.Combine(home, "instances", "codex"));
            var rootPrefix = instancesRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("instances", out var instances)
                || instances.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<CockpitCodexRolloutSource>();
            foreach (var instance in instances.EnumerateArray())
            {
                var accountId = instance.StringProperty("bindAccountId");
                var configuredDirectory = instance.StringProperty("userDataDir");
                if (string.IsNullOrWhiteSpace(accountId)
                    || string.IsNullOrWhiteSpace(configuredDirectory))
                {
                    continue;
                }

                var directory = Path.GetFullPath(configuredDirectory);
                if (!directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(directory)
                    || ContainsReparsePoint(instancesRoot, directory))
                {
                    continue;
                }

                result.Add(new CockpitCodexRolloutSource(
                    accountId,
                    CodexQuotaService.StableCardKey($"cockpit:{accountId}"),
                    directory));
            }

            return result
                .GroupBy(source => source.CardKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(source => source.CardKey, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool ContainsReparsePoint(string root, string directory)
    {
        var current = new DirectoryInfo(directory);
        while (true)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (string.Equals(current.FullName, root, StringComparison.OrdinalIgnoreCase)) return false;
            current = current.Parent;
            if (current is null) return true;
        }
    }

    internal static IReadOnlySet<string> SelectActiveAccountIds(
        IEnumerable<(string? AccountId, int? ProcessId)> bindings,
        Func<int, bool> processIsActive) => SelectActiveAccountIds(
            bindings.Select(binding => (binding.AccountId, binding.ProcessId, false)),
            processIsActive,
            () => []);

    internal static IReadOnlySet<string> SelectActiveAccountIds(
        IEnumerable<(string? AccountId, int? ProcessId, bool IsDefault)> bindings,
        Func<int, bool> processIsActive,
        Func<IReadOnlyList<ProcessEntry>> processSnapshot)
    {
        var candidates = bindings
            .Where(binding => !string.IsNullOrWhiteSpace(binding.AccountId))
            .ToArray();
        var activeProcessIds = candidates
            .Where(binding => binding.ProcessId is > 0)
            .Select(binding => binding.ProcessId!.Value)
            .Distinct()
            .Where(processIsActive)
            .ToHashSet();
        var activeAccountIds = candidates
            .Where(binding => binding.ProcessId is > 0
                && activeProcessIds.Contains(binding.ProcessId.Value))
            .Select(binding => binding.AccountId!)
            .ToHashSet(StringComparer.Ordinal);

        var defaultBinding = candidates.FirstOrDefault(binding => binding.IsDefault);
        if (string.IsNullOrWhiteSpace(defaultBinding.AccountId)
            || activeAccountIds.Contains(defaultBinding.AccountId))
        {
            return activeAccountIds;
        }

        var processes = processSnapshot();
        var activeProcesses = processes
            .Where(process => ActiveProcessNames.Contains(process.ProcessName))
            .ToDictionary(process => process.ProcessId);
        var activeRootCount = activeProcesses.Values.Count(process =>
            !activeProcesses.ContainsKey(process.ParentProcessId));
        var activeManagedProcessCount = candidates
            .Where(binding => !binding.IsDefault
                && binding.ProcessId is > 0
                && activeProcessIds.Contains(binding.ProcessId.Value))
            .Select(binding => binding.ProcessId!.Value)
            .Distinct()
            .Count();
        if (activeRootCount > activeManagedProcessCount)
        {
            activeAccountIds.Add(defaultBinding.AccountId);
        }

        return activeAccountIds;
    }

    private static int? ProcessId(string? value, JsonElement item)
    {
        if (item.TryGetProperty("lastPid", out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return int.TryParse(value, out var fallback) ? fallback : null;
    }

    private static bool IsActiveProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return ActiveProcessNames.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<ProcessEntry> ReadProcessSnapshot()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == 0 || snapshot == InvalidHandleValue) return [];

        try
        {
            var result = new List<ProcessEntry>();
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
            };
            if (!Process32First(snapshot, ref entry)) return result;

            do
            {
                var processName = Path.GetFileNameWithoutExtension(entry.ExecutableFile);
                if (ActiveProcessNames.Contains(processName))
                {
                    result.Add(new ProcessEntry(
                        (int)entry.ProcessId,
                        (int)entry.ParentProcessId,
                        processName));
                }
            }
            while (Process32Next(snapshot, ref entry));

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    internal readonly record struct ProcessEntry(
        int ProcessId,
        int ParentProcessId,
        string ProcessName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint UsageCount;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

internal sealed record CockpitCodexRolloutSource(
    string AccountId,
    string CardKey,
    string CodexHome);
