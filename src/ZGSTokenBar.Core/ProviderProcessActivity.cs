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

    public static IReadOnlySet<ProviderKind> DetectActiveProviders()
    {
        try
        {
            var runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
