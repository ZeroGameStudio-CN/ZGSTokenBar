using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>The registered interactive homes are independent of a launcher's historical process IDs.</summary>
internal static class InteractiveCodexHomes
{
    internal static string RegistryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZGS", "codex-home-bridge", "instances.json");

    internal static IReadOnlyList<string> Read(string? registryPath)
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
            foreach (var instance in instances.EnumerateArray())
            {
                if (instance.ValueKind != JsonValueKind.Object) continue;
                var home = instance.StringProperty("codexHome");
                if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home)) continue;
                var full = Path.GetFullPath(home);
                if (Directory.Exists(full)) homes.Add(full);
            }
            return homes.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return []; }
    }
}
