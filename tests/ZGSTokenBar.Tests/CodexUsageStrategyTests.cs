using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

internal static class CodexUsageStrategyTests
{
    internal static void Run(string? capture = null)
    {
        var now = new DateTimeOffset(2026, 11, 4, 6, 9, 0, TimeSpan.Zero);
        TestMultipleEvents(now, capture);
        TestConsumptionPressure(now, capture);
        var zh = NativeText.For("zh-CN");
        Check(CodexStrategyPopoverForm.Countdown(now.AddHours(3).AddMinutes(20), now, CodexStrategyDeadline.Reset, zh)
                == "3小时20分后重置"
            && CodexStrategyPopoverForm.Countdown(now.AddHours(18), now, CodexStrategyDeadline.Subscription, zh)
                == "18小时后到期"
            && CodexStrategyPopoverForm.Countdown(now.AddSeconds(45.2), now, CodexStrategyDeadline.ResetCredit, zh)
                == "46秒内兑卡",
            "Countdowns retain useful hour/minute precision and count individual seconds near a deadline.");
        Check(CodexStrategyPopoverForm.Countdown(now, now, CodexStrategyDeadline.Reset, zh) == "已到点，等待刷新"
            && CodexStrategyPopoverForm.Countdown(now.AddSeconds(-1), now, CodexStrategyDeadline.Subscription, zh) == "已到期"
            && CodexStrategyPopoverForm.Countdown(null, now, CodexStrategyDeadline.Reset, zh) == "时间待刷新"
            && CodexStrategyPopoverForm.Countdown(now.AddDays(2).AddHours(4), now, CodexStrategyDeadline.ResetCredit, NativeText.For("en"))
                == "Redeem within 2d 4h",
            "Unknown, elapsed and bilingual countdowns never imply a refreshed quota or display negative time.");
        var ids = new[] { "plan", "duplicate", "tomorrow", "small", "blocked", "stale", "api", "free" };
        var accounts = ids.Select(id => new CodexAccountInfo(
            id, $"{id}@example.test", id == "api" ? "api_key" : id == "free" ? "ChatGPT Free" : "pro", id == "plan")).ToArray();
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
            Quota("free", 20, 0.1) with { ResetCreditsAvailable = 5, NextResetCreditExpiry = now.AddMinutes(1) },
        };
        var strategy = CodexUsageStrategy.Create(accounts, quotas, now);
        Check(strategy.Accounts.Select(row => row.Account.AccountId)
            .SequenceEqual(new[] { "plan", "tomorrow", "small", "blocked", "stale" }),
            "Prioritize usable quotas, group low quota separately, deduplicate shared quota and skip API keys.");
        Check(strategy.Accounts.Select(row => row.Priority).SequenceEqual(new int?[] { 1, 2, 3, null, null }),
            "Usable accounts have explicit contiguous priorities while waiting and stale accounts remain unranked.");
        var cardFirst = CodexUsageStrategy.Create(accounts, quotas.Select(quota =>
            quota.AccountId == "tomorrow" ? quota with { NextResetCreditExpiry = now.AddHours(1) } : quota).ToArray(), now);
        Check(cardFirst.Accounts[0].Account.AccountId == "tomorrow"
            && cardFirst.Accounts[0].Priority == 1
            && cardFirst.Accounts[0].PriorityReason == CodexStrategyDeadline.ResetCredit
            && cardFirst.Accounts[0].PriorityDeadline == now.AddHours(1),
            "A known earlier reset-credit cutoff changes the actual account recommendation.");
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
        Check(form.AccessibleDescription!.Contains("1. p***n@example.test")
            && form.AccessibleDescription.Contains("先用 · 订阅有效期结束")
            && form.AccessibleDescription.Contains("可并行 · 自然重置")
            && form.AccessibleDescription.Contains("少量可用"),
            "The displayed recommendation communicates order, reasons, parallel choices and low-quota limits.");
        Check(form.AccessibleDescription.Contains("20小时后到期")
            && form.AccessibleDescription.Contains("22小时后重置")
            && form.AccessibleDescription.Contains("30分0秒后重置")
            && form.AccessibleDescription.Contains("建议 22小时30分内复查"),
            "Recommendations, recovery and reset-credit review use countdowns in the actual page.");
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
        var clock = now;
        using var bar = new BarForm(new AppSettings { EnableAnimations = false }, snapshot,
            renderOnly: true, renderDpi: 96, codexAccounts: accounts, utcNow: () => clock);
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
        var strategyProperty = typeof(BarForm).GetProperty("CodexStrategy", flags)!;
        clock = now.AddSeconds(1);
        bar.RefreshCodexStrategyClock();
        var ticking = (CodexUsageStrategy)strategyProperty.GetValue(bar)!;
        Check(ticking.EvaluatedAt == clock,
            "The visible-page clock recomputes its strategy from the existing observation.");
        using var tickedBitmap = form.RenderForTest(ticking, zh, QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(form.AccessibleDescription!.Contains("29分59秒后重置"),
            "The displayed countdown advances without a new quota response.");
        clock = now.AddMinutes(30);
        bar.RefreshCodexStrategyClock();
        var due = (CodexUsageStrategy)strategyProperty.GetValue(bar)!;
        Check(due.Accounts.Single(row => row.Account.AccountId == "small").State == CodexStrategyState.Refresh
            && due.Accounts.Single(row => row.Account.AccountId == "small").Priority is null,
            "A reset deadline removes the old recommendation until new quota is observed.");
        Check(!BarForm.ShouldDismissPopover(false, true, false, false, TimeSpan.FromSeconds(1))
            && !BarForm.ShouldDismissPopover(false, false, false, false, TimeSpan.FromMilliseconds(100))
            && BarForm.ShouldDismissPopover(false, false, false, false, TimeSpan.FromMilliseconds(200)),
            "Hover stays open inside the page and tolerates crossing the gap.");
        Check(!BarForm.ShouldDismissPopover(true, false, false, false, TimeSpan.FromSeconds(2))
            && BarForm.ShouldDismissPopover(true, false, true, false, null)
            && BarForm.ShouldDismissPopover(true, true, false, true, null),
            "Pinned pages survive pointer leave and close on outside click or Escape.");
    }

    private static void TestMultipleEvents(DateTimeOffset now, string? capture)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            reset_credits = new object[]
            {
                new { id = "a", status = "available", reset_type = "full", expires_at = now.AddHours(10).ToUnixTimeSeconds() },
                new { id = "a", status = "available", reset_type = "full", expires_at = now.AddHours(10).ToUnixTimeSeconds() },
                new { id = "b", status = "available", reset_type = "full", expires_at = now.AddHours(10).ToUnixTimeMilliseconds().ToString() },
                new { id = "c", status = "available", reset_type = "full", expires_at = now.AddHours(15).ToString("O") },
                new { id = "used", status = "used", reset_type = "full", expires_at = now.AddHours(1).ToUnixTimeSeconds() },
                new { id = "old", status = "available", reset_type = "full", expires_at = now.AddHours(-1).ToUnixTimeSeconds() },
                new { id = "conflict", status = "available", expires_at = now.AddHours(1).ToUnixTimeSeconds() },
                new { id = "conflict", status = "used", expires_at = now.AddHours(1).ToUnixTimeSeconds() },
            },
        }));
        var batches = CockpitCodexQuotaReader.ReadResetCreditBatches(document.RootElement, now, 4);
        Check(batches.SequenceEqual(new[]
        {
            new CodexResetCreditBatch(now.AddHours(10), 2, true),
            new CodexResetCreditBatch(now.AddHours(15), 1, true),
        }), "Credit details retain every future expiry batch, deduplicate IDs and reject spent or conflicting records.");
        Check(CockpitCodexQuotaReader.ReadResetCreditBatches(document.RootElement, now, 0).Count == 0
            && CockpitCodexQuotaReader.ReadResetCreditBatches(document.RootElement, now, 1).Count == 0,
            "A zero or conflicting authoritative balance cannot manufacture available credit batches.");
        var account = new CodexAccountInfo("multiple", "multiple@example.test", "pro", true);
        var quota = new CodexAccountQuota(account.AccountId,
        [
            new("5h", 20, now.AddHours(2), TimeSpan.FromHours(5)),
            new("7d", 50, now.AddHours(6), TimeSpan.FromDays(7)),
        ], now)
        {
            SubscriptionActiveUntil = now.AddHours(18),
            ResetCreditsAvailable = 4,
            ResetCreditBatches = batches,
            NextResetCreditExpiry = now.AddHours(10),
        };
        var strategy = CodexUsageStrategy.Create([account], [quota], now);
        var events = strategy.Accounts.Single().UpcomingEvents;
        Check(events.Select(item => item.At).SequenceEqual(new[] { 2, 6, 10, 15, 18 }.Select(hours => now.AddHours(hours)))
            && strategy.Urgent.Count == 5 && strategy.Accounts[0].UnresolvedCards == 1,
            "Both quota windows, every credit batch and the subscription deadline stay visible for the same account.");
        var free = account with { AccountId = "free", Plan = "free" };
        Check(CodexUsageStrategy.Create([free], [quota with { AccountId = "free" }], now).Accounts.Count == 0,
            "Free accounts contribute no strategy records, deadlines or priority.");
        var restored = JsonSerializer.Deserialize<CodexAccountQuota>(JsonSerializer.Serialize(quota))!;
        Check(restored.ResetCreditBatches.SequenceEqual(batches),
            "Credit batches survive the existing snapshot serialization path.");
        var legacy = JsonSerializer.Deserialize<CodexAccountQuota>(
            """{"AccountId":"legacy","Windows":[],"ResetCreditsAvailable":3}""")!;
        Check(legacy.ResetCreditBatches.Count == 0, "Older snapshots remain readable without a credit-batch field.");
        using var panel = new CodexStrategyPopoverForm();
        using var bitmap = panel.RenderForTest(strategy, NativeText.For("zh-CN"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(panel.AccessibleDescription!.Contains("重置卡 · 2 张")
            && panel.AccessibleDescription.Contains("重置卡 · 1 张")
            && panel.AccessibleDescription.Contains("18小时后到期")
            && panel.AccessibleDescription.Contains("10小时内兑卡")
            && panel.AccessibleDescription.Contains("15小时内兑卡"),
            "The full page exposes simultaneous deadlines and their actual batch sizes.");
        if (capture is not null) bitmap.Save(Path.ChangeExtension(capture, ".multiple.png"), System.Drawing.Imaging.ImageFormat.Png);
        var needsCard = quota with
        {
            Windows = [new("7d", 100, now.AddDays(2), TimeSpan.FromDays(7))],
            ResetCreditsAvailable = 1,
            ResetCreditBatches = [new(now.AddMinutes(20), 1, true)],
            NextResetCreditExpiry = now.AddMinutes(20),
        };
        using var urgentBitmap = panel.RenderForTest(CodexUsageStrategy.Create([account], [needsCard], now),
            NativeText.For("zh-CN"), QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(panel.AccessibleDescription!.IndexOf("现在先复查重置卡", StringComparison.Ordinal)
                < panel.AccessibleDescription.IndexOf("等待恢复或刷新", StringComparison.Ordinal)
            && panel.AccessibleDescription.Contains("可兑卡"),
            "A near-expiring reset for an exhausted account is an immediate review action, not a buried usage recommendation.");
    }

    private static void TestConsumptionPressure(DateTimeOffset now, string? capture)
    {
        var accounts = new[]
        {
            new CodexAccountInfo("early", "early@example.test", "pro", false),
            new CodexAccountInfo("slow", "slow@example.test", "pro", true),
        };
        CodexAccountQuota Quota(string id, double used, double hours) => new(
            id, [new("7d", used, now.AddHours(hours), TimeSpan.FromDays(7))], now) { CardKey = id };
        var quotas = new[] { Quota("early", 80, 2), Quota("slow", 20, 12) };
        QuotaPaceEstimate Pace(double rate, QuotaTrendConfidence confidence = QuotaTrendConfidence.Stable) =>
            new(QuotaPaceStatus.ProjectedExhaustion,
                Recent: new(TimeSpan.FromHours(1), rate, now.AddHours(1), false, confidence),
                ValidUntil: now.AddMinutes(5)) { ForecastExpiresAt = now.AddMinutes(5) };
        var estimates = new Dictionary<string, QuotaPaceEstimate>
        {
            [QuotaPaceTracker.SeriesKey("early", quotas[0].Windows[0])] = Pace(20),
            [QuotaPaceTracker.SeriesKey("slow", quotas[1].Windows[0])] = Pace(5),
        };
        var chronological = CodexUsageStrategy.Create(accounts, quotas, now);
        var pressured = CodexUsageStrategy.Create(accounts, quotas, now, estimates);
        Check(chronological.Accounts[0].Account.AccountId == "early"
            && pressured.Accounts[0].Account.AccountId == "slow"
            && pressured.Accounts[0].SuggestedStartAt == now,
            "Measured slow consumption can require starting before an account with an earlier but easier deadline.");
        using (var panel = new CodexStrategyPopoverForm())
        using (var bitmap = panel.RenderForTest(pressured, NativeText.For("zh-CN"),
                   QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette)))
        {
            Check(panel.AccessibleDescription!.Contains("按近期节奏，建议现在使用"),
                "A pace-based recommendation exposes its reason.");
            if (capture is not null) bitmap.Save(Path.ChangeExtension(capture, ".pressure.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        estimates[QuotaPaceTracker.SeriesKey("slow", quotas[1].Windows[0])] = Pace(80);
        Check(CodexUsageStrategy.Create(accounts, quotas, now, estimates).Accounts[0].Account.AccountId == "early",
            "Recommendation order responds to a changed measured consumption rate.");
        estimates[QuotaPaceTracker.SeriesKey("slow", quotas[1].Windows[0])] = Pace(5, QuotaTrendConfidence.Provisional);
        Check(CodexUsageStrategy.Create(accounts, quotas, now, estimates).Accounts.Single(row =>
                row.Account.AccountId == "slow").SuggestedStartAt is null,
            "Provisional consumption samples do not invent a start-time forecast.");

        var cards = quotas[1] with
        {
            Windows = [new("7d", 80, now.AddHours(48), TimeSpan.FromDays(7))],
            ResetCreditsAvailable = 3,
            ResetCreditBatches = [new(now.AddHours(10), 3, true)],
        };
        estimates[QuotaPaceTracker.SeriesKey("slow", cards.Windows[0])] = Pace(10);
        var stacked = CodexUsageStrategy.Create(accounts, [quotas[0], cards], now, estimates);
        Check(stacked.Accounts[0].Account.AccountId == "slow" && stacked.Accounts[0].SuggestedStartAt == now,
            "Several expiring full resets account for consumption between redemptions.");
        var naturalFirst = cards with { Windows = [new("7d", 80, now.AddHours(4), TimeSpan.FromDays(7))] };
        Check(CodexUsageStrategy.Create([accounts[1]], [naturalFirst], now, estimates).Accounts[0].SuggestedStartAt == now.AddHours(2),
            "An intervening scheduled reset is re-evaluated from the next observed round instead of counting both refills in advance.");
        using var bar = new BarForm(new AppSettings { EnableAnimations = false },
            new QuotaSnapshot([], [], now) { CodexAccounts = quotas },
            renderOnly: true, codexAccounts: accounts, utcNow: () => now);
        var property = typeof(BarForm).GetProperty("CodexStrategy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check(((CodexUsageStrategy)property.GetValue(bar)!).Accounts[0].Account.AccountId == "early",
            "The initial Bar strategy falls back to known deadlines.");
        estimates[QuotaPaceTracker.SeriesKey("slow", quotas[1].Windows[0])] = Pace(5);
        bar.SetQuotaPaceEstimates(estimates);
        Check(((CodexUsageStrategy)property.GetValue(bar)!).Accounts[0].Account.AccountId == "slow",
            "New pace observations refresh the strategy through the existing Bar entry point.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
