using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Intake;
using ZGSTokenBar.Plugin.Router;

internal static class CrossPopoverHandoffTests
{
    internal static void Run()
    {
        foreach (var dpi in new[] { 96, 192 })
        foreach (var animate in new[] { false, true })
        foreach (var destination in new[] { RouterPlugin.Id, IntakeObserverPlugin.Id })
        {
            var now = DateTimeOffset.UtcNow;
            var card = new QuotaCard("fixture", ProviderKind.Codex, "Codex", "pro", "#10a37f", true,
                [new("7d", 20, now.AddDays(3), TimeSpan.FromDays(7))]) { HasAccountIdentity = true };
            using var bar = new BarForm(new AppSettings { EnableAnimations = false },
                new QuotaSnapshot([card], [], now), renderOnly: true, renderDpi: dpi,
                activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex })
                { Location = new(-30000, -30000), StartPosition = FormStartPosition.Manual, Opacity = 0 };
            using var router = new RouterMonitorForm(observe: false) { Opacity = 0 };
            using var intake = new IntakeObserverForm(true, observeOnShow: false) { Opacity = 0 };
            bar.PluginPopoverRequested += (_, request) =>
            {
                MonitorPanelForm popup = request.PluginId == RouterPlugin.Id ? router : intake;
                popup.ShowFor(bar, request.Anchor, request.Pinned, request.Scale, request.Theme);
                request.Popup = popup;
            };
            bar.SetPluginMiniCards([
                new(RouterPlugin.Id, "Router", RouterModuleTests.Card("idle", 0), new Dictionary<string, string>()),
                new(IntakeObserverPlugin.Id, "Intake", IntakeObserverPlugin.Launcher, new Dictionary<string, string>())]);
            bar.Show();
            using var image = new Bitmap(bar.Width, bar.Height);
            bar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            Set(bar, "_animationsEnabled", animate);
            var target = ((IEnumerable)Get<object>(bar, "_taskbarWindowBounds")).Cast<object>().First();
            var quotaBounds = (RectangleF)target.GetType().GetProperty("Bounds")!.GetValue(target)!;
            var quotaPoint = Center(quotaBounds);
            var quotaScreen = Center(bar.QuotaTargetScreenBounds(quotaBounds));
            var nextBounds = bar.MiniPluginBounds(destination)!.Value;
            var nextPoint = Center(nextBounds);
            var nextScreen = Center(bar.QuotaTargetScreenBounds(nextBounds));
            bar.TrackMiniHover(quotaPoint); bar.ShowHoveredPopover(quotaScreen);
            var quota = Get<QuotaPopoverForm>(bar, "_quotaPopover");
            Check(quota.Visible, "The real native Codex quota hover opens first");
            bar.TrackMiniHover(nextPoint);
            var started = Get<DateTime>(quota, "_exitStarted");
            for (var step = 0; step < 12; step++)
            {
                Thread.Sleep(2);
                bar.TrackMiniHover(nextPoint with { X = nextPoint.X + step % 2 });
            }
            Check(Get<DateTime>(quota, "_exitStarted") == started,
                "Moving over the destination cannot restart the old Codex fade or its leave deadline");
            bar.SetQuotaPaceEstimates(new Dictionary<string, QuotaPaceEstimate>());
            Check(!quota.Visible || quota.IsClosing, "A data refresh cannot resurrect the outgoing quota hover");
            Check(Get<System.Windows.Forms.Timer>(bar, "_popoverHoverTimer").Enabled,
                "Retiring the old popup cannot cancel the destination's pending hover");
            var deadline = Environment.TickCount64 + 5000;
            while (quota.Visible && Environment.TickCount64 < deadline)
            { Application.DoEvents(); bar.TrackMiniHover(nextPoint); Thread.Sleep(5); }
            Check(!quota.Visible, "The outgoing Codex panel finishes closing while the pointer keeps moving");
            bar.ShowHoveredPopover(nextScreen);
            Check(bar.VisiblePluginPopoverId == destination && (destination == RouterPlugin.Id ? router.Visible : intake.Visible),
                "Codex quota hands off to the complete monitor without leaving and re-entering its icon");
            bar.HidePluginPopover(destination);
            bar.RecoverStationaryHover(new(20000, 20000));
            bar.TrackMiniHover(quotaPoint); bar.ShowHoveredPopover(quotaScreen);
            Check(quota.Visible && bar.VisiblePluginPopoverId is null,
                "Returning to Codex clears the old monitor ownership");
            VerifyLeave(bar, quota, quotaScreen,
                () => bar.SetQuotaPaceEstimates(new Dictionary<string, QuotaPaceEstimate>()));
            bar.TrackMiniHover(quotaPoint); bar.ShowHoveredPopover(quotaScreen);
            bar.ActivateMiniTarget(quotaPoint);
            bar.TrackMiniHover(nextPoint); bar.ShowHoveredPopover(nextScreen);
            Check(quota.Visible && bar.VisiblePluginPopoverId is null,
                "A deliberately pinned quota panel retains ownership until explicit dismissal");
            bar.MonitorPopover(quotaScreen, false, false, now.UtcDateTime);
            bar.MonitorPopover(new(20000, 20000), true, false, now.UtcDateTime.AddMilliseconds(1));
            Check(!quota.Visible || quota.IsClosing, "An outside click still dismisses a pinned quota panel");

            Invoke(bar, "UpdateCodexAccountsHover", (quotaBounds, card));
            Invoke(bar, "ShowCodexAccountsPopover", (quotaBounds, card));
            var accounts = Get<CodexAccountsPopoverForm>(bar, "_codexAccountsPopover");
            bar.TrackMiniHover(new(20000, 20000));
            Check(accounts.Visible && !accounts.IsClosing, "Leaving the account anchor allows travel into its popup");
            VerifyLeave(bar, accounts, Center(accounts.Bounds), () => Invoke(bar, "RefreshCodexAccountsPopover"));
            VerifyExitIsStable(accounts, () => accounts.HidePopover(), animate);

            Invoke(bar, "UpdateCodexAccountsHover", (quotaBounds, card));
            Invoke(bar, "ShowCodexAccountsPopover", (quotaBounds, card));
            var sibling = card with { Key = "sibling" };
            Invoke(bar, "UpdateCodexAccountsHover", (quotaBounds, sibling));
            Check(!accounts.Visible || accounts.IsClosing, "Changing accounts retires the old shown account before its leave timer runs");
            bar.MonitorPopover(new(20000, 20000), false, false, DateTime.UtcNow.AddMilliseconds(151));
            bar.ShowHoveredPopover(quotaScreen);
            Check(accounts.Visible && !accounts.IsClosing &&
                Get<(RectangleF Bounds, QuotaCard Card)?>(bar, "_codexPopoverTarget")?.Card.Key == sibling.Key,
                "The outgoing account cannot clear the incoming account's pending hover");

            Invoke(bar, "UpdateSystemUsageHover", true);
            Invoke(bar, "UpdateCodexAccountsHover", new object?[] { null }!);
            Invoke(bar, "UpdateQuotaHover", new object?[] { null }!);
            Check(Get<System.Windows.Forms.Timer>(bar, "_popoverHoverTimer").Enabled,
                "Clearing the previous account target cannot cancel the system hover armed earlier in the same mouse message");
            Invoke(bar, "ShowSystemUsagePopover", false);
            var system = Get<SystemUsagePopoverForm>(bar, "_systemUsagePopover");
            bar.TrackMiniHover(nextPoint);
            VerifyExitIsStable(system, () => system.HidePopover(), animate);
            bar.Close();
        }
    }

    private static void VerifyLeave(BarForm bar, Form popup, Point inside, Action refresh)
    {
        var now = DateTime.UtcNow;
        var outside = new Point(20000, 20000);
        bar.MonitorPopover(inside, false, false, now);
        bar.MonitorPopover(outside, false, false, now.AddMilliseconds(1));
        bar.MonitorPopover(inside, false, false, now.AddMilliseconds(81));
        Check(popup.Visible, "Re-entering a hover popup cancels its leave deadline");
        bar.MonitorPopover(outside, false, false, now.AddMilliseconds(100));
        refresh();
        bar.MonitorPopover(outside, false, false, now.AddMilliseconds(251));
        Check(!popup.Visible || (bool)popup.GetType().GetProperty("IsClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popup)!,
            "Leaving any simple popup closes it after the grace period, even through a data refresh");
    }

    private static void VerifyExitIsStable(Form popup, Action hide, bool animate)
    {
        var started = Get<DateTime>(popup, "_exitStarted");
        Thread.Sleep(2); hide();
        Check(Get<DateTime>(popup, "_exitStarted") == started, "Repeated dismissal preserves the original exit deadline");
        Check(!popup.Visible || animate, "Reduced-motion dismissal is immediate");
    }

    private static void Invoke(object owner, string method, params object[] arguments) =>
        owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);

    private static Point Center(RectangleF bounds) => new((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
    private static Point Center(Rectangle bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    private static T Get<T>(object owner, string field) => (T)owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Set(object owner, string field, object value) => owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
