using System.Net;
using System.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.Tests;

public static class UsageProviderFixtureTests
{
    private const string Secret = "test-token-not-real";

    public static int Run(string[]? args = null)
    {
        VerifyParsers();
        VerifyLedger();
        VerifyAccounts();
        VerifyEndpoints();
        VerifyEngine().GetAwaiter().GetResult();
        VerifyProjectionAndSvg();
        VerifyGlassDock();
        if (args is { Length: 1 }) WriteGallery(args[0]);
        Console.WriteLine("PASS usage provider fixtures");
        return 0;
    }

    public static void WriteGallery(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "usage-before.svg"), UsageBoardSvg.Before(true));
        var board = SampleBoard();
        File.WriteAllText(Path.Combine(directory, "usage-after-dark.svg"), UsageBoardSvg.Render(board, UsageThemes.Dark, false, true));
        File.WriteAllText(Path.Combine(directory, "usage-after-light.svg"), UsageBoardSvg.Render(board, UsageThemes.Light, false, true));
        File.WriteAllText(Path.Combine(directory, "usage-after-compact.svg"), UsageBoardSvg.Render(board, UsageThemes.Dark, true, true));
        File.WriteAllText(Path.Combine(directory, "usage-taskbar-dark.svg"), UsageBoardSvg.Taskbar(board, UsageThemes.Dark, true));
        File.WriteAllText(Path.Combine(directory, "usage-taskbar-light.svg"), UsageBoardSvg.Taskbar(board, UsageThemes.Light, true));
        var dock = GlassDockLayout.Plan(GalleryRings());
        File.WriteAllText(Path.Combine(directory, "glass-dock-dark.svg"), GlassDockSvg.Pill(dock, false, 85, true));
        File.WriteAllText(Path.Combine(directory, "glass-dock-light.svg"), GlassDockSvg.Pill(dock, true, 85, false));
        File.WriteAllText(Path.Combine(directory, "glass-dock-flyout.svg"), GlassDockSvg.Flyout(GlassDockComposer.Groups(GalleryRings(), DateTimeOffset.Parse("2026-11-10T12:00:00Z"), true), false, null));
    }

    private static GlassDockRing[] GalleryRings() =>
    [
        new(UsageProviderIds.Codex, "Codex", "账号1", 1, 16, DateTimeOffset.Parse("2026-11-10T14:14:00Z"), null, "#10a37f"),
        new(UsageProviderIds.Codex, "Codex", "账号2", 2, 7, DateTimeOffset.Parse("2026-11-10T14:14:00Z"), null, "#10a37f"),
        new(UsageProviderIds.Codex, "Codex", "账号3", 3, 5, DateTimeOffset.Parse("2026-11-10T14:14:00Z"), null, "#10a37f"),
        new(UsageProviderIds.Claude, "Claude", "Claude", 0, 72, DateTimeOffset.Parse("2026-11-16T00:00:00Z"), null, "#d97757"),
        new(UsageProviderIds.Grok, "Grok", "Grok", 0, 40, DateTimeOffset.Parse("2026-11-10T23:20:00Z"), null, "#111113"),
        new("deepseek", "DeepSeek", "DeepSeek", 0, null, null, "¥23.5", "#1aa89a"),
    ];

    private static void VerifyParsers()
    {
        var grok = UsageParsers.ParseGrok("""
            {
              "config": {
                "creditUsagePercent": 42,
                "currentPeriod": {"start": "2026-11-03T00:00:00Z", "end": "2026-11-10T00:00:00Z"},
                "prepaidBalance": {"val": 1446},
                "onDemandUsed": {"val": 250},
                "onDemandCap": {"val": 1000},
                "productUsage": [
                  {"product": "GrokBuild", "usagePercent": 40},
                  {"product": "GrokChat", "usagePercent": 2},
                  {"product": "GrokImagine", "usagePercent": 0}
                ]
              }
            }
            """);
        Check(grok?.Windows[0].Label == "Weekly", "grok weekly window");
        Check(grok?.Windows[0].UsedPercent == 42, "grok used percent");
        Check(grok?.Windows[0].ResetsAt == DateTimeOffset.Parse("2026-11-10T00:00:00Z"), "grok reset");
        Check(grok?.Credits?.PrepaidBalance == 14.46m, "grok prepaid dollars");
        Check(grok?.Credits?.OnDemandUsed == 2.50m, "grok on-demand dollars");
        Check(grok?.Products.Count == 2 && grok.Products[0].Product == "Grok Build", "grok product shares");
        Check(UsageParsers.GrokRemainingText("""{"config":{"creditUsagePercent":42}}""") == "58%", "remaining display");
        Check(UsageParsers.ParseGrok("""{"message":"unavailable"}""") is null, "grok error is not a quota");
        Check(UsageParsers.ParseGrok("""{"remainingPercent":40}""")?.Windows[0].UsedPercent == 60, "legacy remaining");
        var rpc = UsageParsers.ParseGrok("""
            {"billingCycle":{"billingPeriodStart":"2026-05-01T00:00:00Z","billingPeriodEnd":"2026-06-01T00:00:00Z"},
             "monthlyLimit":{"val":100},"usage":{"totalUsed":{"val":25}}}
            """);
        Check(rpc?.Windows[0].Label == "Monthly" && rpc.Windows[0].UsedPercent == 25, "legacy rpc ratio");
        var unknown = UsageParsers.ParseGrok("""{"config":{"currentPeriod":{"start":"2026-11-03T00:00:00Z","end":"2026-11-10T00:00:00Z"}}}""");
        Check(unknown?.ErrorKey == "usage_unknown" && unknown.Windows[0].UsedPercent is null, "period without percent stays unknown");
        Check(UsageParsers.ParseGrok("""{"config":{"creditUsagePercent":42,"productUsage":[{"product":"GrokBuild","usagePercent":1}]}}""")?.Products.Count == 0,
            "product shares that do not add up are dropped");
        Check(UsageParsers.ParseGrok("""{"config":{"prepaidBalance":{}}}""")?.Credits?.PrepaidBalance == 0, "empty cent is zero");

        var claude = UsageParsers.FromClaude(ClaudeUsageParser.Parse("""
            {"five_hour":{"utilization":12.5,"resets_at":"2026-11-10T15:00:00Z"},
             "seven_day":{"utilization":40,"resets_at":"2026-11-16T00:00:00Z"}}
            """));
        Check(claude.Windows.Count == 2 && claude.Windows[0].UsedPercent == 12.5 && claude.Windows[1].Label == "7d", "claude windows");

        var codex = UsageParsers.FromCodex(CodexUsageParser.Parse("""
            {"plan_type":"plus","rate_limit":{
              "primary_window":{"used_percent":10,"limit_window_seconds":18000,"resets_at":"2026-11-10T12:00:00Z"},
              "secondary_window":{"used_percent":20,"limit_window_seconds":604800,"resets_at":"2026-11-16T00:00:00Z"}}}
            """));
        Check(codex.Plan == "plus" && codex.Windows[0].Label == "5h" && codex.Windows[1].Label == "7d", "codex windows");

        var cursor = UsageParsers.ParseCursor("""
            {"billingCycleStart":"2026-04-02T00:00:00Z","billingCycleEnd":"2026-05-02T00:00:00Z","membershipType":"pro",
             "individualUsage":{"plan":{"autoPercentUsed":15,"apiPercentUsed":80,"totalPercentUsed":40},
               "onDemand":{"used":2309,"limit":null}}}
            """);
        Check(cursor?.Windows[0].Label == "Auto" && cursor.Windows[0].UsedPercent == 15, "cursor auto pool");
        Check(cursor?.Windows[1].Label == "API" && cursor.Windows[1].UsedPercent == 80, "cursor api pool");
        Check(cursor?.Credits?.OnDemandUsed == 23.09m, "cursor on-demand cents");

        var copilot = UsageParsers.ParseCopilot("""
            {"copilot_plan":"individual","quota_reset_date":"2026-12-01",
             "quota_snapshots":{"premium_interactions":{"percent_remaining":80,"unlimited":false},
               "chat":{"percent_remaining":100,"unlimited":true}}}
            """);
        Check(copilot?.Windows[0].UsedPercent == 20 && copilot.Windows[1].Unlimited, "copilot remaining and unlimited");
        Check(UsageParsers.ParseCopilot("""{"quota_snapshots":{"chat":{"unlimited":false}}}""") is null, "copilot without a percent is unreadable");

        var deepseek = UsageParsers.ParseDeepSeek("""
            {"is_available":true,"balance_infos":[
              {"currency":"USD","total_balance":"1.00","granted_balance":"0.25","topped_up_balance":"0.75"},
              {"currency":"CNY","total_balance":"10.50","granted_balance":"1.00","topped_up_balance":"9.50"}]}
            """);
        Check(deepseek?.Credits?.Currency == "CNY" && deepseek.Credits.PrepaidBalance == 10.50m, "deepseek prefers CNY");
        Check(UsageParsers.ParseDeepSeek("""{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"3","granted_balance":"2","topped_up_balance":"2"}]}""") is null,
            "inconsistent deepseek balance is rejected");

        var openCode = UsageParsers.ParseOpenCode("""
            {"windows":[
              {"name":"rolling","percent":12,"resets_at":"2026-11-10T18:00:00Z"},
              {"name":"weekly","percent":40,"resets_at":"2026-11-16T00:00:00Z"},
              {"name":"monthly","percent":10,"resets_at":"2026-12-01T00:00:00Z"}]}
            """);
        Check(openCode?.Windows.Count == 3 && openCode.Windows[1].Label == "Weekly" && openCode.Windows[1].UsedPercent == 40, "opencode windows");

        Check(UsageParsers.ParseXai("""{"prepaid_balance_cents":1446}""")?.Credits?.PrepaidBalance == 14.46m, "xai cents");
        Check(UsageParsers.ParseXai("""{"amount":"14.46","currency_code":"USD"}""")?.Credits?.PrepaidBalance == 14.46m, "xai amount");
        Check(UsageParsers.ParseOpenRouter("""{"data":{"total_credits":50,"total_usage":12.5}}""")?.Credits?.PrepaidBalance == 37.5m, "openrouter remaining");

        var rows = UsageParsers.AntigravityRows(
            "Gemini Models\tWeekly Limit Remaining\t100%\t2026-11-14T17:41:52Z\n" +
            "Claude and GPT models\tWeekly Limit Remaining\t0%\t2026-11-08T03:50:06Z\n");
        Check(rows[0] == "Gemini Models 100%", "antigravity text keeps remaining scale");
        Check(UsageParsers.AntigravityRows("invalid").Length == 0, "malformed antigravity output is empty");
        var gemini = UsageParsers.ParseAntigravity("""{"buckets":[{"model":"Gemini Models","remainingFraction":0.25,"resetTime":"2026-11-14T17:41:52Z"}]}""");
        Check(gemini?.Windows[0].UsedPercent == 75, "antigravity json fraction");
    }

    private static void VerifyLedger()
    {
        var now = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
        var ledger = UsageLedgerReader.Summarize(
        [
            new UsageLedgerEvent(now.AddHours(-1), 100, 0.10m),
            new UsageLedgerEvent(now.AddDays(-3), 50, 0.05m),
            new UsageLedgerEvent(now.AddDays(-20), 25, null),
            new UsageLedgerEvent(now.AddDays(-40), 999, 9m),
        ], now);
        Check(ledger.Today.Tokens == 100 && ledger.Today.ApiEquivalentCost == 0.10m, "today ledger");
        Check(ledger.SevenDays.Tokens == 150 && ledger.SevenDays.ApiEquivalentCost == 0.15m, "seven day ledger");
        Check(ledger.ThirtyDays.Tokens == 175 && ledger.ThirtyDays.ApiEquivalentCost == 0.15m, "thirty day ledger");
        var parsed = UsageLedgerReader.Parse("""{"turns":[{"endedAt":"2026-11-10T01:00:00Z","totalTokens":7,"costUsd":0.01}]}""");
        Check(parsed.Count == 1 && parsed[0].Tokens == 7, "usage.json turns");
    }

    private static void VerifyAccounts()
    {
        var root = Temp();
        try
        {
            var grok = Path.Combine(root, ".grok");
            Directory.CreateDirectory(grok);
            File.WriteAllText(Path.Combine(grok, "auth.json"), """
                {"https://auth.x.ai::ada":{"key":"test-token-not-real","email":"ada@example.com","expires_at":"2099-01-01T00:00:00Z"},
                 "https://auth.x.ai::old":{"key":"expired-token","email":"old@example.com","expires_at":"2020-01-01T00:00:00Z"}}
                """);
            var found = UsageAccountDirectory.Discover(
                ProviderCatalog.Find(UsageProviderIds.Grok)!,
                new UsagePaths(root, root, root),
                new MapUsageEnvironment(new Dictionary<string, string>()),
                false);
            Check(found.Count == 2, "grok discovers both logins");
            Check(found.Single(account => account.Label == "ada@example.com").Secret == Secret, "active grok secret stays in memory only");
            Check(found.Single(account => account.Label == "old@example.com").Secret is null, "expired grok login is not sent");
            Check(found.Single(account => account.Label == "old@example.com").ToString().Contains(Secret) == false, "credential text omits the secret");

            var bytes = Encoding.UTF8.GetBytes("prefix cursorAuth/accessToken eyJhbGciOiJub25lIn0.eyJzdWIiOiJ1c2VyXzEyMyJ9.sig trailer");
            var db = Path.Combine(root, "state.vscdb");
            File.WriteAllBytes(db, bytes);
            var token = UsageAccountDirectory.ProbeSqliteText(db, "cursorAuth/accessToken");
            Check(token is not null && token.StartsWith("eyJ", StringComparison.Ordinal), "cursor session is read from the local database");
            Check(UsageAccountDirectory.JwtSubject(token!) == "user_123", "cursor user id comes from the token subject");

            File.WriteAllText(Path.Combine(root, "hosts.yml"), "github.com:\n  oauth_token: gho_example\n  user: octocat\n");
            var github = UsageAccountDirectory.ReadGitHubToken(Path.Combine(root, "hosts.yml"));
            Check(github?.Label == "octocat" && github.Secret == "gho_example", "copilot reads the GitHub CLI token");

            var repeated = UsageAccounts.Normalize(
            [
                new UsageAccountSnapshot("Ada", "Zed", null, "a", [], null, null, [], UsageFreshness.Unavailable, DateTimeOffset.UnixEpoch, "unavailable"),
                new UsageAccountSnapshot("ada", "Ada", null, "a", [], null, null, [], UsageFreshness.Current, DateTimeOffset.UnixEpoch.AddDays(1), null),
                .. Enumerable.Range(0, 20).Select(index => new UsageAccountSnapshot("n" + index, "N" + index, null, "a", [], null, null, [], UsageFreshness.Current, null, null)),
            ]);
            Check(repeated.Count == 16, "accounts are capped");
            Check(repeated.Any(account => account.AccountId.Equals("ada", StringComparison.OrdinalIgnoreCase))
                && repeated.Count(account => account.AccountId.Equals("ada", StringComparison.OrdinalIgnoreCase)) == 1, "duplicate accounts collapse");
            Check(repeated.Single(account => account.AccountId.Equals("ada", StringComparison.OrdinalIgnoreCase)).Freshness == UsageFreshness.Current,
                "the current duplicate wins");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task VerifyEngine()
    {
        var root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".grok"));
            File.WriteAllText(Path.Combine(root, ".grok", "auth.json"), """
                {"email":"ada@example.com","key":"test-token-not-real","expires_at":"2099-01-01T00:00:00Z"}
                """);
            var transport = new ScriptedTransport("""{"config":{"creditUsagePercent":42,"prepaidBalance":{"val":100}}}""");
            var board = await UsageProviderEngine.LoadAsync(new UsageLoadRequest(
                DateTimeOffset.Parse("2026-11-10T00:00:00Z"),
                new UsagePaths(root, root, root),
                _ => true,
                transport,
                new MapUsageEnvironment(new Dictionary<string, string>()),
                true), CancellationToken.None);
            var rendered = UsageBoardSvg.Render(board, UsageThemes.Dark, false, true);
            Check(!rendered.Contains(Secret, StringComparison.Ordinal), "svg omits the credential");
            Check(!Describe(board).Contains(Secret, StringComparison.Ordinal), "snapshot omits the credential");
            Check(board.Providers.Single(provider => provider.ProviderId == UsageProviderIds.Grok).Accounts[0].Windows[0].UsedPercent == 42, "engine parses grok");
            Check(transport.Calls.All(call => call.Method == "GET" && !call.Url.Contains(Secret, StringComparison.Ordinal)), "token stays out of the URL");
            Check(transport.Calls.Any(call => call.Url.StartsWith("https://cli-chat-proxy.grok.com/v1/billing", StringComparison.Ordinal)), "grok billing URL");
            using var http = new HttpClient(new BlockingHandler());
            var blocked = await new HttpUsageTransport(http).SendAsync(
                new UsageHttpRequest("GET", new Uri("http://example.test/v1/billing"), new Dictionary<string, string>()),
                CancellationToken.None);
            Check(blocked.ErrorKey == "endpoint_blocked", "non-https endpoints are not called");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void VerifyProjectionAndSvg()
    {
        var now = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        var snapshot = new QuotaSnapshot(
            [
                new QuotaCard("claude.account", ProviderKind.Claude, "Claude", "Pro", "#d97757", true,
                    [new QuotaWindow("5h", 10, now.AddHours(5), TimeSpan.FromHours(5))]) { CapturedAt = now },
            ],
            [],
            now)
        {
            CodexAccounts =
            [
                new CodexAccountQuota("one@example.com", [new QuotaWindow("5h", 30, now.AddHours(4), TimeSpan.FromHours(5))], now),
                new CodexAccountQuota("two@example.com", [new QuotaWindow("7d", 80, now.AddDays(3), TimeSpan.FromDays(7))], now),
            ],
        };
        var projected = UsageProjection.FromQuotaSnapshot(snapshot);
        Check(projected.Single(provider => provider.ProviderId == UsageProviderIds.Codex).Accounts.Count == 2, "codex accounts stay grouped");
        Check(projected.Any(provider => provider.ProviderId == UsageProviderIds.Claude), "claude projects onto the board");
        var merged = UsageProviderEngine.Merge(UsageBoard.Empty, projected);
        Check(merged.Providers[0].ProviderId == UsageProviderIds.Claude, "catalog order is stable");
        var svg = UsageBoardSvg.Render(SampleBoard(), UsageThemes.Light, false, true);
        Check(svg.Contains("Grok", StringComparison.Ordinal) && svg.Contains("58%", StringComparison.Ordinal), "mockup shows the reading");
        Check(svg.Contains("#F4F6FB", StringComparison.Ordinal), "light canvas");
        Check(UsageBoardLayout.PreferCompact(SampleBoard()) == false, "small boards stay expanded");
        Check(UsageBoardLayout.Height(SampleBoard(), true) < UsageBoardLayout.Height(SampleBoard(), false), "compact is shorter");
        Check(ProviderCatalog.PersistentMiniIds.Contains(UsageProviderIds.Grok), "grok remains a persistent mini card");
    }

    private static UsageBoard SampleBoard()
    {
        var reset = DateTimeOffset.Parse("2026-11-12T00:00:00Z");
        UsageProviderSnapshot Provider(string id, params UsageAccountSnapshot[] accounts)
        {
            var definition = ProviderCatalog.Find(id)!;
            return new UsageProviderSnapshot(definition.Id, definition.DisplayName, definition.Accent, definition.Order, accounts);
        }

        UsageAccountSnapshot Account(string label, double used, UsageLedger? ledger = null, bool products = false) => new(
            label, label, "Pro", "sample",
            [new UsageWindow("weekly", "Weekly", used, reset, TimeSpan.FromDays(7))],
            new UsageCredits("USD", 14.46m, null, null),
            ledger,
            products ? [new UsageProductShare("Grok Build", 40)] : [],
            UsageFreshness.Current,
            reset.AddDays(-2),
            null);
        var ledger = new UsageLedger(new UsageLedgerPeriod(1200, 0.42m), new UsageLedgerPeriod(8400, 2.1m), new UsageLedgerPeriod(24000, 6.4m));
        return new UsageBoard(
        [
            Provider(UsageProviderIds.Grok, Account("ada@example.com", 42, ledger, true), Account("work@example.com", 70)),
            Provider(UsageProviderIds.Cursor, Account("Cursor", 15)),
        ]);
    }

    private static string Describe(UsageBoard board) =>
        string.Join('\n', board.Providers.SelectMany(provider => provider.Accounts.Select(account =>
            provider.DisplayName + " " + account.Label + " " + account.Plan + " " + account.ErrorKey + " " + account.SourceKind)));

    private static string Temp() => Path.Combine(Path.GetTempPath(), "zgstokenbar-usage-" + Guid.NewGuid().ToString("N"));

    private static void VerifyGlassDock()
    {
        var board = SampleBoard();
        var rings = GlassDockComposer.FromBoard(board);
        Check(rings.Count == 3, "board rings include every account");
        Check(rings.Count(ring => ring.AccountNumber > 0) == 2, "multi-account provider badges each account");
        var plan = GlassDockLayout.Plan(rings);
        Check(plan.Width == GlassDockLayout.WidthFor(3, false) && plan.Width < GlassDockLayout.Width && plan.Height == GlassDockLayout.Height, "dock fits its rings");
        Check(plan.Visible[0].RemainingPercent == 30, "tightest remaining leads");
        Check(plan.OverflowCount == 0, "three accounts fit");
        var many = Enumerable.Range(0, 12).Select(index => new GlassDockRing(
            "p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "P" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "a",
            0,
            80 - index,
            null,
            null,
            "#10a37f")).ToArray();
        var bounded = GlassDockLayout.Plan(many);
        var single = GlassDockLayout.Plan(many.Take(1).ToArray());
        Check(bounded.Width == GlassDockLayout.Width && single.Width < bounded.Width, "width is capped however many providers report");
        Check(bounded.Visible.Count == 4 && bounded.OverflowCount == 8, "four rings then +N");
        Check(bounded.Visible[0].RemainingPercent == 80 - 11, "tightest of the long list is first");
        Check(bounded.Slots.Count == 5 && bounded.Slots[^1].Chip, "overflow chip is laid out");
        Check(bounded.Slots.All(slot => slot.Bounds.Right <= GlassDockLayout.SystemLeft - GlassDockLayout.DividerGap), "rings stay left of the system cluster");
        var limited = GlassDockLayout.Plan(many, 2);
        Check(limited.Visible.Count == 2 && limited.OverflowCount == 10 && limited.Width == GlassDockLayout.WidthFor(2, true), "ring limit is configurable");
        var expanded = GlassDockLayout.PlanExpanded(many);
        Check(expanded.Visible.Count == 12 && expanded.OverflowCount == 0 && expanded.Width == GlassDockLayout.WidthFor(12, true) && expanded.Slots[^1].Chip, "+N expands to every ring");
        Check(GlassDockLayout.RingSweep(4) >= 22f && GlassDockLayout.RingSweep(0) == 0f && GlassDockLayout.RingSweep(null) == 0f && GlassDockLayout.PercentText(4.4) == "4%", "small remaining stays legible");
        Check(GlassDockLayout.Band(19) == "red" && GlassDockLayout.Band(20) == "amber" && GlassDockLayout.Band(50) == "amber" && GlassDockLayout.Band(51) == "green", "quota bands");
        var quota = GlassDockComposer.FromQuota(
            [
                new QuotaCard("claude", ProviderKind.Claude, "Claude", null, "#d97757", true, [new QuotaWindow("5h", 28, null, TimeSpan.FromHours(5))]),
            ],
            [
                new CodexAccountQuota("one", [new QuotaWindow("5h", 84, null, TimeSpan.FromHours(5))]),
                new CodexAccountQuota("two", [new QuotaWindow("5h", 95, null, TimeSpan.FromHours(5))]),
            ]);
        var codexPlan = GlassDockLayout.Plan(quota);
        Check(codexPlan.Visible[0].ProviderId == UsageProviderIds.Codex && codexPlan.Visible[0].RemainingPercent == 5, "codex accounts are separate rings");
        Check(codexPlan.Visible.Count(ring => ring.AccountNumber > 0) == 2, "both codex accounts keep a badge");
        var groups = GlassDockComposer.Groups(rings, DateTimeOffset.Parse("2026-11-10T12:00:00Z"), true);
        Check(groups[0].Title == "Grok" && groups[0].Accounts.Count == 2 && groups[0].Accounts[0].Label == "账号1", "flyout keeps provider groups");
    }

    private static void VerifyEndpoints()
    {
        Check(UsageEndpoints.IsAllowed("GET", new Uri("https://cli-chat-proxy.grok.com/v1/billing?format=credits")), "grok endpoint");
        Check(UsageEndpoints.IsAllowed("GET", new Uri("https://management-api.x.ai/v1/billing/teams/team_1/prepaid/balance")), "xai endpoint");
        Check(!UsageEndpoints.IsAllowed("POST", new Uri("https://management-api.x.ai/v1/billing/teams/team_1/prepaid/balance")), "xai is read only");
        Check(UsageEndpoints.IsAllowed("POST", new Uri("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary")), "antigravity quota read");
        Check(!UsageEndpoints.IsAllowed("GET", new Uri("https://example.com/v1/billing")), "unknown hosts are blocked");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ScriptedTransport(string body) : IUsageTransport
    {
        public List<(string Method, string Url)> Calls { get; } = [];

        public Task<UsageHttpResult> SendAsync(UsageHttpRequest request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.Url.ToString()));
            return Task.FromResult(new UsageHttpResult((int)HttpStatusCode.OK, body, null));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("blocked request was sent");
    }
}
