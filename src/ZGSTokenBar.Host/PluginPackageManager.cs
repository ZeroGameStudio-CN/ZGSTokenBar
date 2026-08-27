using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Host;

public sealed record InstalledPluginStatus(
    string PluginId,
    string Version,
    string Path,
    bool Valid,
    string? Error = null);

public sealed class PluginTrustException : Exception
{
    public PluginTrustException(string safeMessage) : base(safeMessage)
    {
        SafeMessage = safeMessage;
    }

    public string SafeMessage { get; }
}

public sealed class PluginPackageManager
{
    public const long MaximumArchiveBytes = 64L * 1024 * 1024;
    public const long MaximumExpandedBytes = 128L * 1024 * 1024;
    public const int MaximumFiles = 256;
    private const string ManifestName = "plugin-manifest.v1.json";
    private readonly string _pluginsRoot;
    private readonly string _lockPath;

    public PluginPackageManager(string dataRoot)
    {
        var root = Path.GetFullPath(dataRoot);
        _pluginsRoot = Path.Combine(root, "plugins");
        _lockPath = Path.Combine(_pluginsRoot, ".install.lock");
    }

    public InstalledPluginStatus Install(string packagePath, string expectedSha256)
    {
        var package = Path.GetFullPath(packagePath);
        if (!File.Exists(package)) throw new PluginTrustException("Plugin package was not found.");
        var packageInfo = new FileInfo(package);
        if (packageInfo.Length is <= 0 or > MaximumArchiveBytes)
        {
            throw new PluginTrustException("Plugin package exceeds the archive limit.");
        }
        if (!ValidDigest(expectedSha256)
            || !FixedEquals(FileDigest(package), expectedSha256))
        {
            throw new PluginTrustException("Plugin package SHA-256 does not match.");
        }

        Directory.CreateDirectory(_pluginsRoot);
        using var installLock = new FileStream(
            _lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var archive = ZipFile.OpenRead(package);
        if (archive.Entries.Count is <= 1 or > MaximumFiles + 1)
        {
            throw new PluginTrustException("Plugin package has an invalid file count.");
        }
        var entries = ValidateArchiveEntries(archive);
        if (!entries.TryGetValue(ManifestName, out var manifestEntry))
        {
            throw new PluginTrustException("Plugin manifest is missing.");
        }
        if (manifestEntry.Length is <= 0 or > ZgsHostApi.MaximumFrameBytes)
        {
            throw new PluginTrustException("Plugin manifest is too large.");
        }

        PluginManifest manifest;
        using (var manifestStream = manifestEntry.Open())
        {
            try
            {
                manifest = JsonSerializer.Deserialize(
                        manifestStream,
                        PluginSdkJsonContext.Default.PluginManifest)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new PluginTrustException("Plugin manifest is invalid.");
            }
        }
        ValidateProcessManifest(manifest);

        var declared = new Dictionary<string, PluginPackageFile>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var file in manifest.Files)
        {
            var path = NormalizePackagePath(file.Path);
            if (!declared.TryAdd(path, file))
            {
                throw new PluginTrustException("Plugin manifest contains a duplicate file.");
            }
            if (file.Bytes < 0 || !ValidDigest(file.Sha256))
            {
                throw new PluginTrustException("Plugin manifest contains invalid file metadata.");
            }
            if (file.Bytes > MaximumExpandedBytes - expandedBytes)
            {
                throw new PluginTrustException("Plugin package exceeds the expanded size limit.");
            }
            expandedBytes += file.Bytes;
        }
        if (declared.Count is <= 0 or > MaximumFiles)
        {
            throw new PluginTrustException("Plugin manifest has an invalid file count.");
        }
        if (!declared.ContainsKey(NormalizePackagePath(manifest.Entrypoint!)))
        {
            throw new PluginTrustException("Plugin entrypoint is not declared.");
        }
        var archiveFiles = entries.Keys
            .Where(path => !string.Equals(path, ManifestName, StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!archiveFiles.SetEquals(declared.Keys))
        {
            throw new PluginTrustException("Plugin package contains undeclared or missing files.");
        }

        var target = SafeTarget(manifest.Id, manifest.Version);
        if (Directory.Exists(target))
        {
            throw new PluginTrustException("This plugin version is already installed.");
        }
        var staging = Path.Combine(_pluginsRoot, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var pair in declared)
            {
                var entry = entries[pair.Key];
                var metadata = pair.Value;
                if (entry.Length != metadata.Bytes)
                {
                    throw new PluginTrustException("Plugin file length does not match the manifest.");
                }
                var destination = SafeExtractPath(staging, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long copied = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copied += read;
                    if (copied > metadata.Bytes)
                    {
                        throw new PluginTrustException("Plugin file exceeds its declared length.");
                    }
                    hasher.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }
                if (copied != metadata.Bytes
                    || !FixedEquals(Convert.ToHexString(hasher.GetHashAndReset()), metadata.Sha256))
                {
                    throw new PluginTrustException("Plugin file digest does not match the manifest.");
                }
            }
            ValidatePackageAssets(staging, manifest);
            File.WriteAllBytes(
                Path.Combine(staging, ManifestName),
                JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    PluginSdkJsonContext.Default.PluginManifest));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        return new(manifest.Id, manifest.Version, target, true);
    }

    public bool Remove(string pluginId)
    {
        if (!PluginValidation.IsStableId(pluginId))
        {
            throw new PluginTrustException("Plugin ID is invalid.");
        }
        Directory.CreateDirectory(_pluginsRoot);
        using var installLock = new FileStream(
            _lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var target = Path.GetFullPath(Path.Combine(_pluginsRoot, pluginId));
        EnsureBelowRoot(target);
        if (!Directory.Exists(target)) return false;
        Directory.Delete(target, recursive: true);
        return true;
    }

    public IReadOnlyList<InstalledPluginStatus> InspectInstalled()
    {
        if (!Directory.Exists(_pluginsRoot)) return [];
        var results = new List<InstalledPluginStatus>();
        foreach (var pluginDirectory in Directory.EnumerateDirectories(_pluginsRoot)
                     .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var versionDirectory in Directory.EnumerateDirectories(pluginDirectory)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(Inspect(versionDirectory));
            }
        }
        return results;
    }

    public IReadOnlyList<IZgsPlugin> LoadProcessPlugins(
        IPluginCredentialBroker? credentialBroker = null)
    {
        var plugins = new List<IZgsPlugin>();
        var active = InspectInstalled()
            .Where(status => status.Valid)
            .GroupBy(status => status.PluginId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(status => Version.Parse(status.Version))
                .ThenByDescending(status => status.Path, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(status => status.PluginId, StringComparer.Ordinal);
        foreach (var status in active)
        {
            try
            {
                var manifest = JsonSerializer.Deserialize(
                        File.ReadAllBytes(Path.Combine(status.Path, ManifestName)),
                        PluginSdkJsonContext.Default.PluginManifest)
                    ?? throw new JsonException();
                plugins.Add(new ProcessPluginProxy(manifest, status.Path, credentialBroker));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
        return plugins;
    }

    private InstalledPluginStatus Inspect(string directory)
    {
        var pluginId = Path.GetFileName(Path.GetDirectoryName(directory)) ?? "unknown";
        var version = Path.GetFileName(directory);
        try
        {
            EnsureBelowRoot(directory);
            var manifestPath = Path.Combine(directory, ManifestName);
            var manifest = JsonSerializer.Deserialize(
                    File.ReadAllBytes(manifestPath),
                    PluginSdkJsonContext.Default.PluginManifest)
                ?? throw new JsonException();
            ValidateProcessManifest(manifest);
            if (!string.Equals(manifest.Id, pluginId, StringComparison.Ordinal)
                || !string.Equals(manifest.Version, version, StringComparison.Ordinal))
            {
                throw new PluginTrustException("Installed plugin path does not match its manifest.");
            }
            var declared = manifest.Files
                .ToDictionary(
                    file => NormalizePackagePath(file.Path),
                    StringComparer.OrdinalIgnoreCase);
            var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
                .Where(path => !string.Equals(path, ManifestName, StringComparison.Ordinal))
                .ToArray();
            if (!actual.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(declared.Keys))
            {
                throw new PluginTrustException("Installed plugin file set has drifted.");
            }
            foreach (var path in actual)
            {
                var metadata = declared[path];
                var file = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
                if (new FileInfo(file).Length != metadata.Bytes
                    || !FixedEquals(FileDigest(file), metadata.Sha256))
                {
                    throw new PluginTrustException("Installed plugin digest has drifted.");
                }
            }
            return new(pluginId, version, directory, true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or PluginTrustException)
        {
            return new(pluginId, version, directory, false, "trust_failed");
        }
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateArchiveEntries(ZipArchive archive)
    {
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new PluginTrustException("Plugin package cannot contain directories.");
            }
            var path = NormalizePackagePath(entry.FullName);
            if (!result.TryAdd(path, entry))
            {
                throw new PluginTrustException("Plugin package contains duplicate paths.");
            }
        }
        return result;
    }

    private static void ValidateProcessManifest(PluginManifest manifest)
    {
        var errors = PluginValidation.ValidateManifest(manifest);
        if (errors.Count > 0
            || manifest.Runtime != PluginRuntime.Process
            || manifest.Required
            || string.IsNullOrWhiteSpace(manifest.Entrypoint))
        {
            throw new PluginTrustException("Process plugin manifest is incompatible.");
        }
        if (manifest.HandshakeTimeoutSeconds is > 5
            || manifest.CallTimeoutSeconds is > 15
            || manifest.DisposeTimeoutSeconds is > 2
            || manifest.HandshakeTimeoutSeconds is <= 0
            || manifest.CallTimeoutSeconds is <= 0
            || manifest.DisposeTimeoutSeconds is <= 0)
        {
            throw new PluginTrustException("Plugin timeout exceeds the host policy.");
        }
        if (manifest.Icon is not null && !manifest.Files.Any(file =>
                string.Equals(
                    NormalizePackagePath(file.Path),
                    NormalizePackagePath(manifest.Icon),
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new PluginTrustException("Plugin icon is not declared.");
        }
        foreach (var locale in manifest.Locales)
        {
            if (!manifest.Files.Any(file =>
                    string.Equals(
                        NormalizePackagePath(file.Path),
                        NormalizePackagePath(locale),
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new PluginTrustException("Plugin locale is not declared.");
            }
        }
    }

    private static void ValidatePackageAssets(string staging, PluginManifest manifest)
    {
        if (manifest.Icon is { } icon)
        {
            var path = SafeExtractPath(staging, NormalizePackagePath(icon));
            var info = new FileInfo(path);
            if (!string.Equals(info.Extension, ".png", StringComparison.OrdinalIgnoreCase)
                || info.Length is <= 24 or > 64 * 1024)
            {
                throw new PluginTrustException("Plugin icon is invalid.");
            }
            using var stream = File.OpenRead(path);
            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) != header.Length
                || !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || !header[12..16].SequenceEqual("IHDR"u8))
            {
                throw new PluginTrustException("Plugin icon is not a PNG.");
            }
            var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
            if (width is < 16 or > 256 || height is < 16 or > 256)
            {
                throw new PluginTrustException("Plugin icon dimensions are outside the allowed range.");
            }
        }

        foreach (var locale in manifest.Locales)
        {
            var normalized = NormalizePackagePath(locale);
            if (normalized is not ("locales/en.json" or "locales/zh-CN.json"))
            {
                throw new PluginTrustException("Plugin locale is not supported.");
            }
            var path = SafeExtractPath(staging, normalized);
            if (new FileInfo(path).Length is <= 0 or > 32 * 1024)
            {
                throw new PluginTrustException("Plugin locale exceeds the size limit.");
            }
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (document.RootElement.ValueKind is not JsonValueKind.Object)
                {
                    throw new PluginTrustException("Plugin locale must be a flat object.");
                }
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!keys.Add(property.Name)
                        || !PluginValidation.IsStableId(property.Name)
                        || !property.Name.StartsWith(manifest.Id + ".", StringComparison.Ordinal)
                        || property.Value.ValueKind is not JsonValueKind.String
                        || property.Value.GetString() is { Length: > 512 })
                    {
                        throw new PluginTrustException("Plugin locale contains an invalid entry.");
                    }
                }
            }
            catch (JsonException)
            {
                throw new PluginTrustException("Plugin locale JSON is invalid.");
            }
        }
    }

    private string SafeTarget(string pluginId, string version)
    {
        if (!PluginValidation.IsStableId(pluginId)
            || !Version.TryParse(version, out _))
        {
            throw new PluginTrustException("Plugin identity is invalid.");
        }
        var target = Path.GetFullPath(Path.Combine(_pluginsRoot, pluginId, version));
        EnsureBelowRoot(target);
        return target;
    }

    private static string SafeExtractPath(string root, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginTrustException("Plugin file escapes the staging root.");
        }
        return target;
    }

    private void EnsureBelowRoot(string target)
    {
        var prefix = Path.GetFullPath(_pluginsRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(target).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginTrustException("Plugin target escapes the install root.");
        }
    }

    private static string NormalizePackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains('\\')
            || path.Contains(':')
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.EndsWith("/", StringComparison.Ordinal))
        {
            throw new PluginTrustException("Plugin path is invalid.");
        }
        var segments = path.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new PluginTrustException("Plugin path is invalid.");
        }
        return string.Join('/', segments);
    }

    private static bool ValidDigest(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string FileDigest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool FixedEquals(string left, string right)
    {
        if (!ValidDigest(left) || !ValidDigest(right)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }
}
