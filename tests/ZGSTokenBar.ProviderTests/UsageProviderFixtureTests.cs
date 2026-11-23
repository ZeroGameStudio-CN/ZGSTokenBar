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
        VerifyConcurrentStreamingEngine().GetAwaiter().GetResult();
        VerifyUsageBoardCache();
        VerifyTransientRefreshRetention();
        ReviewFixTests.Run();
        VerifyProjectionAndSvg();
        VerifyGlassDock();
        VerifyRegistry().GetAwaiter().GetResult();
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
        Check(rows[0] == "Gemini · 7d 100%", "antigravity text keeps remaining scale");
        Check(UsageParsers.AntigravityRows("invalid").Length == 0, "malformed antigravity output is empty");
        var codexCredits = CodexUsageParser.Parse("""{"plan_type":"plus","rate_limit":{},"credits":{"has_credits":true,"unlimited":false,"balance":"12.5"}}""").Credits;
        Check(codexCredits is { HasCredits: true, Unlimited: false, Balance: 12.5 }, "codex credits balance (string form, as CodexBar reads it)");
        Check(CodexUsageParser.Parse("""{"rate_limit":{}}""").Credits is null, "codex without credits");
        var grokPeriod = UsageParsers.ParseGrok("""{"config":{"currentPeriod":{"start":"2026-11-01T00:00:00Z","end":"2026-12-01T00:00:00Z"}},"usage":{"used":1,"limit":10}}""")?.Period;
        Check(grokPeriod is null || grokPeriod is { Kind: "billing", End: not null }, "grok billing period when present");
        var cursorPeriod = UsageParsers.ParseCursor("""{"billingCycleStart":"2026-11-03T00:00:00Z","billingCycleEnd":"2026-12-03T00:00:00Z","individualUsage":{"plan":{"used":10,"limit":100}}}""")?.Period;
        Check(cursorPeriod is null || cursorPeriod is { Kind: "billing", Source: "billingCycleStart/End" }, "cursor billing cycle period");
        var copilotPeriod = UsageParsers.ParseCopilot("""{"quota_reset_date":"2026-12-01T00:00:00Z","quota_snapshots":{"chat":{"unlimited":true}}}""")?.Period;
        Check(copilotPeriod is { Kind: "quota-reset", Source: "quota_reset_date" }, "copilot quota reset date");
        var gemini = UsageParsers.ParseAntigravity("""{"buckets":[{"model":"Gemini Models","remainingFraction":0.25,"resetTime":"2026-11-14T17:41:52Z"}]}""");
        Check(gemini?.Windows[0].UsedPercent == 75, "antigravity json fraction");
        var envelope = UsageParsers.ParseAntigravity("""
            {"status":"SUCCESS","response":"Gemini Models\tWeekly Limit Remaining\t100%\t2026-11-16T15:46:23Z\n","command":{"name":"usage","data":{"groups":[
             {"name":"Gemini Models","buckets":[{"name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-16T15:46:23Z"},
               {"name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"2026-11-11T18:25:53Z"}]},
             {"name":"Claude and GPT models","buckets":[{"name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.036091599613428116,"reset_time":"2026-11-16T15:42:01Z"},
               {"name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"2026-11-11T18:25:53Z"}]},
             {"name":"Gemini 4 Argon","buckets":[{"name":"Gemini 4 Argon Weekly Limit","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-16T15:41:46Z"}]}]}}}
            """);
        var claudeWeekly = envelope?.Windows.FirstOrDefault(window => window.Id == "Claude and GPT models · 7d" && window.Label == "Claude/GPT · 7d");
        Check(envelope?.Windows.Count == 5 && claudeWeekly is { UsedPercent: > 96.3 and < 96.5 }
            && claudeWeekly.ResetsAt == DateTimeOffset.Parse("2026-11-16T15:42:01Z"), "agy JSON envelope keeps exact fractions and resets per window");
        Check(PercentFormat.Remaining(91.43) == "91%" && PercentFormat.Remaining(91.99) == "91%"
            && PercentFormat.Remaining(3.6091) == "3.6%" && PercentFormat.Remaining(0.4) == "<1%"
            && PercentFormat.Remaining(100) == "100%" && PercentFormat.Remaining(0) == "0%"
            && PercentFormat.RemainingFromUsed(100 - 71) == "71%" && PercentFormat.Used(8.57) == "8.6%" && PercentFormat.Used(96.39) == "97%"
            && GlassDockLayout.PercentText(91.43) == UsageFormatting.Percent(91.43) && GlassDockLayout.PercentText(91.99) == "91%"
            && GlassDockLayout.PercentText(3.61) == "3%",
            "one percent rule: remaining floors, used ceils, ring and hover agree");
        var redacted = SecretRedactor.Redact("Authorization: Bearer abcdefghijklmnop {\"access_token\":\"xyz123\"} sk-or-v1-abcdefgh1234 eyJhbGciOi.eyJzdWIiOiIx.c2lnbmF0dXJl ?token=zzz");
        Check(!redacted.Contains("abcdefghijklmnop") && !redacted.Contains("xyz123") && !redacted.Contains("abcdefgh1234")
            && !redacted.Contains("eyJzdWIiOiIx") && !redacted.Contains("zzz"), "diagnostics redact tokens");
        var agyWindows = envelope?.Windows ?? [];
        var pinned = AntigravityGroups.Pin(agyWindows, "ARGON");
        Check(pinned.Count == agyWindows.Count && AntigravityGroups.GroupOf(pinned[0]) == "Gemini 4 Argon"
            && pinned[0].ResetsAt == DateTimeOffset.Parse("2026-11-16T15:41:46Z") && pinned[0].Label == "Argon · 7d",
            "antigravity pins the argon group first, case-insensitively, with a short label");
        Check(AntigravityGroups.OtherGroupLow(pinned), "antigravity warns when another group (Claude/GPT 3.6%) is nearly exhausted");
        var withoutArgon = agyWindows.Where(window => AntigravityGroups.GroupOf(window) != "Gemini 4 Argon").ToArray();
        Check(ReferenceEquals(AntigravityGroups.Pin(withoutArgon, "argon"), withoutArgon)
            && ReferenceEquals(AntigravityGroups.Pin(agyWindows, AntigravityGroups.Auto), agyWindows),
            "antigravity falls back to the most-remaining order when argon is missing or auto");
        var claudePinned = AntigravityGroups.Pin(agyWindows, "claude");
        Check(claudePinned[0].Id == "Claude and GPT models · 7d" && !AntigravityGroups.OtherGroupLow(claudePinned),
            "antigravity pins any group, tightest window first");
        var board = new UsageBoard([new UsageProviderSnapshot(UsageProviderIds.Antigravity, "Antigravity", "#4285f4", 0,
            [new UsageAccountSnapshot("local", "Antigravity", null, "cli", agyWindows, null, null, [], UsageFreshness.Current, null, null)])]);
        var groups = AntigravityGroups.Groups(board);
        Check(groups.Count == 3 && groups.Contains("Gemini 4 Argon")
            && AntigravityGroups.GroupOf(AntigravityGroups.Pin(board, "argon").Providers[0].Accounts[0].Windows[0]) == "Gemini 4 Argon",
            "antigravity board lists detected groups and pins argon");
        var annotated = AntigravityGroups.Pin(board, "argon").Providers[0].Accounts[0];
        Check(annotated.Warning
            && annotated.Windows.Where(window => window.Highlighted).All(window => AntigravityGroups.GroupOf(window) == "Gemini 4 Argon")
            && annotated.Windows.Any(window => window.Highlighted)
            && annotated.Windows.Where(window => !window.Highlighted).All(window => AntigravityGroups.GroupOf(window) != "Gemini 4 Argon"),
            "the provider layer stars the ring's group and flags the account when another group runs low");
        var claudeAccount = AntigravityGroups.Pin(board, "claude").Providers[0].Accounts[0];
        Check(!claudeAccount.Warning && claudeAccount.Windows[0].Highlighted,
            "pinning the low group itself clears the warning");
        var autoAccount = AntigravityGroups.Pin(board, AntigravityGroups.Auto).Providers[0].Accounts[0];
        Check(autoAccount.Windows.Any(window => window.Highlighted) && autoAccount.Warning == AntigravityGroups.OtherGroupLow(agyWindows),
            "auto mode still annotates the headline group and the warning");
        Check(UsageParsers.ParseAntigravity("""{"response":"Gemini Models\tWeekly Limit Remaining\t40%\t2026-11-16T15:46:23Z\n"}""")?.Windows[0].UsedPercent == 60,
            "agy JSON envelope falls back to its text table");
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

    private static async Task VerifyRegistry()
    {
        var ids = UsageProviderRegistry.All.Select(provider => provider.Definition.Id).ToArray();
        Check(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length, "provider ids are unique");
        Check(ProviderCatalog.All.Select(definition => definition.Id).SequenceEqual(ids), "the catalog is the registry, in order");
        Check(ids.All(id => UsageProviderRegistry.Find(id)?.Definition.Id == id), "every provider is found by id");
        Check(UsageProviderRegistry.All.All(provider => provider.RefreshInterval > TimeSpan.Zero), "every provider has a refresh interval");
        Check(UsageProviderRegistry.Find(UsageProviderIds.Codex)!.FetchesUsage == false, "codex stays projected");
        Check(UsageProviderRegistry.All.Where(provider => provider.FetchesUsage).All(provider =>
            provider.BuildRequest(new UsageCredential("a", "a", "test", Secret, new Dictionary<string, string> { ["team"] = "team1" })) is { } request
                && UsageEndpoints.IsAllowed(request.Method, request.Url)), "every provider request is on the endpoint allowlist");

        var root = Temp();
        try
        {
            var runs = new List<string>();
            var fallback = new UsageCliFallback((executable, arguments) =>
            {
                runs.Add(string.Join(' ', arguments));
                return Task.FromResult<string?>(arguments.Contains("/usage") ? null : "{}");
            });
            var bin = Path.Combine(root, "agy", "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "agy.exe"), "");
            var context = new UsageDetectContext(new UsagePaths(root, root, root), new MapUsageEnvironment(new Dictionary<string, string>()), true);
            var now = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
            await fallback.ApplyAsync(UsageBoard.Empty, context, id => id == UsageProviderIds.Antigravity, now);
            Check(runs.Count == 0, "an installed, disconnected AGY never starts browser-capable CLI authentication");
            await fallback.ApplyAsync(UsageBoard.Empty, context, id => id == UsageProviderIds.Antigravity, now.AddMinutes(5));
            Check(runs.Count == 0, "repeated background reads remain noninteractive");
            await fallback.ApplyAsync(UsageBoard.Empty, context, id => id == UsageProviderIds.Antigravity, now.AddMinutes(11));
            Check(runs.Count == 0, "a later refresh cannot restart AGY sign-in");
            await fallback.ApplyAsync(UsageBoard.Empty, context, _ => false, now.AddMinutes(30));
            Check(runs.Count == 0, "a disabled provider never spawns its CLI");
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
        Check(GlassDockLayout.Plan(rings.Select(ring => ring with { RemainingPercent = 100 - ring.RemainingPercent }).Reverse().ToArray())
            .Visible.Select(ring => ring.StableId).SequenceEqual(plan.Visible.Select(ring => ring.StableId)),
            "quota updates and input ordering cannot reorder stable account identities");
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
        Check(bounded.Visible[0].ProviderId == "p0", "stable provider order leads independently of quota");
        Check(bounded.Slots.Count == 5 && bounded.Slots[^1].Chip, "overflow chip is laid out");
        Check(bounded.Slots.All(slot => slot.Bounds.Right <= GlassDockLayout.SystemLeft - GlassDockLayout.DividerGap), "rings stay left of the system cluster");
        Check(bounded.Slots.Where(slot => !slot.Chip).All(slot => slot.Bounds.Width <= GlassDockLayout.RingSize + 4) && GlassDockLayout.WidthFor(4, false) - GlassDockLayout.WidthFor(3, false) == GlassDockLayout.ItemWidth + GlassDockLayout.RingGap, "percent sits inside the ring so N rings take about N ring widths");
        Check(GlassDockLayout.BarsBounds.Height >= GlassDockLayout.MetricsRows * 7 && GlassDockLayout.RateBounds.Left >= GlassDockLayout.BarsBounds.Right, "four labelled system rows sit before the tok/s readout");
        var limited = GlassDockLayout.Plan(many, 2);
        Check(limited.Visible.Count == 2 && limited.OverflowCount == 10 && limited.Width == GlassDockLayout.WidthFor(2, true), "ring limit is configurable");
        var expanded = GlassDockLayout.PlanExpanded(many);
        Check(expanded.Visible.Count == 12 && expanded.OverflowCount == 0 && expanded.Width == GlassDockLayout.ExpandedWidthFor(12)
            && expanded.Slots[^1].Chip && expanded.Slots.Count(slot => slot.Chip) == 1, "+N expands to every ring and ends with the collapse chip");
        var collapsedFour = GlassDockLayout.Plan(many);
        var lead = collapsedFour.Visible.Count;
        Check(expanded.Slots.Take(lead).Select(slot => slot.Bounds).SequenceEqual(collapsedFour.Slots.Take(lead).Select(slot => slot.Bounds))
            && expanded.Slots[^1].Bounds.Left > expanded.Slots[^2].Bounds.Right,
            "expanding keeps the collapsed rings in place and moves the collapse chip to the far right");
        var zeroCapped = GlassDockLayout.PlanWithRingCap(many, 0, showIntake: true, showRouter: true);
        Check(zeroCapped.Visible.Count == 0 && zeroCapped.OverflowCount == 12 && !zeroCapped.OverflowUrgent && zeroCapped.Width == 162,
            "zero ring cap folds every ring into the overflow chip while keeping monitors");
        var urgentZero = GlassDockLayout.PlanWithRingCap(many.Select((ring, i) => i == 11 ? ring with { RemainingPercent = 5 } : ring).ToArray(), 0, showIntake: true, showRouter: true);
        Check(urgentZero.OverflowUrgent && GlassDockLayout.PlanWithRingCap([], 0, showIntake: true, showRouter: true) is { Visible.Count: 0, OverflowCount: 0, Slots.Count: 0 },
            "zero ring cap keeps urgency for hidden low-quota rings and leaves empty input chipless");
        var four = many.Take(4).ToArray();
        Check(GlassDockLayout.FitRingCap(four, 4, 277, showIntake: true, showRouter: true) is null, "fitting returns null when unconstrained width fits");
        Check(GlassDockLayout.FitRingCap(four, 4, 276, showIntake: true, showRouter: true) == 3, "tight gap reduces 4 unchipped rings (277) to 3 chipped (266)");
        Check(GlassDockLayout.FitRingCap(four, 4, 230, showIntake: true, showRouter: true) == 2
            && GlassDockLayout.FitRingCap(four, 4, 200, showIntake: true, showRouter: true) == 1
            && GlassDockLayout.FitRingCap(four, 4, 162, showIntake: true, showRouter: true) == 0
            && GlassDockLayout.FitRingCap(four, 4, 161, showIntake: true, showRouter: true) == -1,
            "ring cap steps cleanly down through 2, 1, 0 and reports -1 below minimum width");
        var three = many.Take(3).ToArray();
        Check(GlassDockLayout.FitRingCap(three, 4, 240, showIntake: true, showRouter: true) == 2,
            "fitting considers overflow chip width when stepping 3 unchipped rings (241) down to 2 chipped (230)");
        var ultra = GlassDockLayout.PlanUltraCompact(many);
        Check(ultra.UltraCompact && ultra.Width == GlassDockLayout.UltraCompactWidth && ultra.Visible.Count == 0
            && ultra.OverflowCount == many.Length && ultra.Slots.Count == 1 && ultra.Slots[0].Chip
            && ultra.RouterBounds.IsEmpty && ultra.IntakeBounds.IsEmpty
            && GlassDockLayout.RateBoundsFor(ultra.Width).Left - ultra.Slots[0].Bounds.Right == GlassDockLayout.DividerGap,
            "ultra-compact plan folds to +N and tok/s cleanly");
        var ultraEmpty = GlassDockLayout.PlanUltraCompact([]);
        Check(ultraEmpty.UltraCompact && ultraEmpty.Slots.Count == 0 && ultraEmpty.OverflowCount == 0
            && ultraEmpty.Width < ultra.Width,
            "ultra-compact without accounts drops the chip cleanly");
        Check(GlassDockLayout.RingSweep(4) >= 22f && GlassDockLayout.RingSweep(0) == 0f && GlassDockLayout.RingSweep(null) == 0f && GlassDockLayout.PercentText(4.4) == "4%", "small remaining stays legible");
        Check(GlassDockLayout.RingStart(100) == -90f && Math.Abs(GlassDockLayout.RingStart(50) - 90f) < .01f && Math.Abs(GlassDockLayout.RingStart(75)) < .01f, "remaining starts after clockwise use from 12 o'clock");
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
        Check(codexPlan.Visible.Where(ring => ring.ProviderId == UsageProviderIds.Codex).Select(ring => ring.RemainingPercent)
            .OrderBy(value => value).SequenceEqual(new double?[] { 5, 16 }), "codex accounts retain separate quota rings without quota-based sorting");
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

    private static async Task VerifyConcurrentStreamingEngine()
    {
        var root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".grok"));
            File.WriteAllText(Path.Combine(root, ".grok", "auth.json"), """
                {"email":"ada@example.com","key":"test-token-not-real","expires_at":"2099-01-01T00:00:00Z"}
                """);
            var grokGate = new TaskCompletionSource<UsageHttpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var openRouterStreamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamed = new List<string>();
            var env = new MapUsageEnvironment(new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "test-openrouter-key" });
            var transport = new GatingTransport(grokGate.Task);

            var loadTask = UsageProviderEngine.LoadAsync(
                new UsageLoadRequest(
                    DateTimeOffset.Parse("2026-11-10T00:00:00Z"),
                    new UsagePaths(root, root, root),
                    id => id is UsageProviderIds.Grok or UsageProviderIds.OpenRouter,
                    transport,
                    env,
                    true),
                snapshot =>
                {
                    lock (streamed) streamed.Add(snapshot.ProviderId);
                    if (snapshot.ProviderId == UsageProviderIds.OpenRouter) openRouterStreamed.TrySetResult();
                },
                CancellationToken.None);

            await openRouterStreamed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!loadTask.IsCompleted, "fast provider completes and streams before slow earlier provider unblocks");

            grokGate.SetResult(new UsageHttpResult((int)HttpStatusCode.OK, """{"config":{"creditUsagePercent":15,"prepaidBalance":{"val":50}}}""", null));
            var board = await loadTask.WaitAsync(TimeSpan.FromSeconds(5));
            Check(board.Providers.Select(p => p.ProviderId).SequenceEqual([UsageProviderIds.Grok, UsageProviderIds.OpenRouter]),
                "final board preserves canonical registry order regardless of completion order");
            lock (streamed) Check(streamed.FirstOrDefault() == UsageProviderIds.OpenRouter, "faster provider streamed first");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void VerifyUsageBoardCache()
    {
        var root = Temp();
        try
        {
            var store = new AppSettingsStore(dataDirectory: root);
            var now = DateTimeOffset.Parse("2026-11-23T12:00:00Z");
            var grokDef = ProviderCatalog.Find(UsageProviderIds.Grok)!;
            var agyDef = ProviderCatalog.Find(UsageProviderIds.Antigravity)!;
            var codexDef = ProviderCatalog.Find(UsageProviderIds.Codex)!;
            var board = new UsageBoard(
            [
                UsageProviderEngine.Snapshot(grokDef,
                [
                    new UsageAccountSnapshot("a1", "Grok", null, "grok",
                        [new UsageWindow("credits", "Credits", 25, null, null)],
                        null, null, [], UsageFreshness.Current, now.AddMinutes(-2), null),
                    new UsageAccountSnapshot("a2", "Empty", null, "grok",
                        [], null, null, [], UsageFreshness.Unavailable, now, "unavailable"),
                ]),
                UsageProviderEngine.Snapshot(agyDef,
                [
                    new UsageAccountSnapshot("antigravity", "Antigravity", null, "agy-cli",
                        [new UsageWindow("w", "Gemini", 40, now.AddHours(3), TimeSpan.FromDays(7))],
                        null, null, [], UsageFreshness.Current, now.AddMinutes(-1), null),
                ]),
                // Projected providers (Codex/Claude) belong in quota-cache.json, never usage-board-cache.json.
                UsageProviderEngine.Snapshot(codexDef,
                [
                    new UsageAccountSnapshot("c1", "Codex", null, "codex",
                        [new UsageWindow("5h", "5h", 10, null, null)],
                        null, null, [], UsageFreshness.Current, now, null),
                ]),
            ]);

            var agyHash = AppSettingsStore.HashUsageIdentity("user@example.com");
            store.SaveUsageBoardCache(board, now, agyHash);
            Check(File.Exists(store.UsageBoardCachePath), "usage board cache persists to disk");
            Check(!File.ReadAllText(store.UsageBoardCachePath).Contains("user@example.com", StringComparison.OrdinalIgnoreCase),
                "usage board cache never stores raw account email");

            var loaded = store.LoadUsageBoardCache(now.AddMinutes(5), agyHash);
            Check(loaded is not null
                && loaded.Providers.Select(p => p.ProviderId).SequenceEqual([UsageProviderIds.Grok, UsageProviderIds.Antigravity])
                && loaded.Providers[0].Accounts.Count == 1
                && loaded.Providers[0].Accounts[0].Freshness == UsageFreshness.Cached
                && loaded.Providers[1].Accounts[0].Freshness == UsageFreshness.Cached,
                "cached Mini readings restore as Cached and omit Projected/Unavailable accounts");

            var wrongAccount = store.LoadUsageBoardCache(now.AddMinutes(5), AppSettingsStore.HashUsageIdentity("other@example.com"));
            Check(wrongAccount is not null
                && wrongAccount.Providers.Select(p => p.ProviderId).SequenceEqual([UsageProviderIds.Grok]),
                "account switch hides mismatched cached Antigravity reading without dropping other tools");

            Check(store.LoadUsageBoardCache(now.AddHours(25), agyHash) is null,
                "expired usage board cache is ignored");

            File.WriteAllText(store.UsageBoardCachePath, "{not-json");
            Check(store.LoadUsageBoardCache(now.AddMinutes(5), agyHash) is null,
                "corrupt usage board cache recovers safely");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void VerifyTransientRefreshRetention()
    {
        var now = DateTimeOffset.Parse("2026-11-23T12:00:00Z");
        var agyDef = ProviderCatalog.Find(UsageProviderIds.Antigravity)!;
        var sampleAt = now.AddMinutes(-5);
        var previous = UsageProviderEngine.Snapshot(agyDef,
        [
            new UsageAccountSnapshot("antigravity", "Antigravity", null, "agy-cli",
                [new UsageWindow("w", "Gemini Models", 74, now.AddHours(3), TimeSpan.FromDays(7))],
                null, null, [], UsageFreshness.Current, sampleAt, null),
        ]);
        var transientUnavailable = UsageProviderEngine.Snapshot(agyDef,
        [
            new UsageAccountSnapshot("antigravity", "Antigravity", null, "agy-cli",
                [], null, null, [], UsageFreshness.Unavailable, null, "unavailable"),
        ]);

        var retained = UsageProviderEngine.ReconcileProviderRefresh(previous, transientUnavailable, now);
        Check(retained is { Accounts.Count: 1 }
            && retained.Accounts[0].Freshness == UsageFreshness.Cached
            && retained.Accounts[0].SampledAt == sampleAt
            && retained.Accounts[0].Windows[0].UsedPercent == 74,
            "transient unavailability retains recent same-account reading as Cached without forging SampledAt");

        var fresh = UsageProviderEngine.Snapshot(agyDef,
        [
            new UsageAccountSnapshot("antigravity", "Antigravity", null, "agy-cli",
                [new UsageWindow("w", "Gemini Models", 80, now.AddHours(3), TimeSpan.FromDays(7))],
                null, null, [], UsageFreshness.Current, now, null),
        ]);
        var updated = UsageProviderEngine.ReconcileProviderRefresh(retained, fresh, now.AddMinutes(1));
        Check(updated is { Accounts.Count: 1 }
            && updated.Accounts[0].Freshness == UsageFreshness.Current
            && updated.Accounts[0].Windows[0].UsedPercent == 80,
            "live reading supersedes retained Cached reading");

        var signedOut = UsageProviderEngine.Snapshot(agyDef,
        [
            new UsageAccountSnapshot("antigravity", "Antigravity", null, "agy-cli",
                [], null, null, [], UsageFreshness.MissingCredentials, now, "missing_credentials"),
        ]);
        var afterSignOut = UsageProviderEngine.ReconcileProviderRefresh(previous, signedOut, now);
        Check(afterSignOut is { Accounts.Count: 1 }
            && afterSignOut.Accounts[0].Freshness == UsageFreshness.MissingCredentials
            && afterSignOut.Accounts[0].Windows.Count == 0,
            "missing credentials are never masked by cached fallback");

        var afterSwitch = UsageProviderEngine.ReconcileProviderRefresh(previous, transientUnavailable, now, accountIdentityChanged: true);
        Check(afterSwitch is { Accounts.Count: 1 } && afterSwitch.Accounts[0].Freshness == UsageFreshness.Unavailable,
            "account switch never retains prior account quota");

        var expired = UsageProviderEngine.ReconcileProviderRefresh(previous, transientUnavailable, sampleAt.AddMinutes(31));
        Check(expired is { Accounts.Count: 1 } && expired.Accounts[0].Freshness == UsageFreshness.Unavailable,
            "expired transient retention (>30m) degrades back to Unavailable");

        Check(UsageProviderEngine.ReconcileProviderRefresh(previous, null, now) is null,
            "null candidate (disabled or uninstalled) drops the provider");

        foreach (var permanentKey in new[] { "endpoint_blocked", "payload_too_large", "usage_unknown" })
        {
            var permanent = UsageProviderEngine.Snapshot(agyDef,
            [
                new UsageAccountSnapshot("antigravity", "Antigravity", null, "agy-cli",
                    [], null, null, [], UsageFreshness.Unavailable, now, permanentKey),
            ]);
            var afterPermanent = UsageProviderEngine.ReconcileProviderRefresh(previous, permanent, now);
            Check(afterPermanent is { Accounts.Count: 1 }
                && afterPermanent.Accounts[0].Freshness == UsageFreshness.Unavailable
                && afterPermanent.Accounts[0].ErrorKey == permanentKey,
                $"non-transient failure {permanentKey} is never masked by cached fallback");
        }

        var providerWideFailure = UsageProviderEngine.ReconcileProviderRefresh(
            previous, UsageProviderEngine.Snapshot(agyDef, []), now);
        Check(providerWideFailure is { Accounts.Count: 1 }
            && providerWideFailure.Accounts[0].Freshness == UsageFreshness.Cached
            && providerWideFailure.Accounts[0].Windows[0].UsedPercent == 74,
            "provider-wide read failure retains the recent reading");

        var httpDef = ProviderCatalog.Find(UsageProviderIds.Grok)!;
        UsageAccountSnapshot HttpAccount(string id, double? used, UsageFreshness freshness, DateTimeOffset? sampled, string? error) =>
            new(id, id, null, "credential",
                used is null ? [] : [new UsageWindow("w", "Weekly", used, now.AddHours(3), TimeSpan.FromDays(7))],
                null, null, [], freshness, sampled, error);
        var httpPrevious = UsageProviderEngine.Snapshot(httpDef,
        [
            HttpAccount("acct-a", 10, UsageFreshness.Current, sampleAt, null),
            HttpAccount("acct-b", 20, UsageFreshness.Current, sampleAt, null),
            HttpAccount("acct-old", 30, UsageFreshness.Current, sampleAt, null),
        ]);

        var switched = UsageProviderEngine.ReconcileProviderRefresh(httpPrevious, UsageProviderEngine.Snapshot(httpDef,
        [
            HttpAccount("acct-new", null, UsageFreshness.Unavailable, now, "rate_limited"),
        ]), now);
        Check(switched is { Accounts.Count: 1 }
            && switched.Accounts[0].AccountId == "acct-new"
            && switched.Accounts[0].Freshness == UsageFreshness.Unavailable,
            "a different account's transient failure never shows another account's quota");

        var partial = UsageProviderEngine.ReconcileProviderRefresh(httpPrevious, UsageProviderEngine.Snapshot(httpDef,
        [
            HttpAccount("acct-a", 15, UsageFreshness.Current, now, null),
            HttpAccount("ACCT-B", null, UsageFreshness.Unavailable, now, "http_error"),
        ]), now);
        Check(partial is { Accounts.Count: 2 }
            && partial.Accounts.Single(account => account.AccountId == "acct-a") is { Freshness: UsageFreshness.Current } liveA
            && liveA.Windows[0].UsedPercent == 15
            && partial.Accounts.Single(account => account.AccountId.Equals("acct-b", StringComparison.OrdinalIgnoreCase)) is
                { Freshness: UsageFreshness.Cached } retainedB
            && retainedB.Windows[0].UsedPercent == 20
            && retainedB.SampledAt == sampleAt
            && partial.Accounts.All(account => account.AccountId != "acct-old"),
            "partial refresh retains only the failing account's own reading and never resurrects removed accounts");

        var root = Temp();
        try
        {
            var store = new AppSettingsStore(dataDirectory: root);
            var agyHash = AppSettingsStore.HashUsageIdentity("user@example.com");
            store.SaveUsageBoardCache(new UsageBoard([retained!]), now, agyHash);
            var reloaded = store.LoadUsageBoardCache(now.AddMinutes(2), agyHash);
            Check(reloaded is { Providers.Count: 1 }
                && reloaded.Providers[0].Accounts[0].Freshness == UsageFreshness.Cached
                && reloaded.Providers[0].Accounts[0].Windows[0].UsedPercent == 74,
                "usage board cache persists recent retained Cached reading while still discarding Unavailable");

            store.SaveUsageBoardCache(new UsageBoard([transientUnavailable]), now, agyHash);
            Check(!File.Exists(store.UsageBoardCachePath),
                "saving an empty/unavailable-only board clears stale usage board cache");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class GatingTransport(Task<UsageHttpResult> grokResponse) : IUsageTransport
    {
        public Task<UsageHttpResult> SendAsync(UsageHttpRequest request, CancellationToken cancellationToken)
        {
            if (request.Url.Host == "cli-chat-proxy.grok.com") return grokResponse;
            if (request.Url.Host == "openrouter.ai")
            {
                return Task.FromResult(new UsageHttpResult((int)HttpStatusCode.OK,
                    """{"data":{"total_credits":25,"total_usage":5}}""", null));
            }
            return Task.FromResult(UsageHttpResult.Blocked());
        }
    }
}
