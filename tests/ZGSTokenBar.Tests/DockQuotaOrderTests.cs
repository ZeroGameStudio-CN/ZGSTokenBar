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
        string[] Plan(IReadOnlyDictionary<string, int> order) => GlassDockLayout.Plan(
            rows.Select(r => r with { AccountOrder = order[r.StableId] }).ToArray(), 4).Visible.Select(r => r.StableId).ToArray();
    }
    private static GlassDockRing Ring(string id, string provider, int position, double? quota) => new(provider, provider, id, 1, quota, null, null, "#10a37f")
        { StableId = id, ProviderOrder = position, InUse = true };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
