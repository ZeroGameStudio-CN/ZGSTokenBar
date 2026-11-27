using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Intake;
using ZGSTokenBar.Plugin.Router;

internal static class TaskbarFreshSeatTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run()
    {
        var context = SynchronizationContext.Current;
        try
        {
            // Read-only placement decisions against a synthetic row: no real taskbar or input.
            var row = new Rectangle(0, 1000, 1920, 48);
            var tray = new Rectangle(1700, 1000, 220, 48);
            var apps = new Rectangle(1200, 1000, 400, 48);
            var seat = new Rectangle(18, 1002, 600, 44);
            nint parent = 123;
            var track = new TaskbarPlacement.PlacementTrack("fixture", new(0, 0, 1920, 1048), true, true, 6, 1094, 1002)
                { TaskbarWindow = parent, TaskbarBounds = row, TrayBounds = tray };
            Check(TaskbarPlacement.LargestGapWidth(track, [apps, tray]) == 1188
                && TaskbarPlacement.LargestGapWidth(track, [apps, tray], extraClearance: 8) == 1180
                && TaskbarPlacement.LargestGapWidth(track, [new(200, 1000, 400, 48), tray]) == 1088
                && TaskbarPlacement.LargestGapWidth(track, [new(0, 1000, 1700, 48), tray]) <= 0,
                "LargestGapWidth measures left/right clear gaps and handles crowded rows.");
            using var bar = new BarForm(new AppSettings(), QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: 96);
            Set("_embedRequested", true); Set("_taskbarDocked", true); Set("_watchedTaskbar", parent);
            Set("_taskbarRowOverlayFor", parent); Set("_taskbarRowKey", (parent, row, tray));
            Set("_taskbarRowProbePending", true);
            var now = Environment.TickCount64;
            var state = (TaskbarRowStability)typeof(BarForm).GetField("_taskbarRowStability", Instance)!.GetValue(bar)!;
            state.OnLayoutChanged(now - 550);
            var obsolete = state.LayoutRevision;
            state.OnLayoutChanged(now - 50);
            Check(!state.ConfirmLayout(obsolete), "A read crossed by a later notice cannot authorize attachment.");
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, now - 80));
            Check(Safe(seat.Size), "A fresh clear reading keeps the existing unchanged seat during a layout-notice burst.");
            Check(state.AwaitingLayout, "Retaining a seat never confirms an obsolete layout revision.");
            Set("_taskbarRowOverlayFor", (nint)0);
            Check(!Safe(seat.Size), "A fresh but unconfirmed read cannot attach or move into a row.");
            Set("_taskbarRowOverlayFor", parent);
            Set("_taskbarRowSample", (new Rectangle[] { new(550, 1000, 300, 48), tray }, Environment.TickCount64));
            Check(!Safe(seat.Size), "A real button overlap still leaves the row immediately.");
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64 - 700));
            Check(!Safe(seat.Size), "Missing fresh evidence cannot retain stale geometry indefinitely.");
            Set("_taskbarRowSample", (new Rectangle[] { apps, tray }, Environment.TickCount64));
            Check(!Safe(new Size(seat.Width + 20, seat.Height)), "An unverified resize cannot use the old seat.");
            Set("_taskbarRowSample", ((Rectangle[]?)null, Environment.TickCount64));
            Check(!Safe(seat.Size), "An unavailable read is never a successful clear gap.");

            AdaptiveGapFitting();
            CrowdedRowStaysEmbeddedInTaskbar();
            ProbeBurstDoesNotStarveAndFailuresEscalate();

            void Set(string name, object value) => typeof(BarForm).GetField(name, Instance)!.SetValue(bar, value);
            bool Safe(Size size)
            {
                object?[] args = [track, size, seat.Location, seat, false, Point.Empty, "", false, false];
                return (bool)typeof(BarForm).GetMethod("TryGetSafeRowLocation", Instance)!.Invoke(bar, args)!;
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    private static void AdaptiveGapFitting()
    {
        var now = DateTimeOffset.UtcNow;
        QuotaCard Card(string key, double used) => new(key, ProviderKind.Codex, "Codex · " + key, "pro", "#10a37f", true,
            [new("7d", used, now.AddDays(3), TimeSpan.FromDays(7))]) { HasAccountIdentity = true };
        var snapshot = new QuotaSnapshot(
            [Card("1", 20), Card("2", 30), Card("3", 40)],
            [new(ProviderKind.Codex, true, "fixture", ProviderHealthCode.Current)],
            now);
        using var bar = new BarForm(new AppSettings { TaskbarDocked = true, EnableAnimations = false },
            snapshot, renderOnly: true, renderDpi: 96, activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
        bar.SetPluginMiniCards([
            new(RouterPlugin.Id, "Router", RouterModuleTests.Card("idle"), new Dictionary<string, string>()),
            new(IntakeObserverPlugin.Id, "自动分拣", IntakeObserverPlugin.Launcher, new Dictionary<string, string>())]);
        Check(bar.Width == 241 && bar.TaskbarFitRingCap is null, "Compact 3-ring dock starts unconstrained at 241px.");
        Check(bar.TryAdaptTaskbarRingCap(230) && bar.TaskbarFitRingCap == 2 && bar.Width == 230, "Tight gap shrinks dock to 2 rings.");
        Check(bar.TryAdaptTaskbarRingCap(194) && bar.TaskbarFitRingCap == 1 && bar.Width == 194, "Tighter gap shrinks dock to 1 ring.");
        Check(bar.TryAdaptTaskbarRingCap(162) && bar.TaskbarFitRingCap == 0 && !bar.TaskbarUltraCompact && bar.Width == 162, "Minimal gap folds all rings into +N.");
        Check(bar.TryAdaptTaskbarRingCap(95) && bar.TaskbarUltraCompact && bar.Width == GlassDockLayout.UltraCompactWidth,
            "Narrow gap below 162px drops into ultra-compact (+N + tok/s) mode.");
        Check(!bar.TryAdaptTaskbarRingCap(40) && bar.TaskbarUltraCompact && bar.Width == GlassDockLayout.UltraCompactWidth,
            "Falling below ultra-compact width keeps ultra-compact instead of bloating back to overlay.");
        Check(!bar.TryAdaptTaskbarRingCap(170) && bar.TaskbarUltraCompact,
            "Expanding out of ultra-compact obeys 16px hysteresis.");
        Check(bar.TryAdaptTaskbarRingCap(178) && !bar.TaskbarUltraCompact && bar.TaskbarFitRingCap == 0 && bar.Width == 162,
            "Sufficient clearance expands out of ultra-compact back to 0 rings + monitors.");
        Check(!bar.TryAdaptTaskbarRingCap(200) && bar.TaskbarFitRingCap == 0, "Expansion obeys 16px hysteresis.");
        Check(bar.TryAdaptTaskbarRingCap(210) && bar.TaskbarFitRingCap == 1 && bar.Width == 194, "Sufficient clearance expands back one ring.");
        Check(bar.TryAdaptTaskbarRingCap(257) && bar.TaskbarFitRingCap is null && !bar.TaskbarUltraCompact && bar.Width == 241, "Full clearance removes cap cleanly.");
        Check(bar.TryAdaptTaskbarRingCap(194) && bar.TaskbarFitRingCap == 1, "Re-shrinks cleanly.");
        bar.SetGlassExpandedForAcceptance(true);
        var expandedWidth = bar.Width;
        Check(!bar.TryAdaptTaskbarRingCap(162) && bar.Width == expandedWidth, "Explicit +N expansion is never suppressed by gap cap.");
        bar.SetGlassExpandedForAcceptance(false);
        Check(bar.TryAdaptTaskbarRingCap(null) && bar.TaskbarFitRingCap is null && !bar.TaskbarUltraCompact && bar.Width == 241,
            "Clearing the gap cap restores full rings.");
    }

    private static void CrowdedRowStaysEmbeddedInTaskbar()
    {
        var now = DateTimeOffset.UtcNow;
        QuotaCard Card(string key, double used) => new(key, ProviderKind.Codex, "Codex · " + key, "pro", "#10a37f", true,
            [new("7d", used, now.AddDays(3), TimeSpan.FromDays(7))]) { HasAccountIdentity = true };
        var snapshot = new QuotaSnapshot(
            [Card("1", 20), Card("2", 30), Card("3", 40)],
            [new(ProviderKind.Codex, true, "fixture", ProviderHealthCode.Current)],
            now);
        using var parent = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Bounds = new(-30000, -30000, 1920, 48),
            Opacity = 0,
        };
        parent.Show();
        using var bar = new BarForm(new AppSettings { TaskbarDocked = true, EnableAnimations = false },
            snapshot, renderOnly: true, renderDpi: 96, activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex })
            { Location = new(-29000, -30000), Opacity = 0 };
        bar.Show();
        Application.DoEvents();
        PluginMiniCardView[] monitorCards =
        [
            new(RouterPlugin.Id, "Router", RouterModuleTests.Card("idle"), new Dictionary<string, string>()),
            new(IntakeObserverPlugin.Id, "自动分拣", IntakeObserverPlugin.Launcher, new Dictionary<string, string>()),
        ];
        bar.SetPluginMiniCards(monitorCards);
        Check(TaskbarEmbedding.TryGetScreenBounds(parent.Handle, out var row), "Crowded fixture has real row geometry.");
        var tray = new Rectangle(row.Right - 220, row.Top, 220, row.Height);
        var screen = new Rectangle(row.Left, row.Bottom - 1080, row.Width, 1080);
        var reserved = true;
        Set("_embedRequested", true);
        Set("_taskbarDocked", true);
        Set("_watchedTaskbar", parent.Handle);
        Set("_taskbarRowKey", (parent.Handle, row, tray));
        Set("_taskbarRowProbePending", true);
        Set("_primaryTaskbarProbe", (Func<nint>)(() => parent.Handle));
        Set("_fullscreenWindowProbe", (Func<string?, bool>)(_ => false));
        Set("_reservedTaskbarRowProbe", (Func<TaskbarPlacement.PlacementTrack, bool>)(_ => reserved));
        var track = new TaskbarPlacement.PlacementTrack("fixture", screen, true, true, row.Left + 6, tray.Left - bar.Width - 6,
            row.Top + (row.Height - bar.Height) / 2) { TaskbarWindow = parent.Handle, TaskbarBounds = row, TrayBounds = tray };
        var anchor = new Point(row.Left + 900, track.Fixed);

        Set("_taskbarRowSample", (new Rectangle[] { new(row.Left, row.Top, 500, row.Height), tray },
            Environment.TickCount64 - TaskbarRowStability.ReturnDelayMs));
        Place();
        Set("_taskbarRowSample", (new Rectangle[] { new(row.Left, row.Top, 500, row.Height), tray }, Environment.TickCount64));
        Place();
        Check(bar.Visible && bar.EmbeddedInTaskbar && bar.TaskbarEmbedStatus == "embedded"
            && bar.Width == 241 && !bar.TaskbarUltraCompact,
            "Open row embeds at full ring width.");

        bar.SetPluginMiniCards([]);
        Place();
        var smallerSeat = bar.ScreenBounds;
        Check(smallerSeat.Width < 241, "Removing monitors creates a smaller confirmed bar.");
        Set("_taskbarRowSample", ((Rectangle[]?)null, Environment.TickCount64));
        bar.SetPluginMiniCards(monitorCards);
        Place();
        Check(bar.Visible && bar.NativeTaskbarParentVerified && bar.ScreenBounds == smallerSeat,
            "A content-driven expansion waits at the displayed size while the layout is unavailable.");
        Set("_taskbarRowSample", (new Rectangle[] { new(row.Left, row.Top, 500, row.Height), tray }, Environment.TickCount64));
        Place();
        Check(bar.Visible && bar.NativeTaskbarParentVerified && bar.Width == 241,
            "The deferred expansion fits into the next confirmed layout.");

        // Crowded taskbar: leave zero clear gap anywhere in the row.
        Set("_taskbarRowSample", (new Rectangle[] { new(row.Left, row.Top, tray.Left - row.Left, row.Height), tray }, Environment.TickCount64));
        Place();
        Check(bar.Visible && bar.EmbeddedInTaskbar && bar.TaskbarEmbedStatus == "embedded:crowded"
            && bar.TaskbarUltraCompact && bar.Width == GlassDockLayout.UltraCompactWidth
            && row.Contains(bar.ScreenBounds) && bar.ScreenBounds.Right <= tray.Left
            && bar.ObserveDockHealth() == true,
            "Full taskbar shrinks to ultra-compact (+N + tok/s) and pins inside Shell_TrayWnd left of the tray.");

        // Open a 100px gap: fits ultra-compact with no icon overlap, transitioning cleanly out of crowded state.
        Set("_taskbarRowSample", (new Rectangle[] { new(row.Left, row.Top, tray.Left - row.Left - 112, row.Height), tray }, Environment.TickCount64));
        Place();
        Check(bar.Visible && bar.EmbeddedInTaskbar && bar.TaskbarEmbedStatus == "embedded"
            && bar.TaskbarUltraCompact && bar.Width == GlassDockLayout.UltraCompactWidth,
            "Moderate gap keeps ultra-compact in the clear gap without overlap.");

        // Reopen wide space: expands back to full ring dock cleanly.
        Set("_taskbarRowSample", (new Rectangle[] { new(row.Left, row.Top, 500, row.Height), tray }, Environment.TickCount64));
        Place();
        Check(bar.Visible && bar.EmbeddedInTaskbar && bar.TaskbarEmbedStatus == "embedded"
            && !bar.TaskbarUltraCompact && bar.TaskbarFitRingCap is null && bar.Width == 241,
            "Reopened taskbar space restores rings seamlessly.");

        // Detach + refuse native SetParent on crowded row: stays in reserved row as overlay:crowded, never covers unreserved row.
        typeof(BarForm).GetMethod("DetachFromTaskbar", Instance)!.Invoke(bar, ["fixture-detach"]);
        Set("_embedIncompatibleTaskbar", parent.Handle);
        Set("_taskbarRowSample", (new Rectangle[] { new(row.Left, row.Top, tray.Left - row.Left, row.Height), tray }, Environment.TickCount64));
        Place();
        Check(bar.Visible && !bar.EmbeddedInTaskbar && bar.TaskbarEmbedStatus == "overlay:crowded"
            && row.Contains(bar.ScreenBounds) && bar.ObserveDockHealth() == true,
            "Reserved crowded row uses overlay:crowded when native attach is refused.");

        reserved = false;
        Place();
        Check(bar.Visible && !row.IntersectsWith(bar.ScreenBounds) && !bar.TaskbarUltraCompact,
            "Unreserved auto-hide row never receives a top-level crowded overlay.");

        void Set(string field, object value) => typeof(BarForm).GetField(field, Instance)!.SetValue(bar, value);
        void Place() => typeof(BarForm).GetMethod("ApplyDockedTarget", Instance)!.Invoke(bar, [track, bar.Size, anchor, true]);
    }

    private static void ProbeBurstDoesNotStarveAndFailuresEscalate()
    {
        using var fakeTray = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = new(-30000, -30000, 400, 48), Opacity = 0 };
        fakeTray.Show();
        using var bar = new BarForm(new AppSettings { TaskbarDocked = true, EnableAnimations = false },
            QuotaSnapshot.Empty(DateTimeOffset.UtcNow), renderOnly: true, renderDpi: 96) { Location = new(-29500, -30000), Opacity = 0 };
        bar.Show();
        Set("_embedRequested", true);
        Set("_taskbarDocked", true);
        Set("_watchedTaskbar", fakeTray.Handle);
        Set("_taskbarRowHook", (nint)0);
        Set("_taskbarRowProbeAfter", 0L);

        var onChanged = typeof(BarForm).GetMethod("OnTaskbarRowChanged", Instance)!;
        onChanged.Invoke(bar, [(nint)0, 0x8000u, fakeTray.Handle, 0, 0, 0u, 0u]);
        var firstDeadline = (long)Get("_taskbarRowProbeAfter")!;
        Check(firstDeadline > 0, "Initial taskbar notice schedules a settle probe deadline.");
        Thread.Sleep(15);
        onChanged.Invoke(bar, [(nint)0, 0x8003u, fakeTray.Handle, 0, 0, 0u, 0u]);
        var secondDeadline = (long)Get("_taskbarRowProbeAfter")!;
        Check(secondDeadline == firstDeadline, "Subsequent notices during the debounce window do not starve the pending probe.");
        Application.DoEvents();

        var handleFailure = typeof(BarForm).GetMethod("HandleEmbedAttachFailure", Instance)!;
        handleFailure.Invoke(bar, [fakeTray.Handle, "setparent-failed:5"]);
        Check(bar.TaskbarLastEmbedFailure == "setparent-failed:5" && (nint)Get("_embedIncompatibleTaskbar")! == 0,
            "First SetParent failure records diagnostic without blacklisting the taskbar.");
        handleFailure.Invoke(bar, [fakeTray.Handle, "setparent-failed:5"]);
        Check((nint)Get("_embedIncompatibleTaskbar")! == 0, "Second SetParent failure still allows retry.");
        handleFailure.Invoke(bar, [fakeTray.Handle, "setparent-failed:5"]);
        Check((nint)Get("_embedIncompatibleTaskbar")! == fakeTray.Handle, "Three consecutive SetParent failures isolate the taskbar tray handle.");
        handleFailure.Invoke(bar, [fakeTray.Handle + 1, "setparent-failed:5"]);
        Check((int)Get("_embedFailureCount")! == 1 && (nint)Get("_embedIncompatibleTaskbar")! == fakeTray.Handle,
            "A new tray handle resets failure backoff tracking cleanly.");

        void Set(string name, object value) => typeof(BarForm).GetField(name, Instance)!.SetValue(bar, value);
        object? Get(string name) => typeof(BarForm).GetField(name, Instance)!.GetValue(bar);
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
