using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class CodexUsageStrategyTests
{
    internal static void Run(string? capture = null)
    {
        var now = new DateTimeOffset(2026, 11, 4, 6, 9, 0, TimeSpan.Zero);
        var ids = new[] { "plan", "duplicate", "tomorrow", "small", "blocked", "stale", "api" };
        var accounts = ids.Select(id => new CodexAccountInfo(
            id, $"{id}@example.test", id == "api" ? "api_key" : "pro", id == "plan")).ToArray();
        CodexAccountQuota Quota(string id, double used, double hours) => new(
            id, [new QuotaWindow("7d", used, now.AddHours(hours), TimeSpan.FromDays(7))], now);
        var quotas = new[]
        {
            Quota("plan", 9, 35) with
            {
                QuotaIdentity = "shared-plan",
                SubscriptionActiveUntil = now.AddHours(20),
                ResetCreditsAvailable = 3,
            },
            Quota("duplicate", 9, 35) with { QuotaIdentity = "shared-plan" },
            Quota("tomorrow", 45, 22) with
            {
                ResetCreditsAvailable = 1,
                NextResetCreditExpiry = now.AddHours(23),
                NextResetCreditCoversMainQuota = true,
            },
            Quota("small", 99, 0.5),
            Quota("blocked", 100, 0.75),
            Quota("stale", 20, 3) with { CapturedAt = now.AddHours(-1) },
        };
        var strategy = CodexUsageStrategy.Create(accounts, quotas, now);
        Check(strategy.Accounts.Select(row => row.Account.AccountId)
            .SequenceEqual(new[] { "plan", "tomorrow", "small", "blocked", "stale" }),
            "Prioritize usable quotas, group low quota separately, deduplicate shared quota and skip API keys.");
        Check(strategy.Accounts[0].DeadlineKind == CodexStrategyDeadline.Subscription
            && strategy.Accounts[0].Deadline == now.AddHours(20)
            && strategy.Accounts[0].Cards == 3 && strategy.Accounts[0].CardExpiry is null,
            "Use the earlier subscription cutoff and preserve an unknown reset-credit date.");
        Check(strategy.Accounts[1].ResetBeforeCard && strategy.Accounts[1].CanRedeem
            && strategy.Accounts[1].ReviewBy == now.AddHours(22.5),
            "Review eligibility after natural reset with time left before the card expires.");
        Check(strategy.Accounts[3].State == CodexStrategyState.Waiting
            && strategy.Accounts[3].Recovery == now.AddHours(0.75)
            && strategy.Accounts[4].State == CodexStrategyState.Refresh,
            "Zero quota is a recovery event and stale quota is not scheduled.");
        Check(strategy.Urgent.Select(item => item.At).SequenceEqual(
            new[] { now.AddHours(0.5), now.AddHours(20), now.AddHours(22), now.AddHours(23) }),
            "Keep simultaneous cutoffs and exclude recovery times from the deadline badge.");
        var afterReset = CodexUsageStrategy.Create(accounts, quotas, now.AddHours(0.75));
        Check(afterReset.Accounts.Single(row => row.Account.AccountId == "blocked").State == CodexStrategyState.Refresh,
            "Require a new quota observation at the predicted recovery time.");

        using var form = new CodexStrategyPopoverForm();
        using var bitmap = form.RenderForTest(strategy, NativeText.For("zh-CN"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        if (capture is not null) bitmap.Save(capture, System.Drawing.Imaging.ImageFormat.Png);
        Check(bitmap.Width == 640 && bitmap.Height > 280
            && Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, 15)).Distinct().Count() > 1,
            $"The production strategy panel renders the concurrent account sections ({bitmap.Size}).");
        Check(bitmap.GetPixel(0, 0).A == 0 && bitmap.GetPixel(bitmap.Width / 2, bitmap.Height - 3).A > 0,
            "The complete strategy view has rounded transparent corners and an anchor tail.");
        using var scaled = form.RenderForTest(strategy, NativeText.For("zh-CN"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), 192);
        Check(scaled.Size == new Size(bitmap.Width * 2, bitmap.Height * 2),
            "The complete hover page scales its frame and content together.");
        using var side = form.RenderForTest(strategy, NativeText.For("en"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), 96, PopoverTailSide.Left);
        Check(side.Width == bitmap.Width + 8 && side.Height == bitmap.Height - 8
            && side.GetPixel(2, side.Height / 2).A > 0,
            "The anchored strategy page also renders beside a vertical taskbar.");
        if (capture is not null)
            scaled.Save(Path.ChangeExtension(capture, ".192dpi.png"), System.Drawing.Imaging.ImageFormat.Png);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        form.ClientSize = new Size(640, 180);
        var scrollbar = form.Controls.OfType<VScrollBar>().Single();
        Check(form.Region!.IsVisible(scrollbar.Left + scrollbar.Width / 2, scrollbar.Top + 8)
            && scrollbar.Maximum >= scrollbar.LargeChange,
            "A height-limited page keeps its scrollbar inside the rounded surface.");
        typeof(CodexStrategyPopoverForm).GetMethod("OnMouseWheel", flags)!.Invoke(form,
            [new MouseEventArgs(MouseButtons.None, 0, 100, 100, -120)]);
        Check(scrollbar.Value > 0, "The complete hover page can scroll through all account rows.");

        var card = new QuotaCard("plan", ProviderKind.Codex, "Codex", "pro", "#10a37f", true, quotas[0].Windows);
        var snapshot = new QuotaSnapshot([card], [], now) { CodexAccounts = quotas };
        using var bar = new BarForm(new AppSettings { EnableAnimations = false }, snapshot,
            renderOnly: true, renderDpi: 96, codexAccounts: accounts);
        bar.CreateControl();
        using var barBitmap = new Bitmap(bar.ClientSize.Width, bar.ClientSize.Height);
        bar.DrawToBitmap(barBitmap, bar.ClientRectangle);
        Check(!bar.CodexStrategyBoundsForAcceptance.IsEmpty
            && bar.CodexStrategyBoundsForAcceptance.Right <= barBitmap.Width,
            "The strategy button is reachable inside the Codex bar module.");
        var strategyBounds = bar.CodexStrategyBoundsForAcceptance;
        typeof(BarForm).GetMethod("OnMouseMove", flags)!.Invoke(bar,
            [new MouseEventArgs(MouseButtons.None, 0, (int)(strategyBounds.Left + 8), (int)(strategyBounds.Top + 8), 0)]);
        var hoverTimer = (System.Windows.Forms.Timer)typeof(BarForm).GetField("_popoverHoverTimer", flags)!.GetValue(bar)!;
        Check((bool)typeof(BarForm).GetField("_hoverCodexStrategy", flags)!.GetValue(bar)!
            && hoverTimer.Enabled
            && typeof(BarForm).GetField("_hoverHintTarget", flags)!.GetValue(bar)!.ToString() == "None",
            "Hovering the button schedules the full strategy page without the single-line hint.");
        if (capture is not null)
        {
            bar.DrawToBitmap(barBitmap, bar.ClientRectangle);
            barBitmap.Save(Path.ChangeExtension(capture, ".bar.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        typeof(BarForm).GetMethod("OnMouseLeave", flags)!.Invoke(bar, [EventArgs.Empty]);
        Check(!hoverTimer.Enabled, "Leaving before the hover delay cancels the pending page.");
        var withStrategy = bar.Width;
        bar.SetCodexAccounts([]);
        Check(bar.Width == withStrategy - (int)strategyBounds.Width,
            "The strategy button owns additional width instead of shrinking the quota content.");
        bar.SetCodexAccounts(accounts);
        Check(bar.Width == withStrategy, "Refreshing the directory restores the independent button space.");
        Check(!BarForm.ShouldDismissPopover(false, true, false, false, TimeSpan.FromSeconds(1))
            && !BarForm.ShouldDismissPopover(false, false, false, false, TimeSpan.FromMilliseconds(100))
            && BarForm.ShouldDismissPopover(false, false, false, false, TimeSpan.FromMilliseconds(200)),
            "Hover stays open inside the page and tolerates crossing the gap.");
        Check(!BarForm.ShouldDismissPopover(true, false, false, false, TimeSpan.FromSeconds(2))
            && BarForm.ShouldDismissPopover(true, false, true, false, null)
            && BarForm.ShouldDismissPopover(true, true, false, true, null),
            "Pinned pages survive pointer leave and close on outside click or Escape.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
