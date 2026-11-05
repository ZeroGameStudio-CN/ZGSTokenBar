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
        TestResetCreditScheduling(now, capture);
        TestCreditPriority(now, capture);
        TestRenewalAdvice(now, capture);
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
            .SequenceEqual(new[] { "small", "tomorrow", "plan", "blocked", "stale" }),
            "Keep the small imminent balance first, then account for a reset plus credit before the plain subscription cutoff.");
        Check(strategy.Accounts.Select(row => row.Priority).SequenceEqual(new int?[] { 1, 2, 3, null, null }),
            "Usable accounts have explicit contiguous priorities while waiting and stale accounts remain unranked.");
        var cardFirst = CodexUsageStrategy.Create(accounts, quotas.Select(quota =>
            quota.AccountId == "tomorrow" ? quota with { NextResetCreditExpiry = now.AddHours(.25) } : quota).ToArray(), now);
        Check(cardFirst.Accounts[0].Account.AccountId == "tomorrow"
            && cardFirst.Accounts[0].Priority == 1
            && cardFirst.Accounts[0].PriorityReason == CodexStrategyDeadline.ResetCredit
            && cardFirst.Accounts[0].PriorityDeadline == now.AddHours(.25),
            "A known earlier reset-credit cutoff changes the actual account recommendation.");
        var plan = strategy.Accounts.Single(row => row.Account.AccountId == "plan");
        Check(plan.DeadlineKind == CodexStrategyDeadline.Subscription
            && plan.Deadline == now.AddHours(20)
            && plan.Cards == 3 && plan.CardExpiry is null,
            "Use the earlier subscription cutoff and preserve an unknown reset-credit date.");
        var tomorrow = strategy.Accounts.Single(row => row.Account.AccountId == "tomorrow");
        Check(tomorrow.ResetBeforeCard && tomorrow.CanRedeem
            && tomorrow.ReviewBy == now.AddHours(22.5),
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
        Check(form.VisibleTextForAcceptance.Contains("1. s***l@example.test")
            && form.VisibleTextForAcceptance.Contains("可并行 · 订阅")
            && form.VisibleTextForAcceptance.Contains("可并行")
            && form.VisibleTextForAcceptance.Contains("少量 · 短任务")
            && form.VisibleTextForAcceptance.Contains("预测待采样"),
            "The displayed recommendation communicates order, reasons, parallel choices and low-quota limits.");
        var exactDeadline = now.AddMinutes(30).ToLocalTime().ToString("MM-dd HH:mm");
        Check(Occurrences(form.VisibleTextForAcceptance, "22小时后重置") == 1
            && form.VisibleTextForAcceptance.Contains("20小时后到期")
            && form.VisibleTextForAcceptance.Contains("30分0秒后重置")
            && !form.VisibleTextForAcceptance.Contains(exactDeadline)
            && form.DetailAt(new Point(100, 80))?.Contains(exactDeadline) == true
            && form.DetailAt(new Point(-1, -1)) is null,
            "Primary countdowns appear once and exact dates remain accessible in row details.");
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
        var displayQuota = quota with { CardKey = "multiple" };
        var displayPaces = new Dictionary<string, QuotaPaceEstimate>
        {
            [QuotaPaceTracker.SeriesKey("multiple", quota.Windows[0])] = new(QuotaPaceStatus.ResetsBeforeExhaustion,
                Recent: new(TimeSpan.FromHours(1), 5, now.AddHours(16), true, QuotaTrendConfidence.Stable),
                ValidUntil: now.AddMinutes(5)) { ForecastExpiresAt = now.AddMinutes(5) },
            [QuotaPaceTracker.SeriesKey("multiple", quota.Windows[1])] = new(QuotaPaceStatus.ProjectedExhaustion,
                Recent: new(TimeSpan.FromHours(1), 10, now.AddHours(5), false, QuotaTrendConfidence.Stable),
                ValidUntil: now.AddMinutes(5)) { ForecastExpiresAt = now.AddMinutes(5) },
        };
        var displayStrategy = CodexUsageStrategy.Create([account], [displayQuota], now, displayPaces);
        using var panel = new CodexStrategyPopoverForm();
        using var bitmap = panel.RenderForTest(displayStrategy, NativeText.For("zh-CN"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(panel.VisibleTextForAcceptance.Contains("2 张重置卡")
            && panel.VisibleTextForAcceptance.Contains("1 张重置卡")
            && panel.VisibleTextForAcceptance.Contains("18小时后到期")
            && panel.VisibleTextForAcceptance.Contains("10小时内兑卡")
            && panel.VisibleTextForAcceptance.Contains("15小时内兑卡")
            && Occurrences(panel.VisibleTextForAcceptance, "2小时后重置") == 1,
            "The full page exposes simultaneous deadlines and their actual batch sizes.");
        Check(quota.Windows.All(window => panel.VisibleTextForAcceptance.Contains(NativeText.For("zh-CN")
                .QuotaPace(displayPaces[QuotaPaceTracker.SeriesKey("multiple", window)], now, window.ResetsAt).Right)),
            "Each window keeps its own existing exhaustion or reset-first forecast.");
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
        var actionIndex = panel.VisibleTextForAcceptance.IndexOf("先处理", StringComparison.Ordinal);
        Check(actionIndex >= 0 && actionIndex < panel.VisibleTextForAcceptance.IndexOf("暂不可用", StringComparison.Ordinal)
            && panel.VisibleTextForAcceptance.Contains("可兑卡")
            && panel.VisibleTextForAcceptance.Contains("m***e@example.test · 在用"),
            "A near-expiring reset for an exhausted account is an immediate review action, not a buried usage recommendation.");
        var later = quota with
        {
            Windows = [new("7d", 50, now.AddDays(5), TimeSpan.FromDays(7))],
            SubscriptionActiveUntil = null,
            ResetCreditsAvailable = 2,
            ResetCreditBatches = [new(now.AddDays(2), 1, true), new(now.AddDays(3), 1, true)],
        };
        using var laterBitmap = panel.RenderForTest(CodexUsageStrategy.Create([account], [later], now),
            NativeText.For("zh-CN"), QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(Occurrences(panel.VisibleTextForAcceptance, "2天内兑卡") == 1
            && panel.VisibleTextForAcceptance.Contains("3天内兑卡"),
            "A later primary credit deadline appears once without hiding subsequent batches.");
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
        QuotaPaceEstimate Pace(double rate, QuotaWindow window, QuotaTrendConfidence confidence = QuotaTrendConfidence.Stable)
        {
            var exhaustedAt = now.AddHours((100 - window.UsedPercent!.Value) / rate);
            var resetFirst = exhaustedAt >= window.ResetsAt;
            return new(resetFirst ? QuotaPaceStatus.ResetsBeforeExhaustion : QuotaPaceStatus.ProjectedExhaustion,
                Recent: new(TimeSpan.FromHours(1), rate, exhaustedAt, resetFirst, confidence),
                ValidUntil: now.AddMinutes(5)) { ForecastExpiresAt = now.AddMinutes(5) };
        }
        var estimates = new Dictionary<string, QuotaPaceEstimate>
        {
            [QuotaPaceTracker.SeriesKey("early", quotas[0].Windows[0])] = Pace(20, quotas[0].Windows[0]),
            [QuotaPaceTracker.SeriesKey("slow", quotas[1].Windows[0])] = Pace(5, quotas[1].Windows[0]),
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
            var text = NativeText.For("zh-CN");
            Check(pressured.Accounts.All(account => account.Progress.Count == account.Windows.Count)
                && quotas.All(quota => panel.VisibleTextForAcceptance.Contains(text.QuotaPace(
                    estimates[QuotaPaceTracker.SeriesKey(quota.CardKey!, quota.Windows[0])], now, quota.Windows[0].ResetsAt).Right))
                && panel.VisibleTextForAcceptance.Contains("在用")
                && panel.VisibleTextForAcceptance.Contains("周余量"),
                "The strategy reuses each window's existing forecast and identifies active accounts and remaining quota.");
            if (capture is not null) bitmap.Save(Path.ChangeExtension(capture, ".pressure.png"), System.Drawing.Imaging.ImageFormat.Png);
            var earlyKey = QuotaPaceTracker.SeriesKey("early", quotas[0].Windows[0]);
            var cycle = new QuotaPaceEstimate(QuotaPaceStatus.ProjectedExhaustion,
                Cycle: new(0, 0, 20, now.AddHours(1)), ValidUntil: now.AddMinutes(5))
                { ForecastExpiresAt = now.AddMinutes(5) };
            var cycleEstimates = new Dictionary<string, QuotaPaceEstimate>(estimates) { [earlyKey] = cycle };
            using var cycleBitmap = panel.RenderForTest(CodexUsageStrategy.Create(accounts, quotas, now, cycleEstimates),
                text, QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
            Check(panel.VisibleTextForAcceptance.Contains(text.QuotaPace(cycle, now, quotas[0].Windows[0].ResetsAt).Right),
                "Cycle-average forecasts use the same presentation as the existing quota popover.");
            cycleEstimates[earlyKey] = cycle with { ForecastExpiresAt = now.AddSeconds(-1) };
            using var staleBitmap = panel.RenderForTest(CodexUsageStrategy.Create(accounts, quotas, now, cycleEstimates),
                text, QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
            Check(panel.VisibleTextForAcceptance.Contains("预测待刷新"), "Expired forecasts are explicitly unavailable.");
        }
        estimates[QuotaPaceTracker.SeriesKey("slow", quotas[1].Windows[0])] = Pace(80, quotas[1].Windows[0]);
        Check(CodexUsageStrategy.Create(accounts, quotas, now, estimates).Accounts[0].Account.AccountId == "early",
            "Recommendation order responds to a changed measured consumption rate.");
        estimates[QuotaPaceTracker.SeriesKey("slow", quotas[1].Windows[0])] = Pace(5, quotas[1].Windows[0], QuotaTrendConfidence.Provisional);
        Check(CodexUsageStrategy.Create(accounts, quotas, now, estimates).Accounts.Single(row =>
                row.Account.AccountId == "slow").SuggestedStartAt is null,
            "Provisional consumption samples do not invent a start-time forecast.");

        var cards = quotas[1] with
        {
            Windows = [new("7d", 80, now.AddHours(48), TimeSpan.FromDays(7))],
            ResetCreditsAvailable = 3,
            ResetCreditBatches = [new(now.AddHours(10), 3, true)],
        };
        estimates[QuotaPaceTracker.SeriesKey("slow", cards.Windows[0])] = Pace(10, cards.Windows[0]);
        var stacked = CodexUsageStrategy.Create(accounts, [quotas[0], cards], now, estimates);
        Check(stacked.Accounts[0].Account.AccountId == "slow" && stacked.Accounts[0].SuggestedStartAt == now,
            "Several expiring full resets account for consumption between redemptions.");
        var naturalFirst = cards with { Windows = [new("7d", 80, now.AddHours(4), TimeSpan.FromDays(7))] };
        Check(CodexUsageStrategy.Create([accounts[1]], [naturalFirst], now, estimates).Accounts[0].SuggestedStartAt == now,
            "An intervening scheduled reset contributes to the estimated workload before credits expire.");
        using var bar = new BarForm(new AppSettings { EnableAnimations = false },
            new QuotaSnapshot([], [], now) { CodexAccounts = quotas },
            renderOnly: true, codexAccounts: accounts, utcNow: () => now);
        var property = typeof(BarForm).GetProperty("CodexStrategy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check(((CodexUsageStrategy)property.GetValue(bar)!).Accounts[0].Account.AccountId == "early",
            "The initial Bar strategy falls back to known deadlines.");
        estimates[QuotaPaceTracker.SeriesKey("slow", quotas[1].Windows[0])] = Pace(5, quotas[1].Windows[0]);
        bar.SetQuotaPaceEstimates(estimates);
        Check(((CodexUsageStrategy)property.GetValue(bar)!).Accounts[0].Account.AccountId == "slow",
            "New pace observations refresh the strategy through the existing Bar entry point.");
    }

    private static void TestResetCreditScheduling(DateTimeOffset now, string? capture)
    {
        var accounts = new[]
        {
            new CodexAccountInfo("plain", "plain@example.test", "pro", false),
            new CodexAccountInfo("overlap", "overlap@example.test", "pro", true),
        };
        var plain = new CodexAccountQuota("plain", [new("7d", 80, now.AddHours(2), TimeSpan.FromDays(7))], now)
            { CardKey = "plain" };
        var overlap = new CodexAccountQuota("overlap", [new("7d", 80, now.AddHours(4), TimeSpan.FromDays(7))], now)
        {
            CardKey = "overlap", ResetCreditsAvailable = 1,
            ResetCreditBatches = [new(now.AddHours(6), 1, true)],
        };
        QuotaPaceEstimate Pace(double rate, QuotaWindow window)
        {
            var exhausted = now.AddHours((100 - window.UsedPercent!.Value) / rate);
            var resetFirst = exhausted >= window.ResetsAt;
            return new(resetFirst ? QuotaPaceStatus.ResetsBeforeExhaustion : QuotaPaceStatus.ProjectedExhaustion,
                Recent: new(TimeSpan.FromHours(1), rate, exhausted, resetFirst, QuotaTrendConfidence.Stable),
                ValidUntil: now.AddMinutes(5)) { ForecastExpiresAt = now.AddMinutes(5) };
        }
        Dictionary<string, QuotaPaceEstimate> Paces(CodexAccountQuota quota, double rate) => quota.Windows.ToDictionary(
            window => QuotaPaceTracker.SeriesKey(quota.CardKey!, window), window => Pace(rate, window));
        var estimates = Paces(overlap, 10);
        estimates[QuotaPaceTracker.SeriesKey("plain", plain.Windows[0])] = Pace(20, plain.Windows[0]);
        var strategy = CodexUsageStrategy.Create(accounts, [plain, overlap], now, estimates);
        var row = strategy.Accounts[0];
        Check(row.Account.AccountId == "overlap" && row.SuggestedStartAt == now
            && row.PressureEvent?.Kind == CodexStrategyDeadline.ResetCredit
            && row.PressureIncludesReset && row.ConsumptionHours == 12 && row.ConsumptionPressure == 2,
            "A later reset followed by one expiring credit outranks an easier earlier deadline at the observed pace.");
        using var panel = new CodexStrategyPopoverForm();
        using var bitmap = panel.RenderForTest(strategy, NativeText.For("zh-CN"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(panel.VisibleTextForAcceptance.Contains("自然重置与卡到期叠加")
            && panel.AccessibleDescription!.Contains("需约 12 小时")
            && !panel.AccessibleDescription.Contains("自然重置后复查"),
            "The visible reason and detailed consumption estimate explain the combined priority without blindly waiting for reset.");
        if (capture is not null) bitmap.Save(Path.ChangeExtension(capture, ".overlap.png"));
        using var english = panel.RenderForTest(strategy, NativeText.For("en"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), 192);
        Check(panel.VisibleTextForAcceptance.Contains("Reset and credit expiry overlap"), "The combined reason is localized.");
        if (capture is not null) english.Save(Path.ChangeExtension(capture, ".overlap.en.192dpi.png"));

        estimates[QuotaPaceTracker.SeriesKey("overlap", overlap.Windows[0])] = Pace(80, overlap.Windows[0]);
        Check(CodexUsageStrategy.Create(accounts, [plain, overlap], now, estimates).Accounts[0].Account.AccountId == "plain",
            "A fast enough consumption pace removes the need to promote the overlapping deadlines.");
        var noResetBeforeCredit = overlap with { ResetCreditBatches = [new(now.AddHours(3), 1, true)] };
        var creditFirst = CodexUsageStrategy.Create([accounts[1]], [noResetBeforeCredit], now, Paces(overlap, 10)).Accounts[0];
        Check(creditFirst.ConsumptionHours == 12 && creditFirst.PressureIncludesReset && creditFirst.SuggestedStartAt == now,
            "When a credit expires first, reserve time to consume its refill before the following natural reset too.");
        var ending = overlap with { SubscriptionActiveUntil = now.AddHours(6) };
        var expires = CodexUsageStrategy.Create([accounts[1]], [ending], now, Paces(ending, 10)).Accounts[0];
        Check(expires.ConsumptionHours == 22,
            "When the subscription also ends, the last credit refill must be consumed before that same cutoff.");
        var endingAtReset = overlap with { SubscriptionActiveUntil = now.AddHours(4) };
        Check(CodexUsageStrategy.Create([accounts[1]], [endingAtReset], now, Paces(endingAtReset, 10)).Accounts[0].ConsumptionHours == 12,
            "A natural reset exactly at subscription end does not create an extra usable round.");
        var partial = overlap with { ResetCreditBatches = [new(now.AddHours(6), 1, false)] };
        Check(CodexUsageStrategy.Create([accounts[1]], [partial], now, Paces(partial, 10)).Accounts[0].ConsumptionHours == 2,
            "A partial or unconfirmed credit cannot manufacture a full refill in the estimate.");
        var unknownEarly = overlap with { ResetCreditBatches = [new(now.AddHours(1), 1, false)] };
        Check(CodexUsageStrategy.Create([accounts[1]], [unknownEarly], now, Paces(unknownEarly, 10)).Accounts[0].SuggestedStartAt is null,
            "An unquantified early card deadline is not postponed by a later usage estimate.");
        var dual = overlap with
        {
            Windows = [new("5h", 80, now.AddHours(1), TimeSpan.FromHours(5)), new("7d", 40, now.AddHours(20), TimeSpan.FromDays(7))],
            ResetCreditBatches = [new(now.AddHours(3), 1, true)],
        };
        var dualPaces = Paces(dual, 10);
        dualPaces[QuotaPaceTracker.SeriesKey("overlap", dual.Windows[0])] = Pace(20, dual.Windows[0]);
        Check(CodexUsageStrategy.Create([accounts[1]], [dual], now, dualPaces).Accounts[0].ConsumptionHours == 6,
            "A short-window reset replenishes only that window; short and weekly workloads constrain rather than add to each other.");
        var changingBottleneck = overlap with
        {
            Windows = [new("5h", 99, now.AddHours(5), TimeSpan.FromHours(5)), new("7d", 10, now.AddHours(48), TimeSpan.FromDays(7))],
            ResetCreditsAvailable = 2, ResetCreditBatches = [new(now.AddHours(2), 2, true)],
        };
        var changingPaces = Paces(changingBottleneck, 10);
        changingPaces[QuotaPaceTracker.SeriesKey("overlap", changingBottleneck.Windows[0])] = Pace(1, changingBottleneck.Windows[0]);
        Check(CodexUsageStrategy.Create([accounts[1]], [changingBottleneck], now, changingPaces).Accounts[0].ConsumptionHours == 11,
            "Redemption discards nonbinding remainder: one hour of current use plus ten hours between cards, not nineteen pooled hours.");
        dualPaces[QuotaPaceTracker.SeriesKey("overlap", dual.Windows[1])] = Pace(10, dual.Windows[1]) with { ForecastExpiresAt = now };
        Check(CodexUsageStrategy.Create([accounts[1]], [dual], now, dualPaces).Accounts[0].SuggestedStartAt is null,
            "A stale bottleneck forecast falls back to known deadlines instead of inventing consumption speed.");
        Check(CodexUsageStrategy.Create([accounts[1]], [overlap], now.AddHours(4), estimates).Accounts[0].State == CodexStrategyState.Refresh,
            "Forecasting a refill never makes pre-reset observations usable after the reset time.");
    }

    private static void TestCreditPriority(DateTimeOffset now, string? capture)
    {
        var balance = CodexUsageParser.Parse("""
            {"rate_limit_reset_credits":{"available_count":3,"applicable_available_count":0}}
            """);
        Check(balance.ResetCreditsAvailable == 3 && balance.ApplicableResetCredits == 0,
            "The observed usage response distinguishes total reset credits from credits redeemable now.");
        var nested = CodexUsageParser.Parse("""
            {"rate_limit_status":{"rate_limit_reset_credits":{"available_count":0,"applicable_available_count":0}}}
            """);
        Check(nested.ResetCreditsAvailable == 0 && nested.ApplicableResetCredits == 0
            && CodexUsageParser.Parse("{}").ResetCreditsAvailable is null,
            "An authoritative zero survives nested response parsing; an omitted balance remains unknown.");
        foreach (var invalid in new[] { "-1", "1.5", "2147483648", "null" })
            Check(CodexUsageParser.Parse($"{{\"rate_limit_reset_credits\":{{\"available_count\":{invalid}}}}}")
                .ResetCreditsAvailable is null, "Invalid balances cannot create a credit count.");
        Check(CodexUsageParser.Parse("""
            {"rate_limit_reset_credits":{"available_count":1,"applicable_available_count":2}}
            """).ApplicableResetCredits is null, "A contradictory applicable balance is not treated as eligibility.");

        using var mixed = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            reset_credits = new[]
            {
                new { id = "full-a", reset_type = "codex_rate_limits", expires_at = now.AddHours(28).ToUnixTimeSeconds() },
                new { id = "full-b", reset_type = "full", expires_at = now.AddHours(28).ToUnixTimeSeconds() },
                new { id = "partial", reset_type = "other", expires_at = now.AddHours(28).ToUnixTimeSeconds() },
            },
        }));
        var mixedBatches = CockpitCodexQuotaReader.ReadResetCreditBatches(mixed.RootElement, now, 3);
        Check(mixedBatches.Count == 2 && mixedBatches.Single(batch => batch.CoversMainQuota).Count == 2
            && mixedBatches.Single(batch => !batch.CoversMainQuota).Count == 1,
            "Different reset scopes sharing an expiry retain their independent usable quantities.");
        var plain = new CodexAccountInfo("plain", "plain@example.test", "pro", true);
        var cards = new CodexAccountInfo("cards", "cards@example.test", "pro", false);
        CodexAccountQuota Quota(string id, double hours) => new(id,
            [new("7d", 50, now.AddHours(hours), TimeSpan.FromDays(7))], now) { CardKey = id };
        var earlier = Quota("plain", 10);
        var backlog = Quota("cards", 144) with
        {
            ResetCreditsAvailable = 4,
            ResetCreditBatches = [new(now.AddHours(28), 4, true)],
        };
        var strategy = CodexUsageStrategy.Create([plain, cards], [earlier, backlog], now);
        var row = strategy.Accounts[0];
        Check(row.Account.AccountId == "cards" && row.AttentionAt == now.AddHours(8)
            && row.SuggestedStartAt is null && row.ConsumptionHours is null && row.PressureIncludesCredit
            && row.UpcomingEvents.Count == 0 && strategy.Urgent.Count == 1,
            "Four later cards require earlier attention even without pace; the 24h badge does not truncate planning or invent usage hours.");
        var fewer = backlog with { ResetCreditsAvailable = 1, ResetCreditBatches = [new(now.AddHours(28), 1, true)] };
        Check(CodexUsageStrategy.Create([plain, cards], [earlier, fewer], now).Accounts[0].Account.AccountId == "plain",
            "Reducing the card backlog removes its artificial urgency while retaining the earlier natural reset.");
        QuotaPaceEstimate Pace(double rate) => new(QuotaPaceStatus.ProjectedExhaustion,
            Recent: new(TimeSpan.FromHours(1), rate, now.AddHours(50 / rate), false, QuotaTrendConfidence.Stable),
            ValidUntil: now.AddMinutes(5)) { ForecastExpiresAt = now.AddMinutes(5) };
        var paces = new Dictionary<string, QuotaPaceEstimate>
        {
            [QuotaPaceTracker.SeriesKey("plain", earlier.Windows[0])] = Pace(50),
            [QuotaPaceTracker.SeriesKey("cards", backlog.Windows[0])] = Pace(10),
        };
        var forecast = CodexUsageStrategy.Create([plain, cards], [earlier, backlog], now, paces).Accounts[0];
        Check(forecast.Account.AccountId == "cards" && forecast.SuggestedStartAt == now
            && forecast.ConsumptionHours == 35 && forecast.PressureEvent?.At == now.AddHours(28),
            "A known card expiry beyond 24 hours participates in the measured consumption schedule.");
        var overlap = backlog with { Windows = [backlog.Windows[0] with { ResetsAt = now.AddHours(25) }] };
        Check(CodexUsageStrategy.Create([cards], [overlap], now, paces).Accounts[0] is
            { ConsumptionHours: 45, PressureIncludesReset: true, PressureIncludesCredit: true },
            "Later natural resets and credit batches share the same cumulative workload without adding quota windows together.");
        var ending = backlog with { SubscriptionActiveUntil = now.AddHours(26) };
        Check(CodexUsageStrategy.Create([cards], [ending], now, paces).Accounts[0] is
            { ConsumptionHours: 45, PressureEvent.Kind: CodexStrategyDeadline.Subscription },
            "Subscription end bounds both card redemption and consumption of the final refill.");
        var dual = backlog with { Windows = [backlog.Windows[0], new("5h", 40, now.AddHours(30), TimeSpan.FromHours(5))] };
        var incompletePace = CodexUsageStrategy.Create([plain, cards], [earlier, dual], now, paces).Accounts[0];
        Check(incompletePace.Account.AccountId == "cards" && incompletePace.SuggestedStartAt is null
            && incompletePace.ConsumptionHours is null && incompletePace.PressureIncludesCredit,
            "One missing window pace retains relative multi-card pressure without publishing a partial hours estimate.");
        var mixedQuota = backlog with { ResetCreditsAvailable = 3, ResetCreditBatches = mixedBatches };
        var mixedRow = CodexUsageStrategy.Create([cards], [mixedQuota], now, paces).Accounts[0];
        Check(mixedRow.Events.Count(item => item.Kind == CodexStrategyDeadline.ResetCredit) == 2
            && mixedRow.ConsumptionHours == 15,
            "A partial credit neither suppresses full credits at the same expiry nor creates a full refill itself.");
        var partialFirst = mixedQuota with
        {
            ResetCreditBatches = [new(now.AddHours(20), 1, false), new(now.AddHours(28), 2, true)],
        };
        Check(CodexUsageStrategy.Create([cards], [partialFirst], now).Accounts[0].CanRedeem
            && !CodexUsageStrategy.Create([cards], [partialFirst with { ApplicableResetCredits = 0 }], now).Accounts[0].CanRedeem,
            "An earlier partial batch cannot hide an eligible full batch, and authoritative ineligibility overrides the heuristic.");

        var unknown = backlog with { ResetCreditBatches = [], ApplicableResetCredits = 0 };
        var unknownRow = CodexUsageStrategy.Create([cards], [unknown], now).Accounts[0];
        Check(unknownRow.Cards == 4 && unknownRow.UnresolvedCards == 4 && !unknownRow.CanRedeem
            && unknownRow.Events.All(item => item.Kind != CodexStrategyDeadline.ResetCredit)
            && !unknownRow.PressureIncludesCredit,
            "A count alone stays visible without inventing an expiry, reset scope or redeemable quota.");
        using var panel = new CodexStrategyPopoverForm();
        using var bitmap = panel.RenderForTest(CodexUsageStrategy.Create([cards], [unknown], now), NativeText.For("zh-CN"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(panel.VisibleTextForAcceptance.Contains("共 4 张") && panel.VisibleTextForAcceptance.Contains("当前可兑 0 张")
            && panel.VisibleTextForAcceptance.Contains("4 张有效期待核对"),
            "Each account exposes its balance, current applicability and missing expiry rather than hiding them in a global note.");
        if (capture is not null) bitmap.Save(Path.ChangeExtension(capture, ".credit-balance.png"));
        using var workload = panel.RenderForTest(strategy, NativeText.For("zh-CN"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(panel.VisibleTextForAcceptance.Contains("多张卡临期") && !panel.AccessibleDescription!.Contains("需约"),
            "Relative workload ordering explains multiple credits without claiming a measured consumption time.");
        if (capture is not null) workload.Save(Path.ChangeExtension(capture, ".credit-order.png"));
        using var english = panel.RenderForTest(CodexUsageStrategy.Create([cards], [unknown], now), NativeText.For("en"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), 192);
        Check(panel.VisibleTextForAcceptance.Contains("0 redeemable now") && panel.VisibleTextForAcceptance.Contains("Check expiry for 4"),
            "Credit inventory and uncertainty are localized with the production renderer.");
        if (capture is not null) english.Save(Path.ChangeExtension(capture, ".credit-balance.en.192dpi.png"));
    }

    private static void TestRenewalAdvice(DateTimeOffset now, string? capture)
    {
        var account = new CodexAccountInfo("former", "former@example.test", "free", false)
            { SubscriptionActiveUntil = now.AddDays(15) };
        var quota = new CodexAccountQuota(account.AccountId, [new("30d", 0, now.AddDays(30), TimeSpan.FromDays(30))], now)
            { CardKey = "former-key" };
        var recordedReset = now.AddDays(4);
        var tracker = new CodexQuotaTokenTracker();
        Check(tracker.Observe(new("former-key", "7d", TimeSpan.FromDays(7).Ticks, now.AddDays(-1), 84,
            recordedReset, CodexQuotaTokenTracker.ProfileLifetimeSourceKey, 1000), now), "Synthetic prior paid-quota observation is accepted.");
        var restored = new CodexQuotaTokenTracker(JsonSerializer.Deserialize<CodexQuotaTokenHistory>(JsonSerializer.Serialize(tracker.Export())));
        var summaries = restored.ExportSummaries(now).ToDictionary(summary =>
            new CodexQuotaTokenSeriesKey(summary.CardKey, summary.WindowLabel, summary.DurationTicks).ToString());
        var strategy = CodexUsageStrategy.Create([account], [quota], now, tokenSummaries: summaries);
        Check(strategy.Accounts.Count == 0 && strategy.Urgent.Count == 0 && strategy.Renewals.Count == 1
            && strategy.Renewals[0].HighestUsedPercent == 84 && strategy.Renewals[0].RecordedResetAt == recordedReset,
            "A former paid account can expose its historical weekly reset without turning Free monthly limits or stale subscription metadata into usable Pro quota.");
        using var panel = new CodexStrategyPopoverForm();
        using var bitmap = panel.RenderForTest(strategy, NativeText.For("zh-CN"),
            QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(panel.VisibleTextForAcceptance.Contains("续开时机")
            && panel.VisibleTextForAcceptance.Contains("历史最低剩余 16%")
            && panel.VisibleTextForAcceptance.Contains("原定重置")
            && panel.VisibleTextForAcceptance.Contains("后复查")
            && !panel.VisibleTextForAcceptance.Contains("最后记录")
            && !panel.VisibleTextForAcceptance.Contains("暂无付费账号"),
            "Renewal cards show historical quota and reset timing without repeated notes.");
        var hover = panel.DetailAt(new Point(30, 100));
        Check(hover?.Contains("最后记录") == true && hover.Contains("以刷新结果为准"),
            "Record freshness and the renewal caveat remain available on hover.");
        if (capture is not null) bitmap.Save(Path.ChangeExtension(capture, ".renewal.png"));
        foreach (var locale in new[] { "zh-CN", "en" })
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var localized = panel.RenderForTest(strategy, NativeText.For(locale),
                QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette), dpi);
            if (capture is not null) localized.Save(Path.ChangeExtension(capture, $".renewal-{locale}-{dpi}.png"));
        }
        using var afterReset = panel.RenderForTest(CodexUsageStrategy.Create([account], [quota], recordedReset, tokenSummaries: summaries),
            NativeText.For("zh-CN"), QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette));
        Check(panel.VisibleTextForAcceptance.Contains("原定时间已到") && !panel.VisibleTextForAcceptance.Contains("已恢复"),
            "Reaching a historical reset date asks for fresh evidence, not a presumed quota restoration.");
        Check(CodexUsageStrategy.Create([account with { Plan = "pro" }], [quota], now, tokenSummaries: summaries).Renewals.Count == 0
            && CodexUsageStrategy.Create([account], [quota with { CardKey = "other" }], now, tokenSummaries: summaries).Renewals.Count == 0
            && CodexUsageStrategy.Create([account], [quota], now.AddDays(12), tokenSummaries: summaries).Renewals.Count == 0,
            "Active subscriptions, another account's history and obsolete cycles cannot produce renewal advice.");
        using var bar = new BarForm(new AppSettings { EnableAnimations = false },
            new QuotaSnapshot([], [], now) { CodexAccounts = [quota] }, renderOnly: true, codexAccounts: [account], utcNow: () => now);
        var property = typeof(BarForm).GetProperty("CodexStrategy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check(((CodexUsageStrategy)property.GetValue(bar)!).Renewals.Count == 0, "No history means no invented renewal schedule.");
        bar.SetCodexQuotaTokenSummaries(summaries);
        Check(((CodexUsageStrategy)property.GetValue(bar)!).Renewals.Count == 1,
            "Existing historical-summary updates invalidate the strategy so renewal advice reaches the real bar.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static int Occurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;
}
