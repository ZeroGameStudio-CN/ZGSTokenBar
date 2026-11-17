using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZGSTokenBar.App;

internal sealed record IntakeServiceSource(string Id, string Name, string SshTarget, string Unit, string StateDirectory);
internal sealed record IntakeServiceRecord(string Id, string ItemId, string Project, string Number, string Title,
    string Kind, string Phase, DateTimeOffset? ObservedAt)
{
    internal bool InFlight => Phase is "sending" or "model_started" or "classified" or "waiting_upstream"
        or "waiting_original" or "submitting" or "submitted" or "retry";
}
internal sealed record IntakeServiceReading(string Schema, string SourceId, DateTimeOffset ObservedAt, bool Active,
    string ServiceState, string Invocation, string[] Projects, IntakeServiceRecord[] Progress,
    IntakeServiceRecord[] History, string[] Gaps);
internal sealed record IntakeServiceResult(IntakeServiceSource Source, IntakeServiceReading? Reading, string? Error);

/// <summary>Explicitly registered operator observations. Never runs an action or changes the remote service.</summary>
internal sealed class IntakeServiceMonitorClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Func<IntakeServiceSource, CancellationToken, Task<IntakeServiceReading>> _read;
    internal IReadOnlyList<IntakeServiceSource> Sources { get; }
    internal string? ConfigurationError { get; }
    internal static string ProfilePath(string dataDirectory) => Path.Combine(dataDirectory, "intake-services.json");

    internal IntakeServiceMonitorClient(string dataDirectory)
    {
        _read = ReadSourceAsync;
        try
        {
            using var stream = File.OpenRead(ProfilePath(dataDirectory));
            if (stream.Length > 65536) throw new InvalidDataException();
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException();
            var sources = doc.RootElement.GetProperty("sources").Deserialize<IntakeServiceSource[]>(Json) ?? [];
            ValidateSources(sources);
            Sources = sources;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            Sources = [];
            ConfigurationError = "configuration_unavailable";
        }
    }

    internal IntakeServiceMonitorClient(IReadOnlyList<IntakeServiceSource> sources,
        Func<IntakeServiceSource, CancellationToken, Task<IntakeServiceReading>> read)
    {
        ValidateSources(sources); Sources = sources; _read = read;
    }

    internal static void ValidateSources(IReadOnlyList<IntakeServiceSource> sources)
    {
        if (sources.Count is < 1 or > 8 || sources.Any(s => s is null) || sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new InvalidDataException("invalid_sources");
        foreach (var source in sources)
            if (!Regex.IsMatch(source.Id ?? "", @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}\z")
                || string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 80 || source.Name.Any(char.IsControl)
                || !Regex.IsMatch(source.SshTarget ?? "", @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,120}\z")
                || !Regex.IsMatch(source.Unit ?? "", @"\A[a-zA-Z0-9][a-zA-Z0-9_.@-]{0,100}\.service\z")
                || !(source.StateDirectory ?? "").StartsWith("/var/lib/", StringComparison.Ordinal)
                || (source.StateDirectory ?? "").Contains("..", StringComparison.Ordinal) || (source.StateDirectory ?? "").Any(char.IsControl))
                throw new InvalidDataException("invalid_source");
    }

    internal async Task<IReadOnlyList<IntakeServiceResult>> ReadAsync(CancellationToken token)
    {
        using var gate = new SemaphoreSlim(2);
        return await Task.WhenAll(Sources.Select(async source =>
        {
            await gate.WaitAsync(token);
            try
            {
                var reading = await _read(source, token);
                ValidateReading(source, reading);
                return new IntakeServiceResult(source, reading, null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException
                or System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException)
            { return new IntakeServiceResult(source, null, "unavailable"); }
            finally { gate.Release(); }
        }));
    }

    internal static void ValidateReading(IntakeServiceSource source, IntakeServiceReading value)
    {
        if (value.Schema != "zgstokenbar.intake-monitor.v1" || value.SourceId != source.Id
            || value.Projects is null || value.Projects.Length > 64 || value.Progress is null || value.Progress.Length > 64
            || value.History is null || value.History.Length > 160 || value.Gaps is null || value.Gaps.Length > 32
            || value.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5) || value.ObservedAt < DateTimeOffset.UtcNow.AddMinutes(-5))
            throw new InvalidDataException("invalid_reading");
        if (value.Projects.Any(p => p is null || !Regex.IsMatch(p, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z")))
            throw new InvalidDataException("invalid_projects");
        foreach (var row in value.Progress.Concat(value.History))
            if (row is null || row.Id is null || row.Id.Length > 300 || row.Title is null || row.Title.Length > 160
                || row.Phase is null || row.Phase.Length > 100 || row.Kind is null || row.Kind.Length > 80
                || row.Project is null || row.Project.Length > 64 || row.Number is null || row.Number.Length > 32
                || row.ItemId is null || row.ItemId.Length > 36 || row.ItemId.Length > 0 && !Guid.TryParseExact(row.ItemId, "D", out _))
                throw new InvalidDataException("invalid_record");
    }

    private static async Task<IntakeServiceReading> ReadSourceAsync(IntakeServiceSource source, CancellationToken token)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZGSTokenBar.App.IntakeServiceRead.py")
            ?? throw new InvalidDataException("collector_missing");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var script = await reader.ReadToEndAsync(token);
        var config = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(source, Json));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        using var process = new Process { StartInfo = new("ssh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
        }};
        foreach (var arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=5", source.SshTarget, "python3 -" })
            process.StartInfo.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("collector_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var halt = deadline.Token.Register(() => Stop(process));
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream, 2 * 1024 * 1024, deadline.Token);
        var errors = ReadBoundedAsync(process.StandardError.BaseStream, 8192, deadline.Token);
        try
        {
            await process.StandardInput.WriteAsync($"import base64,json\nCONFIG=json.loads(base64.b64decode('{config}'))\nSOURCE_BASE64='{encoded}'\n{script}".AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new IOException("collector_unavailable");
            return JsonSerializer.Deserialize<IntakeServiceReading>(await output, Json) ?? throw new InvalidDataException();
        }
        finally { Stop(process); }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var data = new MemoryStream(); var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (data.Length + count > limit) throw new InvalidDataException("output_limit");
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }
}
