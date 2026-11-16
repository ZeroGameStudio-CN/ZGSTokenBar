using System.Drawing;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginAdapters;

internal static class CodexTierFilterTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(string? captures = null)
    {
        var context = SynchronizationContext.Current;
        try
        {
            var legacy = JsonSerializer.Deserialize<AppSettings>("{}")!;
            Check(legacy.CodexDockVisibleTiers.SequenceEqual(CodexDockTierFilter.All), "Old settings default to every tier.");
            legacy.CodexDockVisibleTiers = null!; legacy.Normalize();
            Check(legacy.CodexDockVisibleTiers.SequenceEqual(CodexDockTierFilter.All), "Null uses the compatible default.");
            legacy.CodexDockVisibleTiers = [" PRO ", "plus", "pro", "invalid"]; legacy.Normalize();
            Check(legacy.CodexDockVisibleTiers.SequenceEqual(new[] { "plus", "pro" }), "Selections normalize and deduplicate.");
            Check(JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(legacy))!.CodexDockVisibleTiers.SequenceEqual(legacy.CodexDockVisibleTiers), "Selections survive persistence.");
            legacy.CodexDockVisibleTiers = []; legacy.Normalize();
            Check(legacy.CodexDockVisibleTiers.Length == 0, "Explicit empty selection remains empty.");
            Check(CodexDockTierFilter.Category("ChatGPT-Pro") == "pro" && CodexDockTierFilter.Category("Free") == "free"
                && CodexDockTierFilter.Category("business") == "other" && CodexDockTierFilter.Category(null) == "other", "Unknown plans are not guessed as paid tiers.");
            foreach (var dpi in new[] { 96, 144, 192 }) { Rendering(dpi, captures); SettingsUi(dpi, captures); }
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static void Rendering(int dpi, string? captures)
    {
        var now = DateTimeOffset.UtcNow;
        QuotaCard Card(string id, string? plan) => new(id, ProviderKind.Codex, "Codex · " + id, plan, "#10a37f", true,
            [new("7d", 20, now.AddDays(2), TimeSpan.FromDays(7))]) { HasAccountIdentity = true, CapturedAt = now };
        QuotaCard[] cards = [Card("free", "free"), Card("plus", "plus"), Card("pro", "pro"), Card("unknown", null)];
        var health = new ProviderHealth(ProviderKind.Codex, true, "fixture", ProviderHealthCode.Current);
        var snapshot = new QuotaSnapshot(cards, [health], now);
        string Projection()
        {
            var data = CorePluginProjection.Provider(UsageProviderIds.Codex, "codex", "icon", "accent",
                new ProviderResult(ProviderKind.Codex, snapshot.Cards, health));
            return JsonSerializer.Serialize(new { snapshot, data.MiniCards, data.Details });
        }
        var before = Projection();
        using var bar = new BarForm(new AppSettings { EnableAnimations = false }, snapshot, renderOnly: true, renderDpi: dpi,
            activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
        bar.SetGlassExpandedForAcceptance(true); Paint("all");
        Check(bar.VisibleCodexAccountCount == 4, "All tiers including Free are visible by default.");
        var filtered = new AppSettings { EnableAnimations = false, GlassDockExpanded = true, CodexDockVisibleTiers = ["plus", "pro", "other"] };
        bar.ApplySettings(filtered); Paint("free-off");
        Check(bar.VisibleCodexAccountCount == 3 && Plan().Visible.All(ring => ring.StableId != "card:free") && Plan().OverflowCount == 0, "Free cannot leak into expanded dock or overflow.");
        Check(bar.DockVisibilityEntries().Count == 4, "Filtered accounts remain available in Settings.");
        bar.SetGlassExpandedForAcceptance(false); Paint("collapsed");
        Check(bar.VisibleCodexAccountCount == 3, "Collapsed selection uses the same filter.");
        bar.SetUsageProviderActivity(UsageProviderIds.Codex, false); Paint("idle");
        Check(Plan().Visible.Count == 0 && Plan().OverflowCount == 3, "Only allowed tiers enter idle overflow.");
        bar.SetGlassExpandedForAcceptance(true); Paint("idle-expanded");
        Check(bar.VisibleCodexAccountCount == 3, "Expansion never bypasses the tier filter.");
        bar.SetHiddenDockItems(["card:pro"]); Paint("manual-hidden");
        Check(bar.VisibleCodexAccountCount == 2, "Manual account hiding still wins.");
        filtered.CodexDockVisibleTiers = []; bar.ApplySettings(filtered); Paint("none");
        Check(Plan().Visible.Count == 0 && Plan().OverflowCount == 0, "An empty selection hides accounts without disabling collection.");
        Check(snapshot.Cards.Count == 4 && Projection() == before, "Complete source and plugin data remain unchanged for statistics and logging.");
        GlassDockPlan Plan() => (GlassDockPlan)typeof(BarForm).GetMethod("CurrentGlassPlan", Instance)!.Invoke(bar, null)!;
        void Paint(string state)
        {
            using var image = new Bitmap(bar.Width, bar.Height); bar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            if (captures is null) return;
            Directory.CreateDirectory(captures); image.Save(Path.Combine(captures, $"tiers-{state}-{dpi}.png"));
        }
    }

    private static void SettingsUi(int dpi, string? captures)
    {
        using var form = new SettingsForm(new AppSettings { Locale = "zh-CN" }, dpi, renderOnly: true,
            renderWorkingArea: new(-30000, -30000, 3840, 2160)) { Opacity = 0 };
        var toggles = (IDictionary)typeof(SettingsForm).GetField("_codexTierToggles", Instance)!.GetValue(form)!;
        bool Checked(object toggle) => (bool)toggle.GetType().GetProperty("Checked")!.GetValue(toggle)!;
        Check(toggles.Count == 4 && toggles.Values.Cast<object>().All(Checked), "The UI defaults to all four checked tiers.");
        toggles["free"]!.GetType().GetProperty("Checked")!.SetValue(toggles["free"], false);
        var result = (AppSettings)typeof(SettingsForm).GetMethod("BuildSettings", Instance)!.Invoke(form, null)!;
        Check(result.CodexDockVisibleTiers.SequenceEqual(new[] { "plus", "pro", "other" }), "Free can be cancelled independently of provider enablement.");
        using var reopened = new SettingsForm(result, dpi, renderOnly: true, renderWorkingArea: new(-30000, -30000, 3840, 2160));
        var saved = (IDictionary)typeof(SettingsForm).GetField("_codexTierToggles", Instance)!.GetValue(reopened)!;
        Check(!Checked(saved["free"]!) && saved.Keys.Cast<string>().Where(key => key != "free").All(key => Checked(saved[key]!)), "Reopening settings preserves the selection.");
        if (captures is null) return;
        form.Location = new(-29000, -29000); form.Show();
        typeof(SettingsForm).GetMethod("SelectPage", Instance)!.Invoke(form, ["display", false]);
        form.PerformLayout(); Application.DoEvents();
        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        Directory.CreateDirectory(captures); image.Save(Path.Combine(captures, $"tier-settings-{dpi}.png"));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
