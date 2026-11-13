using System.Drawing;
using System.Drawing.Imaging;
using ZGSTokenBar.App;
using ZGSTokenBar.Core;

/// <summary>
/// Offscreen renders of the panels reviewed for layout (tok/s, system, Antigravity and Grok hovers)
/// with realistic, fully populated fixtures: <c>--panel-captures &lt;dir&gt;</c>.
/// </summary>
internal static class PanelCaptures
{
    internal static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var at = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(at);
        var hourly = Enumerable.Range(0, 24).Select(hour => (long)(40_000_000 + 60_000_000 * Math.Abs(Math.Sin(hour / 3.0)) + hour * 2_000_000)).ToArray();
        CodexConversationTurn Turn(int index, string title, string model, string effort, long output, double elapsed, double measured, double estimated) =>
            new($"session-{index:0000}", $"turn-{index}", model, effort, at.AddSeconds(-elapsed), at, output, elapsed, false, true)
            {
                Title = title,
                LiveRate = measured + estimated > 0 ? new ConversationOutputRate($"session-{index:0000}", $"turn-{index}", measured, estimated) : null,
            };
        var turns = new[]
        {
            Turn(1, "重构 Glass dock 渲染与图标缓存", "gpt-5.5", "high", 18_420, 312, 61.4, 0),
            Turn(2, "Fix provider refresh cadence", "gpt-5.5", "medium", 7_210, 95, 0, 38.2),
            Turn(3, "ZOS-1088 进入戰鬥後無法看到白色提示", "gpt-5.5", "high", 12_400, 180, 0, 37.4),
            Turn(4, "查找 DreamFX 的 Unity 版本与迁移路径", "gpt-5.5", "medium", 6_800, 140, 21.6, 0),
            Turn(5, "推进 gd 关卡导出与校验流水线", "gpt-5.5", "medium", 9_100, 220, 0, 36.6),
            Turn(6, "zgs-ops-workflow · b7b019d4 重试策略", "gpt-5.5", "low", 3_400, 60, 13.1, 0),
            Turn(7, "立绘精细拆分与离线动作烘焙", "gpt-5.5", "high", 22_000, 410, 9.4, 0),
            Turn(8, "Router 监视面板与速率卡视觉对齐", "gpt-5.5", "medium", 5_200, 88, 0, 17.8),
        };
        foreach (var locale in new[] { "zh-CN", "en" })
        {
            var text = NativeText.For(locale);
            var theme = QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette);
            var today = DateOnly.FromDateTime(new DateTime(2026, 11, 13));
            var daily = ActivityFixture(today, TokenActivityGrain.Daily);
            var weekly = daily with { Grain = TokenActivityGrain.Weekly };
            var rateContent = new SystemUsagePopoverContent(snapshot, false, true)
            {
                TodayTokens = 1_840_000_000,
                HourlyTokens = hourly,
                Cache = new TokenCacheStats(1_800_000_000, 18_000_000, 99.0, 98.4),
                Activity = daily,
            };
            Save(directory, $"panel-rates-{locale}.png", popover => popover.RenderForTest(rateContent, text, theme, 144));
            Save(directory, $"panel-rates-sessions-{locale}.png", popover => popover.RenderForTest(rateContent with { Conversations = turns }, text, theme, 144));
            Save(directory, $"panel-rates-activity-daily-{locale}.png", popover => popover.RenderForTest(rateContent with { Conversations = turns }, text, theme, 144));
            Save(directory, $"panel-rates-activity-weekly-{locale}.png", popover => popover.RenderForTest(rateContent with { Conversations = turns, Activity = weekly }, text, theme, 144));
            Save(directory, $"panel-system-{locale}.png", popover => popover.RenderForTest(new SystemUsagePopoverContent(snapshot, false), text, theme, 144));
        }

        var antigravity = UsageProviderEngine.Snapshot(UsageProviderRegistry.All.First(p => p.Definition.Id == UsageProviderIds.Antigravity).Definition,
        [
            new UsageAccountSnapshot("agy-cli", "someone@example.com", null, "agy-cli",
            [
                new UsageWindow("argon-7d", "Argon · 7d", 20, at.AddDays(6), TimeSpan.FromDays(7)) { Highlighted = true },
                new UsageWindow("gemini-5h", "Gemini · 5h", 1, at.AddHours(3), TimeSpan.FromHours(5)),
                new UsageWindow("gemini-7d", "Gemini · 7d", 1, at.AddDays(4), TimeSpan.FromDays(7)),
                new UsageWindow("claude-7d", "Claude/GPT · 7d", 96.4, at.AddDays(4), TimeSpan.FromDays(7)),
                new UsageWindow("claude-5h", "Claude/GPT · 5h", 0, at.AddHours(4), TimeSpan.FromHours(5)),
            ], null, null, [], UsageFreshness.Current, at, null)
            {
                // Claude/GPT · 7d is nearly exhausted while the bar shows Argon: kept as the provider warning state.
                Warning = true,
            },
        ]);
        var grok = UsageProviderEngine.Snapshot(UsageProviderRegistry.All.First(p => p.Definition.Id == UsageProviderIds.Grok).Definition,
        [
            new UsageAccountSnapshot("grok-account", "user@example.test", "SuperGrok Heavy", "billing",
                [new UsageWindow("credits", "Weekly", 81, at.AddDays(6.5), TimeSpan.FromDays(7))],
                null, null, [], UsageFreshness.Current, at, null)
            {
                Period = new UsageSubscriptionPeriod(at.AddDays(-2), at.AddDays(5), "billing", "fixture"),
            },
        ]);
        var codex = new QuotaSnapshot(
            [
                new QuotaCard("codex", ProviderKind.Codex, "Codex", "pro", "#10a37f", true,
                [
                    new QuotaWindow("5h", 22, at.AddHours(2), TimeSpan.FromHours(5)),
                    new QuotaWindow("7d", 62.4, at.AddDays(1.5), TimeSpan.FromDays(7)),
                ]),
            ],
            [new ProviderHealth(ProviderKind.Codex, true, "current", ProviderHealthCode.Current)],
            at);
        BarForm Bar(QuotaSnapshot quota, params UsageProviderSnapshot[] providers) => BarAt(144, quota, providers);
        BarForm BarAt(int dpi, QuotaSnapshot quota, params UsageProviderSnapshot[] providers)
        {
            var form = new BarForm(new AppSettings { Locale = "zh-CN", UseTaskbarRings = true, EnableAnimations = false },
                quota, renderOnly: true, renderDpi: dpi);
            form.SetUsageBoard(new UsageBoard(providers));
            form.SetPluginMiniCards(providers.Select(provider => new PluginMiniCardView(provider.ProviderId, provider.DisplayName,
                UsageProjection.MiniCard(provider, true), new Dictionary<string, string>(StringComparer.Ordinal))).ToArray());
            return form;
        }
        void Hover(BarForm form, string providerId, string name)
        {
            using var bitmap = form.RenderGlassRingPopoverForAcceptance(providerId, 144, at);
            if (bitmap is null) { Console.WriteLine($"no hover for {providerId}"); return; }
            var path = Path.Combine(directory, $"panel-hover-{name}.png");
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine(path);
        }
        foreach (var provider in new[] { antigravity, grok })
        {
            using var form = Bar(QuotaSnapshot.Empty(at), provider);
            Hover(form, provider.ProviderId, provider.DisplayName.ToLowerInvariant());
        }
        using (var form = Bar(codex))
            Hover(form, UsageProviderIds.Codex, "codex");

        // Collapsed bar (offscreen) with amber (Codex 37%), green (Antigravity 80%) and red (Grok 19%)
        // rings, each carrying its pace tick, at 1x / 1.5x / 2x / 3x.
        foreach (var dpi in new[] { 96, 144, 192, 288 })
        {
            using var bar = BarAt(dpi, codex, antigravity, grok);
            bar.CreateControl();
            using var bitmap = new Bitmap(bar.ClientSize.Width, bar.ClientSize.Height, PixelFormat.Format32bppPArgb);
            bar.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var path = Path.Combine(directory, $"panel-bar-collapsed-{dpi}dpi.png");
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine(path);
        }

        RouterModuleTests.CaptureMonitor(directory);
    }

    internal static TokenActivityView ActivityFixture(DateOnly today, TokenActivityGrain grain)
    {
        var byDay = new Dictionary<DateOnly, long>();
        var start = TokenActivityCalendar.StartMonday(today, TokenActivityView.DefaultWeekCount);
        for (var i = 0; i < TokenActivityView.DefaultWeekCount * 7; i++)
        {
            var date = start.AddDays(i);
            if (date > today) break;
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var wave = 0.55 + 0.45 * Math.Sin(i / 5.5);
            var spike = i % 11 == 0 ? 1.8 : 1;
            var tokens = i % 17 == 3 ? 0 : (long)((weekend ? 80_000_000d : 420_000_000d) * wave * spike);
            byDay[date] = tokens;
        }
        return TokenActivityCalendar.FromDailyTotals(byDay, today, 743_000_000_000, grain: grain);
    }

    private static SystemUsageSnapshot Snapshot(DateTimeOffset at) =>
        new SystemUsageSnapshot(34, 32UL << 30, 32UL << 30, 64UL << 30, 12, "3D", 2, 16, at)
        {
            NetworkReceiveBytesPerSecond = 12.5 * 1024 * 1024, NetworkSendBytesPerSecond = 128 * 1024, NetworkInterfaces = 1,
            NetworkHistory = new(at.AddDays(-13).AddHours(-1), 1_352_400_000_000, 1_539_300_000_000, at, false),
            LiveRate = new LiveOutputRate(at, 53.2, 33.2, 20, 2, 1, 4, 0), ObservedOutputTokensPerSecond = 53.2, OutputRateCapturedAt = at,
            CumulativeTokens = 735_400_000_000,
            TopProcesses =
            [
                new("Memory Compression", 1, 0, 15_800_000_000, 0),
                new("Unity", 10, 3, 15_100_000_000, 0),
                new("dotnet", 13, 0, 4_800_000_000, 0),
                new("ChatGPT", 27, 0, 3_300_000_000, .5),
                new("dwm", 1, .5, 64 * 1_048_576, 3),
            ],
        };

    private static void Save(string directory, string name, Func<SystemUsagePopoverForm, Bitmap> render)
    {
        using var popover = new SystemUsagePopoverForm();
        using var bitmap = render(popover);
        var path = Path.Combine(directory, name);
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height}");
    }
}
