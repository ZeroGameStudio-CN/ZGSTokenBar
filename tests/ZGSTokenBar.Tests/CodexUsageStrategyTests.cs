using System.Drawing;
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
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
