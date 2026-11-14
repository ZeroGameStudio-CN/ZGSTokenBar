using System.Drawing;
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
            form.Show();
        }
        form.LoadRecentAsync().GetAwaiter().GetResult();
        Check(form.Observation is { Projects: 2, Recent: 1, ItemLoaded: false },
            "Projects use read permissions and cross-project rows do not appear");
        form.LoadItemAsync("ZOS-27").GetAwaiter().GetResult();
        Check(form.Observation is { Timeline: 4, ItemLoaded: true }, "Current state, verified recovery, and two transitions");
        form.LoadDiagnosticsAsync().GetAwaiter().GetResult();
        Check(form.Observation.Timeline == 5 && form.Observation.Status.Contains("部分记录"),
            "Only the selected item's diagnostic rows appear and partial is visible");
        if (captures is not null)
        {
            form.PerformLayout();
            System.Windows.Forms.Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(captures, "work-intake-preview.png"));
        }
        denied = true;
        form.LoadDiagnosticsAsync().GetAwaiter().GetResult();
        Check(form.Observation is { Timeline: 4, ItemLoaded: true } && form.Observation.Status.Contains("无诊断权限"),
            "Diagnostic denial does not discard authorized business history");
        switched = true;
        form.LoadRecentAsync().GetAwaiter().GetResult();
        Check(form.Observation is { Projects: 0, Recent: 0, Timeline: 0, ItemLoaded: false },
            "Authorization changes clear the previous account's contents");
        Check(reads.All(read => read is "whoami" or "get" or "list" or "history" or "plugin"),
            "No write requests");
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
        Check(bar.ClientSize.Width - plainWidth == (GlassDockLayout.IntakeSize + GlassDockLayout.ModuleGap) * 2
              && bar.MiniPluginBounds(RouterPlugin.Id) == routerBefore
              && current.Left == routerBefore!.Value.Right + GlassDockLayout.ModuleGap,
            "Enabling intake leaves Router in place and gives intake a separate slot");
        bar.ActivateMiniTarget(Center(current));
        Check(clicks.SequenceEqual([plugin.Manifest.Id]), "The intake hit opens only the intake target");

        bar.SetPluginMiniCards([intake]);
        Paint();
        Check(bar.MiniPluginBounds(RouterPlugin.Id) is null && bar.MiniPluginBounds(plugin.Manifest.Id) == current,
            "Turning Router off does not remove the intake launcher");
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
        using var routerHover = new RouterHoverCardForm();
        using var routerPanel = new System.Windows.Forms.Form { ShowInTaskbar = false, Location = new(-30000, -30000) };
        var router = new PluginMiniCardView(RouterPlugin.Id, "Router", RouterModuleTests.Card("idle", 0),
            new Dictionary<string, string>());
        var intake = new PluginMiniCardView(IntakeObserverPlugin.Id, "自动分拣", IntakeObserverPlugin.Launcher,
            new Dictionary<string, string>());
        var requests = 0;
        var activated = new List<string>();
        bar.PluginActivated += (_, id) => activated.Add(id);
        bar.PluginPopoverRequested += (_, request) =>
        {
            Check(request.PluginId == RouterPlugin.Id, "The intake hint does not invoke a plugin or open its panel");
            requests++;
            if (request.Pinned)
            {
                if (!routerPanel.Visible) routerPanel.Show(bar);
                request.Popup = routerPanel;
                return;
            }
            routerHover.ShowFor(bar, request.Anchor, router.Card, true, request.Scale);
            request.Popup = routerHover;
        };
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
        Check(bar.VisiblePluginPopoverId is null && requests == 0, "A pending hover cannot open at the previous icon");
        bar.ShowHoveredPopover(intakeScreen);
        Check(bar.VisiblePluginPopoverId == IntakeObserverPlugin.Id && activated.Count == 0,
            "Intake shows its own hint without activation");
        bar.TrackMiniHover(new PointF(-1, -1));
        bar.MonitorPluginPopover(outside, false, false, now);
        bar.SetSnapshot(QuotaSnapshot.Empty(DateTimeOffset.UtcNow));
        bar.MonitorPluginPopover(outside, false, false, now.AddMilliseconds(149));
        Check(bar.VisiblePluginPopoverId == IntakeObserverPlugin.Id, "Moving through the gap keeps the brief leave grace");
        bar.MonitorPluginPopover(outside, false, false, now.AddMilliseconds(150));
        Check(bar.VisiblePluginPopoverId is null, "Refreshing the bar does not prolong a departing intake hint");

        bar.TrackMiniHover(routerPoint);
        bar.ShowHoveredPopover(routerScreen);
        Check(bar.VisiblePluginPopoverId == RouterPlugin.Id && requests == 1, "Router uses its own hover card");
        bar.TrackMiniHover(intakePoint);
        Check(bar.VisiblePluginPopoverId is null && !routerHover.Visible, "Changing icons dismisses the old hover before opening the next");
        bar.ShowHoveredPopover(intakeScreen);
        bar.ActivateMiniTarget(intakePoint);
        Check(bar.VisiblePluginPopoverId is null && activated.SequenceEqual([IntakeObserverPlugin.Id]),
            "Click dismisses the hint and opens only the independent intake panel");

        bar.ActivateMiniTarget(routerPoint);
        bar.TrackMiniHover(intakePoint);
        bar.ShowHoveredPopover(intakeScreen);
        Check(routerPanel.Visible && bar.VisiblePluginPopoverId == RouterPlugin.Id && requests == 2,
            "Hover does not replace a pinned Router panel");
        bar.ActivateMiniTarget(intakePoint);
        Check(!routerPanel.Visible && activated.Count == 2, "An explicit intake click switches panels");

        bar.TrackMiniHover(intakePoint);
        bar.ShowHoveredPopover(intakeScreen);
        bar.SetPluginMiniCards([router]);
        bar.ShowHoveredPopover(intakeScreen);
        Check(bar.VisiblePluginPopoverId is null, "Disabling intake clears its visible and pending hover");
        bar.Close();

        if (captures is not null)
        {
            Directory.CreateDirectory(captures);
            using var hint = new TaskbarHintPopoverForm();
            var text = NativeText.For("zh-CN");
            using var bitmap = hint.RenderForTest(text.IntakeObserver, text.IntakeObserverHint,
                QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), 192);
            bitmap.Save(Path.Combine(captures, "intake-hover-192.png"));
        }

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
