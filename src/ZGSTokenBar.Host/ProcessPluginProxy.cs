using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Host;

public sealed class ProcessPluginProxy : IZgsPlugin, IDataSource, ICommandContributor
{
    private readonly string _installDirectory;
    private readonly IPluginCredentialBroker? _credentialBroker;
    private readonly SemaphoreSlim _callGate = new(1, 1);
    private readonly object _diagnosticSync = new();
    private Process? _process;
    private WindowsJobObject? _job;
    private Task? _stderrTask;
    private string _diagnostic = string.Empty;
    private bool _started;

    public ProcessPluginProxy(
        PluginManifest manifest,
        string installDirectory,
        IPluginCredentialBroker? credentialBroker = null)
    {
        Manifest = manifest;
        _installDirectory = Path.GetFullPath(installDirectory);
        _credentialBroker = credentialBroker;
    }

    public PluginManifest Manifest { get; }
    public IReadOnlyList<CommandDescriptor> Commands { get; private set; } = [];
    public IReadOnlyList<SettingsContribution> Settings { get; private set; } = [];
    public string Diagnostic
    {
        get { lock (_diagnosticSync) return _diagnostic; }
    }
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    public byte[]? ReadIconPng()
    {
        if (Manifest.Icon is null) return null;
        return ReadDeclaredFile(Manifest.Icon, 64 * 1024);
    }

    public IReadOnlyDictionary<string, string> ReadLocalization(string locale)
    {
        var requested = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "locales/zh-CN.json"
            : "locales/en.json";
        var relative = Manifest.Locales.FirstOrDefault(path =>
                string.Equals(NormalizePath(path), requested, StringComparison.OrdinalIgnoreCase))
            ?? Manifest.Locales.FirstOrDefault(path =>
                string.Equals(NormalizePath(path), "locales/en.json", StringComparison.OrdinalIgnoreCase));
        if (relative is null) return new Dictionary<string, string>(StringComparer.Ordinal);

        var bytes = ReadDeclaredFile(relative, 32 * 1024);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString() ?? string.Empty,
            StringComparer.Ordinal);
    }

    public async ValueTask StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken)
    {
        if (_started) return;
        VerifyInstalledFiles();
        var startInfo = new ProcessStartInfo(ResolveEntrypoint())
        {
            WorkingDirectory = _installDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "PATH" })
        {
            inherited[name] = Environment.GetEnvironmentVariable(name);
        }
        startInfo.Environment.Clear();
        foreach (var pair in inherited)
        {
            if (!string.IsNullOrEmpty(pair.Value)) startInfo.Environment[pair.Key] = pair.Value;
        }
        startInfo.Environment["ZGSTOKENBAR_PLUGIN_ID"] = Manifest.Id;
        startInfo.Environment["ZGSTOKENBAR_PLUGIN_DATA"] = context.DataRoot;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new HostCommandException("trust_failed", "Plugin process could not start.");
        }
        WindowsJobObject job;
        try
        {
            job = WindowsJobObject.Attach(process);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
            process.Dispose();
            throw new HostCommandException(
                "trust_failed",
                "Plugin process isolation could not be established.");
        }
        _process = process;
        _job = job;
        _stderrTask = DrainStderrAsync(process.StandardError, cancellationToken);

        try
        {
            var filesDigest = FilesDigest();
            var handshake = await CallAsync(
                "plugin.handshake",
                Object(
                    ("apiMajor", ZgsHostApi.Major),
                    ("apiMinor", ZgsHostApi.Minor),
                    ("pluginId", Manifest.Id),
                    ("version", Manifest.Version),
                    ("filesDigest", filesDigest)),
                HandshakeTimeout(),
                cancellationToken);
            var accepted = handshake.Deserialize(PluginSdkJsonContext.Default.ProcessHandshakeResult)
                ?? throw new JsonException();
            if (accepted.ApiMajor != ZgsHostApi.Major
                || accepted.ApiMinor > ZgsHostApi.Minor
                || !string.Equals(accepted.PluginId, Manifest.Id, StringComparison.Ordinal)
                || !string.Equals(accepted.Version, Manifest.Version, StringComparison.Ordinal)
                || !string.Equals(accepted.FilesDigest, filesDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new HostCommandException(
                    "trust_failed",
                    "Plugin handshake identity did not match.");
            }
            var descriptionValue = await CallAsync(
                "plugin.describe",
                null,
                CallTimeout(),
                cancellationToken);
            var description = descriptionValue.Deserialize(
                    PluginSdkJsonContext.Default.ProcessPluginDescription)
                ?? throw new JsonException();
            ValidateDescription(description);
            Commands = description.Commands;
            Settings = description.Settings;
            _started = true;
        }
        catch
        {
            await StopProcessAsync();
            throw;
        }
    }

    public async ValueTask<PluginDataSnapshot> RefreshAsync(
        PluginRefreshContext context,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        var value = await CallAsync(
            "plugin.refresh",
            Object(
                ("now", context.Now.ToUniversalTime().ToString("O")),
                ("reason", context.Reason),
                ("previousDataRevision", context.PreviousDataRevision)),
            CallTimeout(),
            cancellationToken);
        return value.Deserialize(PluginSdkJsonContext.Default.PluginDataSnapshot)
            ?? throw new HostCommandException("internal", "Plugin returned an invalid snapshot.");
    }

    public async ValueTask<CommandResult> InvokeAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        var value = await CallAsync(
            "plugin.command",
            JsonSerializer.SerializeToElement(
                invocation,
                PluginSdkJsonContext.Default.CommandInvocation),
            CallTimeout(),
            cancellationToken);
        return value.Deserialize(PluginSdkJsonContext.Default.CommandResult)
            ?? new(false, Error: new("internal", "Plugin returned an invalid command result."));
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_process is null) return;
        try
        {
            await CallAsync("plugin.dispose", null, DisposeTimeout(), cancellationToken);
        }
        catch
        {
        }
        await StopProcessAsync();
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _callGate.Dispose();
    }

    private async ValueTask<JsonElement> CallAsync(
        string method,
        JsonElement? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var process = _process
            ?? throw new HostCommandException("plugin_disabled", "Plugin process is not running.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await _callGate.WaitAsync(deadline.Token);
        var requestMayHaveBeenWritten = false;
        try
        {
            if (process.HasExited)
            {
                throw new HostCommandException("internal", "Plugin process exited.");
            }
            var request = new ApiRequestEnvelope(
                1,
                Guid.NewGuid().ToString("N"),
                method,
                parameters);
            requestMayHaveBeenWritten = true;
            await ProcessFrameProtocol.WriteAsync(
                process.StandardInput.BaseStream,
                request,
                ApiJsonContext.Default.ApiRequestEnvelope,
                deadline.Token);
            ApiResponseEnvelope? response = null;
            for (var brokerRequests = 0; response is null && brokerRequests <= 8; brokerRequests++)
            {
                var frame = await ProcessFrameProtocol.ReadElementAsync(
                    process.StandardOutput.BaseStream,
                    deadline.Token);
                if (frame.ValueKind is JsonValueKind.Object
                    && frame.TryGetProperty("method", out _))
                {
                    var pluginRequest = frame.Deserialize(ApiJsonContext.Default.ApiRequestEnvelope)
                        ?? throw new JsonException();
                    await HandlePluginRequestAsync(
                        process.StandardInput.BaseStream,
                        pluginRequest,
                        deadline.Token);
                    continue;
                }
                response = frame.Deserialize(ApiJsonContext.Default.ApiResponseEnvelope)
                    ?? throw new JsonException();
            }
            if (response is null) throw new InvalidDataException();
            if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                _started = false;
                await StopProcessAsync();
                throw new HostCommandException("internal", "Plugin response ID did not match.");
            }
            if (!response.Ok)
            {
                throw new HostCommandException(
                    response.Error?.Code ?? "internal",
                    response.Error?.Message ?? "Plugin call failed.",
                    response.Error?.Retryable ?? false,
                    response.Error?.Details);
            }
            return response.Result ?? EmptyObject();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _started = false;
            await StopProcessAsync();
            throw new HostCommandException("timeout", "Plugin call timed out.", true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (requestMayHaveBeenWritten)
            {
                _started = false;
                await StopProcessAsync();
            }
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException or EndOfStreamException)
        {
            _started = false;
            await StopProcessAsync();
            throw new HostCommandException("internal", "Plugin protocol failed.");
        }
        finally
        {
            _callGate.Release();
        }
    }

    private async ValueTask HandlePluginRequestAsync(
        Stream input,
        ApiRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        ApiResponseEnvelope response;
        if (request.SchemaVersion != 1
            || !PluginValidation.IsRequestId(request.RequestId)
            || !string.Equals(
                request.Method,
                "host.credential.resolve",
                StringComparison.Ordinal)
            || request.Params is not JsonElement parameters
            || parameters.ValueKind is not JsonValueKind.Object
            || parameters.EnumerateObject().Any(property => property.Name != "slot")
            || !parameters.TryGetProperty("slot", out var slotValue)
            || slotValue.ValueKind is not JsonValueKind.String)
        {
            response = new(
                1,
                request.RequestId ?? "invalid",
                false,
                null,
                new("credential_forbidden", "Plugin host request is forbidden."));
        }
        else
        {
            var slot = slotValue.GetString() ?? string.Empty;
            if (!Manifest.CredentialSlots.Contains(slot, StringComparer.Ordinal))
            {
                response = new(
                    1,
                    request.RequestId,
                    false,
                    null,
                    new("credential_forbidden", "Credential slot is not declared."));
            }
            else
            {
                var secret = _credentialBroker is null
                    ? null
                    : await _credentialBroker.ResolveAsync(
                        Manifest.Id,
                        slot,
                        cancellationToken);
                response = secret is null
                    ? new(
                        1,
                        request.RequestId,
                        false,
                        null,
                        new("credential_required", "Credential is not configured."))
                    : new(
                        1,
                        request.RequestId,
                        true,
                        Object(("value", secret)),
                        null);
            }
        }
        await ProcessFrameProtocol.WriteAsync(
            input,
            response,
            ApiJsonContext.Default.ApiResponseEnvelope,
            cancellationToken);
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    using var timeout = new CancellationTokenSource(DisposeTimeout());
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { await KillAndWaitAsync(process); }
                }
            }
            catch
            {
                await KillAndWaitAsync(process);
            }
            process.Dispose();
        }
        _job?.Dispose();
        _job = null;
        if (_stderrTask is not null)
        {
            try { await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
            _stderrTask = null;
        }
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    private async Task DrainStderrAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[512];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try { read = await reader.ReadAsync(buffer, cancellationToken); }
            catch { return; }
            if (read == 0) return;
            var text = Sanitize(new string(buffer, 0, read));
            lock (_diagnosticSync)
            {
                _diagnostic += text;
                if (_diagnostic.Length > 4096) _diagnostic = _diagnostic[^4096..];
            }
        }
    }

    private void ValidateDescription(ProcessPluginDescription description)
    {
        if (PluginValidation.ValidateCommands(Manifest, description.Commands).Count > 0
            || PluginValidation.ValidateSettings(Manifest, description.Settings).Count > 0)
        {
            throw new HostCommandException(
                "trust_failed",
                "Plugin description is invalid.");
        }
    }

    private string ResolveEntrypoint()
    {
        var relative = Manifest.Entrypoint
            ?? throw new HostCommandException("trust_failed", "Plugin entrypoint is missing.");
        var path = Path.GetFullPath(Path.Combine(
            _installDirectory,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _installDirectory.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            throw new HostCommandException("trust_failed", "Plugin entrypoint is invalid.");
        }
        return path;
    }

    private byte[] ReadDeclaredFile(string relativePath, int maximumBytes)
    {
        var normalized = NormalizePath(relativePath);
        var declaration = Manifest.Files.FirstOrDefault(file =>
            string.Equals(NormalizePath(file.Path), normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new HostCommandException("trust_failed", "Plugin asset is not declared.");
        var path = Path.GetFullPath(Path.Combine(
            _installDirectory,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _installDirectory.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            throw new HostCommandException("trust_failed", "Plugin asset is unavailable.");
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != declaration.Bytes || bytes.Length > maximumBytes
            || !string.Equals(
                Convert.ToHexString(SHA256.HashData(bytes)),
                declaration.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new HostCommandException("trust_failed", "Plugin asset integrity check failed.");
        }
        return bytes;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private string FilesDigest()
    {
        var joined = string.Join(
            Environment.NewLine,
            Manifest.Files
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => $"{file.Path}:{file.Bytes}:{file.Sha256.ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))
            .ToLowerInvariant();
    }

    private void VerifyInstalledFiles()
    {
        foreach (var file in Manifest.Files)
        {
            var normalized = NormalizePath(file.Path);
            var path = Path.GetFullPath(Path.Combine(
                _installDirectory,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = _installDirectory.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path)
                || new FileInfo(path).Length != file.Bytes)
            {
                throw new HostCommandException("trust_failed", "Plugin file integrity check failed.");
            }
            using var stream = File.OpenRead(path);
            var actual = SHA256.HashData(stream);
            var expected = Convert.FromHexString(file.Sha256);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new HostCommandException("trust_failed", "Plugin file integrity check failed.");
            }
        }
    }

    private TimeSpan HandshakeTimeout() =>
        TimeSpan.FromSeconds(Math.Clamp(Manifest.HandshakeTimeoutSeconds ?? 5, 1, 5));

    private TimeSpan CallTimeout() =>
        TimeSpan.FromSeconds(Math.Clamp(Manifest.CallTimeoutSeconds ?? 15, 1, 15));

    private TimeSpan DisposeTimeout() =>
        TimeSpan.FromSeconds(Math.Clamp(Manifest.DisposeTimeoutSeconds ?? 2, 1, 2));

    private void EnsureStarted()
    {
        if (!_started)
        {
            throw new HostCommandException("plugin_disabled", "Plugin process is not running.");
        }
    }

    private static string Sanitize(string value)
    {
        var lines = value.Replace((char)13, (char)10).Split((char)10);
        return string.Join(
            Environment.NewLine,
            lines.Select(line =>
            {
                var trimmed = line.Length > 512 ? line[..512] : line;
                foreach (var marker in new[] { "token", "secret", "password", "authorization" })
                {
                    var index = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0) trimmed = trimmed[..index] + marker + "=[redacted]";
                }
                return trimmed;
            }));
    }

    private static JsonElement Object(params (string Key, object Value)[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case string text: writer.WriteStringValue(text); break;
                    case int number: writer.WriteNumberValue(number); break;
                    case long number: writer.WriteNumberValue(number); break;
                    default: throw new InvalidOperationException();
                }
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
