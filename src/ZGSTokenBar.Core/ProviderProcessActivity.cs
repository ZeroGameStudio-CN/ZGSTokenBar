using System.Diagnostics;

namespace ZGSTokenBar.Core;

public static class ProviderProcessActivity
{
    private static readonly IReadOnlyDictionary<ProviderKind, string[]> ProcessNames =
        new Dictionary<ProviderKind, string[]>
        {
            [ProviderKind.Claude] = ["claude", "claude-code", "claude_desktop"],
            [ProviderKind.Codex] = ["chatgpt", "codex", "codex-cli"],
        };

    private static readonly HashSet<string> WatchedNames =
        ProcessNames.Values.SelectMany(names => names).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> GrokNames = new(["grok"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Null means the observation failed. The observer's own usage CLI is not user activity.</summary>
    public static bool? DetectGrokActivity()
    {
        try
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return CockpitCodexInstanceActivity.TryReadRunningProcessNames(GrokNames, found, Environment.ProcessId)
                ? found.Count != 0 : null;
        }
        catch { return null; }
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
