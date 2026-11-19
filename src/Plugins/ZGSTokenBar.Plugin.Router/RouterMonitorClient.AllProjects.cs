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
            (Project: row, View: await ProjectViewAsync(RouterJson.Text(row, "key"), token)), token);
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
            ? cached.Projects : await ReadAsync("api/projects", token);
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
                var page = history ? await HistoryAsync(key, kind, outcome, day, query, prefix.Count, token, size)
                    : await QueueAsync(key, prefix.Count, token, size);
                total = RouterJson.Integer(page, "total");
                metadata = page;
                var rows = RouterJson.Rows(page, "items").ToArray();
                prefix.AddRange(rows);
                if (rows.Length == 0) break;
            } while (prefix.Count < target && prefix.Count < total);
            return (Project: project, Rows: prefix, Total: total, Metadata: metadata);
        }, token);
        var rows = pages.SelectMany(page => page.Rows.Select(row =>
        {
            var copy = JsonNode.Parse(row.GetRawText())!.AsObject();
            copy["projectKey"] = RouterJson.Text(page.Project, "key");
            copy["projectName"] = RouterJson.Text(page.Project, "name");
            copy["task"] = RouterJson.Text(page.Project, "name") + " · " + RouterJson.Text(row, "task");
            return Element(copy);
        })).ToArray();
        var ordered = history ? rows.OrderByDescending(row => DateTimeOffset.TryParse(RouterJson.Text(row, "createdLocal"),
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : DateTimeOffset.MinValue)
            .ThenBy(row => RouterJson.Text(row, "id"), StringComparer.Ordinal)
            : rows.OrderBy(row => RouterJson.Text(row, "projectName"), StringComparer.Ordinal)
                .ThenBy(row => RouterJson.Text(row, "projectKey"), StringComparer.Ordinal).ThenBy(row => RouterJson.Integer(row, "rank"));
        var counts = new JsonObject { ["completed"] = pages.Sum(page => RouterJson.Integer(RouterJson.Field(page.Metadata, "counts"), "completed")),
            ["failed"] = pages.Sum(page => RouterJson.Integer(RouterJson.Field(page.Metadata, "counts"), "failed")),
            ["cancelled"] = pages.Sum(page => RouterJson.Integer(RouterJson.Field(page.Metadata, "counts"), "cancelled")) };
        var days = pages.SelectMany(page => RouterJson.Rows(page.Metadata, "days"))
            .Select(row => RouterJson.Text(row, "date")).Distinct(StringComparer.Ordinal).OrderDescending()
            .Select(date => (JsonNode)new JsonObject { ["date"] = date }).ToArray();
        return Element(new JsonObject { ["items"] = new JsonArray(ordered.Skip(offset).Take(limit).Select(row => JsonNode.Parse(row.GetRawText())).ToArray()),
            ["total"] = pages.Sum(page => page.Total), ["offset"] = offset, ["limit"] = limit,
            ["counts"] = counts, ["days"] = new JsonArray(days), ["groups"] = new JsonArray() });
    }

    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static async Task<T[]> MapProjectsAsync<T>(JsonElement[] projects, Func<JsonElement, Task<T>> read, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(4);
        return await Task.WhenAll(projects.Select(async project =>
        {
            await gate.WaitAsync(token);
            try { return await read(project); }
            finally { gate.Release(); }
        }));
    }
}
