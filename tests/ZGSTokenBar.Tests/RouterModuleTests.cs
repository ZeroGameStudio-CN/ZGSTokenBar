using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.App;
using ZGSTokenBar.Plugin.Router;
using ZGSTokenBar.PluginSdk;
using ZGSTokenBar.Core;

internal static class RouterModuleTests
{
    internal static void Run()
    {
        PopoverInteraction();
        using var handler = new Fixture();
        using var client = new RouterMonitorClient(handler);
        var snapshot = client.SnapshotAsync().GetAwaiter().GetResult();
        Check(snapshot.ProjectKey == Fixture.Project && snapshot.IsFresh(DateTimeOffset.UtcNow), "Exact project and fresh observation");
        Check(handler.Routes.All(uri => uri.Host == "127.0.0.1" && uri.Port == 18761), "Fixed loopback only");
        var queue = client.QueueAsync(snapshot.ProjectKey, 20).GetAwaiter().GetResult();
        Check(RouterJson.Integer(queue, "offset") == 20 && RouterJson.Rows(queue, "items").Count() == 20, "Queue page beyond first");
        var compactQueue = client.QueueAsync(snapshot.ProjectKey, 2, limit: 2).GetAwaiter().GetResult();
        Check(RouterJson.Rows(compactQueue, "items").Count() == 2 && handler.Routes.Last().Query.Contains("limit=2"), "Visible queue page is bounded at the API");
        Throws<ArgumentOutOfRangeException>(() => client.QueueAsync(snapshot.ProjectKey, 0, limit: 101).GetAwaiter().GetResult());
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
        CaptureIcons(directory);
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var form = new RouterMonitorForm(new RouterMonitorClient(new Fixture()), observe: false)
            { ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000) };
            form.SetPinned(true);
            Check(!form.ShowInTaskbar && form.FormBorderStyle == System.Windows.Forms.FormBorderStyle.None, "Anchored popup has no independent window chrome");
            var scale = dpi / 96f;
            form.ConfigureFor(new Rectangle(400, (int)(600 * scale), 48, 44), true, scale,
                QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), new Rectangle(0, 0, (int)(1280 * scale), (int)(800 * scale)));
            form.Show();
            form.RefreshViewAsync().GetAwaiter().GetResult();
            form.PerformLayout();
            System.Windows.Forms.Application.DoEvents();
            var close = AllControls(form).OfType<System.Windows.Forms.Button>().Single(button => button.AccessibleName == "收起弹层");
            var closePosition = form.PointToClient(close.PointToScreen(Point.Empty));
            Check(closePosition.X + close.Width <= form.ClientSize.Width, $"Header controls fit popup at {dpi} DPI");
            Save(form, Path.Combine(directory, $"router-queue-{dpi}.png"));
            form.HistoryAsync().GetAwaiter().GetResult();
            form.PerformLayout();
            System.Windows.Forms.Application.DoEvents();
            var grid = AllControls(form).OfType<System.Windows.Forms.DataGridView>().Single(g => g.Columns.Count == 6);
            Check(grid.Columns.Cast<System.Windows.Forms.DataGridViewColumn>().Sum(c => c.Width) <= grid.ClientSize.Width,
                $"History columns fit native popup at {dpi} DPI: client {grid.ClientSize.Width}, columns {grid.Columns.Cast<System.Windows.Forms.DataGridViewColumn>().Sum(c => c.Width)}");
            var gridPosition = form.PointToClient(grid.PointToScreen(Point.Empty));
            Check(gridPosition.X + grid.Width <= form.ClientSize.Width, $"History viewport fits popup at {dpi} DPI");
            foreach (var table in AllControls(form).OfType<System.Windows.Forms.DataGridView>())
            {
                Check(table.Visible && table.ScrollBars == System.Windows.Forms.ScrollBars.None, "Queue and history share one scroll-free page");
                Check(table.Rows.Cast<System.Windows.Forms.DataGridViewRow>().Sum(row => row.Height) <= table.ClientSize.Height - table.ColumnHeadersHeight,
                    $"Visible page rows fit without scrolling at {dpi} DPI");
            }
            Save(form, Path.Combine(directory, $"router-history-{dpi}.png"));
            var projectButtons = AllControls(form).OfType<RouterButton>().Where(button => button.AccessibleName?.StartsWith("切换工程 ") == true).ToArray();
            Check(projectButtons.Length == 3 && projectButtons.Count(button => button.Selected) == 1, "Registered projects use flat selected chips");
            projectButtons.Single(button => button.AccessibleName == "切换工程 ZGSProject_5").PerformClick();
            System.Windows.Forms.Application.DoEvents();
            Check(form.Snapshot?.ProjectKey == new string('e', 64), "Project chip selects the exact registered key");
            projectButtons.Single(button => button.AccessibleName == "切换工程 POB").PerformClick();
            System.Windows.Forms.Application.DoEvents();
            var first = ((JsonElement)grid.Rows[0].Tag!).GetProperty("id").GetString();
            AllControls(form).OfType<System.Windows.Forms.Button>().Single(button => button.AccessibleName == "历史下一页").PerformClick();
            System.Windows.Forms.Application.DoEvents();
            Check(((JsonElement)grid.Rows[0].Tag!).GetProperty("id").GetString() != first, "Older history remains accessible through paging");
            AllControls(form).OfType<System.Windows.Forms.Button>().Single(button => button.AccessibleName == "筛选记录").PerformClick();
            System.Windows.Forms.Application.DoEvents();
            foreach (var table in AllControls(form).OfType<System.Windows.Forms.DataGridView>())
                Check(table.Rows.Cast<System.Windows.Forms.DataGridViewRow>().Sum(row => row.Height) <= table.ClientSize.Height - table.ColumnHeadersHeight,
                    $"Filter expansion also remains scroll-free at {dpi} DPI");
            Save(form, Path.Combine(directory, $"router-filters-{dpi}.png"));
            AllControls(form).OfType<System.Windows.Forms.Button>().Single(button => button.AccessibleName == "筛选记录").PerformClick();
            form.SetPinned(false);
            Save(form, Path.Combine(directory, $"router-hover-{dpi}.png"));
            form.SetPinned(true);
            form.ApplyUnavailable("合成断线情形：保留原任务和回执，等待重新观察。");
            Check(form.FreshnessText.Contains("未确认"), "Unavailable is not idle");
            Save(form, Path.Combine(directory, $"router-unavailable-{dpi}.png"));
            form.Close();

            using var empty = new RouterMonitorForm(new RouterMonitorClient(new Fixture { Empty = true }), observe: false)
            { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000) };
            empty.SetPinned(true); empty.Show(); empty.RefreshViewAsync().GetAwaiter().GetResult();
            empty.ConfigureFor(new Rectangle(400, (int)(600 * scale), 48, 44), true, scale,
                QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), new Rectangle(0, 0, (int)(1280 * scale), (int)(800 * scale)));
            empty.PerformLayout(); System.Windows.Forms.Application.DoEvents();
            Save(empty, Path.Combine(directory, $"router-empty-{dpi}.png"));
            if (dpi == 144)
            {
                empty.ConfigureFor(new Rectangle(400, 900, 48, 44), true, scale, QuotaBackgroundPalette.Resolve("navy"), new Rectangle(0, 0, 1920, 1200));
                empty.PerformLayout(); Save(empty, Path.Combine(directory, "router-navy-144.png"));
            }
            empty.Close();
        }
    }
    private static IEnumerable<System.Windows.Forms.Control> AllControls(System.Windows.Forms.Control parent) =>
        parent.Controls.Cast<System.Windows.Forms.Control>().SelectMany(child => new[] { child }.Concat(AllControls(child)));

    private static MiniCardContribution Card(string status, long queued = 0) => new("card.router", RouterPlugin.Id,
        "router", ContributionKind.Metric, 0, "Router", "router.icon", "accent.router",
        [new("router.state", new("text", Text: "状态说明只在弹层出现"), status), new("router.queued", new("integer", Integer: queued))]);

    private static void PopoverInteraction()
    {
        Check(!BarForm.ShouldDismissPluginPopover(false, true, false, false, TimeSpan.FromSeconds(1)), "Anchor/popup pointer remains visible");
        Check(!BarForm.ShouldDismissPluginPopover(false, false, false, false, TimeSpan.FromMilliseconds(149)), "Shared hover grace period");
        Check(BarForm.ShouldDismissPluginPopover(false, false, false, false, TimeSpan.FromMilliseconds(150)), "Hover leaves both surfaces");
        Check(!BarForm.ShouldDismissPluginPopover(true, false, false, false, TimeSpan.FromSeconds(5)), "Pin survives pointer leave");
        Check(BarForm.ShouldDismissPluginPopover(true, false, true, false, null), "Outside click dismisses pin");
        Check(BarForm.ShouldDismissPluginPopover(true, true, false, true, null), "Escape dismisses pin");
        Check(TaskbarMiniLayoutMath.MinimumAreaContentWidthFor(MiniAreaIds.Router) == 32, "Icon module has independent compact minimum");
        Check(TaskbarMiniLayoutMath.AreaWidth(RouterCardRenderer.Width, false, MiniAreaIds.Router) < TaskbarMiniLayoutMath.ServiceCardWidth, "Router width is compact without text");

        var settings = new AppSettings();
        settings.PluginEnabled[MiniAreaIds.SystemMetrics] = false;
        using var bar = new BarForm(settings, QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: 96)
        { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000), ShowInTaskbar = false };
        using var popup = new RouterMonitorForm(new RouterMonitorClient(new Fixture()), observe: false)
        { Location = new(-30000, -30000) };
        var view = new PluginMiniCardView(RouterPlugin.Id, "Router", Card("running", 3), new Dictionary<string, string>());
        bar.SetPluginMiniCards([view]);
        bar.PluginPopoverRequested += (_, request) =>
        {
            popup.SetPinned(request.Pinned);
            if (!popup.Visible) popup.Show(bar);
            request.Popup = popup;
        };
        bar.Show();
        Check(bar.OpenPluginPopover(RouterPlugin.Id), "Existing bar dispatches native popup");
        Check(popup.Pinned && popup.Visible && !popup.ShowInTaskbar, "Click pins an owned popup without a taskbar window");
        bar.SetPluginMiniCards([view with { Card = Card("cleanup", 2) }]);
        Check(popup.Visible && popup.Pinned, "Data-only refresh preserves a pinned popup");
        bar.HidePluginPopover();
        Check(!popup.Visible, "Presentation hide never sends Router controls");
    }

    private static void CaptureIcons(string directory)
    {
        var states = new[] { "idle", "running", "cleanup", "waiting", "attention", "offline", "unknown" };
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            var scale = dpi / 96f;
            using var bitmap = new Bitmap((int)(states.Length * 76 * scale), (int)(76 * scale));
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(17, 21, 29));
            graphics.ScaleTransform(scale, scale);
            using var font = new Font("Segoe UI", 8, FontStyle.Regular, GraphicsUnit.Pixel);
            using var cardFill = new SolidBrush(Color.FromArgb(30, 41, 59));
            for (var i = 0; i < states.Length; i++)
            {
                var bounds = new RectangleF(i * 76 + 4, 4, 48, 36);
                graphics.FillRoundedRectangle(cardFill, bounds, 7);
                RouterCardRenderer.Draw(graphics, bounds, Card(states[i], i % 2 == 0 ? 0 : 3), font);
                graphics.DrawString(states[i], font, Brushes.LightSlateGray, i * 76 + 4, 48);
            }
            bitmap.Save(Path.Combine(directory, $"router-bar-icons-{dpi}.png"), ImageFormat.Png);
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
        internal bool LoseControlReply, OldApi, WrongProject, Stale, Oversized, Empty;
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
            if (route == "/api/projects") return Json(new[] { new { key = Project, name = "POB", pending = 41 }, new { key = new string('e', 64), name = "ZGSProject_5", pending = 2 }, new { key = new string('f', 64), name = "POB_Manual", pending = 0 } });
            var selectedProject = request.RequestUri.Query.Split('&').FirstOrDefault(part => part.TrimStart('?').StartsWith("project="))?.Split('=', 2)[1] ?? Project;
            if (route == "/api/view") return Json(new
            {
                projectKey = WrongProject ? new string('d', 64) : selectedProject,
                observedAt = DateTimeOffset.UtcNow.AddMinutes(Stale ? -1 : 0).ToUnixTimeMilliseconds() / 1000d,
                requestId = Empty ? "" : new string('a', 32), headline = Empty ? "空闲" : "清理中", task = Empty ? "—" : "合成原生运行与清理观察 · 保留完整长任务名",
                queuedCount = Empty ? 0 : 41, elapsed = "1:25", progressClock = Empty ? "" : "距原生变化 0:08", detail = Empty ? "" : "等待原生清理证据；当前任务输入保留。", tone = Empty ? "idle" : "warning",
                owner = new { label = Empty ? "" : "成长面板视觉验证" }, resourceRecovery = new { primary = new { action = "stop", label = "停止并收尾" } },
            });
            var parameters = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => part.Length == 2 ? part[1] : "");
            var offset = parameters.TryGetValue("offset", out var offsetText) ? int.Parse(offsetText) : 0;
            var limit = parameters.TryGetValue("limit", out var limitText) ? int.Parse(limitText) : 25;
            if (route == "/api/queue") return Json(new
            {
                offset, total = Empty ? 0 : 41,
                items = Enumerable.Range(offset, Empty ? 0 : Math.Min(limit, Math.Max(0, 41 - offset))).Select(i => new { id = i.ToString("x32"), task = "合成测试 · " + i + " · " + new string('长', 28), source = "原对话 " + i, wait = "2:10", rank = i + 1, last = false, version = new string('b', 64) }),
            });
            if (route == "/api/history") return Json(new
            {
                total = 76, offset,
                items = Enumerable.Range(offset, Math.Min(limit, Math.Max(0, 76 - offset))).Select(i => new { id = i.ToString("x32"), createdLocal = "2026-10-27T21:00:00+08:00", task = "独立测试与回执 " + i,
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
