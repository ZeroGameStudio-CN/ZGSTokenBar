using System.Drawing;
using ZGSTokenBar.Core;

internal static class DockQuotaOrderTests
{
    internal static void Run()
    {
        var state = new GlassDockAccountOrder();
        var a = Ring("a", "codex", 10, 20); var b = Ring("b", "codex", 10, 90); var g = Ring("g", "grok", 900, 99);
        GlassDockRing[] rows = [g, a, b];
        var rank = state.Update(rows, 1000, false);
        Check(Plan(rank).SequenceEqual(new[] { "b", "a", "g" }), "Tools keep their group; quota ranks only accounts of the same tool.");
        rows = [g, a with { RemainingPercent = 95 }, b with { RemainingPercent = 10 }];
        rank = state.Update(rows, 2000, false);
        Check(Plan(rank).SequenceEqual(new[] { "b", "a", "g" }), "One changed quota sample cannot reshuffle the row.");
        rank = state.Update(rows, 12000, true);
        Check(Plan(rank).SequenceEqual(new[] { "b", "a", "g" }), "Hover or pin interaction defers a settled quota change.");
        rank = state.Update(rows, 12001, false);
        Check(Plan(rank).SequenceEqual(new[] { "a", "b", "g" }), "A persistent change takes effect when interaction ends.");
        rows = [g, a with { InUse = false, OverflowOnly = true }, b];
        rank = state.Update(rows, 13000, false);
        Check(Plan(rank).SequenceEqual(new[] { "b", "g" }), "Activity changes still choose the primary row before quota ranking.");
        var empty = state.Update([], 14000, false);
        Check(empty.Count == 0, "Removing accounts drops their old order state.");

        CrowdedCollapsedYieldsZeroToUsableActiveTool();
        MotionGlidesAndRetargetsSmoothly();

        string[] Plan(IReadOnlyDictionary<string, int> order) => GlassDockLayout.Plan(
            rows.Select(r => r with { AccountOrder = order[r.StableId] }).ToArray(), 4).Visible.Select(r => r.StableId).ToArray();
    }

    private static void CrowdedCollapsedYieldsZeroToUsableActiveTool()
    {
        var state = new GlassDockAccountOrder();
        GlassDockRing[] crowded =
        [
            Ring("codex85", "codex", 10, 85),
            Ring("codex11", "codex", 10, 11),
            Ring("codex0", "codex", 10, 0),
            Ring("agy54", "antigravity", 850, 54),
            Ring("grok74", "grok", 900, 74),
        ];
        var order = state.Update(crowded, 1000, false);
        var ordered = crowded.Select(r => r with { AccountOrder = order[r.StableId] }).ToArray();
        var collapsed = GlassDockLayout.Plan(ordered, maxVisible: 4);
        Check(collapsed.Visible.Select(r => r.StableId).SequenceEqual(["codex85", "codex11", "agy54", "grok74"])
            && collapsed.OverflowCount == 1 && collapsed.OverflowUrgent,
            "Crowded collapsed dock moves active 0% Codex into urgent +N so usable active tools remain visible.");
        var expanded = GlassDockLayout.PlanExpanded(ordered, maxVisible: 4);
        Check(expanded.Visible.Select(r => r.StableId).SequenceEqual(["codex85", "codex11", "agy54", "grok74", "codex0"]),
            "Expanded crowded dock retains primary positions before appended overflow ring.");
    }

    private static void MotionGlidesAndRetargetsSmoothly()
    {
        var a = Ring("a", "codex", 10, 80);
        var b = Ring("b", "codex", 10, 40);
        var c = Ring("c", "grok", 900, 74) with { OverflowOnly = true };

        var snap = new GlassDockMotion();
        var collapsedPlan = GlassDockLayout.Plan([a, b, c], maxVisible: 4);
        var initial = snap.Update(collapsedPlan, 1000, animate: false);
        Check(!initial.Animating && initial.Rings.Count == 2 && initial.ChipBounds is not null,
            "Disabled animation snaps immediately to static plan.");

        var motion = new GlassDockMotion();
        _ = motion.Update(collapsedPlan, 1000, animate: true);
        var expandedPlan = GlassDockLayout.PlanExpanded([a, b, c], maxVisible: 4);
        var expandStart = motion.Update(expandedPlan, 1000, animate: true);
        var oldChipX = initial.ChipBounds!.Value.X;
        Check(expandStart.Animating && expandStart.Width < expandedPlan.Width && MathF.Abs(expandStart.Width - collapsedPlan.Width) < 0.5f,
            "Expand animation starts smoothly from previous collapsed width.");
        var expandMid = motion.Update(expandedPlan, 1000 + GlassDockMotion.DurationMilliseconds / 2, animate: true);
        var enteringC = expandMid.Rings.Single(r => r.Ring.StableId == "c");
        Check(expandMid.Animating && expandMid.Width > collapsedPlan.Width && expandMid.Width < expandedPlan.Width
            && enteringC.X > oldChipX && enteringC.Alpha is > 0.05f and < 0.99f && !enteringC.Exiting,
            "Mid-expand glides width and fades entering ring in from chip origin.");

        // Retarget mid-flight back to collapsed: must preserve mid-flight X without jumping to target.
        var reCollapseStart = motion.Update(collapsedPlan, 1000 + GlassDockMotion.DurationMilliseconds / 2, animate: true);
        var exitingC = reCollapseStart.Rings.Single(r => r.Ring.StableId == "c");
        Check(reCollapseStart.Animating && MathF.Abs(exitingC.X - enteringC.X) < 0.5f && exitingC.Exiting,
            "Mid-flight retargeting preserves interpolated ring X without jumping.");
        var settled = motion.Update(collapsedPlan, 1000 + GlassDockMotion.DurationMilliseconds * 2, animate: true);
        Check(!settled.Animating && settled.Rings.Count == 2 && settled.Rings.All(r => !r.Exiting)
            && MathF.Abs(settled.Width - collapsedPlan.Width) < 0.1f,
            "Completed collapse drops exiting ring and settles cleanly at collapsed width.");

        // Reorder swap without width change glides horizontally.
        var swapMotion = new GlassDockMotion();
        _ = swapMotion.Update(GlassDockLayout.Plan([a with { AccountOrder = 0 }, b with { AccountOrder = 1 }], maxVisible: 4), 2000, animate: true);
        var swappedPlan = GlassDockLayout.Plan([b with { AccountOrder = 0 }, a with { AccountOrder = 1 }], maxVisible: 4);
        var swappedStart = swapMotion.Update(swappedPlan, 2000, animate: true);
        var swappedMid = swapMotion.Update(swappedPlan, 2000 + GlassDockMotion.DurationMilliseconds / 2, animate: true);
        var slot0 = swappedPlan.Slots[0].Bounds.X;
        var slot1 = swappedPlan.Slots[1].Bounds.X;
        var midA = swappedMid.Rings.Single(r => r.Ring.StableId == "a").X;
        var midB = swappedMid.Rings.Single(r => r.Ring.StableId == "b").X;
        Check(swappedStart.Animating && swappedMid.Animating && midA > slot0 && midA < slot1 && midB > slot0 && midB < slot1,
            "Ring reorder glides both rings continuously between slot coordinates.");
    }
    private static GlassDockRing Ring(string id, string provider, int position, double? quota) => new(provider, provider, id, 1, quota, null, null, "#10a37f")
        { StableId = id, ProviderOrder = position, InUse = true };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
