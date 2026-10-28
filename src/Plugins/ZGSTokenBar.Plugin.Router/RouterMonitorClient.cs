using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZGSTokenBar.Plugin.Router;

/// <summary>The Router owns every state and control decision; this adapter only transports bounded messages.</summary>
public sealed class RouterMonitorClient : IDisposable
{
    public static readonly Uri Address = new("http://127.0.0.1:18761/");
    private readonly HttpClient _http;
    public RouterMonitorClient(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<JsonElement> ReadAsync(string route, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        cancellationToken = deadline.Token;
        if (!route.StartsWith("api/", StringComparison.Ordinal) && route != "health")
            throw new ArgumentException("Use a fixed monitor route.", nameof(route));
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await ReadBodyAsync(response, 1024 * 1024, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        return json.RootElement.Clone();
    }

    public async Task<RouterSnapshot> SnapshotAsync(string? projectKey = null, CancellationToken cancellationToken = default)
    {
        var health = await ReadAsync("health", cancellationToken);
        if (RouterJson.Integer(health, "apiVersion") != 1 || RouterJson.Text(health, "instanceId").Length != 32)
            throw new RouterCompatibilityException();
        var projects = await ReadAsync("api/projects", cancellationToken);
        if (projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() > 128)
            throw new InvalidDataException("Invalid project catalog.");
        var selected = projects.EnumerateArray().FirstOrDefault(row => RouterJson.Text(row, "key") == projectKey);
        if (selected.ValueKind == JsonValueKind.Undefined)
            selected = projects.EnumerateArray().FirstOrDefault(row => RouterJson.Integer(row, "pending") > 0);
        if (selected.ValueKind == JsonValueKind.Undefined) selected = projects.EnumerateArray().FirstOrDefault();
        if (selected.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("No registered projects.");
        var key = RouterJson.Text(selected, "key");
        if (!RouterJson.IsHex(key, 64)) throw new InvalidDataException("Invalid project identity.");
        var view = await ReadAsync("api/view?project=" + key, cancellationToken);
        if (RouterJson.Text(view, "projectKey") != key || RouterJson.Number(view, "observedAt") is not { } observed
            || observed > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5)
            throw new InvalidDataException("Invalid observation binding.");
        return new(RouterJson.Text(health, "instanceId"), key, projects, view, DateTimeOffset.UtcNow);
    }

    public Task<JsonElement> QueueAsync(string project, int offset, CancellationToken cancellationToken = default, int limit = 20) =>
        ReadAsync($"api/queue?project={Project(project)}&offset={Math.Max(0, offset)}&limit={PageLimit(limit)}", cancellationToken);

    public Task<JsonElement> HistoryAsync(string project, string kind, string outcome, string day, string query,
        int offset, CancellationToken cancellationToken = default, int limit = 25) =>
        ReadAsync($"api/history?project={Project(project)}&category={Escape(kind)}&outcome={Escape(outcome)}"
            + $"&day={Escape(day)}&query={Escape(query.Length > 160 ? query[..160] : query)}&offset={Math.Max(0, offset)}&limit={PageLimit(limit)}", cancellationToken);

    public async Task<string> ExportAsync(string project, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/export?project=" + Project(project), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await ReadBodyAsync(response, 16 * 1024 * 1024, cancellationToken);
        response.EnsureSuccessStatusCode();
        return Encoding.UTF8.GetString(bytes);
    }

    // No retries: even a timeout may have registered the original control intent.
    public async Task<JsonElement> ControlAsync(RouterSnapshot snapshot, string requestId, string action,
        string? queueVersion = null, int? position = null, string? nativeStop = null, bool queuedOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (!snapshot.IsFresh(DateTimeOffset.UtcNow) || !RouterJson.IsHex(requestId, 32))
            throw new InvalidOperationException("数据已过期，请刷新原请求。");
        var body = new JsonObject
        {
            ["instanceId"] = snapshot.InstanceId, ["project"] = Project(snapshot.ProjectKey),
            ["requestId"] = requestId, ["action"] = action, ["queueVersion"] = queueVersion,
            ["position"] = position, ["nativeStop"] = nativeStop, ["queuedOnly"] = queuedOnly,
            ["intentId"] = Guid.NewGuid().ToString("N"), ["issuedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/control");
        request.Headers.Add("X-Router-Control", "1");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await ReadBodyAsync(response, 1024 * 1024, cancellationToken);
        using var json = JsonDocument.Parse(bytes);
        var value = json.RootElement.Clone();
        if (!response.IsSuccessStatusCode || !RouterJson.Boolean(value, "ok"))
            throw new InvalidOperationException(RouterJson.Text(value, "error", "控制结果未确认，请刷新原请求；不会自动重试。"));
        if (RouterJson.Text(value, "requestId") != requestId) throw new InvalidDataException("Control receipt identity changed.");
        return value;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Monitor response exceeds its bound.");
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
            throw new InvalidDataException("Monitor redirects are not accepted.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Monitor response exceeds its bound.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static string Project(string key) => RouterJson.IsHex(key, 64) ? key : throw new ArgumentException("Select a registered project.");
    private static int PageLimit(int limit) => limit is >= 1 and <= 100 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));
    private static string Escape(string value) => Uri.EscapeDataString(value);
    public void Dispose() => _http.Dispose();
}

public sealed class RouterCompatibilityException : Exception
{
    public RouterCompatibilityException() : base("Router 监视接口需要更新，原任务继续运行。") { }
}

public sealed record RouterSnapshot(string InstanceId, string ProjectKey, JsonElement Projects, JsonElement View, DateTimeOffset ReceivedAt)
{
    public bool IsFresh(DateTimeOffset now) => now >= ReceivedAt && now - ReceivedAt < TimeSpan.FromSeconds(10)
        && RouterJson.Number(View, "observedAt") is { } observed && now.ToUnixTimeMilliseconds() / 1000d - observed is >= -5 and < 10;
}

public static class RouterJson
{
    public static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    public static string Text(JsonElement value, string name, string fallback = "") => Field(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? fallback : fallback;
    public static double? Number(JsonElement value, string name) => Field(value, name).TryNumber();
    public static int Integer(JsonElement value, string name) => (int)(Number(value, name) ?? 0);
    public static bool Boolean(JsonElement value, string name) => Field(value, name).ValueKind == JsonValueKind.True;
    public static IEnumerable<JsonElement> Rows(JsonElement value, string name) => Field(value, name) is { ValueKind: JsonValueKind.Array } rows ? rows.EnumerateArray() : [];
    public static bool IsHex(string value, int length) => value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static double? TryNumber(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
