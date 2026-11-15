using System.Drawing;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Intake;
using ZGSTokenBar.Plugin.Router;

internal static class TaskbarDpiReconnectTests
{
    internal static void Run(string? captures = null)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new QuotaSnapshot(
            [new("codex.first", ProviderKind.Codex, "Codex · 1", "pro", "#10a37f", true,
                [new("7d", 58, now.AddDays(3), TimeSpan.FromDays(7))]),
             new("codex.second", ProviderKind.Codex, "Codex · 2", "pro", "#10a37f", true,
                [new("7d", 2, now.AddDays(4), TimeSpan.FromDays(7))])],
            [new(ProviderKind.Codex, true, "fixture", ProviderHealthCode.Current)], now);
        using var bar = new BarForm(new AppSettings { TaskbarDocked = true, EnableAnimations = false },
            snapshot, renderOnly: true, activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
        bar.SetPluginMiniCards([
            new(RouterPlugin.Id, "Router", RouterModuleTests.Card("idle"), new Dictionary<string, string>()),
            new(IntakeObserverPlugin.Id, "自动分拣", IntakeObserverPlugin.Launcher, new Dictionary<string, string>())]);
        var activations = new List<string>();
        bar.PluginActivated += (_, id) => activations.Add(id);

        Check(bar.ApplyTaskbarDpi(192), "The initial taskbar sets its own 200% scale");
        Paint("200");
        var handle = bar.Handle;
        var large = bar.ClientSize;
        var logical = bar.MiniPluginBounds(IntakeObserverPlugin.Id)!.Value;
        var largeHit = bar.QuotaTargetScreenBounds(logical).Size;
        Check(large.Height == 88 && bar.UiScale == 2, "The 200% dock is 88 physical pixels high");

        // Deliberately send no WM_DPICHANGED or restart: the next target-taskbar read repairs it.
        Check(bar.ApplyTaskbarDpi(96), "A 100% taskbar invalidates the old 200% scale");
        Paint("100");
        Check(bar.Handle == handle && bar.RenderDpi == 96 && bar.UiScale == 1,
            "One existing window adopts the taskbar DPI without a restart");
        Check(bar.ClientSize.Height == 44 && bar.ClientSize.Width * 2 == large.Width,
            "The whole dock scales proportionally, including its width");
        Check(bar.MiniPluginBounds(IntakeObserverPlugin.Id) == logical
            && bar.QuotaTargetScreenBounds(logical).Width * 2 == largeHit.Width,
            "Logical targets stay aligned while physical click regions scale with the pixels");
        Check(bar.VisibleCodexAccountCount == 2, "Both account rings survive reconnect scaling");
        bar.ActivateMiniTarget(new PointF(logical.Left + logical.Width / 2, logical.Top + logical.Height / 2));
        Check(activations.SequenceEqual([IntakeObserverPlugin.Id]), "The scaled icon retains its own click target");
        Check(!bar.ApplyTaskbarDpi(96), "An unchanged taskbar does not reset geometry or popovers");
        Check(!bar.ApplyTaskbarDpi(0) && bar.RenderDpi == 96, "An unavailable taskbar reading cannot replace a verified scale");

        foreach (var dpi in new[] { 144, 192, 96 })
        {
            Check(bar.ApplyTaskbarDpi(dpi), "A later display switch adopts its target DPI");
            Paint(dpi.ToString());
            Check(bar.Handle == handle && bar.ClientSize.Height == (int)Math.Round(44 * dpi / 96d),
                "Repeated remote/local switches reuse one window and the correct proportional height");
        }

        void Paint(string name)
        {
            using var bitmap = new Bitmap(bar.ClientSize.Width, bar.ClientSize.Height);
            bar.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            if (captures is null) return;
            Directory.CreateDirectory(captures);
            bitmap.Save(Path.Combine(captures, $"dock-reconnect-{name}.png"));
        }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
