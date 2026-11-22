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
        Check(Plan(rank).SequenceEqual(new[] { "a", "b", "g" }), "Tools keep their group; usable accounts rank least-to-most remaining within the group.");
        rows = [g, a with { RemainingPercent = 95 }, b with { RemainingPercent = 10 }];
        rank = state.Update(rows, 2000, false);
        Check(Plan(rank).SequenceEqual(new[] { "a", "b", "g" }), "One changed quota sample cannot reshuffle the row.");
        rank = state.Update(rows, 12000, true);
        Check(Plan(rank).SequenceEqual(new[] { "a", "b", "g" }), "Hover or pin interaction defers a settled quota change.");
        rank = state.Update(rows, 12001, false);
        Check(Plan(rank).SequenceEqual(new[] { "b", "a", "g" }), "A persistent change takes effect when interaction ends.");
        rows = [g, a with { InUse = false, OverflowOnly = true }, b];
        rank = state.Update(rows, 13000, false);
        Check(Plan(rank).SequenceEqual(new[] { "b", "g" }), "Activity changes still choose the primary row before quota ranking.");
        var empty = state.Update([], 14000, false);
        Check(empty.Count == 0, "Removing accounts drops their old order state.");

        ZeroAndUnknownSinkToGroupTail();
        CrowdedCollapsedYieldsZeroToUsableActiveTool();
        MotionGlidesAndRetargetsSmoothly();

        string[] Plan(IReadOnlyDictionary<string, int> order) => GlassDockLayout.Plan(
            rows.Select(r => r with { AccountOrder = order[r.StableId] }).ToArray(), 4).Visible.Select(r => r.StableId).ToArray();
    }

    private static void ZeroAndUnknownSinkToGroupTail()
    {
        var state = new GlassDockAccountOrder();
        GlassDockRing[] rings =
        [
            Ring("cUnknown", "codex", 10, null),
            Ring("c85", "codex", 10, 85),
            Ring("c0", "codex", 10, 0),
            Ring("c11", "codex", 10, 11),
        ];
        var order = state.Update(rings, 1000, false);
        var plan = GlassDockLayout.Plan(rings.Select(r => r with { AccountOrder = order[r.StableId] }).ToArray(), 4);
        Check(plan.Visible.Select(r => r.StableId).SequenceEqual(["c11", "c85", "c0", "cUnknown"]),
            "Usable rings rank least-to-most first, then 0% exhausted, then unknown.");
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
        Check(collapsed.Visible.Select(r => r.StableId).SequenceEqual(["codex11", "codex85", "agy54", "grok74"])
            && collapsed.OverflowCount == 1 && collapsed.OverflowUrgent,
            "Crowded collapsed dock moves active 0% Codex into urgent +N so usable active tools remain visible.");
        var expanded = GlassDockLayout.PlanExpanded(ordered, maxVisible: 4);
        Check(expanded.Visible.Select(r => r.StableId).SequenceEqual(["codex11", "codex85", "agy54", "grok74", "codex0"]),
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

        // Quota arc % and system metric bars tween smoothly between readings.
        var tweenMotion = new GlassDockMotion();
        var basePlan = GlassDockLayout.Plan([Ring("t1", "codex", 10, 80)], maxVisible: 4);
        var startInteraction = new GlassDockInteraction(null, false, false, false, false, false, 20, 20, 20, 20);
        var tweenInit = tweenMotion.Update(basePlan, 3000, animate: true, startInteraction);
        Check(tweenInit.Rings.Single().DisplayedRemaining is { } initRem && Math.Abs(initRem - 80) < 0.01
            && tweenInit.Cpu is { } initCpu && Math.Abs(initCpu - 20) < 0.01,
            "Initial motion snapshot lands cleanly on baseline quota and metric values.");

        var updatedPlan = GlassDockLayout.Plan([Ring("t1", "codex", 10, 40)], maxVisible: 4);
        var hoveredKey = GlassDockMotion.KeyOf(updatedPlan.Visible[0]);
        var targetInteraction = new GlassDockInteraction(hoveredKey, false, false, false, true, false, 80, 80, 80, 80);
        var tweenStart = tweenMotion.Update(updatedPlan, 3000, animate: true, targetInteraction);
        var tweenMid = tweenMotion.Update(updatedPlan, 3000 + GlassDockMotion.DurationMilliseconds / 2, animate: true, targetInteraction);
        var midRing = tweenMid.Rings.Single();
        Check(tweenStart.Animating && tweenMid.Animating
            && midRing.DisplayedRemaining is > 40 and < 80
            && tweenMid.Cpu is > 20 and < 80
            && tweenMid.Memory is > 20 and < 80
            && tweenMid.Disk is > 20 and < 80
            && tweenMid.Gpu is > 20 and < 80,
            "Ring arc remaining % and all four system metric bars interpolate smoothly mid-tween.");

        // Hover glow fades in over HoverInMilliseconds and fades out over HoverOutMilliseconds.
        var hoverInMid = tweenMotion.Update(updatedPlan, 3000 + GlassDockMotion.HoverInMilliseconds / 2, animate: true, targetInteraction);
        var hoverInEnd = tweenMotion.Update(updatedPlan, 3000 + GlassDockMotion.HoverInMilliseconds, animate: true, targetInteraction);
        Check(hoverInMid.Rings.Single().HoverAmount is > 0.05f and < 0.99f
            && hoverInMid.SystemHover is > 0.05f and < 0.99f
            && MathF.Abs(hoverInEnd.Rings.Single().HoverAmount - 1f) < 0.01f
            && MathF.Abs(hoverInEnd.SystemHover - 1f) < 0.01f,
            "Hover glow fades in smoothly to full opacity over HoverInMilliseconds.");

        var leaveInteraction = new GlassDockInteraction(null, false, false, false, false, false, 80, 80, 80, 80);
        _ = tweenMotion.Update(updatedPlan, 4000, animate: true, leaveInteraction);
        var hoverOutMid = tweenMotion.Update(updatedPlan, 4000 + GlassDockMotion.HoverOutMilliseconds / 2, animate: true, leaveInteraction);
        var hoverOutEnd = tweenMotion.Update(updatedPlan, 4000 + GlassDockMotion.HoverOutMilliseconds, animate: true, leaveInteraction);
        Check(hoverOutMid.Animating
            && hoverOutMid.Rings.Single().HoverAmount is > 0.01f and < 0.95f
            && hoverOutMid.SystemHover is > 0.01f and < 0.95f
            && MathF.Abs(hoverOutEnd.Rings.Single().HoverAmount) < 0.01f
            && MathF.Abs(hoverOutEnd.SystemHover) < 0.01f,
            "Hover glow fades out smoothly over HoverOutMilliseconds and settles cleanly.");
    }
    private static GlassDockRing Ring(string id, string provider, int position, double? quota) => new(provider, provider, id, 1, quota, null, null, "#10a37f")
        { StableId = id, ProviderOrder = position, InUse = true };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
