using System.Text.Json;

namespace ZGSTokenBar.Core;

internal sealed record InteractiveCodexHome(string Home, string Label, int Number);

/// <summary>The registered interactive homes are independent of a launcher's historical process IDs.</summary>
internal static class InteractiveCodexHomes
{
    internal static string RegistryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZGS", "codex-home-bridge", "instances.json");

    internal static IReadOnlyList<string> Read(string? registryPath) => Entries(registryPath).Select(entry => entry.Home).ToArray();

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
