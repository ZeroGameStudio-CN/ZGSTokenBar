using System.Net;
using System.Drawing;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.Core;
using ZGSTokenBar.App;

internal static class InteractiveQuotaTests
{
    internal static void Run(string? captures = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "zgstokenbar-interactive-quota-" + Guid.NewGuid().ToString("N"));
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        Directory.CreateDirectory(root);
        try
        {
            var first = Home("first", "account-a");
            var second = Home("second", "account-b");
            var duplicate = Home("duplicate", "account-a");
            Environment.SetEnvironmentVariable("CODEX_HOME", second);
            var registry = Path.Combine(root, "interactive.json");
            File.WriteAllText(registry, JsonSerializer.Serialize(new { instances = new[] {
                new { codexHome = first }, new { codexHome = second }, new { codexHome = duplicate },
                new { codexHome = "relative-home" } } }));
            File.WriteAllText(Path.Combine(root, "codex_instances.json"),
                """{"instances":[{"bindAccountId":"obsolete-a","lastPid":12}],"defaultSettings":{"bindAccountId":"obsolete-b","lastPid":13}}""");
            using var handler = new Handler();
            using var http = new HttpClient(handler);
            var service = new CodexQuotaService(http, root, registry);
            var result = service.FetchAsync(default).GetAwaiter().GetResult();
            Check(result.Cards.Count == 2, "Two registered interactive accounts survive stale launcher process IDs");
            Check(result.Cards.Select(card => card.Key).Distinct().Count() == 2, "Each account has its own stable identity");
            Check(result.Cards.SelectMany(card => card.Windows).Select(window => window.UsedPercent)
                .Order().SequenceEqual(new double?[] { 57, 100 }), "Live quota belongs to the authenticated account");
            Check(handler.UsageReads == 2, "Duplicate registered homes do not duplicate quota reads");
            Check(result.Cards.All(card => card.HasAccountIdentity), "Live accounts retain explicit identity");
            var previous = QuotaCoordinator.MergeResults([result], null, DateTimeOffset.UtcNow);
            handler.FailSecond = true;
            var partial = service.FetchAsync(default).GetAwaiter().GetResult();
            var merged = QuotaCoordinator.MergeResults([partial], previous, DateTimeOffset.UtcNow);
            Check(merged.Cards.Count == 2 && merged.Cards.Count(card => card.Windows.All(window => window.UsedPercent is null)) == 1,
                "A partial quota failure retains both accounts and marks only the failed one unknown");
            using (var bar = new BarForm(new AppSettings(), merged, renderOnly: true, renderDpi: 96,
                activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex }))
            {
                Paint("failed-account-ring-96.png");
                var width = bar.Width;
                Check(bar.VisibleCodexAccountCount == 2, "Both account rings remain painted during a quota failure");
                handler.FailSecond = false;
                var recovered = service.FetchAsync(default).GetAwaiter().GetResult();
                bar.SetSnapshot(QuotaCoordinator.MergeResults([recovered], merged, DateTimeOffset.UtcNow));
                Paint("recovered-account-ring-96.png");
                Check(bar.VisibleCodexAccountCount == 2 && bar.Width == width,
                    "Recovery reuses the same two ring slots without layout changes");

                void Paint(string name)
                {
                    using var image = new Bitmap(bar.Width, bar.Height);
                    bar.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    if (captures is not null)
                    {
                        Directory.CreateDirectory(captures);
                        image.Save(Path.Combine(captures, name));
                    }
                }
            }
            File.WriteAllText(registry, """{"instances":[{"codexHome":"relative-home"}]}""");
            Check(InteractiveCodexHomes.Read(registry).Count == 0, "Relative registry paths are never probed");
            File.WriteAllText(registry, "invalid");
            Check(InteractiveCodexHomes.Read(registry).Count == 0, "Malformed registries fail closed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            Directory.Delete(root, recursive: true);
        }

        string Home(string name, string account)
        {
            var home = Path.Combine(root, name); Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "auth.json"), JsonSerializer.Serialize(new {
                tokens = new { access_token = "fixture-" + account, account_id = account } }));
            return home;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int UsageReads;
        internal bool FailSecond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Get, "Quota observation cannot mutate authentication or usage");
            var account = request.Headers.GetValues("ChatGPT-Account-Id").Single();
            if (!request.RequestUri!.AbsolutePath.EndsWith("/usage"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            Interlocked.Increment(ref UsageReads);
            Check(request.Headers.Authorization?.Parameter == "fixture-" + account, "Header and credential identities agree");
            if (FailSecond && account == "account-b")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var body = JsonSerializer.Serialize(new { plan_type = "pro", rate_limit = new {
                primary_window = new { used_percent = account == "account-a" ? 57 : 100,
                    limit_window_seconds = 604800, reset_at = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
