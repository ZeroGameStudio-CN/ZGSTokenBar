using System.Reflection;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal static class BundledPluginInstaller
{
    private const string ResourcePrefix = "ZGSTokenBar.App.BundledPlugins.";

    public static IReadOnlyList<InstalledPluginStatus> EnsureInstalled(string dataRoot)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(".zgsplugin", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0) return [];

        var manager = new PluginPackageManager(dataRoot);
        var installed = new List<InstalledPluginStatus>(resources.Length);
        foreach (var resource in resources)
        {
            string? temporaryPath = null;
            try
            {
                using var input = assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidDataException("Bundled plugin resource is unavailable.");
                temporaryPath = Path.Combine(
                    Path.GetTempPath(),
                    $"zgstokenbar-bundled-plugin-{Guid.NewGuid():N}.zgsplugin");
                var expected = ExtractPackage(input, temporaryPath);
                var status = manager.EnsureInstalled(temporaryPath, expected);
                manager.ClearBundledInstallFailure(resource);
                installed.Add(status);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or PluginTrustException)
            {
                var identity = temporaryPath is null
                    ? null
                    : ReadIdentity(temporaryPath);
                var pluginId = identity?.PluginId ?? "zgstokenbar.bundled.install";
                var version = identity?.Version ?? "0.0.0";
                try
                {
                    installed.Add(manager.RecordBundledInstallFailure(
                        resource,
                        pluginId,
                        version));
                }
                catch (Exception markerException) when (
                    markerException is IOException or UnauthorizedAccessException)
                {
                    installed.Add(new InstalledPluginStatus(
                        pluginId,
                        version,
                        string.Empty,
                        false,
                        "trust_failed"));
                }
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        return installed;
    }

    private static BundleIdentity? ReadIdentity(string packagePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var entry = archive.Entries.SingleOrDefault(candidate => string.Equals(
                candidate.FullName,
                "plugin-manifest.v1.json",
                StringComparison.Ordinal));
            if (entry is null || entry.Length is <= 0 or > ZgsHostApi.MaximumFrameBytes)
            {
                return null;
            }
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize(
                stream,
                PluginSdkJsonContext.Default.PluginManifest);
            return manifest is not null
                && PluginValidation.IsStableId(manifest.Id)
                && Version.TryParse(manifest.Version, out _)
                    ? new(manifest.Id, manifest.Version)
                    : null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or InvalidOperationException)
        {
            return null;
        }
    }

    internal static string ExtractPackage(Stream input, string destination)
    {
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            copied += read;
            if (copied > PluginPackageManager.MaximumArchiveBytes)
            {
                throw new InvalidDataException("Bundled plugin exceeds the archive limit.");
            }
            digest.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private sealed record BundleIdentity(string PluginId, string Version);
}
