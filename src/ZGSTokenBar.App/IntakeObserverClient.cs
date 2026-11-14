using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZGSTokenBar.App;

internal sealed record IntakeProject(string Key, string Title)
{
    public override string ToString() => Title == Key ? Key : $"{Title} · {Key}";
}

internal sealed record IntakeAccess(string Subject, string Revision, DateTimeOffset? ExpiresAt,
    IReadOnlyList<IntakeProject> Projects)
{
    internal bool SameSession(IntakeAccess other) =>
        Subject == other.Subject && Revision == other.Revision;
}

internal sealed record IntakeItem(Guid Id, string Number, string Project, string Title, string Status,
    string Phase, bool Blocked, DateTimeOffset? UpdatedAt, Guid? Thread, string EvidenceMode);
internal sealed record IntakeTransition(DateTimeOffset? Timestamp, string Command, string Status);
internal sealed record IntakeDiagnosticEvent(DateTimeOffset? Timestamp, string Name, string Phase, string Outcome, string Error);
internal sealed record IntakeDiagnostics(string Status, int FactCount, bool HasMore,
    IReadOnlyList<IntakeDiagnosticEvent> Events);

internal sealed class IntakeReadException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal sealed class IntakeObserverClient
{
    private const int RecentLimit = 16;
    internal const int HistoryLimit = 60;
    private const int BodyLimit = 4 * 1024 * 1024;
    private static readonly Regex ProjectKey = new(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ItemNumber = new(@"\A[A-Za-z][A-Za-z0-9_-]{0,15}-[1-9][0-9]{0,11}\z", RegexOptions.CultureInvariant);
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<JsonElement>> _read;

    internal IntakeObserverClient() : this(ReadInstalledAsync) { }

    internal IntakeObserverClient(Func<IReadOnlyList<string>, CancellationToken, Task<JsonElement>> read) => _read = read;

    internal static bool IsReference(string value) => Guid.TryParseExact(value, "D", out _) || ItemNumber.IsMatch(value);

    internal Uri? WorkItemLink(Guid id)
    {
        // The organization chooses its web origin locally; the public binary
        // contains no organization hostname. Only this fixed UI route opens.
        var configured = Environment.GetEnvironmentVariable("ZGSTOKENBAR_WORKFLOW_WEB_ORIGIN");
        return Uri.TryCreate(configured, UriKind.Absolute, out var origin)
            && origin.Scheme == Uri.UriSchemeHttps && origin.UserInfo.Length == 0
            && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0
            ? new Uri(origin, "/#/tasks?work_item_id=" + id.ToString("D")) : null;
    }

    internal async Task<IntakeAccess> AccessAsync(CancellationToken token)
    {
        var value = await _read(["whoami"], token).ConfigureAwait(false);
        var subject = Text(value, "subject");
        var revision = Text(value, "authorization_revision");
        if (subject.Length == 0 || revision.Length == 0) throw new IntakeReadException("invalid");
        var access = Rows(value, "project_access")
            .Where(row => Rows(row, "capabilities").Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "work_item.read"))
            .Select(row => Text(row, "project_key")).Where(key => ProjectKey.IsMatch(key))
            .Distinct(StringComparer.Ordinal).ToArray();
        var titles = Rows(value, "projects").Where(row => access.Contains(Text(row, "project_key")))
            .GroupBy(row => Text(row, "project_key"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Text(group.First(), "title", 80), StringComparer.Ordinal);
        return new(subject, revision, Time(value, "expires_at"), access.Select(key => new IntakeProject(key,
            titles.GetValueOrDefault(key) is { Length: > 0 } title ? title : key)).ToArray());
    }

    internal async Task<IReadOnlyList<IntakeItem>> RecentAsync(string project, bool blocked, CancellationToken token)
    {
        if (!ProjectKey.IsMatch(project)) throw new IntakeReadException("invalid");
        var args = new List<string> { "list", "--project-key", project, "--limit",
            RecentLimit.ToString(CultureInfo.InvariantCulture), "--sort", "updated_desc" };
        if (blocked) args.Add("--blocked");
        var value = await _read(args, token).ConfigureAwait(false);
        return Rows(value, "items").Take(RecentLimit).Select(ParseItem).Where(row => row.Project == project).ToArray();
    }

    internal async Task<IntakeItem> ItemAsync(string reference, CancellationToken token)
    {
        if (!IsReference(reference)) throw new IntakeReadException("reference");
        var value = await _read(["get", reference], token).ConfigureAwait(false);
        var item = ParseItem(Field(value, "item"));
        if (Guid.TryParseExact(reference, "D", out var id) ? item.Id != id
            : !string.Equals(item.Number, reference, StringComparison.OrdinalIgnoreCase))
            throw new IntakeReadException("invalid");
        return item;
    }

    internal async Task<IReadOnlyList<IntakeTransition>> HistoryAsync(Guid id, CancellationToken token)
    {
        var value = await _read(["history", id.ToString("D")], token).ConfigureAwait(false);
        return Rows(value, "transitions").Select(row => new IntakeTransition(
                Time(row, "created_at"), Text(row, "command", 64), Text(row, "to_status", 48)))
            .OrderByDescending(row => row.Timestamp).Take(HistoryLimit).ToArray();
    }

    internal async Task<IntakeDiagnostics> DiagnosticsAsync(Guid id, CancellationToken token)
    {
        var input = JsonSerializer.Serialize(new { selector = new { kind = "work_item", id, run_id = (string?)null }, cursor = (string?)null });
        var value = await _read(["plugin", "run", "logs.diagnostic-bundle", "--input", input], token).ConfigureAwait(false);
        var output = Field(value, "result");
        if (Field(value, "ok").ValueKind != JsonValueKind.True || output.ValueKind != JsonValueKind.Object)
            throw new IntakeReadException("unavailable");
        var query = Field(output, "query");
        if (!Guid.TryParseExact(Text(query, "work_item_id"), "D", out var observed) || observed != id)
            throw new IntakeReadException("invalid");
        var status = Text(output, "status", 20);
        if (status is not ("complete" or "partial" or "not_found")) throw new IntakeReadException("invalid");
        var events = Rows(output, "diagnostic_events").Where(row =>
                Guid.TryParseExact(Text(row, "work_item_id"), "D", out var eventId) && eventId == id)
            .Select(row => new IntakeDiagnosticEvent(Time(row, "timestamp"), Text(row, "event_name", 80),
                Text(row, "phase", 64), Text(row, "outcome", 64), Text(row, "error_code", 80)))
            .OrderByDescending(row => row.Timestamp).Take(40).ToArray();
        return new(status, Rows(output, "authoritative_facts").Count(),
            Field(output, "next_cursor").ValueKind == JsonValueKind.String, events);
    }

    private static IntakeItem ParseItem(JsonElement value)
    {
        var id = Text(value, "work_item_id");
        var project = Text(value, "project_key");
        if (!Guid.TryParseExact(id, "D", out var uuid) || !ProjectKey.IsMatch(project))
            throw new IntakeReadException("invalid");
        var execution = Field(value, "execution");
        var phase = Text(execution, "phase", 48);
        var evidence = Field(value, "agent_evidence");
        if (evidence.ValueKind == JsonValueKind.String && evidence.GetString() is { Length: > 0 and <= 65_536 } encoded)
        {
            try { using var doc = JsonDocument.Parse(encoded); evidence = doc.RootElement.Clone(); }
            catch (JsonException) { evidence = default; }
        }
        Guid? thread = Guid.TryParseExact(Text(evidence, "thread_id"), "D", out var found) ? found : null;
        var mode = Text(evidence, "mode");
        if (mode is not ("verified_original_blocked_continuation" or "verified_original_gui_report_adoption")) mode = "";
        return new(uuid, Text(value, "work_item_number", 32), project, Text(value, "title", 160),
            Text(value, "status", 48), phase, phase == "blocked" || Rows(value, "blocker_tags").Any(),
            Time(value, "updated_at"), thread, mode);
    }

    private static JsonElement Field(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;

    private static IEnumerable<JsonElement> Rows(JsonElement value, string key)
    {
        var array = Field(value, key);
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];
    }

    private static string Text(JsonElement value, string key, int limit = 256)
    {
        var field = Field(value, key);
        if (field.ValueKind != JsonValueKind.String || field.GetString() is not { } text) return "";
        var clean = string.Concat(text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c)).Trim();
        return clean.Length <= limit ? clean : clean[..limit];
    }

    private static DateTimeOffset? Time(JsonElement value, string key)
    {
        var field = Field(value, key);
        if (field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var stamp)
            && stamp is > 0 and <= 253402300799999)
            return DateTimeOffset.FromUnixTimeMilliseconds(stamp);
        return field.ValueKind == JsonValueKind.String && field.TryGetDateTimeOffset(out var offset) ? offset : null;
    }

    private static async Task<JsonElement> ReadInstalledAsync(IReadOnlyList<string> command, CancellationToken token)
    {
        var (python, launcher) = Installation(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        using var process = new Process
        {
            StartInfo = new(python)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(launcher)!,
            },
        };
        foreach (var arg in new[] { "-E", "-s", "-X", "utf8", "-B", launcher, "--timeout", "12", "--json" }.Concat(command))
            process.StartInfo.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        try { if (!process.Start()) throw new IntakeReadException("unavailable"); }
        catch (Win32Exception) { throw new IntakeReadException("unavailable"); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(16));
        using var halt = deadline.Token.Register(() => Stop(process));
        try
        {
            // Drain both streams concurrently; either overflow cancels this owned CLI child.
            var output = ReadBytesAsync(process.StandardOutput.BaseStream, BodyLimit, deadline);
            var errors = ReadBytesAsync(process.StandardError.BaseStream, 8192, deadline);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IntakeReadException(Reason(Encoding.UTF8.GetString(await errors.ConfigureAwait(false))));
            using var doc = JsonDocument.Parse(await output.ConfigureAwait(false));
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IntakeReadException("unavailable"); }
        catch (JsonException) { throw new IntakeReadException("invalid"); }
        catch (IOException) { throw new IntakeReadException("unavailable"); }
    }

    internal static string Reason(string stderr)
    {
        if (stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("403", StringComparison.Ordinal)
            || stderr.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)) return "denied";
        if (stderr.Contains("auth login", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("401", StringComparison.Ordinal)
            || stderr.Contains("登录", StringComparison.Ordinal)) return "signed_out";
        if (stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("404", StringComparison.Ordinal)) return "not_found";
        if (stderr.Contains("project does not support work items", StringComparison.OrdinalIgnoreCase)) return "no_work_items";
        return "unavailable";
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream, int limit, CancellationTokenSource deadline)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false) is var read && read != 0)
        {
            if (bytes.Length + read > limit)
            {
                deadline.Cancel();
                throw new IntakeReadException("invalid");
            }
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }

    internal static (string Python, string Launcher) Installation(string localAppData)
    {
        if (!Path.IsPathFullyQualified(localAppData)) throw new IntakeReadException("not_installed");
        try
        {
            var root = Path.Combine(localAppData, "Zero OS", "cli");
            var currentPath = Path.Combine(root, "current.json");
            if (!File.Exists(currentPath)) throw new IntakeReadException("not_installed");
            using var current = ReadMetadata(currentPath);
            var selected = current.RootElement;
            var version = Text(selected, "version", 64);
            var sha = Text(selected, "source_commit", 64);
            if (Field(selected, "schema_version").GetInt32() != 2 || Text(selected, "product") != "zero-os-cli"
                || !Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:[-.][a-zA-Z0-9]+)*\z")
                || sha.Length != 40 || !sha.All(Uri.IsHexDigit))
                throw new IntakeReadException("install_invalid");
            var release = Path.Combine(root, "releases", version + "-" + sha[..12]);
            var python = Text(selected, "python_path", 1024);
            if (!string.Equals(Path.GetFullPath(Text(selected, "release_path", 1024)), release, StringComparison.OrdinalIgnoreCase)
                || !Path.IsPathFullyQualified(python) || !string.Equals(Path.GetFileName(python), "python.exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(python))
                throw new IntakeReadException("install_invalid");
            using var manifest = ReadMetadata(Path.Combine(release, "cli-release.json"));
            const string entrypoint = "scripts/zgs_ops_launcher.py";
            var package = manifest.RootElement;
            if (Field(package, "schema_version").GetInt32() != 2 || Text(package, "version") != version
                || Text(package, "source_commit") != sha || Text(package, "entrypoint") != entrypoint
                || Text(package, "product") != "zero-os-cli" || Text(package, "platform") != "windows-x64")
                throw new IntakeReadException("install_invalid");
            var launcher = Path.Combine(release, "scripts", "zgs_ops_launcher.py");
            using var entry = File.OpenRead(launcher);
            if (entry.Length > 1_048_576 || !string.Equals(Convert.ToHexString(SHA256.HashData(entry)),
                Text(Field(package, "files"), entrypoint), StringComparison.OrdinalIgnoreCase))
                throw new IntakeReadException("install_invalid");
            return (python, launcher);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException or ArgumentException)
        { throw new IntakeReadException("install_invalid"); }
    }

    private static JsonDocument ReadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 262_144) throw new IntakeReadException("install_invalid");
        return JsonDocument.Parse(stream);
    }
}
