using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZGSTokenBar.Plugin.Router;

public sealed partial class RouterMonitorClient
{
    public const string AllProjects = "all";
    private sealed record Catalog(JsonElement Projects, DateTimeOffset At);
    private Catalog? _allCatalog;

    private async Task<RouterSnapshot> AllSnapshotAsync(string instance, JsonElement projects, CancellationToken token)
    {
        var views = await MapProjectsAsync(projects.EnumerateArray().ToArray(), async row =>
            (Project: row, View: await ProjectViewAsync(RouterJson.Text(row, "key"), token).ConfigureAwait(false)), token).ConfigureAwait(false);
        var active = views.Where(row => RouterJson.IsHex(RouterJson.Text(row.View, "requestId"), 32))
            .OrderBy(row => RouterJson.Text(row.Project, "name"), StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => RouterJson.Text(row.Project, "key"), StringComparer.Ordinal)
            .Select(row => new RouterActiveProject(RouterJson.Text(row.Project, "name"), row.View)).ToArray();
        var queued = views.Sum(row => RouterJson.Integer(row.View, "queuedCount"));
        var observed = views.Select(row => RouterJson.Number(row.View, "observedAt")).DefaultIfEmpty(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d).Min();
        var tones = views.Select(row => RouterJson.Text(row.View, "tone")).ToArray();
        var tone = tones.Any(value => value is "negative" or "failed" or "attention" or "danger") ? "negative"
            : tones.Any(value => value is "warning" or "cleanup") ? "warning"
            : active.Length > 0 ? "running" : queued > 0 ? "waiting" : "idle";
        var view = Element(new JsonObject {
            ["projectKey"] = AllProjects, ["observedAt"] = observed, ["queuedCount"] = queued, ["activeCount"] = active.Length,
            ["headline"] = active.Length > 0 ? $"{active.Length} 个任务" : queued > 0 ? "等待调度" : "空闲",
            ["tone"] = tone, ["requestId"] = "",
        });
        return new(instance, AllProjects, projects, view, DateTimeOffset.UtcNow)
        {
            QueuedCount = queued, QueueObservedAt = observed, ActiveProjects = active,
        };
    }

    private async Task<JsonElement> AllPageAsync(bool history, string kind, string outcome, string day, string query,
        int offset, int limit, CancellationToken token)
    {
        offset = Math.Max(0, offset);
        var cached = _allCatalog;
        var projects = cached is not null && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(10)
            ? cached.Projects : await ReadAsync("api/projects", token).ConfigureAwait(false);
        if (projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() > 128) throw new InvalidDataException("Invalid project catalog.");
        var pages = await MapProjectsAsync(projects.EnumerateArray().ToArray(), async project =>
        {
            var key = RouterJson.Text(project, "key");
            var prefix = new List<JsonElement>();
            JsonElement metadata = default;
            var total = 0;
            var target = Math.Max(limit, checked(offset + limit));
            do
            {
                var size = Math.Min(100, target - prefix.Count);
                var page = history ? await HistoryAsync(key, kind, outcome, day, query, prefix.Count, token, size).ConfigureAwait(false)
                    : await QueueAsync(key, prefix.Count, token, size).ConfigureAwait(false);
                total = RouterJson.Integer(page, "total");
                metadata = page;
                var rows = RouterJson.Rows(page, "items").ToArray();
                prefix.AddRange(rows);
                if (rows.Length == 0) break;
            } while (prefix.Count < target && prefix.Count < total);
            return (Project: project, Rows: prefix, Total: total, Metadata: metadata);
        }, token).ConfigureAwait(false);
        var entries = pages.SelectMany(page =>
        {
            var projectKey = RouterJson.Text(page.Project, "key");
            var projectName = RouterJson.Text(page.Project, "name");
            return page.Rows.Select(row => (
                ProjectKey: projectKey,
                ProjectName: projectName,
                Row: row,
                CreatedAt: history && DateTimeOffset.TryParse(
                    RouterJson.Text(row, "createdLocal"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var at)
                    ? at
                    : DateTimeOffset.MinValue,
                Id: RouterJson.Text(row, "id"),
                Rank: RouterJson.Integer(row, "rank")));
        });
        var ordered = history
            ? entries.OrderByDescending(entry => entry.CreatedAt)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            : entries.OrderBy(entry => entry.ProjectName, StringComparer.Ordinal)
                .ThenBy(entry => entry.ProjectKey, StringComparer.Ordinal)
                .ThenBy(entry => entry.Rank);

        using var buffer = new MemoryStream(16384);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            writer.WriteStartArray("items");
            foreach (var entry in ordered.Skip(offset).Take(limit))
            {
                writer.WriteStartObject();
                if (entry.Row.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in entry.Row.EnumerateObject())
                    {
                        if (prop.Name is "projectKey" or "projectName" or "task") continue;
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteString("projectKey", entry.ProjectKey);
                writer.WriteString("projectName", entry.ProjectName);
                writer.WriteString("task", entry.ProjectName + " · " + RouterJson.Text(entry.Row, "task"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteNumber("total", pages.Sum(page => page.Total));
            writer.WriteNumber("offset", offset);
            writer.WriteNumber("limit", limit);

            writer.WriteStartObject("counts");
            writer.WriteNumber("completed", pages.Sum(page => RouterJson.Integer(RouterJson.Field(page.Metadata, "counts"), "completed")));
            writer.WriteNumber("failed", pages.Sum(page => RouterJson.Integer(RouterJson.Field(page.Metadata, "counts"), "failed")));
            writer.WriteNumber("cancelled", pages.Sum(page => RouterJson.Integer(RouterJson.Field(page.Metadata, "counts"), "cancelled")));
            writer.WriteEndObject();

            writer.WriteStartArray("days");
            foreach (var group in pages.SelectMany(page => RouterJson.Rows(page.Metadata, "days"))
                .Select(row => (Row: row, Date: RouterJson.Text(row, "date")))
                .Where(entry => entry.Date.Length > 0)
                .GroupBy(entry => entry.Date, StringComparer.Ordinal)
                .OrderByDescending(group => group.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("date", group.Key);
                var hasTotal = group.Any(entry => entry.Row.ValueKind == JsonValueKind.Object
                    && entry.Row.TryGetProperty("total", out var totalProp)
                    && totalProp.ValueKind == JsonValueKind.Number);
                if (hasTotal) writer.WriteNumber("total", group.Sum(entry => RouterJson.Integer(entry.Row, "total")));

                var hasCounts = group.Any(entry => RouterJson.Field(entry.Row, "counts").ValueKind == JsonValueKind.Object);
                if (hasCounts)
                {
                    writer.WriteStartObject("counts");
                    writer.WriteNumber("completed", group.Sum(entry => RouterJson.Integer(RouterJson.Field(entry.Row, "counts"), "completed")));
                    writer.WriteNumber("failed", group.Sum(entry => RouterJson.Integer(RouterJson.Field(entry.Row, "counts"), "failed")));
                    writer.WriteNumber("cancelled", group.Sum(entry => RouterJson.Integer(RouterJson.Field(entry.Row, "counts"), "cancelled")));
                    writer.WriteNumber("pending", group.Sum(entry => RouterJson.Integer(RouterJson.Field(entry.Row, "counts"), "pending")));
                    writer.WriteEndObject();
                }

                var hasDurations = group.Any(entry => RouterJson.Field(entry.Row, "durations").ValueKind == JsonValueKind.Object);
                if (hasDurations)
                {
                    writer.WriteStartObject("durations");
                    writer.WriteNumber("dispatchedCount", group.Sum(entry => RouterJson.Integer(RouterJson.Field(entry.Row, "durations"), "dispatchedCount")));
                    writer.WriteNumber("measuredCount", group.Sum(entry => RouterJson.Integer(RouterJson.Field(entry.Row, "durations"), "measuredCount")));
                    writer.WriteNumber("occupiedSeconds", Math.Round(group.Sum(entry => RouterJson.Number(RouterJson.Field(entry.Row, "durations"), "occupiedSeconds") ?? 0d), 3));
                    writer.WriteNumber("executionSeconds", Math.Round(group.Sum(entry => RouterJson.Number(RouterJson.Field(entry.Row, "durations"), "executionSeconds") ?? 0d), 3));
                    writer.WriteNumber("cleanupSeconds", Math.Round(group.Sum(entry => RouterJson.Number(RouterJson.Field(entry.Row, "durations"), "cleanupSeconds") ?? 0d), 3));
                    writer.WriteNumber("editorActiveSeconds", Math.Round(group.Sum(entry => RouterJson.Number(RouterJson.Field(entry.Row, "durations"), "editorActiveSeconds") ?? 0d), 3));
                    writer.WriteNumber("routerOverheadSeconds", Math.Round(group.Sum(entry => RouterJson.Number(RouterJson.Field(entry.Row, "durations"), "routerOverheadSeconds") ?? 0d), 3));
                    writer.WriteNumber("nonEditorScriptSeconds", Math.Round(group.Sum(entry => RouterJson.Number(RouterJson.Field(entry.Row, "durations"), "nonEditorScriptSeconds") ?? 0d), 3));
                    writer.WriteNumber("unmeasuredSeconds", Math.Round(group.Sum(entry => RouterJson.Number(RouterJson.Field(entry.Row, "durations"), "unmeasuredSeconds") ?? 0d), 3));
                    writer.WriteNumber("zeroEditorScriptCount", group.Sum(entry => RouterJson.Integer(RouterJson.Field(entry.Row, "durations"), "zeroEditorScriptCount")));
                    writer.WriteNumber("readyToDispatchSeconds", Math.Round(group.Sum(entry => RouterJson.Number(RouterJson.Field(entry.Row, "durations"), "readyToDispatchSeconds") ?? 0d), 3));
                    writer.WriteNumber("readyToDispatchSamples", group.Sum(entry => RouterJson.Integer(RouterJson.Field(entry.Row, "durations"), "readyToDispatchSamples")));
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("groups");
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        return document.RootElement.Clone();
    }

    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static async Task<T[]> MapProjectsAsync<T>(JsonElement[] projects, Func<JsonElement, Task<T>> read, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(6);
        return await Task.WhenAll(projects.Select(async project =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { return await read(project).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
    }
}
