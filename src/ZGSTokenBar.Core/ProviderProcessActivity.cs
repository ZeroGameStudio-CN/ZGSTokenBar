using System.Diagnostics;

namespace ZGSTokenBar.Core;

public static class ProviderProcessActivity
{
    private static readonly IReadOnlyDictionary<ProviderKind, string[]> ProcessNames =
        new Dictionary<ProviderKind, string[]>
        {
            [ProviderKind.Claude] = ProviderCatalog.Find(UsageProviderIds.Claude)!.ActivityProcessNames.ToArray(),
            [ProviderKind.Codex] = ProviderCatalog.Find(UsageProviderIds.Codex)!.ActivityProcessNames.ToArray(),
        };

    private static readonly HashSet<string> WatchedNames =
        ProcessNames.Values.SelectMany(names => names).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A single read-only process snapshot serves every provider. Unknown is not idle.</summary>
    public static IReadOnlyDictionary<string, bool?> DetectUsageProviderActivity()
    {
        try
        {
            return FromProcessSnapshot(CockpitCodexInstanceActivity.TryReadProcessEntries(out var processes) ? processes : null,
                Environment.ProcessId);
        }
        catch { return FromProcessSnapshot(null, Environment.ProcessId); }
    }

    internal static IReadOnlyDictionary<string, bool?> FromProcessSnapshot(
        IReadOnlyList<CockpitCodexInstanceActivity.ProcessEntry>? processes, int observerId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (processes is not null)
        {
            var byId = processes.GroupBy(process => process.ProcessId).ToDictionary(group => group.Key, group => group.First());
            foreach (var process in processes)
                if (!CockpitCodexInstanceActivity.IsDescendantOf(process.ProcessId, observerId, byId)) names.Add(process.ProcessName);
        }
        return ProviderCatalog.All.ToDictionary(provider => provider.Id,
            provider => processes is null || provider.ActivityProcessNames.Count == 0 ? (bool?)null
                : provider.ActivityProcessNames.Any(names.Contains), StringComparer.Ordinal);
    }

    public static IReadOnlySet<ProviderKind> DetectActiveProviders()
    {
        try
        {
            var runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (CockpitCodexInstanceActivity.TryReadRunningProcessNames(WatchedNames, runningNames))
                return DetectFromProcessNames(runningNames);
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        runningNames.Add(process.ProcessName);
                    }
                    catch
                    {
                        // A process can exit between enumeration and name lookup.
                    }
                }
            }

            return ProcessNames
                .Where(pair => pair.Value.Any(runningNames.Contains))
                .Select(pair => pair.Key)
                .ToHashSet();
        }
        catch
        {
            // A process probe failure must not hide a user's configured providers.
            return Enum.GetValues<ProviderKind>().ToHashSet();
        }
    }

    internal static IReadOnlySet<ProviderKind> DetectFromProcessNames(IEnumerable<string> processNames)
    {
        var runningNames = processNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ProcessNames
            .Where(pair => pair.Value.Any(runningNames.Contains))
            .Select(pair => pair.Key)
            .ToHashSet();
    }
}
