using System.Security;
using System.Text.Json;

namespace ZGSTokenBar.Core;

internal sealed record InteractiveCodexHome(string Home, string Label, int Number);

/// <summary>The registered interactive homes are independent of a launcher's historical process IDs.</summary>
internal static class InteractiveCodexHomes
{
    internal static readonly TimeSpan RecentSessionWindow = TimeSpan.FromMinutes(30);

    internal static string RegistryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZGS", "codex-home-bridge", "instances.json");

    internal static IReadOnlyList<string> Read(string? registryPath) => Entries(registryPath).Select(entry => entry.Home).ToArray();

    internal static IReadOnlySet<string> ActiveHomes(
        IReadOnlyList<string> registeredHomes,
        DateTimeOffset now,
        IReadOnlyList<string>? processCommandLines = null)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (registeredHomes.Count == 0) return active;

        var commandLines = processCommandLines ?? CockpitCodexInstanceActivity.ReadActiveProcessCommandLines();
        foreach (var rawHome in registeredHomes)
        {
            if (NormalizeHome(rawHome) is not { } home) continue;

            var matchedProcess = false;
            foreach (var commandLine in commandLines)
            {
                if (ContainsPathBoundary(commandLine, home))
                {
                    matchedProcess = true;
                    break;
                }
            }

            if (matchedProcess || HasRecentSessionActivity(home, now, RecentSessionWindow))
            {
                active.Add(home);
            }
        }

        return active;
    }

    internal static bool ContainsPathBoundary(string commandLine, string normalizedHome)
    {
        if (string.IsNullOrEmpty(commandLine) || string.IsNullOrEmpty(normalizedHome)) return false;
        var index = 0;
        while (index <= commandLine.Length - normalizedHome.Length)
        {
            var found = commandLine.IndexOf(normalizedHome, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;

            var validBefore = found == 0 || commandLine[found - 1] is var before && (char.IsWhiteSpace(before) || before is '"' or '\'' or '=');
            var afterIndex = found + normalizedHome.Length;
            var validAfter = afterIndex == commandLine.Length || commandLine[afterIndex] is var after && (char.IsWhiteSpace(after) || after is '"' or '\'' or '\\' or '/' or ';' or ',');
            if (validBefore && validAfter) return true;

            index = found + 1;
        }

        return false;
    }

    internal static bool HasRecentSessionActivity(string home, DateTimeOffset now, TimeSpan window)
    {
        try
        {
            var sessionsPath = Path.Combine(home, "sessions");
            var sessionsDir = new DirectoryInfo(sessionsPath);
            if (!sessionsDir.Exists) return false;

            var cutoffUtc = now.UtcDateTime - window;
            if (HasFreshRolloutFile(sessionsDir, cutoffUtc)) return true;

            foreach (var yearDir in sessionsDir.EnumerateDirectories().OrderByDescending(dir => dir.Name, StringComparer.Ordinal).Take(2))
            {
                if (HasFreshRolloutFile(yearDir, cutoffUtc)) return true;
                foreach (var monthDir in yearDir.EnumerateDirectories().OrderByDescending(dir => dir.Name, StringComparer.Ordinal).Take(2))
                {
                    if (HasFreshRolloutFile(monthDir, cutoffUtc)) return true;
                    foreach (var dayDir in monthDir.EnumerateDirectories().OrderByDescending(dir => dir.Name, StringComparer.Ordinal).Take(2))
                    {
                        if (HasFreshRolloutFile(dayDir, cutoffUtc)) return true;
                    }
                }
            }

            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasFreshRolloutFile(DirectoryInfo directory, DateTime cutoffUtc)
    {
        foreach (var file in directory.EnumerateFiles("*.jsonl"))
        {
            if (file.LastWriteTimeUtc >= cutoffUtc) return true;
        }

        return false;
    }

    private static string? NormalizeHome(string? home)
    {
        if (string.IsNullOrWhiteSpace(home)) return null;
        try
        {
            return Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<InteractiveCodexHome> Entries(string? registryPath)
    {
        if (string.IsNullOrWhiteSpace(registryPath)) return [];
        try
        {
            var file = new FileInfo(registryPath);
            if (!file.Exists || file.Length is <= 0 or > 1_048_576) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));
            if (!document.RootElement.TryGetProperty("instances", out var instances)
                || instances.ValueKind != JsonValueKind.Array || instances.GetArrayLength() > 64) return [];
            var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<InteractiveCodexHome>();
            foreach (var instance in instances.EnumerateArray())
            {
                if (instance.ValueKind != JsonValueKind.Object) continue;
                var home = instance.StringProperty("codexHome");
                if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home)) continue;
                var full = Path.GetFullPath(home);
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Directory.Exists(full) && homes.Add(full))
                    result.Add(new(full, instance.StringProperty("label") ?? "", result.Count + 1));
            }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return []; }
    }

    internal static IReadOnlyDictionary<string, string> AccountIds(string? registryPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries(registryPath))
        {
            try
            {
                var file = new FileInfo(Path.Combine(entry.Home, "auth.json"));
                if (!file.Exists || file.Length is <= 0 or > 1_048_576) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));
                var root = document.RootElement;
                var id = root.ObjectProperty("tokens")?.StringProperty("account_id") ?? root.StringProperty("account_id");
                if (!string.IsNullOrWhiteSpace(id)) result[entry.Home] = id;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result;
    }
}
