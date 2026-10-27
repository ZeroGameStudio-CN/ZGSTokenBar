using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.App;
using ZGSTokenBar.Plugin.Router;

internal static class RouterModuleTests
{
    internal static void Run()
    {
        using var handler = new Fixture();
        using var client = new RouterMonitorClient(handler);
        var snapshot = client.SnapshotAsync().GetAwaiter().GetResult();
        Check(snapshot.ProjectKey == Fixture.Project && snapshot.IsFresh(DateTimeOffset.UtcNow), "Exact project and fresh observation");
        Check(handler.Routes.All(uri => uri.Host == "127.0.0.1" && uri.Port == 18761), "Fixed loopback only");
        var queue = client.QueueAsync(snapshot.ProjectKey, 20).GetAwaiter().GetResult();
        Check(RouterJson.Integer(queue, "offset") == 20 && RouterJson.Rows(queue, "items").Count() == 20, "Queue page beyond first");
        client.HistoryAsync(Fixture.Project, "playmode", "failed", "2026-10-27", "测试 & x", 25).GetAwaiter().GetResult();
        var query = handler.Routes.Last().Query;
        Check(query.Contains("offset=25") && query.Contains("outcome=failed") && query.Contains("%26"), "History filter and pagination binding");
        var csv = client.ExportAsync(Fixture.Project).GetAwaiter().GetResult();
        Check(csv.Contains("original-request"), "Original CSV export");

        client.ControlAsync(snapshot, new string('a', 32), "cancel", new string('b', 64), queuedOnly: true).GetAwaiter().GetResult();
        Check(handler.Posts == 1 && handler.LastControl is { } body && RouterJson.Boolean(body, "queuedOnly")
            && RouterJson.Text(body, "instanceId") == snapshot.InstanceId && RouterJson.IsHex(RouterJson.Text(body, "intentId"), 32), "Exact original control envelope");
        handler.LoseControlReply = true;
        Throws<HttpRequestException>(() => client.ControlAsync(snapshot, new string('a', 32), "release").GetAwaiter().GetResult());
        Check(handler.Posts == 2, "Lost control is never automatically retried");
        Throws<InvalidOperationException>(() => client.ControlAsync(snapshot with { ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new string('a', 32), "release").GetAwaiter().GetResult());
        Check(handler.Posts == 2, "Stale controls never leave the client");

        handler.OldApi = true;
        Throws<RouterCompatibilityException>(() => client.SnapshotAsync().GetAwaiter().GetResult());
        handler.OldApi = false; handler.WrongProject = true;
        Throws<InvalidDataException>(() => client.SnapshotAsync().GetAwaiter().GetResult());
        handler.WrongProject = false; handler.Stale = true;
        Check(!client.SnapshotAsync().GetAwaiter().GetResult().IsFresh(DateTimeOffset.UtcNow), "Old observations cannot become idle/current");
        handler.Stale = false; handler.Oversized = true;
        Throws<InvalidDataException>(() => client.SnapshotAsync().GetAwaiter().GetResult());
    }

    internal static void Capture(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var form = new RouterMonitorForm(new RouterMonitorClient(new Fixture()), observe: false)
            { ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000) };
            form.Show();
            form.RefreshViewAsync().GetAwaiter().GetResult();
            var scale = dpi / 96f;
            form.Scale(new SizeF(scale, scale));
            form.PerformLayout();
            Save(form, Path.Combine(directory, $"router-queue-{dpi}.png"));
            form.SelectPage(1);
            form.HistoryAsync().GetAwaiter().GetResult();
            form.PerformLayout();
            Save(form, Path.Combine(directory, $"router-history-{dpi}.png"));
            form.ApplyUnavailable("合成断线情形：保留原任务和回执，等待重新观察。");
            Check(form.FreshnessText.Contains("未确认"), "Unavailable is not idle");
            Save(form, Path.Combine(directory, $"router-unavailable-{dpi}.png"));
            form.Close();
        }
    }
    private static void Save(System.Windows.Forms.Form form, string path)
    {
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, ImageFormat.Png);
    }
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private sealed class Fixture : HttpMessageHandler
    {
        internal const string Project = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        internal readonly List<Uri> Routes = [];
        internal int Posts;
        internal JsonElement? LastControl;
        internal bool LoseControlReply, OldApi, WrongProject, Stale, Oversized;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Routes.Add(request.RequestUri!);
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                Check(request.Headers.GetValues("X-Router-Control").Single() == "1", "Explicit control header");
                LastControl = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
                if (LoseControlReply) throw new HttpRequestException("Synthetic lost reply");
                return Json(new { ok = true, requestId = new string('a', 32), notice = "取消已请求 · 等待清理", result = new { cleanupVerified = false } });
            }
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/health")
            {
                if (Oversized) return new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1024 * 1024 + 1)) };
                return Json(new { apiVersion = OldApi ? 0 : 1, instanceId = new string('d', 32) });
            }
            if (route == "/api/projects") return Json(new[] { new { key = Project, name = "POB", pending = 41 }, new { key = new string('e', 64), name = "另一工程", pending = 0 } });
            if (route == "/api/view") return Json(new
            {
                projectKey = WrongProject ? new string('f', 64) : Project,
                observedAt = DateTimeOffset.UtcNow.AddMinutes(Stale ? -1 : 0).ToUnixTimeMilliseconds() / 1000d,
                requestId = new string('a', 32), headline = "清理中", task = "合成原生运行与清理观察 · 保留完整长任务名",
                queuedCount = 41, elapsed = "1:25", progressClock = "距原生变化 0:08", detail = "等待原生清理证据；当前任务输入保留。",
                owner = new { label = "成长面板视觉验证" }, resourceRecovery = new { primary = new { action = "stop", label = "停止并收尾" } },
            });
            var offset = request.RequestUri.Query.Contains("offset=20") ? 20 : request.RequestUri.Query.Contains("offset=25") ? 25 : 0;
            if (route == "/api/queue") return Json(new
            {
                offset, total = 41,
                items = Enumerable.Range(offset, 20).Select(i => new { id = i.ToString("x32"), task = "合成测试 · " + i + " · " + new string('长', 28), source = "原对话 " + i, wait = "2:10", rank = i + 1, last = false, version = new string('b', 64) }),
            });
            if (route == "/api/history") return Json(new
            {
                total = 76, offset,
                items = Enumerable.Range(offset, 25).Select(i => new { id = i.ToString("x32"), createdLocal = "2026-10-27T21:00:00+08:00", task = "独立测试与回执 " + i,
                    source = "原对话 " + i, outcome = i % 3 == 0 ? "failed" : "completed", failureClass = i % 3 == 0 ? "validation_failed" : "", queueSeconds = 12.5,
                    executionSeconds = 41.2, cleanupSeconds = 3.1, cleanupVerified = true }),
                counts = new { completed = 74, failed = 2, cancelled = 0, pending = 0 },
                days = new[] { new { date = "2026-10-27", total = 76 } },
                groups = new[] { new { count = 76, category = "PlayMode", queueSecondsP95 = 12.5, executionSecondsP95 = 41.2, cleanupSecondsP95 = 3.1 } },
            });
            if (route == "/api/export") return new(HttpStatusCode.OK) { Content = new StringContent("id,task\noriginal-request,synthetic\n", Encoding.UTF8, "text/csv") };
            return new(HttpStatusCode.NotFound);
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
