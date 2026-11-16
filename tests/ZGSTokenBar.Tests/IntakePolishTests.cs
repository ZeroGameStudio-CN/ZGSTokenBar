using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class IntakePolishTests
{
    private const string First = "08e86d2a-3133-4473-8084-f7048c6bf128";
    private const string Second = "63ff7bd0-9985-46a2-a320-fe85f2dcd08c";
    private const string Access = """{"subject":"fixture","authorization_revision":"r1","projects":[{"project_key":"LAB","title":"Lab"}],"project_access":[{"project_key":"LAB","capabilities":["work_item.read"]}]}""";

    internal static void Run(string? captures = null)
    {
        var context = SynchronizationContext.Current;
        try { QueryScopes(); EmptyStates(captures); SequentialSelection(); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static void QueryScopes()
    {
        var reads = new List<string[]>();
        var client = new IntakeObserverClient((args, _) => { reads.Add(args.ToArray()); return Task.FromResult(Json("""{"items":[]} """)); });
        var access = new IntakeAccess("fixture", "r1", null, [new("LAB", "Lab"), new("SYS", "Sys")]);
        foreach (var view in Enum.GetValues<IntakeItemsView>())
        {
            reads.Clear(); client.ItemsAsync(access, null, view, true, default).GetAwaiter().GetResult();
            Check(reads.Count == 2 && reads.All(a => a[0] == "list" && a[1] == "--project-key" && a.Contains("--limit")
                && a.Contains("--blocked") && !a.Contains("--all")), "Every queue query stays bounded and scoped to readable projects.");
            Check(reads.All(a => view switch {
                IntakeItemsView.Triage => a.Contains("--status") && a.Contains("pending_triage"),
                IntakeItemsView.Pending => a.Contains("--execution") && a.Contains("pending"),
                IntakeItemsView.Running => a.Contains("--execution") && a.Contains("running"),
                _ => !a.Contains("--execution") && !a.Contains("--status") }), "Queue filtering is performed by the authoritative endpoint, not a capped recent list.");
        }
        var denied = false; var before = reads.Count;
        try { client.ItemsAsync(access, "OUTSIDE", IntakeItemsView.Recent, false, default).GetAwaiter().GetResult(); }
        catch (IntakeReadException error) when (error.Code == "denied") { denied = true; }
        Check(denied && reads.Count == before, "A project outside read access is rejected before invoking the CLI.");
        var invalid = new IntakeObserverClient((_, _) => Task.FromResult(Json("{}")));
        var rejected = false;
        try { invalid.RecentAsync("LAB", false, default).GetAwaiter().GetResult(); }
        catch (IntakeReadException error) when (error.Code == "invalid") { rejected = true; }
        Check(rejected, "Missing list data cannot become a successful empty queue.");
    }

    private static void EmptyStates(string? captures)
    {
        var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = false;
        var client = new IntakeObserverClient((args, _) => args[0] == "whoami" ? Task.FromResult(Json(Access))
            : fail ? Task.FromException<JsonElement>(new IntakeReadException("unavailable")) : waiting.Task);
        using var form = new IntakeObserverForm(true, client, observeOnShow: false) { Opacity = 0 };
        var grids = Children(form).OfType<RouterGrid>().ToArray();
        var recent = grids.Single(g => g.Columns.Count == 4); var history = grids.Single(g => g.Columns.Count == 3);
        Check(history.EmptyTitle == "尚未选择工单" && !history.EmptyDetail.Contains("新的任务"), "An unselected history is not presented as an empty execution queue.");
        var operation = form.LoadRecentAsync();
        Check(recent.EmptyTitle.Contains("读取") && form.Observation.Status.Contains("读取"), "Pending data is explicitly loading, not an empty queue.");
        Capture("loading");
        waiting.SetResult(Json("""{"items":[]} """)); Wait(operation);
        Check(recent.EmptyTitle == "没有匹配的近期工单", "A successful empty recent view has its own state.");
        Wait(form.SetItemsViewAsync(IntakeItemsView.Pending));
        Check(recent.EmptyTitle == "当前没有待执行工单", "The pending queue's empty message matches its selected source.");
        Capture("empty-pending");
        fail = true; Wait(form.RefreshVisibleAsync());
        Check(recent.EmptyTitle == "工单暂不可用" && form.Observation.Status.Contains("暂不可用"), "An unavailable source cannot be rendered as a successful empty queue.");
        Capture("unavailable");

        void Capture(string name)
        {
            if (captures is null) return;
            Directory.CreateDirectory(captures);
            foreach (var scale in new[] { 1f, 1.5f, 2f })
            {
                form.ConfigureFor(new(-29400, -27800, 52, 52), false, scale,
                    QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), new(-30000, -30000, 3840, 2160));
                if (!form.Visible) form.Show();
                form.PerformLayout(); Application.DoEvents();
                foreach (var control in Children(form).Where(c => c.Visible && c is RouterButton or CheckBox))
                    if (control.Parent is FlowLayoutPanel flow)
                        Check(flow.ClientRectangle.Contains(control.Bounds), "Monitor toolbar controls must fit their row at each DPI.");
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(captures, $"intake-{name}-{(int)(96 * scale)}dpi.png"));
            }
        }
    }

    private static void SequentialSelection()
    {
        var gets = new List<string>();
        var client = new IntakeObserverClient((args, _) =>
        {
            if (args[0] == "whoami") return Task.FromResult(Json(Access));
            if (args[0] == "list") return Task.FromResult(Json(JsonSerializer.Serialize(new { items = new[] { Item(First, "First"), Item(Second, "Second") } })));
            if (args[0] == "get") { gets.Add(args[1]); return Task.FromResult(Json(JsonSerializer.Serialize(new { item = Item(args[1], args[1] == First ? "First" : "Second") }))); }
            if (args[0] == "history") return Task.FromResult(Json("""{"transitions":[]} """));
            throw new InvalidOperationException("Unexpected read.");
        });
        using var form = new IntakeObserverForm(true, client, observeOnShow: false) { Opacity = 0 };
        form.SetPinned(true); Wait(form.LoadRecentAsync());
        var grid = Children(form).OfType<RouterGrid>().Single(g => g.Columns.Count == 4);
        foreach (var index in new[] { 0, 1 })
            typeof(DataGridView).GetMethod("OnCellClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(grid, [new DataGridViewCellEventArgs(0, index)]);
        Check(gets.SequenceEqual(new[] { First, Second }) && form.Observation.ItemLoaded
            && Children(form).OfType<Label>().Any(l => l.Text.Contains("Second")),
            "A single click selects an item, and loading its history does not disable selecting the next item.");
        static object Item(string id, string title) => new { work_item_id = id, work_item_number = id == First ? "ZOS-1" : "ZOS-2",
            project_key = "LAB", title, status = "pending_execution", execution = new { phase = "queued" } };
    }

    private static IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Children(c)));
    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static void Wait(Task operation)
    {
        var until = Environment.TickCount64 + 5000;
        while (!operation.IsCompleted && Environment.TickCount64 < until) { Application.DoEvents(); Thread.Sleep(5); }
        Check(operation.IsCompleted, "The isolated UI read must settle."); operation.GetAwaiter().GetResult();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
