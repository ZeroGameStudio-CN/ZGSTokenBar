using System.Drawing;
using System.Text.Json;
using ZGSTokenBar.App;

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
