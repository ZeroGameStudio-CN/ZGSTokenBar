using System.Drawing;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class GlassDockOverflowTests
{
    internal static void Run(string? captures = null)
    {
        ActiveAccountsAndIdleGrok(captures);
        var used = Ring("used", 80);
        var zero = Ring("zero", 100) with { OverflowOnly = true };
        var idle = Ring("idle", 70) with { OverflowOnly = true };
        var exhausted = Ring("exhausted", 0);
        var unknown = Ring("unknown", null) with { Warn = true };
        GlassDockRing[] rings = [zero, used, idle, exhausted, unknown];
        var plan = GlassDockLayout.Plan(rings, maxVisible: 8);
        Check(plan.Visible.SequenceEqual([exhausted, used, unknown]) && plan.OverflowCount == 2,
            "Unused accounts remain in overflow even when primary slots are available");
        var expanded = GlassDockLayout.PlanExpanded(rings, maxVisible: 8);
        Check(expanded.Visible.Take(plan.Visible.Count).SequenceEqual(plan.Visible)
            && expanded.Visible.Count == rings.Length, "Expanding retains primary positions and reveals every unused account");
        var onlyIdle = GlassDockLayout.Plan([zero, idle]);
        Check(onlyIdle.Visible.Count == 0 && onlyIdle.OverflowCount == 2 && onlyIdle.Slots is [{ Chip: true } chip]
            && chip.Bounds.Left >= GlassDockLayout.PadLeft && chip.Bounds.Right < onlyIdle.RouterBounds.Left,
            "An all-unused set still has a visible non-overlapping +N entry");
        Check(GlassDockLayout.Plan(rings, maxVisible: 1).OverflowUrgent,
            "An unknown authenticated account in overflow keeps an attention indicator");
        var activeCodex = Ring("active Codex", 99) with { InUse = true };
        Check(GlassDockLayout.Plan([exhausted, activeCodex], maxVisible: 1).Visible.SequenceEqual([activeCodex]),
            "A running account wins a primary slot over an idle low-quota reading.");

        var now = DateTimeOffset.UtcNow;
        var cards = new[] { Card("used", 20), Card("zero", 0, active: false), Card("idle", 30, active: false),
            Card("exhausted", 100), Card("unknown", null), Card("signed-out", null, active: false, known: false) };
        foreach (var dpi in new[] { 96, 192 })
        {
            using var bar = new BarForm(new AppSettings { EnableAnimations = false },
                new QuotaSnapshot(cards, [new(ProviderKind.Codex, true, "fixture", ProviderHealthCode.Current)], now),
                renderOnly: true, renderDpi: dpi, activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
            Paint("collapsed");
            var width = bar.Width;
            Check(bar.VisibleCodexAccountCount == 3 && bar.DockVisibilityEntries().Count == 5,
                "The native dock keeps exhausted/unknown accounts visible and never invents a signed-out account");
            bar.SetGlassExpandedForAcceptance(true); Paint("expanded");
            Check(bar.VisibleCodexAccountCount == 5 && bar.Width > width,
                "Explicit expansion paints both zero-usage and inactive authenticated accounts");
            bar.SetGlassExpandedForAcceptance(false); Paint("reclosed");
            Check(bar.Width == width && bar.VisibleCodexAccountCount == 3,
                "Collapsing restores the compact account selection and width");
            bar.SetHiddenDockItems(["card:zero"]);
            bar.SetGlassExpandedForAcceptance(true); Paint("manual-hidden");
            Check(bar.VisibleCodexAccountCount == 4 && !bar.DockVisibilityEntries().Single(item => item.Id == "card:zero").Visible,
                "Manual hiding remains authoritative even inside the expanded account set");

            void Paint(string state)
            {
                using var bitmap = new Bitmap(bar.Width, bar.Height);
                bar.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                if (captures is null) return;
                Directory.CreateDirectory(captures);
                bitmap.Save(Path.Combine(captures, $"dock-overflow-{state}-{dpi}.png"));
            }
        }

        static GlassDockRing Ring(string label, double? remaining) => new("codex", "Codex", label, 0,
            remaining, null, null, "#10a37f");
        QuotaCard Card(string key, double? usedPercent, bool active = true, bool known = true) =>
            new(key, ProviderKind.Codex, "Codex · " + key, "pro", "#10a37f", active,
                [new("7d", usedPercent, now.AddDays(3), TimeSpan.FromDays(7))]) { HasAccountIdentity = known };
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static void ActiveAccountsAndIdleGrok(string? captures)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var dpi in new[] { 96, 192 })
        {
            QuotaCard Card(string key, double used) => new(key, ProviderKind.Codex, "Codex · " + key, "pro", "#10a37f", true,
                [new("7d", used, now.AddDays(3), TimeSpan.FromDays(7))]) { HasAccountIdentity = true };
            using var bar = new BarForm(new AppSettings { EnableAnimations = false },
                new QuotaSnapshot([Card("1", 0), Card("2", 78)], [new(ProviderKind.Codex, true, "fixture", ProviderHealthCode.Current)], now),
                renderOnly: true, renderDpi: dpi, activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
            var account = new UsageAccountSnapshot("fixture", "Grok", "paid", "grok-auth",
                [new("credits", "Monthly", 100, now.AddDays(20), TimeSpan.FromDays(30))], null, null, [], UsageFreshness.Current, now, null);
            var provider = new UsageProviderSnapshot(UsageProviderIds.Grok, "Grok", "#38bdf8", 900, [account]);
            bar.SetUsageBoard(new([provider]));
            bar.SetPluginMiniCards([new(UsageProviderIds.Grok, "Grok", UsageProjection.MiniCard(provider, true), new Dictionary<string, string>())]);
            bar.SetUsageProviderActivity(UsageProviderIds.Grok, false); Paint("idle-grok");
            Check(bar.VisibleCodexAccountCount == 2 && Plan().Visible.All(ring => ring.ProviderId != UsageProviderIds.Grok)
                && Plan().OverflowCount == 1, "Active Codex accounts survive a zero quota reading while idle Grok remains in overflow.");
            bar.SetUsageProviderActivity(UsageProviderIds.Grok, true); Paint("active-grok");
            Check(Plan().Visible.Any(ring => ring.ProviderId == UsageProviderIds.Grok), "Launching Grok promotes its signed-in account again.");
            bar.SetUsageProviderActivity(UsageProviderIds.Grok, null); Paint("unknown-activity");
            Check(Plan().Visible.Any(ring => ring.ProviderId == UsageProviderIds.Grok), "An unavailable process observation cannot hide an account as idle.");
            GlassDockPlan Plan() => (GlassDockPlan)typeof(BarForm).GetMethod("CurrentGlassPlan",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(bar, null)!;
            void Paint(string state)
            {
                using var image = new Bitmap(bar.Width, bar.Height); bar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                if (captures is null) return;
                Directory.CreateDirectory(captures); image.Save(Path.Combine(captures, $"dock-priority-{state}-{dpi}.png"));
            }
        }
    }
}
