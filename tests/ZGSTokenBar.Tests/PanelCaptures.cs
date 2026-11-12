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
        var at = DateTimeOffset.Parse("2026-11-12T07:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
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
        };
        foreach (var locale in new[] { "zh-CN", "en" })
        {
            var text = NativeText.For(locale);
            var theme = QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette);
            var rateContent = new SystemUsagePopoverContent(snapshot, false, true)
            {
                TodayTokens = 1_840_000_000,
                HourlyTokens = hourly,
                Cache = new TokenCacheStats(1_800_000_000, 18_000_000, 99.0, 98.4),
            };
            Save(directory, $"panel-rates-{locale}.png", popover => popover.RenderForTest(rateContent, text, theme, 144));
            Save(directory, $"panel-rates-sessions-{locale}.png", popover => popover.RenderForTest(rateContent with { Conversations = turns }, text, theme, 144));
            Save(directory, $"panel-system-{locale}.png", popover => popover.RenderForTest(new SystemUsagePopoverContent(snapshot, false), text, theme, 144));
        }

        var antigravity = UsageProviderEngine.Snapshot(UsageProviderRegistry.All.First(p => p.Definition.Id == UsageProviderIds.Antigravity).Definition,
        [
            new UsageAccountSnapshot("agy-cli", "someone@example.com", null, "agy-cli",
            [
                new UsageWindow("argon-7d", "Argon · 7d", 20, at.AddDays(4), TimeSpan.FromDays(7)) { Highlighted = true },
                new UsageWindow("gemini-5h", "Gemini · 5h", 1, at.AddHours(3), TimeSpan.FromHours(5)),
                new UsageWindow("gemini-7d", "Gemini · 7d", 1, at.AddDays(4), TimeSpan.FromDays(7)),
                new UsageWindow("claude-7d", "Claude/GPT · 7d", 96.4, at.AddDays(4), TimeSpan.FromDays(7)),
                new UsageWindow("claude-5h", "Claude/GPT · 5h", 0, at.AddHours(4), TimeSpan.FromHours(5)),
            ], null, null, [], UsageFreshness.Current, at, null),
        ]);
        var grok = UsageProviderEngine.Snapshot(UsageProviderRegistry.All.First(p => p.Definition.Id == UsageProviderIds.Grok).Definition,
        [
            new UsageAccountSnapshot("grok-account", "user@example.test", "SuperGrok Heavy", "billing",
                [new UsageWindow("credits", "Weekly", 81, at.AddDays(5), TimeSpan.FromDays(7))],
                null, null, [], UsageFreshness.Current, at, null)
            {
                Period = new UsageSubscriptionPeriod(at.AddDays(-2), at.AddDays(5), "billing", "fixture"),
            },
        ]);
        foreach (var provider in new[] { antigravity, grok })
        {
            using var form = new BarForm(new AppSettings { Locale = "zh-CN", UseTaskbarRings = true, EnableAnimations = false },
                QuotaSnapshot.Empty(at), renderOnly: true, renderDpi: 144);
            form.SetUsageBoard(new UsageBoard([provider]));
            form.SetPluginMiniCards([new PluginMiniCardView(provider.ProviderId, provider.DisplayName, UsageProjection.MiniCard(provider, true),
                new Dictionary<string, string>(StringComparer.Ordinal))]);
            using var bitmap = form.RenderGlassRingPopoverForAcceptance(provider.ProviderId, 144, at);
            if (bitmap is null) { Console.WriteLine($"no hover for {provider.ProviderId}"); continue; }
            var path = Path.Combine(directory, $"panel-hover-{provider.DisplayName.ToLowerInvariant()}.png");
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine(path);
        }
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
