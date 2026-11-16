using System.Drawing;
using System.Reflection;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class DockPolicyTests
{
    internal static void Run(string? captures = null)
    {
        ProcessObservations();
        foreach (var dpi in new[] { 96, 192 })
        {
            foreach (var definition in ProviderCatalog.All) AccountStates(definition, dpi, captures);
            StableSelection(dpi);
            NativeAccounts(dpi);
            ExpandedLoading(dpi);
        }
    }

    private static void ProcessObservations()
    {
        CockpitCodexInstanceActivity.ProcessEntry[] processes = [new(1, 0, "launcher"), new(100, 1, "ZGSTokenBar"),
            new(101, 100, "cmd"), new(102, 101, "grok"), new(103, 102, "agy"), new(200, 1, "CoDeX"),
            new(201, 1, "Claude-Code"), new(202, 1, "Antigravity"), new(203, 1, "Cursor"),
            new(204, 1, "Code"), new(205, 1, "opencode"), new(206, 1, "gh")];
        var state = ProviderProcessActivity.FromProcessSnapshot(processes, 100);
        Check(state[UsageProviderIds.Grok] is false, "The observer's CLI remains idle even through a cmd/worker descendant chain.");
        foreach (var id in new[] { UsageProviderIds.Codex, UsageProviderIds.Claude, UsageProviderIds.Antigravity,
            UsageProviderIds.Cursor, UsageProviderIds.Copilot, UsageProviderIds.OpenCode })
            Check(state[id] is true, "Registered client aliases share the same process observation.");
        foreach (var id in new[] { UsageProviderIds.DeepSeek, UsageProviderIds.Xai, UsageProviderIds.OpenRouter })
            Check(state[id] is null, "API-only providers have unknown activity instead of fabricated usage-based activity.");
        var cliOnly = ProviderProcessActivity.FromProcessSnapshot([new(206, 1, "gh")], 100);
        Check(cliOnly[UsageProviderIds.Copilot] is false, "An ordinary GitHub CLI command does not count as Copilot activity.");
        Check(ProviderProcessActivity.FromProcessSnapshot(null, 100).Values.All(value => value is null),
            "A failed process snapshot is unknown for every provider.");
    }

    private static void AccountStates(ProviderDefinition definition, int dpi, string? captures)
    {
        using var bar = Bar(dpi);
        var account = Account("a", 30);
        Publish(bar, definition, [account]);
        Check(Plan(bar).Visible.Count == 0 && Plan(bar).OverflowCount == 1, "First-seen unknown activity is reachable in overflow.");
        bar.SetUsageProviderActivity(definition.Id, true);
        Paint(bar);
        var identity = Plan(bar).Visible.Single().StableId;
        foreach (double? used in new double?[] { 0, 100, null, 67 })
        {
            Publish(bar, definition, [account with { Windows = [Window(used)], Freshness = UsageFreshness.Unavailable }]);
            Check(Plan(bar).Visible is [{ } ring] && ring.StableId == identity && ring.InUse,
                "Zero, exhausted, unknown and changing quota readings cannot hide or replace an active account.");
        }
        bar.SetUsageProviderActivity(definition.Id, null); Paint(bar);
        Check(Plan(bar).Visible.Count == 1, "A temporary observation failure preserves the last active state.");
        if (captures is not null)
        {
            Directory.CreateDirectory(captures);
            using var image = new Bitmap(bar.Width, bar.Height); bar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(captures, $"unified-{definition.Id}-{dpi}.png"));
        }
        bar.SetUsageProviderActivity(definition.Id, false); Paint(bar);
        Check(Plan(bar).Visible.Count == 0 && Plan(bar).OverflowCount == 1, "Idle accounts fold even with an exhausted quota or error.");
        bar.SetUsageProviderActivity(definition.Id, null); Paint(bar);
        Check(Plan(bar).Visible.Count == 0, "An observation failure cannot promote a previously idle account.");
        Publish(bar, definition, [account with { Freshness = UsageFreshness.MissingCredentials }]);
        Check(Plan(bar).Visible.Count == 0 && Plan(bar).OverflowCount == 0, "Signed-out modules do not invent overflow accounts.");
        Publish(bar, definition, [account with { SourceKind = "probe", Freshness = UsageFreshness.Loading }]);
        Check(Plan(bar).OverflowCount == 0, "An installed-only loading probe is not a signed-in account.");
        Publish(bar, definition, [account]);
        bar.SetUsageProviderActivity(definition.Id, true); bar.SetHiddenDockItems([definition.Id]); Paint(bar);
        Check(Plan(bar).Visible.Count == 0 && Plan(bar).OverflowCount == 0, "Manual provider hiding overrides activity.");
    }

    private static void StableSelection(int dpi)
    {
        var definition = ProviderCatalog.Find(UsageProviderIds.Grok)!;
        using var bar = Bar(dpi);
        var accounts = Enumerable.Range(0, 6).Select(index => Account(index.ToString(), index * 15)).ToArray();
        bar.SetUsageProviderActivity(definition.Id, true); Publish(bar, definition, accounts);
        var before = Plan(bar).Visible.Select(ring => ring.StableId).ToArray();
        Check(before.Length == 4 && Plan(bar).OverflowCount == 2, "More than four active accounts keep a bounded primary row.");
        foreach (double? used in new double?[] { 100, 0, null, 51 })
        {
            Publish(bar, definition, accounts.Reverse().Select(account => account with { Windows = [Window(used)] }).ToArray());
            Check(Plan(bar).Visible.Select(ring => ring.StableId).SequenceEqual(before),
                "Quota changes and response ordering cannot rotate the selected four accounts.");
        }
        var width = bar.Width;
        bar.SetGlassExpandedForAcceptance(true); Paint(bar);
        Check(Plan(bar).Visible.Count == 6 && Plan(bar).Visible.Take(4).Select(ring => ring.StableId).SequenceEqual(before),
            "Expansion preserves the primary positions before revealing the remaining accounts.");
        var hiddenAccount = bar.DockVisibilityEntries().First().Id;
        bar.SetHiddenDockItems([hiddenAccount]); Paint(bar);
        Check(Plan(bar).Visible.Count == 5, "A manually hidden account stays hidden during expansion.");
        bar.SetHiddenDockItems([definition.Id]); Paint(bar);
        Check(Plan(bar).Visible.Count == 0 && bar.DockVisibilityEntries().All(item => !item.Visible),
            "A legacy provider-level hide covers every account without rewriting settings.");
        bar.SetHiddenDockItems([]); bar.SetGlassExpandedForAcceptance(false); Paint(bar);
        Check(bar.Width == width, "Collapsing restores the same bounded geometry.");
    }

    private static void NativeAccounts(int dpi)
    {
        var now = DateTimeOffset.UtcNow;
        QuotaCard Card(string id, bool active, double? used) => new(id, ProviderKind.Codex, "Codex · " + id, "pro", "#10a37f",
            active, [new("7d", used, now.AddDays(3), TimeSpan.FromDays(7))]) { HasAccountIdentity = true };
        using var bar = new BarForm(new AppSettings { EnableAnimations = false },
            new QuotaSnapshot([Card("a", true, null), Card("b", false, 100)], [], now), renderOnly: true, renderDpi: dpi,
            activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
        bar.SetUsageProviderActivity(UsageProviderIds.Codex, true); Paint(bar);
        Check(bar.VisibleCodexAccountCount == 1 && Plan(bar).OverflowCount == 1,
            "A provider's active client cannot promote an account that is not bound to it.");
        bar.SetUsageProviderActivity(UsageProviderIds.Codex, false); Paint(bar);
        Check(bar.VisibleCodexAccountCount == 0 && Plan(bar).OverflowCount == 2,
            "Signed-in native accounts remain reachable after the client stops, including unknown and exhausted quota.");
        bar.SetSnapshot(new QuotaSnapshot([new("signed-out", ProviderKind.Codex, "Codex", null, "#10a37f", true,
            [new("7d", null, null, TimeSpan.FromDays(7))])], [], now));
        bar.SetUsageProviderActivity(UsageProviderIds.Codex, true); Paint(bar);
        Check(Plan(bar).Visible.Count == 0 && Plan(bar).OverflowCount == 0,
            "A running client cannot turn an unsigned native placeholder into an account.");
    }

    private static void ExpandedLoading(int dpi)
    {
        using var bar = new BarForm(new AppSettings { EnableAnimations = false, GlassDockExpanded = true },
            QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: dpi);
        Paint(bar);
        var definition = ProviderCatalog.Find(UsageProviderIds.Grok)!;
        bar.SetUsageProviderActivity(definition.Id, false); Publish(bar, definition, [Account("a", null)]);
        Check(Plan(bar).Visible.Count == 1, "An explicit expansion survives an initially empty/loading account set.");
        Publish(bar, definition, []); Publish(bar, definition, [Account("a", 0)]);
        Check(Plan(bar).Visible.Count == 1, "Delayed readings do not silently collapse the user's expanded choice.");
        bar.SetGlassExpandedForAcceptance(false); Paint(bar);
        Check(Plan(bar).Visible.Count == 0 && Plan(bar).OverflowCount == 1, "Explicit collapse still folds the idle account.");
    }

    private static BarForm Bar(int dpi) => new(new AppSettings { EnableAnimations = false },
        QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: dpi, activeProviders: new HashSet<ProviderKind>());
    private static UsageWindow Window(double? used) => new("quota", "Quota", used, DateTimeOffset.UtcNow.AddDays(1), TimeSpan.FromDays(7));
    private static UsageAccountSnapshot Account(string id, double? used) => new(id, "a***@example.test", "fixture", "fixture-login",
        [Window(used)], null, null, [], UsageFreshness.Current, DateTimeOffset.UtcNow, null);
    private static void Publish(BarForm bar, ProviderDefinition definition, UsageAccountSnapshot[] accounts)
    {
        var provider = new UsageProviderSnapshot(definition.Id, definition.DisplayName, definition.Accent, definition.Order, accounts);
        bar.SetUsageBoard(new([provider]));
        bar.SetPluginMiniCards([new(definition.Id, definition.DisplayName, UsageProjection.MiniCard(provider, true), new Dictionary<string, string>())]);
        Paint(bar);
    }
    private static GlassDockPlan Plan(BarForm bar) => (GlassDockPlan)typeof(BarForm).GetMethod("CurrentGlassPlan",
        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bar, null)!;
    private static void Paint(BarForm bar) { using var image = new Bitmap(bar.Width, bar.Height); bar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
