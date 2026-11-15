using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Intake;
using ZGSTokenBar.Plugin.Router;

internal static class IntakeObserverTests
{
    private const string ItemId = "08e86d2a-3133-4473-8084-f7048c6bf128";
    private const string OtherId = "63ff7bd0-9985-46a2-a320-fe85f2dcd08c";
    private const string RecentJson = """
        {"items":[{"work_item_id":"08e86d2a-3133-4473-8084-f7048c6bf128","work_item_number":"ZOS-27",
        "project_key":"LAB","title":"Collision test report","status":"rework","execution":{"phase":"queued"},"updated_at":1794688800000},
        {"work_item_id":"63ff7bd0-9985-46a2-a320-fe85f2dcd08c","work_item_number":"ZOS-40",
        "project_key":"DENIED","title":"Do not project","status":"rework","execution":{"phase":"queued"}}]}
        """;
    private const string ItemJson = """
        {"item":{"work_item_id":"08e86d2a-3133-4473-8084-f7048c6bf128","work_item_number":"ZOS-27",
        "project_key":"LAB","title":"Collision test report","status":"pending_acceptance","execution":{"phase":"not_applicable"},
        "agent_evidence":"{\"mode\":\"verified_original_blocked_continuation\",\"thread_id\":\"c9c04d50-a821-4308-bf1a-f4aa97967ca6\"}",
        "updated_at":1794688800000}}
        """;

    internal static void Run(string? captures = null)
    {
        MiniEntry(captures);
        HoverHandoff(captures);
        PanelParity();
        RefreshSelectedAndRecent();
        var denied = false;
        var switched = false;
        var reads = new List<string>();
        var client = new IntakeObserverClient((args, _) =>
        {
            reads.Add(args[0]);
            if (args[0] == "plugin")
            {
                Check(args is ["plugin", "run", "logs.diagnostic-bundle", "--input", _], "Only the owned diagnosis query is requested");
                using var selector = JsonDocument.Parse(args[4]);
                Check(selector.RootElement.GetProperty("selector").GetProperty("id").GetString() == ItemId,
                    "Diagnostics are bound to the original card");
                if (denied) throw new IntakeReadException("denied");
                return Task.FromResult(Json($$$"""
                    {"ok":true,"result":{"query":{"work_item_id":"{{{ItemId}}}"},"status":"partial","authoritative_facts":[{}],
                    "diagnostic_events":[{"work_item_id":"{{{ItemId}}}","event_name":"http.request.completed","outcome":"success"},
                    {"work_item_id":"{{{OtherId}}}","event_name":"unrelated"}],"next_cursor":"fixture-cursor"}}
                    """));
            }
            if (args[0] == "list")
            {
                Check(args.Contains("LAB") && args.Contains("--limit") && !args.Contains("--all"),
                    "Project comes from access projection and history is bounded");
                return Task.FromResult(Json(RecentJson));
            }
            return Task.FromResult(Json(args[0] switch
            {
                "whoami" when switched => """
                    {"subject":"different-user","authorization_revision":"rev2","projects":[],"project_access":[]}
                    """,
                "whoami" => """
                    {"subject":"fixture-user","authorization_revision":"rev1","projects":[
                        {"project_key":"LAB","title":"Sandbox studio"},{"project_key":"SYS","title":"Operations"},
                        {"project_key":"DENIED","title":"Outside scope"}],"project_access":[
                        {"project_key":"LAB","capabilities":["work_item.read"]},
                        {"project_key":"SYS","capabilities":["work_item.read"]},
                        {"project_key":"DENIED","capabilities":["other.read"]}]}
                    """,
                "get" => ItemJson,
                "history" => """
                    {"transitions":[{"command":"rework","to_status":"rework","created_at":1794688200000},
                      {"command":"submit_result","to_status":"pending_acceptance","created_at":1794688800000}]}
                    """,
                _ => throw new InvalidOperationException("Unexpected command " + args[0]),
            }));
        });

        Check(!IntakeObserverClient.IsReference("--project-key") && !IntakeObserverClient.IsReference("ZOS-27 & pause"),
            "Lookup never accepts switches or shell input");
        var item = client.ItemAsync("ZOS-27", default).GetAwaiter().GetResult();
        Check(item.Thread.HasValue && item.EvidenceMode == "verified_original_blocked_continuation",
            "Original chat and recovery are recognized from structured evidence");
        using var form = new IntakeObserverForm(true, client, observeOnShow: false);
        if (captures is not null)
        {
            Directory.CreateDirectory(captures);
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            form.ConfigureFor(new Rectangle(area.Right - 300, area.Bottom - 1, 52, 1), true,
                System.Windows.Forms.Screen.PrimaryScreen!.Bounds.Width > 2500 ? 2 : 1,
                QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), area);
            form.Show();
            System.Windows.Forms.Application.DoEvents();
        }
        RunOnUi(form.LoadRecentAsync());
        Check(form.Observation is { Projects: 2, Recent: 1, ItemLoaded: false },
            "Projects use read permissions and cross-project rows do not appear");
        RunOnUi(form.LoadItemAsync("ZOS-27"));
        Check(form.Observation is { Timeline: 4, ItemLoaded: true }, "Current state, verified recovery, and two transitions");
        RunOnUi(form.LoadDiagnosticsAsync());
        Check(form.Observation.Timeline == 5 && form.Observation.Status.Contains("部分记录"),
            "Only the selected item's diagnostic rows appear and partial is visible");
        if (captures is not null)
        {
            form.PerformLayout();
            System.Windows.Forms.Application.DoEvents();
            var labels = Controls(form).OfType<System.Windows.Forms.Label>().Select(label =>
                new { label.Text, label.Bounds, label.Visible, Parent = label.Parent?.GetType().Name,
                    Font = label.Font.Size, ParentSize = label.Parent?.ClientSize });
            Check(Controls(form).OfType<System.Windows.Forms.Label>().Where(label =>
                label.Text is "当前工单" or "近期工单" or "历史" or "项目").All(label =>
                    label.Parent!.ClientRectangle.Contains(label.Bounds)), "Section headings fit inside their shared header rows");
            File.WriteAllText(Path.Combine(captures, "panel-label-layout.json"), JsonSerializer.Serialize(labels));
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            using var graphics = Graphics.FromImage(bitmap);
            var target = graphics.GetHdc();
            try { Check(PrintWindow(form.Handle, target, 1), "The fixture's client window renders"); }
            finally { graphics.ReleaseHdc(target); }
            bitmap.Save(Path.Combine(captures, "work-intake-preview.png"));
        }
        denied = true;
        RunOnUi(form.LoadDiagnosticsAsync());
        Check(form.Observation is { Timeline: 4, ItemLoaded: true } && form.Observation.Status.Contains("无诊断权限"),
            "Diagnostic denial does not discard authorized business history");
        switched = true;
        RunOnUi(form.LoadRecentAsync());
        Check(form.Observation is { Projects: 0, Recent: 0, Timeline: 0, ItemLoaded: false },
            "Authorization changes clear the previous account's contents");
        Check(reads.All(read => read is "whoami" or "get" or "list" or "history" or "plugin"),
            "No write requests");
    }

    private static IEnumerable<System.Windows.Forms.Control> Controls(System.Windows.Forms.Control parent) =>
        parent.Controls.Cast<System.Windows.Forms.Control>().SelectMany(control => new[] { control }.Concat(Controls(control)));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint window, nint target, uint flags);

    private static void RunOnUi(Task operation)
    {
        var end = Environment.TickCount64 + 5_000;
        while (!operation.IsCompleted && Environment.TickCount64 < end)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(5);
        }
        Check(operation.IsCompleted, "The isolated read must complete on the UI thread");
        operation.GetAwaiter().GetResult();
    }

    private static void RefreshSelectedAndRecent()
    {
        var revision = 1;
        var listReads = 0;
        var getReads = 0;
        var revoked = false;
        var client = new IntakeObserverClient((args, _) =>
        {
            string body;
            switch (args[0])
            {
                case "whoami":
                    body = revoked
                        ? """{"subject":"other","authorization_revision":"revoked","projects":[],"project_access":[]}"""
                        : """{"subject":"fixture","authorization_revision":"same","projects":[{"project_key":"LAB","title":"Lab"}],"project_access":[{"project_key":"LAB","capabilities":["work_item.read"]}]}""";
                    break;
                case "list":
                    listReads++;
                    body = JsonSerializer.Serialize(new { items = Enumerable.Range(1, 16).Select(index => new {
                        work_item_id = index == 1 ? ItemId : $"00000000-0000-4000-8000-{index:000000000000}",
                        work_item_number = $"ZOS-{index}", project_key = "LAB", title = $"item {index} v{revision}",
                        status = "rework", updated_at = 1794688800000L + revision }) });
                    break;
                case "get":
                    getReads++;
                    body = JsonSerializer.Serialize(new { item = new { work_item_id = ItemId, work_item_number = "ZOS-1",
                        project_key = "LAB", title = $"selected v{revision}", status = "pending_acceptance",
                        updated_at = 1794688800000L + revision } });
                    break;
                case "history": body = """{"transitions":[]}"""; break;
                default: throw new InvalidOperationException("Unexpected fixture request");
            }
            return Task.FromResult(Json(body));
        });
        using var form = new IntakeObserverForm(true, client, observeOnShow: false) { Opacity = 0 };
        form.ConfigureFor(new(400, 1000, 48, 44), true, 1,
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), new(0, 0, 1280, 1032));
        form.Show();
        RunOnUi(form.LoadRecentAsync());
        RunOnUi(form.LoadItemAsync(ItemId));
        System.Windows.Forms.Application.DoEvents();
        var grid = Controls(form).OfType<RouterGrid>().Single(table => table.Columns.Count == 4);
        grid.PageFromWheel(-System.Windows.Forms.SystemInformation.MouseWheelScrollDelta);
        System.Windows.Forms.Application.DoEvents();
        var firstVisible = grid.Rows.Cast<System.Windows.Forms.DataGridViewRow>().First(row => row.Visible);
        var visibleId = ((IntakeItem)firstVisible.Tag!).Id;
        var rowObject = grid.Rows[0];
        var beforeList = listReads; var beforeGet = getReads;
        revision = 2;
        RunOnUi(form.RefreshVisibleAsync());
        System.Windows.Forms.Application.DoEvents();
        Check(listReads == beforeList + 1 && getReads == beforeGet + 1 && form.Observation.ItemLoaded,
            "One visible refresh reads both recent items and the selected item");
        Check(grid.Rows[0].Cells[1].Value?.ToString() == "item 1 v2"
            && Controls(form).OfType<System.Windows.Forms.Label>().Any(label => label.Text.Contains("selected v2")),
            "Both table data and selected details advance to the new revision");
        Check(ReferenceEquals(rowObject, grid.Rows[0])
            && ((IntakeItem)grid.Rows.Cast<System.Windows.Forms.DataGridViewRow>().First(row => row.Visible).Tag!).Id == visibleId,
            "Ordinary refresh preserves row objects and the visible page");
        revoked = true;
        RunOnUi(form.RefreshVisibleAsync());
        Check(form.Observation is { Projects: 0, Recent: 0, Timeline: 0, ItemLoaded: false },
            "Revoked access clears both refreshed surfaces and their selection");
    }

    private static void PanelParity()
    {
        using var intake = new IntakeObserverForm(true, observeOnShow: false);
        using var router = new RouterMonitorForm(observe: false);
        var theme = QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette);
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        foreach (var area in new[] { new Rectangle(0, 0, 3840, 2072), new Rectangle(0, 0, 1920, 1040) })
        {
            var anchor = new Rectangle(area.Right - 450, area.Bottom, 52, 52);
            intake.ConfigureFor(anchor, true, scale, theme, area);
            router.ConfigureFor(anchor, true, scale, theme, area);
            Check(intake.Bounds == router.Bounds && area.Contains(intake.Bounds),
                "Both click panels use exactly the same anchored size and short-screen placement");
            Check(intake.FormBorderStyle == System.Windows.Forms.FormBorderStyle.None && !intake.ShowInTaskbar
                && intake.TopMost && intake.Pinned, "Work intake uses the Router panel host");
            var first = intake.Controls.OfType<System.Windows.Forms.TableLayoutPanel>().Single();
            var second = router.Controls.OfType<System.Windows.Forms.TableLayoutPanel>().Single();
            Check(first.RowCount == second.RowCount && first.RowStyles.Cast<System.Windows.Forms.RowStyle>()
                .Zip(second.RowStyles.Cast<System.Windows.Forms.RowStyle>()).All(rows =>
                    rows.First.SizeType == rows.Second.SizeType && Math.Abs(rows.First.Height - rows.Second.Height) < 1),
                "Navigation, current item, tables and footer use one layout at every DPI");
        }
    }

    private static void MiniEntry(string? captures)
    {
        var plugin = new IntakeObserverPlugin();
        Check(plugin.Manifest.DefaultEnabled && !plugin.Manifest.Required && plugin.Manifest.Requires.Count == 0,
            "Work intake is independently enabled and optional");
        using var bar = new BarForm(new AppSettings(), QuotaSnapshot.Empty(DateTimeOffset.UtcNow),
            renderOnly: true, renderDpi: 192);
        var router = new PluginMiniCardView(RouterPlugin.Id, "Router", RouterModuleTests.Card("idle", 0),
            new Dictionary<string, string>());
        var intake = new PluginMiniCardView(plugin.Manifest.Id, "自动分拣", IntakeObserverPlugin.Launcher,
            new Dictionary<string, string>());
        var clicks = new List<string>();
        bar.PluginActivated += (_, id) => clicks.Add(id);

        bar.SetPluginMiniCards([router]);
        var plainWidth = bar.ClientSize.Width;
        Paint();
        var routerBefore = bar.MiniPluginBounds(RouterPlugin.Id);
        Check(routerBefore is not null && bar.MiniPluginBounds(plugin.Manifest.Id) is null,
            "No intake hit area when switched off");

        bar.SetPluginMiniCards([router, intake]);
        Paint(captures);
        var current = bar.MiniPluginBounds(plugin.Manifest.Id) ?? throw new InvalidOperationException("Intake slot is missing");
        var moduleWidth = (GlassDockLayout.MonitorSize + GlassDockLayout.ModuleGap) * 2;
        Check(bar.ClientSize.Width - plainWidth == moduleWidth
              && bar.MiniPluginBounds(RouterPlugin.Id) == routerBefore
              && current.Size == routerBefore!.Value.Size && current.Top == routerBefore.Value.Top
              && current.Left == routerBefore!.Value.Right + GlassDockLayout.ModuleGap,
            "The two adjacent icons share the same size and baseline without shifting Router");
        bar.ActivateMiniTarget(Center(current));
        Check(clicks.SequenceEqual([plugin.Manifest.Id]), "The intake hit opens only the intake target");

        bar.SetPluginMiniCards([intake]);
        Paint();
        Check(bar.ClientSize.Width == plainWidth && bar.MiniPluginBounds(RouterPlugin.Id) is null
              && bar.MiniPluginBounds(plugin.Manifest.Id) == routerBefore,
            "Intake alone uses the first slot; no empty Router slot remains");
        bar.SetPluginMiniCards([]);
        Paint();
        Check(bar.ClientSize.Width == plainWidth - moduleWidth
              && bar.MiniPluginBounds(plugin.Manifest.Id) is null,
            "Each disabled module releases its full slot");
        bar.SetPluginMiniCards([router]);
        Paint();
        Check(bar.ClientSize.Width == plainWidth && bar.MiniPluginBounds(RouterPlugin.Id) == routerBefore
              && bar.MiniPluginBounds(plugin.Manifest.Id) is null,
            "Turning intake off restores the original bar layout");

        void Paint(string? directory = null)
        {
            using var image = new Bitmap(bar.ClientSize.Width, bar.ClientSize.Height);
            bar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            if (directory is null) return;
            Directory.CreateDirectory(directory);
            image.Save(Path.Combine(directory, "mini-intake-beside-router.png"));
        }

        static PointF Center(RectangleF bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    }

    private static void HoverHandoff(string? captures)
    {
        using var bar = new BarForm(new AppSettings { EnableAnimations = false, Locale = "zh-CN" },
            QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: 192)
        { StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new(-30000, -30000), ShowInTaskbar = false };
        using var routerPanel = new RouterMonitorForm(observe: false) { Opacity = 0 };
        using var intakePanel = new IntakeObserverForm(true, observeOnShow: false) { Opacity = 0 };
        var router = new PluginMiniCardView(RouterPlugin.Id, "Router", RouterModuleTests.Card("idle", 0),
            new Dictionary<string, string>());
        var intake = new PluginMiniCardView(IntakeObserverPlugin.Id, "自动分拣", IntakeObserverPlugin.Launcher,
            new Dictionary<string, string>());
        var requests = 0;
        var activations = 0;
        bar.PluginActivated += (_, _) => activations++;
        bar.PluginPopoverRequested += (_, request) =>
        {
            requests++;
            MonitorPanelForm panel = request.PluginId == IntakeObserverPlugin.Id ? intakePanel : routerPanel;
            panel.ShowFor(bar, request.Anchor, request.Pinned, request.Scale, request.Theme);
            request.Popup = panel;
        };
        routerPanel.PinChanged += (_, pinned) => bar.PinPluginPopover(pinned);
        intakePanel.PinChanged += (_, pinned) => bar.PinPluginPopover(pinned);
        routerPanel.CloseRequested += (_, _) => bar.HidePluginPopover(RouterPlugin.Id);
        intakePanel.CloseRequested += (_, _) => bar.HidePluginPopover(IntakeObserverPlugin.Id);
        bar.SetPluginMiniCards([router, intake]);
        bar.Show();
        using var image = new Bitmap(bar.ClientSize.Width, bar.ClientSize.Height);
        bar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        var routerBounds = bar.MiniPluginBounds(RouterPlugin.Id)!.Value;
        var intakeBounds = bar.MiniPluginBounds(IntakeObserverPlugin.Id)!.Value;
        var routerPoint = Center(routerBounds);
        var intakePoint = Center(intakeBounds);
        var routerScreen = Center(bar.QuotaTargetScreenBounds(routerBounds));
        var intakeScreen = Center(bar.QuotaTargetScreenBounds(intakeBounds));
        var outside = new Point(10000, 10000);
        var now = DateTime.UtcNow;

        bar.TrackMiniHover(routerPoint);
        bar.TrackMiniHover(intakePoint);
        bar.ShowHoveredPopover(routerScreen);
        Check(bar.VisiblePluginPopoverId is null && requests == 0, "A superseded hover cannot open the previous panel");
        bar.ShowHoveredPopover(intakeScreen);
        Check(intakePanel.Visible && !intakePanel.Pinned && activations == 0,
            "Intake hover opens its complete unpinned panel through the common host");
        var intakeSize = intakePanel.Size;
        bar.TrackMiniHover(new PointF(-1, -1));
        bar.MonitorPluginPopover(outside, false, false, now);
        bar.SetSnapshot(QuotaSnapshot.Empty(DateTimeOffset.UtcNow));
        bar.MonitorPluginPopover(outside, false, false, now.AddMilliseconds(149));
        Check(intakePanel.Visible, "The shared leave grace allows travel into the panel");
        bar.MonitorPluginPopover(outside, false, false, now.AddMilliseconds(150));
        Check(!intakePanel.Visible, "Leaving the icon and panel dismisses the full hover");

        bar.TrackMiniHover(routerPoint);
        bar.ShowHoveredPopover(routerScreen);
        Check(routerPanel.Visible && !routerPanel.Pinned && routerPanel.Size == intakeSize,
            "Router hover opens the same full-sized layout");
        bar.TrackMiniHover(intakePoint);
        Check(!routerPanel.Visible, "Changing icons closes the previous full hover");
        bar.ShowHoveredPopover(intakeScreen);
        var handle = intakePanel.Handle;
        bar.ActivateMiniTarget(intakePoint);
        Check(intakePanel.Visible && intakePanel.Pinned && intakePanel.Handle == handle
            && intakePanel.Size == intakeSize, "Click pins the same panel without another layout or window");
        var previousRequests = requests;
        bar.TrackMiniHover(routerPoint); bar.ShowHoveredPopover(routerScreen);
        Check(intakePanel.Visible && requests == previousRequests, "A pinned panel is not replaced by incidental hover");
        bar.ActivateMiniTarget(routerPoint);
        Check(routerPanel.Visible && routerPanel.Pinned && !intakePanel.Visible, "An explicit click switches the pinned panel");
        Check(routerPanel.HandlePanelShortcut(System.Windows.Forms.Keys.Escape) && !routerPanel.Visible,
            "Escape uses the common close action for a pinned panel");

        bar.TrackMiniHover(intakePoint); bar.ShowHoveredPopover(intakeScreen);
        bar.MonitorPluginPopover(intakeScreen, false, true, DateTime.UtcNow);
        Check(!intakePanel.Visible, "Escape also dismisses an unpinned full hover");
        bar.TrackMiniHover(intakePoint); bar.ShowHoveredPopover(intakeScreen);
        bar.SetPluginMiniCards([router]);
        Check(!intakePanel.Visible && bar.VisiblePluginPopoverId is null,
            "Disabling the module closes its pinned or hovering panel");
        bar.Close();

        static Point Center(RectangleF bounds) =>
            new((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
    }
    private static JsonElement Json(string value)
    {
        using var doc = JsonDocument.Parse(value);
        return doc.RootElement.Clone();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
