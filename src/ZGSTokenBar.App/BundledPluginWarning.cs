using ZGSTokenBar.Core;
using ZGSTokenBar.Host;

namespace ZGSTokenBar.App;

/// <summary>
/// Decides whether a bundled-plugin install failure deserves the startup balloon. Benign cases
/// stay quiet: the provider is switched off, or a valid installed copy of the same plugin is
/// still loaded (a rebuilt dev package with an unchanged version no longer matches the copy
/// installed earlier, which used to warn on every launch). A genuine failure is shown once and
/// the dismissal is persisted until the set of failures changes.
/// </summary>
internal static class BundledPluginWarning
{
    private const string MarkerName = ".bundled-install-warned";

    internal static bool ShouldWarn(string dataRoot, IReadOnlyList<InstalledPluginStatus> bundled, AppSettings? settings)
    {
        var marker = Path.Combine(dataRoot, "plugins", MarkerName);
        var failures = bundled.Where(status => !status.Valid).ToArray();
        if (failures.Length == 0)
        {
            TryDelete(marker);
            return false;
        }

        HashSet<string> loadable;
        try
        {
            loadable = new PluginPackageManager(dataRoot).InspectInstalled()
                .Where(status => status.Valid)
                .Select(status => status.PluginId)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PluginTrustException)
        {
            loadable = [];
        }

        var genuine = failures
            .Where(status => !loadable.Contains(status.PluginId))
            .Where(status => settings is null || settings.IsPluginEnabled(status.PluginId, true))
            .Select(status => $"{status.PluginId}@{status.Version}:{status.Error}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (genuine.Length == 0) return false;

        var key = string.Join('\n', genuine);
        try
        {
            if (File.Exists(marker) && string.Equals(File.ReadAllText(marker), key, StringComparison.Ordinal)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without a writable marker, still warn: it is a single non-modal balloon.
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
