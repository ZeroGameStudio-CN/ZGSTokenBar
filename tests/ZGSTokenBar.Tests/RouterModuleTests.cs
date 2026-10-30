using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
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
        ProjectOrdering();
        PopoverInteraction();
        TaskHeading();
        HistoryBuffer();
        PagingReads();
        WheelPaging();
        RefusalTiming();
        ExpandedSummary();
        QueueBadgeDigits();
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

    private static void TaskHeading()
    {
        const string original = "POB.Tests.Runtime.DesktopAspectLockTemporaryVerification.BootToBase_ThenVerifyScenario";
        var heading = RouterTaskPresentation.Heading(original);
        Check(heading.Title == "Boot To Base · Then Verify Scenario" && heading.Context == "Desktop Aspect Lock Temporary Verification"
            && heading.Original == original, "Current task is readable while retaining its exact identity");
        Check(RouterTaskPresentation.Heading("2/3 · POB.Fixture(\"a.b\").RunHTTPTest(1.5)").Title == "2/3 · Run HTTP Test(1.5)", "Stage and dotted parameters are preserved");
        Check(RouterTaskPresentation.Heading("D:/scripts/author-install-r2.py").Title == "author-install-r2.py", "Script file names are not treated as namespaces");
        Check(RouterTaskPresentation.Heading("编译").Title == "编译" && RouterTaskPresentation.Heading("—").Title == "暂无正在执行的任务", "Plain and idle task headings");
        Check(RouterTaskPresentation.Source("pro2 · \n" + new string('长', 100)).Length == 72, "Long source is one bounded line");
    }

    private static void RefusalTiming()
    {
        using var form = new RouterMonitorForm(new RouterMonitorClient(new Fixture { PreflightRefusal = true }), observe: false)
        { ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000) };
        form.Show(); Pump(form.RefreshViewAsync());
        var grid = AllControls(form).OfType<RouterGrid>().Single(table => table.Columns.Count == 6);
        Check(grid.Rows[0].Cells[3].Value?.ToString() == "派发前拒绝", "Starter refusals are distinct from assertion failures");
        Check(grid.Columns[4].HeaderText == "占用" && grid.Rows[0].Cells[4].ToolTipText.Contains("原生操作未执行")
            && grid.Rows[0].Cells[4].ToolTipText.Contains("派发恢复等待 2999s"), "Occupied time remains visible with the known preflight/recovery split");
        form.Close();
    }

    private static void ExpandedSummary()
    {
        using var handler = new Fixture();
        var plugin = new RouterPlugin(new RouterMonitorClient(handler));
        try
        {
            var snapshot = plugin.RefreshAsync(new PluginRefreshContext(DateTimeOffset.UtcNow, "test", 0), default).AsTask().GetAwaiter().GetResult();
            string Value(string key) => snapshot.MiniCards.Single().Summary.Single(item => item.LabelKey == key).Value.Text ?? "";
            Check(Value("router.project") == "POB" && Value("router.phase") == "清理" && Value("router.elapsed") == "1:25",
                "Expanded bar reads the public project, phase and elapsed metadata");
            handler.NativeError = true;
            snapshot = plugin.RefreshAsync(new PluginRefreshContext(DateTimeOffset.UtcNow, "test", 0), default).AsTask().GetAwaiter().GetResult();
            Check(Value("router.phase") == "有错误" && snapshot.MiniCards.Single().Summary.Single(item => item.LabelKey == "router.state").Status == "attention",
                "A native error observation has an attention icon even while the original operation continues");
            handler.NativeError = false;
            handler.Empty = true;
            snapshot = plugin.RefreshAsync(new PluginRefreshContext(DateTimeOffset.UtcNow, "test", 0), default).AsTask().GetAwaiter().GetResult();
            Check(Value("router.phase") == "空闲" && Value("router.elapsed") == "", "Idle expanded cards do not retain a running duration");
            handler.Stale = true;
            snapshot = plugin.RefreshAsync(new PluginRefreshContext(DateTimeOffset.UtcNow, "test", 0), default).AsTask().GetAwaiter().GetResult();
            Check(Value("router.phase") == "状态未确认" && Value("router.elapsed") == "", "Stale expanded data is explicitly unconfirmed");
            Check(handler.Posts == 0 && handler.Routes.All(uri => uri.AbsolutePath is "/health" or "/api/projects" or "/api/view"),
                "Expanded presentation needs no additional endpoints or execution controls");
        }
        finally { plugin.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void HistoryBuffer()
    {
        var buffer = new RouterHistoryBuffer();
        JsonElement Window(int offset, int total, int? count = null) => JsonSerializer.SerializeToElement(new
        { offset, total, items = Enumerable.Range(offset, count ?? Math.Min(100, Math.Max(0, total - offset))).Select(i => new { id = i }) });
        buffer.Store("instance/project/filter", Window(0, 206));
        Check(buffer.TryRead("instance/project/filter", 95, 5, out var slice) && slice!.Items.Count == 5
            && RouterJson.Integer(slice.Items[0], "id") == 95, "History window covers local pages through its boundary");
        Check(!buffer.TryRead("instance/project/filter", 96, 5, out _) && !buffer.TryRead("other/project/filter", 0, 5, out _)
            && !buffer.TryRead("instance/project/other-filter", 0, 5, out _), "Uncovered or differently bound history is never reused");
        buffer.Store("last", Window(200, 206));
        Check(buffer.TryRead("last", 204, 5, out slice) && slice!.Items.Count == 2, "Partial final page is retained");
        buffer.Store("empty", Window(0, 0));
        Check(buffer.TryRead("empty", 0, 5, out slice) && slice!.Items.Count == 0, "Empty filtered history is a valid snapshot");
        Throws<InvalidDataException>(() => buffer.Store("bad", Window(0, 206, 101)));
        Throws<InvalidDataException>(() => buffer.Store("short", Window(0, 206, 5)));
        buffer.Clear(); Check(!buffer.TryRead("empty", 0, 5, out _), "History reset removes prior bindings");
    }

    private static void PagingReads()
    {
        using var handler = new Fixture { HistoryTotal = 206 };
        using var form = new RouterMonitorForm(new RouterMonitorClient(handler), observe: false)
        { ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000) };
        form.SetPinned(true); form.Show();
        using var observer = new RouterMonitorClient(new Fixture());
        form.ApplySnapshot(observer.SnapshotAsync().GetAwaiter().GetResult());
        handler.HoldHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = form.HistoryAsync(); var shared = form.HistoryAsync();
        Check(handler.Routes.Count(uri => uri.AbsolutePath == "/api/history") == 1, "Overlapping page reads share one history request");
        handler.HoldHistory.SetResult(); Pump(Task.WhenAll(first, shared)); handler.HoldHistory = null;
        var history = AllControls(form).OfType<System.Windows.Forms.DataGridView>().Single(grid => grid.Columns.Count == 6);
        var next = AllControls(form).OfType<System.Windows.Forms.Button>().Single(button => button.AccessibleName == "历史下一页");
        var requests = handler.Routes.Count; var initial = RouterJson.Text((JsonElement)history.Rows[0].Tag!, "id");
        for (var i = 0; i < 8; i++) next.PerformClick();
        Check(handler.Routes.Count == requests && RouterJson.Text((JsonElement)history.Rows[0].Tag!, "id") != initial,
            "Adjacent history pages render synchronously without network reads");
        handler.HoldHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var obsolete = form.HistoryAsync(forceRefresh: true);
        AllControls(form).OfType<System.Windows.Forms.TextBox>().Single().Text = "latest & filter";
        var latest = form.HistoryAsync();
        handler.HoldHistory.SetResult(); Pump(Task.WhenAll(obsolete, latest)); handler.HoldHistory = null;
        Check(handler.ReadsCanceled == 1 && handler.Routes.Last().Query.Contains("latest%20%26%20filter")
            && !form.FreshnessText.Contains("中断"), "Changed filters supersede only obsolete reads without marking the Router offline");
        requests = handler.Routes.Count;
        form.ApplySnapshot(form.Snapshot! with { InstanceId = new string('b', 32) });
        Pump(form.HistoryAsync());
        Check(handler.Routes.Count == requests + 1, "Server instance changes invalidate the history window");

        Pump(form.RefreshViewAsync());
        var queue = AllControls(form).OfType<System.Windows.Forms.DataGridView>().Single(grid => grid.Columns.Count == 3);
        var queueNext = AllControls(form).OfType<System.Windows.Forms.Button>().Single(button => button.AccessibleName == "队列下一页");
        handler.HoldHealth = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = form.RefreshViewAsync();
        requests = handler.Routes.Count; queueNext.PerformClick();
        Check(handler.Routes.Count == requests + 1 && handler.Routes.Last().AbsolutePath == "/api/queue"
            && RouterJson.Integer((JsonElement)queue.Rows[0].Tag!, "rank") > 1, "Queue paging does not wait for background snapshot polling");
        handler.HoldHealth.SetResult(); Pump(background); handler.HoldHealth = null;
        handler.HoldQueue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldQueue = form.RefreshQueueAsync(); queueNext.PerformClick(); queueNext.PerformClick();
        var newestQueue = form.RefreshQueueAsync(userInitiated: true);
        handler.HoldQueue.SetResult(); Pump(Task.WhenAll(oldQueue, newestQueue)); handler.HoldQueue = null;
        Check(RouterJson.Integer((JsonElement)queue.Rows[0].Tag!, "rank") == 1 + queue.Rows.Count * 3,
            "Rapid queue paging keeps the newest page when older reads finish");
        Check(handler.Posts == 0, "Paging and cache work never send execution controls");
        form.Close();
    }

    private static void Pump(Task task)
    {
        var deadline = Stopwatch.StartNew();
        while (!task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(6))
        { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(1); }
        Check(task.IsCompleted, "Synthetic read completes within its deadline"); task.GetAwaiter().GetResult();
    }

    private static void WheelPaging()
    {
        using var handler = new Fixture { HistoryTotal = 206 };
        using var form = new RouterMonitorForm(new RouterMonitorClient(handler), observe: false)
        { ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000) };
        form.Show(); Pump(form.RefreshViewAsync());
        var grids = AllControls(form).OfType<RouterGrid>().ToArray();
        var queue = grids.Single(grid => grid.Columns.Count == 3); var history = grids.Single(grid => grid.Columns.Count == 6);
        string First(RouterGrid grid) => RouterJson.Text((JsonElement)grid.Rows[0].Tag!, "id");
        void Wheel(RouterGrid grid, int delta)
        {
            var args = new System.Windows.Forms.HandledMouseEventArgs(System.Windows.Forms.MouseButtons.None, 0, 10, 10, delta);
            typeof(RouterGrid).GetMethod("OnMouseWheel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(grid, [args]);
            Check(args.Handled, "Wheel paging consumes the native grid wheel event");
        }
        var original = First(history); var requests = handler.Routes.Count;
        Wheel(history, 120); Wheel(history, -60);
        Check(First(history) == original && handler.Routes.Count == requests, "First-page boundary and partial wheel deltas do not read or skip a page");
        Wheel(history, -60);
        Check(First(history) == history.Rows.Count.ToString("x32") && handler.Routes.Count == requests, "High-resolution wheel deltas accumulate into one cached history page");
        Wheel(history, -240);
        Check(First(history) == (history.Rows.Count * 3).ToString("x32"), "Multi-notch wheel input preserves page distance");
        var point = form.PointToClient(history.PointToScreen(new Point(10, 10)));
        var parentWheel = new System.Windows.Forms.HandledMouseEventArgs(System.Windows.Forms.MouseButtons.None, 0, point.X, point.Y, 120);
        typeof(RouterMonitorForm).GetMethod("OnMouseWheel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, [parentWheel]);
        Check(parentWheel.Handled && First(history) == (history.Rows.Count * 2).ToString("x32"), "Popup wheel delivery routes to the table at the event location");
        var historyPosition = First(history); Wheel(queue, -120);
        Check(RouterJson.Integer((JsonElement)queue.Rows[0].Tag!, "rank") == queue.Rows.Count + 1
            && First(history) == historyPosition && handler.Routes.Last().AbsolutePath == "/api/queue", "Queue wheel paging reads fresh data without moving history");
        Wheel(queue, -12000); var last = First(queue); requests = handler.Routes.Count; Wheel(queue, -120);
        Check(First(queue) == last && handler.Routes.Count == requests, "Last-page wheel input clamps without redundant reads");
        form.ApplyUnavailable("Synthetic disconnect"); requests = handler.Routes.Count; Wheel(history, -120); Wheel(queue, 120);
        Check(handler.Routes.Count == requests && handler.Posts == 0, "Wheel gestures neither act on unavailable data nor send controls");
        form.Close();
    }

    // Explicit opt-in: fixed loopback metadata only; no controls or production settings.
    internal static void Benchmark(string directory)
    {
        Directory.CreateDirectory(directory);
        using var handler = new ReadProbe();
        using var client = new RouterMonitorClient(handler);
        var snapshotTask = client.SnapshotAsync(); Pump(snapshotTask); var snapshot = snapshotTask.Result;
        var watch = Stopwatch.StartNew();
        var small = client.HistoryAsync(snapshot.ProjectKey, "all", "all", "all", "", 0, limit: 5); Pump(small);
        var fiveRowReadMilliseconds = watch.Elapsed.TotalMilliseconds;
        using var form = new RouterMonitorForm(client, observe: false)
        { ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000) };
        form.SetPinned(true); form.Show(); form.ApplySnapshot(snapshot);
        watch.Restart(); Pump(form.HistoryAsync()); var windowReadMilliseconds = watch.Elapsed.TotalMilliseconds;
        var history = AllControls(form).OfType<System.Windows.Forms.DataGridView>().Single(grid => grid.Columns.Count == 6);
        var next = AllControls(form).OfType<System.Windows.Forms.Button>().Single(button => button.AccessibleName == "历史下一页");
        var reads = handler.HistoryReads; var samples = new List<double>();
        var pages = Math.Min(16, Math.Max(0, Math.Min(100, (int)history.Tag!) / Math.Max(1, history.Rows.Count) - 1));
        for (var i = 0; i < pages; i++)
        {
            watch.Restart(); next.PerformClick(); history.Refresh(); System.Windows.Forms.Application.DoEvents();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        Check(samples.Count > 0 && handler.HistoryReads == reads, "Live history pages reuse the bounded read without HTTP");
        var sorted = samples.Order().ToArray();
        File.WriteAllText(Path.Combine(directory, "router-history-performance.json"), JsonSerializer.Serialize(new
        {
            measuredAt = DateTimeOffset.UtcNow, project = snapshot.ProjectKey, retainedRecords = (int)history.Tag!,
            fiveRowReadMilliseconds, windowReadMilliseconds, cachedPages = samples.Count, cachedPageHttpReads = handler.HistoryReads - reads,
            cachedPageMedianMilliseconds = sorted[sorted.Length / 2], cachedPageP95Milliseconds = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
            samplesMilliseconds = samples, handler.ControlRequests,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(handler.ControlRequests == 0, "Live benchmark sends no execution controls"); form.Close();
    }

    private sealed class ReadProbe : DelegatingHandler
    {
        internal int HistoryReads, ControlRequests;
        internal ReadProbe() : base(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) { ControlRequests++; throw new InvalidOperationException("Read-only benchmark"); }
            if (request.RequestUri!.AbsolutePath == "/api/history") HistoryReads++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static void ProjectOrdering()
    {
            var ordered = RouterMonitorForm.OrderedProjects(JsonSerializer.SerializeToElement(new[] {
                new { key = "temp", name = "A temporary", recorded = 1, pending = 20 },
                new { key = "main", name = "POB", recorded = 1000, pending = 0 },
                new { key = "tie-b", name = "B", recorded = 100, pending = 0 },
                new { key = "tie-a", name = "A", recorded = 100, pending = 0 } }));
            Check(ordered.Select(row => row.GetProperty("key").GetString()).SequenceEqual(new[] { "main", "tie-a", "tie-b", "temp" }), "Cumulative task volume ranks common projects ahead of temporary bursts, with deterministic ties");
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
            var taskTitle = AllControls(form).OfType<System.Windows.Forms.Label>().Single(label => label.Text == "Boot To Base · Then Verify Scenario");
            Check(System.Windows.Forms.TextRenderer.MeasureText(taskTitle.Text, taskTitle.Font).Width < taskTitle.ClientSize.Width
                && taskTitle.AccessibleDescription!.Contains("DesktopAspectLockTemporaryVerification"), $"Readable current task retains the original identity at {dpi} DPI");
            Check(AllControls(form).OfType<System.Windows.Forms.Label>().Any(label => label.Text.StartsWith("来源   pro2")
                && label.AccessibleDescription!.Length > label.Text.Length), "Source has its own line and a complete accessible description");
            var metrics = AllControls(form).OfType<System.Windows.Forms.Label>().Single(label => label.Text.StartsWith("运行 "));
            Check(metrics.Visible && metrics.Width > 180 * scale && metrics.Height >= metrics.Font.Height && metrics.Parent!.ClientRectangle.Contains(metrics.Bounds),
                $"Current execution timings remain visible at {dpi} DPI: {metrics.Bounds}, parent {metrics.Parent!.ClientSize}, text {metrics.Text}");
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
            var sections = AllControls(form).OfType<RouterSurface>().Where(surface => !surface.Input).ToArray();
            Check(sections.Select(section => section.AccessibleName).SequenceEqual(new[] { "当前", "排队", "历史" }), "Three named sections share one visible popup");
            Check(sections.Zip(sections.Skip(1)).All(pair => pair.First.Bottom < pair.Second.Top), "Current, queue and history have distinct gaps and do not overlap");
            Check(AllControls(form).OfType<RouterGrid>().All(table => table.Rows.Count >= 7), $"Taller popup exposes at least seven queue/history rows at {dpi} DPI: "
                + string.Join("; ", AllControls(form).OfType<RouterGrid>().Select(table => $"{table.Rows.Count} rows, height {table.ClientSize.Height}, header {table.ColumnHeadersHeight}")));
            Save(form, Path.Combine(directory, $"router-history-{dpi}.png"));
            var projectButtons = AllControls(form).OfType<RouterButton>().Where(button => button.AccessibleName?.StartsWith("切换工程 ") == true).ToArray();
            Check(projectButtons.Length == 3 && projectButtons.Count(button => button.Selected) == 1, "Registered projects use flat selected chips");
            Check(projectButtons[0].AccessibleName == "切换工程 POB", "Most-used project stays first even when the catalog arrives alphabetically");

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
        using var compact = new RouterMonitorForm(new RouterMonitorClient(new Fixture()), observe: false)
        { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000) };
        var smallArea = new Rectangle(0, 0, 1920, 1040);
        compact.ConfigureFor(new Rectangle(400, 980, 48, 44), true, 2,
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), smallArea);
        compact.Show(); compact.ConfigureFor(new Rectangle(400, 980, 48, 44), true, 2,
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), smallArea);
        Pump(compact.RefreshViewAsync()); System.Windows.Forms.Application.DoEvents();
        Check(smallArea.Contains(compact.Bounds), "Taller popup stays inside a short high-DPI desktop");
        foreach (var table in AllControls(compact).OfType<RouterGrid>())
            Check(table.Rows.Cast<System.Windows.Forms.DataGridViewRow>().Sum(row => row.Height) <= table.ClientSize.Height - table.ColumnHeadersHeight,
                "Short desktops retain bounded visible pages without scrollbars");
        Save(compact, Path.Combine(directory, "router-short-desktop-192.png")); compact.Close();
    }
    private static IEnumerable<System.Windows.Forms.Control> AllControls(System.Windows.Forms.Control parent) =>
        parent.Controls.Cast<System.Windows.Forms.Control>().SelectMany(child => new[] { child }.Concat(AllControls(child)));

    private static MiniCardContribution Card(string status, long queued = 0) => new("card.router", RouterPlugin.Id,
        "router", ContributionKind.Metric, 0, "Router", "router.icon", "accent.router",
        [new("router.state", new("text", Text: "状态说明只在弹层出现"), status), new("router.queued", new("integer", Integer: queued)),
         new("router.project", new("text", Text: "POB")), new("router.phase", new("text", Text: status switch { "idle" => "空闲", "cleanup" => "清理", _ => "测试" })),
         new("router.elapsed", new("text", Text: status == "idle" ? "" : "1:25"))]);

    private static void PopoverInteraction()
    {
        Check(!BarForm.ShouldDismissPluginPopover(false, true, false, false, TimeSpan.FromSeconds(1)), "Anchor/popup pointer remains visible");
        Check(!BarForm.ShouldDismissPluginPopover(false, false, false, false, TimeSpan.FromMilliseconds(149)), "Shared hover grace period");
        Check(BarForm.ShouldDismissPluginPopover(false, false, false, false, TimeSpan.FromMilliseconds(150)), "Hover leaves both surfaces");
        Check(!BarForm.ShouldDismissPluginPopover(true, false, false, false, TimeSpan.FromSeconds(5)), "Pin survives pointer leave");
        Check(BarForm.ShouldDismissPluginPopover(true, false, true, false, null), "Outside click dismisses pin");
        Check(BarForm.ShouldDismissPluginPopover(true, true, false, true, null), "Escape dismisses pin");
        Check(TaskbarMiniLayoutMath.MinimumAreaContentWidthFor(MiniAreaIds.Router) == 112, "Expanded Router summary has enough readable content width");
        Check(TaskbarMiniLayoutMath.AreaWidth(RouterCardRenderer.Width, true, MiniAreaIds.Router) < TaskbarMiniLayoutMath.ServiceCardWidth, "Collapsed Router remains an icon module");

        var settings = new AppSettings();
        settings.PluginEnabled[MiniAreaIds.SystemMetrics] = false;
        using var bar = new BarForm(settings, QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: 96)
        { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000), ShowInTaskbar = false };
        using var popup = new RouterMonitorForm(new RouterMonitorClient(new Fixture()), observe: false)
        { Location = new(-30000, -30000) };
        var view = new PluginMiniCardView(RouterPlugin.Id, "Router", Card("running", 3), new Dictionary<string, string>());
        bar.SetPluginMiniCards([view]);
        Check(bar.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Router).Collapsed, "New Router modules default to the compact icon");
        var compactWidth = bar.Width;
        Check(bar.SetMiniAreaFromCommand(MiniAreaIds.Router, false, null), "Existing module control expands the Router summary");
        Check(!bar.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Router).Collapsed && bar.Width > compactWidth,
            "Expanded and compact modes have different useful content widths");
        bar.SetMiniAreaLayouts(new Dictionary<string, MiniAreaLayout> { [MiniAreaIds.Router] = new(false, 48) });
        Check(bar.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Router) is { Collapsed: false, Width: 112 },
            "Saved expanded preference is preserved while old widths are normalized for the new content");
        bar.SetMiniAreaFromCommand(MiniAreaIds.Router, true, null);
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

    private static void QueueBadgeDigits()
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            byte[] Render(long count)
            {
                var scale = dpi / 96f;
                using var bitmap = new Bitmap((int)(34 * scale), (int)(36 * scale));
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.FromArgb(17, 21, 29)); graphics.ScaleTransform(scale, scale);
                using var font = new Font("Cascadia Mono", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
                RouterCardRenderer.Draw(graphics, new RectangleF(0, 0, 34, 36), Card("running", count), font);
                using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
            }
            Check(!Render(10).SequenceEqual(Render(11)), $"Two-digit queue counts retain their final digit at {dpi} DPI");
            Check(Render(100).SequenceEqual(Render(101)), $"Queue badge overflow remains capped at 99+ at {dpi} DPI");
        }
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
            using var font = new Font("Cascadia Mono", 8.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var cardFill = new SolidBrush(Color.FromArgb(30, 41, 59));
            for (var i = 0; i < states.Length; i++)
            {
                var bounds = new RectangleF(i * 76 + 4, 4, 48, 36);
                graphics.FillRoundedRectangle(cardFill, bounds, 7);
                RouterCardRenderer.Draw(graphics, bounds, Card(states[i], new[] { 0, 1, 9, 10, 11, 99, 100 }[i]), font);
                graphics.DrawString(states[i], font, Brushes.LightSlateGray, i * 76 + 4, 48);
            }
            bitmap.Save(Path.Combine(directory, $"router-bar-icons-{dpi}.png"), ImageFormat.Png);
            using var expanded = new Bitmap((int)(RouterCardRenderer.Width * 3 * scale), (int)(48 * scale));
            using var expandedGraphics = Graphics.FromImage(expanded); expandedGraphics.Clear(Color.FromArgb(17, 21, 29)); expandedGraphics.ScaleTransform(scale, scale);
            using var titleFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var detailFont = new Font("Microsoft YaHei UI", 9, FontStyle.Regular, GraphicsUnit.Pixel);
            foreach (var (state, index) in new[] { "running", "cleanup", "idle" }.Select((state, index) => (state, index)))
                RouterCardRenderer.Draw(expandedGraphics, new RectangleF(index * RouterCardRenderer.Width, 6, RouterCardRenderer.Width, 36), Card(state, state == "idle" ? 0 : 3), font, false, titleFont, detailFont);
            expanded.Save(Path.Combine(directory, $"router-bar-expanded-{dpi}.png"), ImageFormat.Png);
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
        internal int Posts, ReadsCanceled, HistoryTotal = 76;
        internal TaskCompletionSource? HoldHistory, HoldQueue, HoldHealth;
        internal JsonElement? LastControl;
        internal bool LoseControlReply, OldApi, WrongProject, Stale, Oversized, Empty, NativeError, PreflightRefusal;
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
            var gate = route switch { "/api/history" => HoldHistory, "/api/queue" => HoldQueue, "/health" => HoldHealth, _ => null };
            if (gate is not null)
            {
                try { await gate.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { ReadsCanceled++; throw; }
            }
            if (route == "/health")
            {
                if (Oversized) return new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1024 * 1024 + 1)) };
                return Json(new { apiVersion = OldApi ? 0 : 1, instanceId = new string('d', 32) });
            }
            if (route == "/api/projects") return Json(new[] { new { key = Project, name = "POB", pending = 41, recorded = 1000 }, new { key = new string('a', 64), name = "Temporary_Long_Project_Name", pending = 0, recorded = 1 }, new { key = new string('f', 64), name = "POB_Manual", pending = 0, recorded = 10 }, new { key = new string('e', 64), name = "ZGSProject_5", pending = 2, recorded = 100 } });
            var selectedProject = request.RequestUri.Query.Split('&').FirstOrDefault(part => part.TrimStart('?').StartsWith("project="))?.Split('=', 2)[1] ?? Project;
            if (route == "/api/view") return Json(new
            {
                projectKey = WrongProject ? new string('d', 64) : selectedProject,
                observedAt = DateTimeOffset.UtcNow.AddMinutes(Stale ? -1 : 0).ToUnixTimeMilliseconds() / 1000d,
                requestId = Empty ? "" : new string('a', 32), headline = Empty ? "空闲" : "清理中", task = Empty ? "—" : "POB.Tests.Runtime.DesktopAspectLockTemporaryVerification.BootToBase_ThenVerifyScenario",
                queuedCount = Empty ? 0 : 41, elapsed = "1:25", progressClock = Empty ? "" : "距原生变化 0:08", detail = Empty ? "" : "等待原生清理证据；当前任务输入保留。", tone = Empty ? "idle" : "warning",
                currentExecution = Empty ? null : new { phase = NativeError ? "running" : "settling", kind = "test" },
                progressState = NativeError ? "native_error" : "observing",
                owner = new { label = Empty ? "" : "pro2 · 现在问题在于 · 桌面宽高比与初始化流程验证 · " + new string('长', 80) }, resourceRecovery = new { primary = new { action = "stop", label = "停止并收尾" } },
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
                total = HistoryTotal, offset,
                items = Enumerable.Range(offset, Math.Min(limit, Math.Max(0, HistoryTotal - offset))).Select(i => new { id = i.ToString("x32"), createdLocal = "2026-10-27T21:00:00+08:00", task = "独立测试与回执 " + i,
                    source = "原对话 " + i, outcome = i % 3 == 0 ? "failed" : "completed", failureClass = PreflightRefusal && i == 0 ? "dispatch_preflight_failed" : i % 3 == 0 ? "validation_failed" : "", queueSeconds = 12.5,
                    preflightSeconds = PreflightRefusal && i == 0 ? (double?)1 : null, dispatchRecoveryWaitSeconds = PreflightRefusal && i == 0 ? (double?)2999 : null,
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
