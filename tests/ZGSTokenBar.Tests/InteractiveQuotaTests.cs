using System.Net;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.Core;

internal static class InteractiveQuotaTests
{
    internal static void Run()
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Get, "Quota observation cannot mutate authentication or usage");
            var account = request.Headers.GetValues("ChatGPT-Account-Id").Single();
            if (!request.RequestUri!.AbsolutePath.EndsWith("/usage"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            Interlocked.Increment(ref UsageReads);
            Check(request.Headers.Authorization?.Parameter == "fixture-" + account, "Header and credential identities agree");
            var body = JsonSerializer.Serialize(new { plan_type = "pro", rate_limit = new {
                primary_window = new { used_percent = account == "account-a" ? 57 : 100,
                    limit_window_seconds = 604800, reset_at = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
