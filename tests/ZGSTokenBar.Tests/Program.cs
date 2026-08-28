using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZGSTokenBar.Core;
using ZGSTokenBar.App;
using ZGSTokenBar.Builtins;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;

if (args.Length > 0
    && string.Equals(args[0], "quota-import-fixture", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("quota-import-fixture: expected <fixture-directory>");
        return 1;
    }
    return RunQuotaImportFixture(args[1], Console.Out, Console.Error);
}

var settingsCaptureIndex = Array.FindIndex(
    args,
    value => string.Equals(value, "--settings-captures", StringComparison.OrdinalIgnoreCase));
if (settingsCaptureIndex >= 0)
{
    var outputDirectory = settingsCaptureIndex + 1 < args.Length
        ? args[settingsCaptureIndex + 1]
        : Path.Combine("artifacts", "native-settings-panel");
    RenderSettingsCaptures(outputDirectory);
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--cockpit-api-service", StringComparison.OrdinalIgnoreCase))
{
    TestCockpitCodexAccountDirectory();
    TestCockpitApiServiceCard();
    Console.WriteLine("PASS Cockpit API service aggregate");
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--ai-gateway-balance", StringComparison.OrdinalIgnoreCase))
{
    TestAiGatewayBalance();
    Console.WriteLine("PASS AI Gateway read-only balance");
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--sub2api-pool", StringComparison.OrdinalIgnoreCase))
{
    TestSub2ApiPool();
    Console.WriteLine("PASS Sub2API aggregate proxy usage and pool");
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--sub2api-account-availability", StringComparison.OrdinalIgnoreCase))
{
    TestSub2ApiAccountAvailabilityContract();
    Console.WriteLine("PASS Sub2API anonymous account availability contract");
    return 0;
}

var quotaTokenCaptureIndex = Array.FindIndex(
    args,
    value => string.Equals(value, "--quota-token-captures", StringComparison.OrdinalIgnoreCase));
if (quotaTokenCaptureIndex >= 0)
{
    var outputDirectory = quotaTokenCaptureIndex + 1 < args.Length
        ? args[quotaTokenCaptureIndex + 1]
        : Path.Combine("artifacts", "quota-token-current-used");
    RenderQuotaTokenEstimateCaptures(outputDirectory);
    return 0;
}

if (args.Length == 1
    && string.Equals(args[0], "--native-window-lifecycle", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        TestNativeWindowLifecycle();
        Console.WriteLine("PASS native HWND lifecycle acceptance");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"FAIL native HWND lifecycle acceptance: {exception}");
        return 1;
    }
}

if (args.Length == 1
    && string.Equals(args[0], "--codex-economy-router", StringComparison.OrdinalIgnoreCase))
{
    TestCodexEconomyRouter();
    Console.WriteLine("PASS Codex economy router");
    return 0;
}

var radarCli = await RadarDeveloperCli.TryRunAsync(args, Console.Out, Console.Error);
if (radarCli.Handled)
{
    return radarCli.ExitCode;
}

var taskbarMiniCaptureIndex = Array.FindIndex(
    args,
    value => string.Equals(value, "--taskbar-mini-captures", StringComparison.OrdinalIgnoreCase));
if (taskbarMiniCaptureIndex >= 0)
{
    var outputDirectory = taskbarMiniCaptureIndex + 1 < args.Length
        ? args[taskbarMiniCaptureIndex + 1]
        : Path.Combine("artifacts", "taskbar-mini");
    RenderTaskbarMiniCaptures(outputDirectory);
    return 0;
}

var localizationCaptureIndex = Array.FindIndex(
    args,
    value => string.Equals(value, "--localization-captures", StringComparison.OrdinalIgnoreCase));
if (localizationCaptureIndex >= 0)
{
    var outputDirectory = localizationCaptureIndex + 1 < args.Length
        ? args[localizationCaptureIndex + 1]
        : Path.Combine("artifacts", "native-localization");
    RenderLocalizationCaptures(outputDirectory);
    return 0;
}

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    var store = new AppSettingsStore();
    var settings = store.Load();
    using var coordinator = new QuotaCoordinator();
    var snapshot = await coordinator.RefreshAsync(settings, store.LoadCache(DateTimeOffset.UtcNow));
    foreach (var health in snapshot.Health)
    {
        Console.WriteLine($"{health.Provider}: connected={health.Connected}");
    }
    foreach (var card in snapshot.Cards)
    {
        Console.WriteLine($"card={card.Label}; provider={card.Provider}; windows={string.Join(',', card.Windows.Select(window => window.Label))}");
    }
    Console.WriteLine($"cards={snapshot.Cards.Count}; windows={snapshot.Cards.Sum(card => card.Windows.Count(window => window.UsedPercent is not null))}");
    return snapshot.Cards.Any(card => card.Windows.Any(window => window.UsedPercent is not null)) ? 0 : 1;
}

if (args.Length > 0)
{
    Console.Error.WriteLine("radar-evaluate: invalid_arguments; use --help");
    return 1;
}

var tests = new (string Name, Action Run)[]
{
    ("Claude usage parser", TestClaudeParser),
    ("Claude Retry-After backoff", TestClaudeRetryAfterBackoff),
    ("Claude explicit OAuth refresh", TestClaudeExplicitOAuthRefresh),
    ("Codex primary/secondary parser", TestCodexParser),
    ("Codex utilization normalization", TestCodexUtilization),
    ("Codex dynamic window classification", TestCodexDynamicWindows),
    ("Codex privacy-safe account labels", TestCodexAccountLabels),
    ("Provider process activity detection", TestProviderProcessActivity),
    ("Cockpit Codex active instance mapping", TestCockpitCodexInstanceActivity),
    ("Cockpit Codex account directory", TestCockpitCodexAccountDirectory),
    ("Codex active auth registry reconciliation", TestCodexActiveAuthRegistryReconciliation),
    ("Codex profile lifetime counter", TestCodexProfileLifetimeCounter),
    ("Current Codex and Cockpit account merge", TestCockpitProCodexAccountMerge),
    ("Failed active Codex account remains visible", TestFailedActiveCodexAccountRemainsVisible),
    ("Inactive Codex accounts do not restore cached cards", TestInactiveCodexAccountsDoNotRestoreCachedCards),
    ("Quota jump stabilization", TestQuotaJumpStabilization),
    ("Claude failed-refresh cache accuracy", TestQuotaCacheFreshness),
    ("Provider exhausted refresh cadence", TestProviderExhaustedRefreshCadence),
    ("Quota pace adaptive trend estimate", TestQuotaPaceEstimate),
    ("Quota pace weighted daily trend", TestQuotaPaceWeightedDailyTrend),
    ("Quota pace immediate cycle budget", TestQuotaCyclePace),
    ("Quota pace reset and freshness guards", TestQuotaPaceGuards),
    ("Quota pace history persistence", TestQuotaPacePersistence),
    ("Persistent history transient I/O protection", TestPersistentHistoryIoProtection),
    ("Bounded HTTP body reader", TestBoundedHttpBodyReader),
    ("Quota pace imported history compaction", TestQuotaImportedCompaction),
    ("Codex rollout quota ownership", TestCodexRolloutQuotaOwnership),
    ("Codex rollout bounded scanner", TestCodexRolloutBoundedScanner),
    ("Codex quota token tracker", TestCodexQuotaTokenTracker),
    ("Codex local token aggregation", TestCodexTokenUsageAggregation),
    ("Codex rollout fixture CLI", TestCodexRolloutFixtureCli),
    ("Codex Radar parser", TestRadarParser),
    ("Codex Radar reset countdown supplement", TestRadarResetCountdownSupplement),
    ("Codex Radar recommendation parser", TestRadarRecommendationParser),
    ("Codex Radar measurement parser", TestRadarMeasurementParser),
    ("Codex Radar request privacy", TestRadarRequestPrivacy),
    ("Codex Radar recommendation continuity", TestRadarRecommendationContinuity),
    ("Codex Radar local scenario evaluator", TestRadarScenarioEvaluator),
    ("Codex Radar presentation", TestRadarPresentation),
    ("Codex Radar developer CLI", TestRadarDeveloperCli),
    ("Native localization catalog", TestNativeLocalization),
    ("Radar persistent reset Mini", TestRadarPersistentResetMini),
    ("Quota pace popover presentation", TestQuotaPacePopoverPresentation),
    ("Codex account popover presentation", TestCodexAccountPopoverPresentation),
    ("Taskbar hint popover presentation", TestTaskbarHintPopoverPresentation),
    ("Taskbar popover motion geometry", TestTaskbarPopoverMotionGeometry),
    ("Codex Radar stable ranking", TestRadarStableRanking),
    ("Codex Radar pixel layout", TestRadarPopoverLayout),
    ("Codex Radar alert contract", TestRadarAlertContract),
    ("Codex Radar state persistence", TestRadarStatePersistence),
    ("Settings normalization", TestSettingsNormalization),
    ("Keep-running watchdog policy", TestKeepRunningWatchdogPolicy),
    ("Release update discovery", TestReleaseUpdateDiscovery),
    ("Settings v2 plugin migration", TestSettingsV2PluginMigration),
    ("Plugin profile state redaction", TestPluginProfileStateRedaction),
    ("Plugin package malformed manifest rejection", TestPluginMalformedManifestRejection),
    ("Plugin host catalog and revisions", TestPluginHostCatalog),
    ("Plugin request ID validation", TestPluginRequestIdValidation),
    ("Window and taskbar position persistence", TestPositionPersistence),
    ("Display topology identity", TestDisplayTopologyIdentity),
    ("Topology-aware placement isolation", TestTopologyPlacementIsolation),
    ("Topology placement migration", TestTopologyPlacementMigration),
    ("Floating placement normalization", TestFloatingPlacementNormalization),
    ("Settings locale migration", TestSettingsLocaleMigration),
    ("Settings taskbar layout migration", TestSettingsTaskbarLayoutMigration),
    ("Atomic credential writes", TestAtomicCredentialWrites),
    ("Settings corruption recovery", TestSettingsCorruptionRecovery),
    ("Adaptive bar sizing", TestAdaptiveSizing),
    ("Codex Mini display mode settings", TestCodexMiniDisplayModeSettings),
    ("System usage sampling math", TestSystemUsageSamplingMath),
    ("System usage background serialization", TestSystemUsageBackgroundSerialization),
    ("System usage sampling allocation", TestSystemUsageSamplingAllocation),
    ("System usage popover presentation", TestSystemUsagePopoverPresentation),
    ("Taskbar visible quota windows", TestTaskbarVisibleQuotaWindows),
    ("Taskbar stacked Codex accounts", TestTaskbarStackedCodexAccounts),
    ("Taskbar Codex area isolation", TestTaskbarCodexAreaIsolation),
    ("Taskbar Codex pool grouping", TestTaskbarCodexPoolGrouping),
    ("Codex pool account projection", TestCodexPoolAccountProjection),
    ("Codex pool presentation", TestCodexPoolPresentation),
    ("Taskbar Codex pool rendering", TestTaskbarCodexPoolRendering),
    ("Taskbar Mini render fault isolation", TestTaskbarMiniRenderFaultIsolation),
    ("Taskbar compact provider summaries", TestTaskbarCompactProviderSummaries),
    ("Taskbar compact AI Gateway balance", TestTaskbarCompactAiGatewayBalance),
    ("Taskbar monitor selection", TestTaskbarMonitorSelection),
    ("Taskbar Mini collapse anchor", TestTaskbarMiniCollapseAnchor),
    ("Taskbar Mini popover placement", TestTaskbarMiniPopoverPlacement),
    ("Quota remaining color gradient", TestQuotaRemainingColorGradient),
    ("Native background palette contract", TestNativeBackgroundPaletteContract),
    ("Native settings panel contract", TestNativeSettingsPanelContract),
    ("Compact reset labels", TestCompactResetLabels),
    ("Daily quota budget marker", TestDailyQuotaBudgetMarker),
    ("Quota milestone alerts", TestQuotaMilestoneAlerts),
    ("Cockpit API service card", TestCockpitApiServiceCard),
    ("Build identity payload fixtures", TestBuildIdentityPayloadFixtures),
    ("Sub2API aggregate proxy usage and pool", TestSub2ApiPool),
    ("Sub2API anonymous account availability contract", TestSub2ApiAccountAvailabilityContract),
    ("AI Gateway read-only balance", TestAiGatewayBalance),
    ("AI Gateway read-only usage", TestAiGatewayUsage),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;

static void TestClaudeParser()
{
    const string json = """
        {
          "five_hour": { "utilization": 12.5, "resets_at": "2026-07-15T12:00:00Z" },
          "seven_day": { "utilization": 43, "resets_at": "2026-07-20T00:00:00Z" },
          "limits": [
            {
              "kind": "weekly_scoped",
              "percent": 38,
              "resets_at": "2026-07-20T00:01:00Z",
              "scope": { "model": { "display_name": "Fable" } }
            }
          ]
        }
        """;
    var usage = ClaudeUsageParser.Parse(json);
    Equal(12.5, usage.FiveHourUsedPercent, "five-hour utilization");
    Equal(43, usage.WeekUsedPercent, "weekly utilization");
    Equal(DateTimeOffset.Parse("2026-07-15T12:00:00Z"), usage.FiveHourResetsAt, "five-hour reset");
    Equal(38, usage.FableWeekUsedPercent, "Fable weekly utilization");
    Equal(DateTimeOffset.Parse("2026-07-20T00:01:00Z"), usage.FableWeekResetsAt, "Fable weekly reset");
}

static void TestAiGatewayBalance()
{
    var fallbackProxy = new RecordingProxy(new Uri("http://proxy.invalid:8080"));
    var observerProxy = new PrivateObserverProxy(fallbackProxy);
    var publicGateway = new Uri("https://gateway.example.com/internal/v1/balance");
    Equal(true, observerProxy.IsBypassed(new Uri("http://127.0.0.1:9192/internal/v1/balance")), "loopback observer bypasses the system proxy");
    Equal(false, observerProxy.IsBypassed(publicGateway), "public HTTPS gateway keeps the system proxy");
    Equal(false, observerProxy.IsBypassed(new Uri("https://observer.example.com:9443/")), "remote HTTPS observers keep the system proxy");
    Equal(fallbackProxy.Proxy, observerProxy.GetProxy(publicGateway), "proxy lookup was delegated");
    Equal(2, fallbackProxy.IsBypassedCalls, "non-private bypass decisions were delegated");
    Equal(1, fallbackProxy.GetProxyCalls, "public proxy lookup was delegated exactly once");

    Equal(true, AiGatewayEndpoint.TryNormalize(
        "https://observer.example.com:9443/" ,
        out var privateEndpoint), "HTTPS observer endpoint is allowed");
    Equal(
        "https://observer.example.com:9443",
        privateEndpoint,
        "observer endpoint is normalized");
    Equal(false, AiGatewayEndpoint.TryNormalize("http://gateway.example.com:9192", out _), "public HTTP endpoint is blocked");
    Equal(false, AiGatewayEndpoint.TryNormalize("https://gateway.example.com/path", out _), "endpoint paths are blocked");

    var now = DateTimeOffset.Parse("2026-08-14T08:00:00Z", CultureInfo.InvariantCulture);
    var token = "observer-test-token";
    var handler = new RecordingHandler(request =>
    {
        Equal("GET", request.Method.Method, "balance uses GET");
        Equal("Bearer", request.Headers.Authorization?.Scheme, "observer uses bearer auth");
        Equal(token, request.Headers.Authorization?.Parameter, "observer token is sent only as auth");
        return JsonResponse($$"""
            {
              "schema_version": 1,
              "source": "zgs-ai-gateway",
              "provider": "deepseek",
              "currency": "CNY",
              "status": "available",
              "total_balance": "12.3400",
              "topped_up_balance": "10",
              "granted_balance": "2.34",
              "observed_at": "{{now:O}}"
            }
            """);
    });
    using (var http = new HttpClient(handler))
    {
        var service = new AiGatewayBalanceService(
            http,
            new MemoryAiGatewayConnectionStore(
                new AiGatewayConnection("http://127.0.0.1:9192", token)));
        var result = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.Current, result.Health.Code, "available balance is current");
        Equal(1, result.Cards.Count, "available balance creates one service card");
        Equal(AiGatewayBalanceStatus.Available, result.Cards[0].Balance?.Status, "balance status is parsed");
        Equal(12.34m, result.Cards[0].Balance?.TotalBalance, "decimal balance is parsed exactly");
        Equal("ai-gateway.balance", result.Cards[0].Key, "balance card key is stable");
    }

    var staleHandler = new RecordingHandler(_ => JsonResponse($$"""
        {
          "schema_version": 1,
          "source": "zgs-ai-gateway",
          "provider": "deepseek",
          "currency": "CNY",
          "status": "available",
          "total_balance": "1",
          "topped_up_balance": null,
          "granted_balance": null,
          "observed_at": "{{now.AddMinutes(-16):O}}"
        }
        """));
    using (var http = new HttpClient(staleHandler))
    {
        var service = new AiGatewayBalanceService(
            http,
            new MemoryAiGatewayConnectionStore(
                new AiGatewayConnection("http://127.0.0.1:9192", token)));
        var result = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.Cached, result.Health.Code, "old observation is cached");
        Equal(AiGatewayBalanceStatus.Stale, result.Cards.Single().Balance?.Status, "old observation is stale");
    }

    var unknownHandler = new RecordingHandler(_ => JsonResponse("""
        {
          "schema_version": 1,
          "source": "zgs-ai-gateway",
          "provider": "deepseek",
          "currency": "CNY",
          "status": "unknown",
          "total_balance": null,
          "topped_up_balance": null,
          "granted_balance": null,
          "observed_at": null
        }
        """));
    using (var http = new HttpClient(unknownHandler))
    {
        var service = new AiGatewayBalanceService(
            http,
            new MemoryAiGatewayConnectionStore(
                new AiGatewayConnection("http://127.0.0.1:9192", token)));
        var result = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(AiGatewayBalanceStatus.Unknown, result.Cards.Single().Balance?.Status, "unknown balance stays unknown without an observation");
    }

    var invalidHandler = new RecordingHandler(_ => JsonResponse("{\"schema_version\":1,\"unexpected\":true}"));
    using (var http = new HttpClient(invalidHandler))
    {
        var service = new AiGatewayBalanceService(
            http,
            new MemoryAiGatewayConnectionStore(
                new AiGatewayConnection("http://127.0.0.1:9192", token)));
        var result = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.HttpError, result.Health.Code, "unexpected response schema is rejected");
        Equal(0, result.Cards.Count, "invalid response never becomes a card");
    }

    var previousCard = new QuotaCard(
        "ai-gateway.balance",
        ProviderKind.AiGateway,
        "AI 网关",
        null,
        "#8b5cf6",
        true,
        [new QuotaWindow("AI", null, null, TimeSpan.Zero)])
    {
        CapturedAt = now.AddMinutes(-1),
        IsService = true,
        Balance = new AiGatewayBalance(
            AiGatewayBalanceStatus.Available,
            "CNY",
            4m,
            3m,
            1m,
            now.AddMinutes(-1)),
    };
    var previous = new QuotaSnapshot(
        [previousCard],
        [new ProviderHealth(ProviderKind.AiGateway, true, "current", ProviderHealthCode.Current)],
        now.AddMinutes(-1));
    var usage = new AiGatewayUsageSummary(
        "CNY",
        AiGatewayBalanceStatus.Available,
        new AiGatewayUsagePeriod(8, 7_000, 1_500, 8_500, 5_600, 1_400, 0, 80m, 0.0043m),
        new AiGatewayUsagePeriod(91, 120_000, 24_000, 144_000, 98_000, 20_000, 2_000, 83.05m, 0.0723m),
        now);
    Equal("UTC 今日 8 次 · 8.50K Token", NativeText.For("zh-CN").AiGatewayTodayUsage(usage), "Chinese AI Gateway daily usage summary");
    Equal("UTC today 8 req · 8.50K tokens", NativeText.For("en").AiGatewayTodayUsage(usage), "English AI Gateway daily usage summary");
    Equal("成本 ¥0.0043 · 缓存 80.0%", NativeText.For("zh-CN").AiGatewayUsageDetail(usage), "Chinese AI Gateway cost and cache summary");
    Equal("Cost ¥0.0043 · Cache 80.0%", NativeText.For("en").AiGatewayUsageDetail(usage), "English AI Gateway cost and cache summary");
    Equal("¥0.0043", NativeText.FormatCnyCost(usage.Today.EstimatedCostCny), "small CNY cost remains visible");
    var merged = QuotaCoordinator.MergeResults(
        [new ProviderResult(
            ProviderKind.AiGateway,
            [],
            new ProviderHealth(
                ProviderKind.AiGateway,
                false,
                "unavailable",
                ProviderHealthCode.Timeout))],
        previous,
        now);
    Equal(AiGatewayBalanceStatus.Stale, merged.Cards.Single().Balance?.Status, "failed refresh preserves a stale balance card");

    var firstFailure = QuotaCoordinator.MergeResults(
        [new ProviderResult(
            ProviderKind.AiGateway,
            [],
            new ProviderHealth(
                ProviderKind.AiGateway,
                false,
                "unavailable",
                ProviderHealthCode.Timeout))],
        null,
        now);
    Equal(1, firstFailure.Cards.Count, "first AI Gateway failure keeps the DeepSeek service visible");
    Equal(true, firstFailure.Cards.Single().IsService, "AI Gateway failure fallback remains a service card");
    Equal(AiGatewayBalanceStatus.Unavailable, firstFailure.Cards.Single().Balance?.Status, "AI Gateway failure fallback is explicitly unavailable");
    Equal<decimal?>(null, firstFailure.Cards.Single().Balance?.TotalBalance, "AI Gateway failure fallback never invents a balance");

    var directory = Path.Combine(Path.GetTempPath(), $"wmt-ai-gateway-{Guid.NewGuid():N}");
    try
    {
        var store = new AppSettingsStore(directory, Path.Combine(directory, "legacy.json"));
        store.SaveCache(previous);
        var loaded = store.LoadCache(now.AddMinutes(20));
        Equal(AiGatewayBalanceStatus.Stale, loaded?.Cards.Single().Balance?.Status, "cached balance ages to stale");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    using var logo = new Bitmap(24, 24);
    using var resetClock = new Bitmap(11, 11);
    Equal(
        "deepseek-v4-flash · 只读",
        NativeText.For("zh-CN").AiGatewayModel,
        "AI Gateway hover identifies the current DeepSeek model");
    Equal(
        "deepseek-v4-flash · read-only",
        NativeText.For("en").AiGatewayModel,
        "English AI Gateway hover identifies the current DeepSeek model");
    foreach (var locale in new[] { "zh-CN", "en" })
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var popover = new QuotaPopoverForm();
            using var bitmap = popover.RenderForTest(
                new QuotaPopoverContent(
                    previousCard,
                    previousCard.Windows.Single(),
                    null,
                    now,
                    null,
                    false,
                    usage),
                NativeText.For(locale),
                logo,
                resetClock,
                dpi,
                now);
            Equal(true, bitmap.Width > 0 && bitmap.Height > 0, $"{locale} {dpi} DPI AI Gateway popover renders");
        }
    }
}

static void TestBuildIdentityPayloadFixtures()
{
    var root = Path.Combine(Path.GetTempPath(), $"ztb-build-identity-{Guid.NewGuid():N}");
    var candidateDirectory = Path.Combine(root, "candidate");
    var runningDirectory = Path.Combine(root, "running");
    var copiedDirectory = Path.Combine(root, "copied");
    var fallbackDirectory = Path.Combine(root, "fallback");
    Directory.CreateDirectory(candidateDirectory);
    Directory.CreateDirectory(runningDirectory);
    Directory.CreateDirectory(copiedDirectory);
    Directory.CreateDirectory(fallbackDirectory);
    try
    {
        const string semanticVersion = "3.0.0";
        var candidatePayload = Encoding.UTF8.GetBytes($"version={semanticVersion}\nimplementation=candidate\n");
        var runningPayload = Encoding.UTF8.GetBytes($"version={semanticVersion}\nimplementation=running\n");
        File.WriteAllBytes(Path.Combine(candidateDirectory, "ZGSTokenBar.dll"), candidatePayload);
        File.WriteAllBytes(Path.Combine(candidateDirectory, "ZGSTokenBar.exe"), Encoding.UTF8.GetBytes("fallback decoy"));
        File.WriteAllBytes(Path.Combine(runningDirectory, "ZGSTokenBar.dll"), runningPayload);
        File.WriteAllBytes(Path.Combine(copiedDirectory, "ZGSTokenBar.dll"), candidatePayload);
        File.WriteAllBytes(Path.Combine(fallbackDirectory, "ZGSTokenBar.exe"), runningPayload);

        var candidateId = FixtureBuildId(candidateDirectory);
        var runningId = FixtureBuildId(runningDirectory);
        Equal(false, string.Equals(candidateId, runningId, StringComparison.Ordinal), "same semantic version has distinct payload identities");
        Equal(candidateId, FixtureBuildId(copiedDirectory), "same payload has the same build identity");
        Equal(true, candidateId is not null
            && candidateId.Length == 64
            && candidateId == candidateId.ToLowerInvariant(), "build identity is a full lowercase digest");
        Equal(".exe", Path.GetExtension(FixtureArtifact(fallbackDirectory)), "missing DLL uses the executable payload");
        Equal<string?>(null, FixtureBuildId(Path.Combine(root, "missing")), "missing payload has no build identity");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    static string FixtureArtifact(string directory)
    {
        var libraryPath = Path.Combine(directory, "ZGSTokenBar.dll");
        return File.Exists(libraryPath) ? libraryPath : Path.Combine(directory, "ZGSTokenBar.exe");
    }

    static string? FixtureBuildId(string directory)
    {
        try
        {
            using var stream = File.OpenRead(FixtureArtifact(directory));
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException
                or CryptographicException)
        {
            return null;
        }
    }
}

static void TestSub2ApiPool()
{
    Equal(true, Sub2ApiPoolEndpoint.TryNormalize(
        "https://observer.example.com:9443/",
        out var privateEndpoint), "HTTPS aggregate observer endpoint is allowed");
    Equal(
        "https://observer.example.com:9443",
        privateEndpoint,
        "aggregate observer endpoint is normalized");
    Equal(false, Sub2ApiPoolEndpoint.TryNormalize("http://observer.example.com:9443", out _), "remote plaintext observer is blocked");
    Equal(true, Sub2ApiPoolEndpoint.TryNormalize("https://gateway.example.com:9443", out _), "remote HTTPS observer is allowed");

    var now = DateTimeOffset.Parse("2026-08-18T08:00:00Z", CultureInfo.InvariantCulture);
    var token = "sub2api-observer-test-token";
    var availableBody = $$"""
        {
          "schema_version": 1,
          "source": "zgs-sub2api",
          "status": "available",
          "available_accounts": 2,
          "total_accounts": 2,
          "rate_limited_accounts": 0,
          "error_accounts": 0,
          "free_concurrency": 4,
          "max_concurrency": 4,
          "observed_at": "{{now:O}}"
        }
        """;
    Sub2ApiPoolFetchResult availableResult;
    using (var http = new HttpClient(new RecordingHandler(request =>
    {
        Equal("GET", request.Method.Method, "Sub2API pool uses GET");
        Equal("/internal/v1/sub2api-pool", request.RequestUri?.AbsolutePath, "Sub2API uses only the observer pool route");
        Equal("Bearer", request.Headers.Authorization?.Scheme, "Sub2API observer uses bearer auth");
        Equal(token, request.Headers.Authorization?.Parameter, "Sub2API token is sent only as auth");
        return JsonResponse(availableBody);
    })))
    {
        var service = new Sub2ApiPoolService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(privateEndpoint, token)));
        availableResult = service.FetchAsync(now).GetAwaiter().GetResult();
    }
    Equal(ProviderHealthCode.Current, availableResult.Code, "available Sub2API pool is current");
    var pool = availableResult.Pool ?? throw new InvalidOperationException("available Sub2API pool is missing");
    Equal(Sub2ApiPoolStatus.Available, pool.Status, "Sub2API available state is parsed");
    Equal(2, pool.AvailableAccounts, "two schedulable accounts are preserved");
    Equal(2, pool.TotalAccounts, "two configured accounts are preserved");
    Equal("2/2", Sub2ApiPoolFormatting.AccountPair(pool), "two fully available accounts display as 2/2, not a summed percentage");
    Equal("4/4", Sub2ApiPoolFormatting.ConcurrencyPair(pool), "free concurrency is displayed as a pair");
    Equal("可用账号 2/2", NativeText.For("zh-CN").Sub2ApiPoolAvailableAccounts(pool), "Chinese pool label is concise");
    Equal("Available accounts 2/2", NativeText.For("en").Sub2ApiPoolAvailableAccounts(pool), "English pool label is concise");

    var usageBody = $$"""
        {
          "schema_version": 1,
          "source": "zgs-sub2api",
          "status": "available",
          "today_requests": 19,
          "today_input_tokens": 1200,
          "today_output_tokens": 340,
          "today_cache_creation_tokens": 0,
          "today_cache_read_tokens": 80,
          "today_tokens": 1540,
          "total_requests": 2430,
          "total_input_tokens": 120000,
          "total_output_tokens": 34000,
          "total_cache_creation_tokens": 0,
          "total_cache_read_tokens": 8000,
          "total_tokens": 154000,
          "observed_at": "{{now:O}}"
        }
        """;
    Sub2ApiUsageFetchResult usageResult;
    using (var http = new HttpClient(new RecordingHandler(request =>
    {
        Equal("GET", request.Method.Method, "Sub2API usage uses GET");
        Equal("/internal/v1/sub2api-usage", request.RequestUri?.AbsolutePath, "Sub2API uses only the observer usage route");
        Equal("Bearer", request.Headers.Authorization?.Scheme, "Sub2API usage uses bearer auth");
        Equal(token, request.Headers.Authorization?.Parameter, "Sub2API usage token is sent only as auth");
        return JsonResponse(usageBody);
    })))
    {
        var service = new Sub2ApiUsageService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(privateEndpoint, token)));
        usageResult = service.FetchAsync(now).GetAwaiter().GetResult();
    }
    Equal(ProviderHealthCode.Current, usageResult.Code, "available Sub2API usage is current");
    var usage = usageResult.Usage ?? throw new InvalidOperationException("available Sub2API usage is missing");
    Equal(Sub2ApiUsageStatus.Available, usage.Status, "Sub2API usage state is parsed");
    Equal(19L, usage.TodayRequests, "today request count is preserved");
    Equal(1540L, usage.TodayTokens, "today proxy tokens are preserved");
    Equal(2430L, usage.TotalRequests, "total request count is preserved");
    Equal(154000L, usage.TotalTokens, "total proxy tokens are preserved");
    Equal("1.54K", NativeText.For("zh-CN").Sub2ApiUsageCompact(usage), "Sub2API Mini displays today's token count");

    var quotaBody = $$"""
        {
          "schema_version": 1,
          "source": "zgs-sub2api",
          "status": "available",
          "account_count": 2,
          "five_hour_account_count": 2,
          "five_hour_remaining_percent": 65,
          "five_hour_remaining_account_equivalents": 1.3,
          "seven_day_account_count": 2,
          "seven_day_remaining_percent": 65,
          "seven_day_remaining_account_equivalents": 1.3,
          "observed_at": "{{now:O}}"
        }
        """;
    Sub2ApiQuotaFetchResult quotaResult;
    using (var http = new HttpClient(new RecordingHandler(request =>
    {
        Equal("GET", request.Method.Method, "Sub2API quota uses GET");
        Equal("/internal/v1/sub2api-quota", request.RequestUri?.AbsolutePath, "Sub2API uses only the observer quota route");
        Equal("Bearer", request.Headers.Authorization?.Scheme, "Sub2API quota uses bearer auth");
        Equal(token, request.Headers.Authorization?.Parameter, "Sub2API quota token is sent only as auth");
        return JsonResponse(quotaBody);
    })))
    {
        var service = new Sub2ApiQuotaService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(privateEndpoint, token)));
        quotaResult = service.FetchAsync(now).GetAwaiter().GetResult();
    }
    Equal(ProviderHealthCode.Current, quotaResult.Code, "available Sub2API quota is current");
    var quota = quotaResult.Quota ?? throw new InvalidOperationException("available Sub2API quota is missing");
    Equal(Sub2ApiQuotaStatus.Available, quota.Status, "Sub2API quota state is parsed");
    Equal(2, quota.AccountCount, "Sub2API quota keeps the de-duplicated account count");
    Equal(2, quota.SevenDayAccountCount, "Sub2API quota keeps seven-day coverage");
    Equal(65d, quota.SevenDayRemainingPercent, "Sub2API quota keeps normalized seven-day remaining percent");
    Equal(1.3d, quota.SevenDayRemainingAccountEquivalents, "Sub2API quota keeps account-equivalent capacity");
    Equal("65%", NativeText.For("zh-CN").Sub2ApiQuotaCompact(quota), "Sub2API Mini omits the aggregate quota window label");
    Equal(
        "7d 额度汇总 1.30/2 账号份额",
        NativeText.For("zh-CN").Sub2ApiQuotaWindowDetails(Sub2ApiQuotaFormatting.PreferredWindow(quota)!),
        "Sub2API quota detail shows the summed account-equivalent capacity");
    Equal(
        "可用账号 4/12 · 并发 4/4",
        NativeText.For("zh-CN").Sub2ApiUsagePool(pool with { AvailableAccounts = 4, TotalAccounts = 12 }),
        "Sub2API quota detail distinguishes pool availability from quota coverage");

    var accountAvailabilityBody = $$"""
        {
          "schema_version": 1,
          "source": "zgs-sub2api",
          "status": "available",
          "coverage": "complete",
          "eligible_account_count": 2,
          "readable_account_count": 2,
          "aggregate_remaining_percent": 65,
          "remaining_account_equivalents": 1.3,
          "accounts": [
            { "slot": 1, "state": "available", "remaining_percent": 80 },
            { "slot": 2, "state": "available", "remaining_percent": 50 }
          ],
          "observed_at": "{{now:O}}"
        }
        """;
    Sub2ApiAccountAvailabilityFetchResult accountAvailabilityResult;
    using (var http = new HttpClient(new RecordingHandler(request =>
    {
        Equal("GET", request.Method.Method, "Sub2API account availability uses GET");
        Equal(
            "/internal/v1/sub2api-account-availability",
            request.RequestUri?.AbsolutePath,
            "Sub2API uses only the anonymous account availability route");
        Equal("Bearer", request.Headers.Authorization?.Scheme, "Sub2API account availability uses bearer auth");
        Equal(token, request.Headers.Authorization?.Parameter, "Sub2API account availability token is sent only as auth");
        return JsonResponse(accountAvailabilityBody);
    })))
    {
        var service = new Sub2ApiAccountAvailabilityService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(privateEndpoint, token)));
        accountAvailabilityResult = service.FetchAsync(now).GetAwaiter().GetResult();
    }
    Equal(ProviderHealthCode.Current, accountAvailabilityResult.Code, "available Sub2API account availability is current");
    var accountAvailability = accountAvailabilityResult.Availability
        ?? throw new InvalidOperationException("available Sub2API account availability is missing");
    Equal(Sub2ApiQuotaStatus.Available, accountAvailability.Status, "Sub2API account availability state is parsed");
    Equal(2, accountAvailability.Accounts?.Count, "anonymous account count is preserved");
    Equal(1, accountAvailability.Accounts?[0].Slot, "first anonymous account slot is preserved");
    Equal(80d, accountAvailability.Accounts?[0].RemainingPercent, "first account availability is preserved");
    Equal(65d, Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(accountAvailability), "account availability mean is normalized");
    Equal("65%", NativeText.For("zh-CN").Sub2ApiAccountAvailabilityCompact(accountAvailability), "Sub2API Mini displays current availability without a window label");

    var staleBody = $$"""
        {
          "schema_version": 1,
          "source": "zgs-sub2api",
          "status": "available",
          "available_accounts": 1,
          "total_accounts": 2,
          "rate_limited_accounts": 1,
          "error_accounts": 0,
          "free_concurrency": 1,
          "max_concurrency": 2,
          "observed_at": "{{now.AddMinutes(-16):O}}"
        }
        """;
    using (var http = new HttpClient(new RecordingHandler(_ => JsonResponse(staleBody))))
    {
        var service = new Sub2ApiPoolService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(privateEndpoint, token)));
        var stale = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.Cached, stale.Code, "old Sub2API observation is cached");
        Equal(Sub2ApiPoolStatus.Stale, stale.Pool?.Status, "old Sub2API observation is stale");
    }

    using (var http = new HttpClient(new RecordingHandler(_ => JsonResponse("{\"schema_version\":1,\"unexpected\":true}"))))
    {
        var service = new Sub2ApiPoolService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(privateEndpoint, token)));
        var invalid = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.HttpError, invalid.Code, "invalid Sub2API schema is rejected");
        Equal<Sub2ApiPoolAvailability?>(null, invalid.Pool, "invalid Sub2API schema never becomes a pool");
    }

    using (var http = new HttpClient(new RecordingHandler(_ => JsonResponse("{\"schema_version\":1,\"unexpected\":true}"))))
    {
        var service = new Sub2ApiUsageService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(privateEndpoint, token)));
        var invalid = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.HttpError, invalid.Code, "invalid Sub2API usage schema is rejected");
        Equal<Sub2ApiUsageSummary?>(null, invalid.Usage, "invalid Sub2API schema never becomes usage");
    }
    using (var http = new HttpClient(new RecordingHandler(_ => JsonResponse("{\"schema_version\":1,\"unexpected\":true}"))))
    {
        var service = new Sub2ApiQuotaService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(privateEndpoint, token)));
        var invalid = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.HttpError, invalid.Code, "invalid Sub2API quota schema is rejected");
        Equal<Sub2ApiQuotaSummary?>(null, invalid.Quota, "invalid Sub2API schema never becomes quota");
    }
    using (var http = new HttpClient(new RecordingHandler(_ => JsonResponse($$"""
        {
          "schema_version": 1,
          "source": "zgs-sub2api",
          "status": "available",
          "coverage": "complete",
          "eligible_account_count": 1,
          "readable_account_count": 1,
          "aggregate_remaining_percent": 65,
          "remaining_account_equivalents": 0.65,
          "accounts": [
            { "slot": 1, "state": "available", "remaining_percent": 65, "account_id": "must-not-leave-worker" }
          ],
          "observed_at": "{{now:O}}"
        }
        """))))
    {
        var service = new Sub2ApiAccountAvailabilityService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(privateEndpoint, token)));
        var invalid = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.HttpError, invalid.Code, "account identity is rejected from the anonymous availability schema");
        Equal<Sub2ApiAccountAvailabilitySummary?>(null, invalid.Availability, "identity-bearing data never becomes account availability");
    }

    var serviceCard = new QuotaCard(
        "codex.api-service",
        ProviderKind.Codex,
        "API · 1",
        "API key",
        "#10a37f",
        true,
        [new QuotaWindow("API", null, null, TimeSpan.Zero)])
    {
        CapturedAt = now,
        IsService = true,
        ServiceCount = 1,
        ServiceDisplayName = "sub2api",
    };
    var snapshot = new QuotaSnapshot(
        [serviceCard],
        [new ProviderHealth(ProviderKind.Codex, false, "API service", ProviderHealthCode.Unavailable)],
        now);
    var ordinarySnapshot = new QuotaSnapshot(
        [serviceCard with { IsService = false, ServiceDisplayName = null }],
        snapshot.Health,
        now);
    Equal(
        ordinarySnapshot,
        QuotaCoordinator.AttachSub2ApiPool(ordinarySnapshot, null, availableResult),
        "configured observer does not create a service card without an active API service");
    Equal(
        ordinarySnapshot,
        QuotaCoordinator.AttachSub2ApiUsage(ordinarySnapshot, null, usageResult),
        "observer usage remains hidden without an active API service");
    Equal(
        ordinarySnapshot,
        QuotaCoordinator.AttachSub2ApiQuota(ordinarySnapshot, null, quotaResult),
        "observer quota remains hidden without an active API service");
    Equal(
        ordinarySnapshot,
        QuotaCoordinator.AttachSub2ApiAccountAvailability(
            ordinarySnapshot,
            null,
            accountAvailabilityResult),
        "observer availability remains hidden without an active API service");
    var attached = QuotaCoordinator.AttachSub2ApiPool(snapshot, null, availableResult);
    Equal(pool, attached.Cards.Single().Sub2ApiPool, "pool attaches only to the existing sub2api service card");
    var attachedUsage = QuotaCoordinator.AttachSub2ApiUsage(attached, null, usageResult);
    Equal(usage, attachedUsage.Cards.Single().Sub2ApiUsage, "usage attaches to the existing sub2api service card");
    var attachedQuota = QuotaCoordinator.AttachSub2ApiQuota(attachedUsage, null, quotaResult);
    Equal(quota, attachedQuota.Cards.Single().Sub2ApiQuota, "quota attaches only to the existing sub2api service card");
    var attachedAccountAvailability = QuotaCoordinator.AttachSub2ApiAccountAvailability(
        attachedQuota,
        null,
        accountAvailabilityResult);
    Equal(
        accountAvailability,
        attachedAccountAvailability.Cards.Single().Sub2ApiAccountAvailability,
        "anonymous account availability attaches only to the existing sub2api service card");

    var staleAttached = QuotaCoordinator.AttachSub2ApiPool(
        snapshot,
        attached,
        new Sub2ApiPoolFetchResult(null, ProviderHealthCode.Timeout));
    Equal(Sub2ApiPoolStatus.Stale, staleAttached.Cards.Single().Sub2ApiPool?.Status, "temporary observer failure keeps only a stale pool snapshot");
    var staleUsageAttached = QuotaCoordinator.AttachSub2ApiUsage(
        attached,
        attachedUsage,
        new Sub2ApiUsageFetchResult(null, ProviderHealthCode.Timeout));
    Equal(Sub2ApiUsageStatus.Stale, staleUsageAttached.Cards.Single().Sub2ApiUsage?.Status, "temporary observer failure keeps only stale usage");
    var staleQuotaAttached = QuotaCoordinator.AttachSub2ApiQuota(
        attachedUsage,
        attachedQuota,
        new Sub2ApiQuotaFetchResult(null, ProviderHealthCode.Timeout));
    Equal(Sub2ApiQuotaStatus.Stale, staleQuotaAttached.Cards.Single().Sub2ApiQuota?.Status, "temporary observer failure keeps only stale quota");
    var staleAccountAvailabilityAttached = QuotaCoordinator.AttachSub2ApiAccountAvailability(
        attachedQuota,
        attachedAccountAvailability,
        new Sub2ApiAccountAvailabilityFetchResult(null, ProviderHealthCode.Timeout));
    Equal(
        Sub2ApiQuotaStatus.Stale,
        staleAccountAvailabilityAttached.Cards.Single().Sub2ApiAccountAvailability?.Status,
        "temporary observer failure keeps only stale account availability");

    using (var mini = new BarForm(
        new AppSettings { Locale = "zh-CN", EnableAnimations = false, EnableRadar = false },
        attachedAccountAvailability,
        renderOnly: true,
        renderDpi: 96,
        activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex }))
    {
        mini.CreateControl();
        using var bitmap = new Bitmap(mini.ClientSize.Width, mini.ClientSize.Height, PixelFormat.Format32bppPArgb);
        mini.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        Equal(TaskbarMiniLayoutMath.ServiceCardWidth, mini.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Codex).Width, "Sub2API Mini retains compact service width");
        Equal(true, Enumerable.Range(0, bitmap.Width).Any(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).A > 0)), "Sub2API Mini renders visible pixels");
    }

    var plusCard = new QuotaCard(
        "codex.plus",
        ProviderKind.Codex,
        "Codex · 1",
        "plus",
        "#10a37f",
        true,
        [new QuotaWindow("7d", 31, now.AddDays(4), TimeSpan.FromDays(7))]);
    var stackedSnapshot = new QuotaSnapshot(
        [plusCard, attachedAccountAvailability.Cards.Single()],
        [new ProviderHealth(ProviderKind.Codex, true, "current", ProviderHealthCode.Current)],
        now);
    var stackedGroup = TaskbarMiniGrouping.Create(stackedSnapshot.Cards).Single();
    Equal(true, stackedGroup.IsStackedCodex, "Sub2API shares the two-row Codex Mini module");
    Equal("codex.api-service", stackedGroup.Cards[1].Key, "Sub2API occupies the second Codex Mini row");

    using (var stackedMini = new BarForm(
        new AppSettings { Locale = "zh-CN", EnableAnimations = false, EnableRadar = false },
        stackedSnapshot,
        renderOnly: true,
        renderDpi: 96,
        activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex }))
    {
        stackedMini.CreateControl();
        using var bitmap = new Bitmap(stackedMini.ClientSize.Width, stackedMini.ClientSize.Height, PixelFormat.Format32bppPArgb);
        stackedMini.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        Equal(TaskbarMiniLayoutMath.CardWidth, stackedMini.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Codex).Width, "stacked Codex Mini keeps a full-width service row");

        var secondRowValueBounds = new Rectangle(
            TaskbarMiniLayoutMath.OuterPadding + TaskbarMiniLayoutMath.CardWidth - 56,
            23,
            48,
            15);
        var quotaValueVisible = Enumerable.Range(secondRowValueBounds.Left, secondRowValueBounds.Width)
            .SelectMany(x => Enumerable.Range(secondRowValueBounds.Top, secondRowValueBounds.Height)
                .Select(y => bitmap.GetPixel(x, y)))
            .Any(pixel => pixel.R <= 90 && pixel.G >= 160 && pixel.B >= 110);
        Equal(true, quotaValueVisible, "stacked Sub2API Mini renders its quota value on the second row");

        var quotaRailVisible = Enumerable.Range(35, 3)
            .Any(y => Enumerable.Range(60, 46)
                .Select(x => bitmap.GetPixel(x, y))
                .Count(pixel => pixel.R <= 90 && pixel.G >= 160 && pixel.B >= 110) >= 30);
        Equal(true, quotaRailVisible, "stacked Sub2API Mini renders its aggregate quota rail on the second row");
    }

    using var popover = new QuotaPopoverForm();
    using var providerLogo = new Bitmap(24, 24);
    using var resetClock = new Bitmap(10, 10);
    using var popoverBitmap = popover.RenderForTest(
        new QuotaPopoverContent(attachedAccountAvailability.Cards.Single(), serviceCard.Windows.Single(), null, now, null, false),
        NativeText.For("zh-CN"),
        providerLogo,
        resetClock,
        96,
        now);
    Equal(
        new Size(
            QuotaPopoverForm.LogicalBodyWidth,
            QuotaPopoverForm.AccountAvailabilityBodyHeight(2, includeProgressRail: true) + 8),
        popoverBitmap.Size,
        "Sub2API account availability popover retains deterministic dimensions");
}

static void TestSub2ApiAccountAvailabilityContract()
{
    var now = DateTimeOffset.Parse("2026-08-18T08:00:00Z", CultureInfo.InvariantCulture);
    const string endpoint = "https://observer.example.com:9443";
    const string token = "synthetic-sub2api-observer-token";

    Equal(1, QuotaPopoverForm.AccountAvailabilityColumnCount(2), "two Sub2API accounts use one vertical column");
    Equal(2, QuotaPopoverForm.AccountAvailabilityColumnCount(6), "six Sub2API accounts use two columns");
    Equal(3, QuotaPopoverForm.AccountAvailabilityColumnCount(12), "twelve Sub2API accounts use three columns");

    static string JsonInt(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
    static string JsonDouble(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "null";
    static string JsonTimestamp(DateTimeOffset? value) => value is { } timestamp
        ? JsonSerializer.Serialize(timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        : "null";
    static string Payload(
        string status,
        string coverage,
        int? eligible,
        int? readable,
        double? aggregate,
        double? equivalents,
        string accounts,
        DateTimeOffset? observedAt) => $$"""
        {
          "schema_version": 1,
          "source": "zgs-sub2api",
          "status": "{{status}}",
          "coverage": "{{coverage}}",
          "eligible_account_count": {{JsonInt(eligible)}},
          "readable_account_count": {{JsonInt(readable)}},
          "aggregate_remaining_percent": {{JsonDouble(aggregate)}},
          "remaining_account_equivalents": {{JsonDouble(equivalents)}},
          "accounts": {{accounts}},
          "observed_at": {{JsonTimestamp(observedAt)}}
        }
        """;

    Sub2ApiAccountAvailabilityFetchResult Fetch(string body)
    {
        using var http = new HttpClient(new RecordingHandler(request =>
        {
            Equal("GET", request.Method.Method, "anonymous availability uses GET");
            Equal(
                "/internal/v1/sub2api-account-availability",
                request.RequestUri?.AbsolutePath,
                "anonymous availability uses only the v1 route");
            Equal("Bearer", request.Headers.Authorization?.Scheme, "anonymous availability uses bearer auth");
            Equal(token, request.Headers.Authorization?.Parameter, "anonymous availability keeps identity in auth only");
            return JsonResponse(body);
        }));
        var service = new Sub2ApiAccountAvailabilityService(
            http,
            new MemorySub2ApiPoolConnectionStore(new Sub2ApiPoolConnection(endpoint, token)));
        return service.FetchAsync(now).GetAwaiter().GetResult();
    }

    void Reject(string body, string label)
    {
        var result = Fetch(body);
        Equal(ProviderHealthCode.HttpError, result.Code, $"{label} is rejected");
        Equal<Sub2ApiAccountAvailabilitySummary?>(null, result.Availability, $"{label} yields no state");
    }

    var completeAccounts = "["
        + "{\"slot\":1,\"state\":\"available\",\"remaining_percent\":92},"
        + "{\"slot\":2,\"state\":\"available\",\"remaining_percent\":77},"
        + "{\"slot\":3,\"state\":\"available\",\"remaining_percent\":64},"
        + "{\"slot\":4,\"state\":\"available\",\"remaining_percent\":51},"
        + "{\"slot\":5,\"state\":\"available\",\"remaining_percent\":36}]";
    var complete = Fetch(Payload("available", "complete", 5, 5, 64, 3.2, completeAccounts, now));
    Equal(ProviderHealthCode.Current, complete.Code, "complete availability is current");
    var completeSummary = complete.Availability ?? throw new InvalidOperationException("complete state missing");
    Equal(Sub2ApiQuotaStatus.Available, completeSummary.Status, "freshness is separate from coverage");
    Equal(Sub2ApiAccountAvailabilityCoverage.Complete, completeSummary.Coverage, "five accounts are complete");
    Equal(5, completeSummary.EligibleAccountCount, "eligible count is preserved");
    Equal(5, completeSummary.ReadableAccountCount, "readable count is preserved");
    Equal(64d, completeSummary.AggregateRemainingPercent, "complete aggregate percent is preserved");
    Equal(3.2d, completeSummary.RemainingAccountEquivalents, "complete aggregate equivalents are preserved");
    Equal(5, completeSummary.Accounts?.Count, "all anonymous slots are preserved");
    Equal(Sub2ApiAccountAvailabilityState.Available, completeSummary.Accounts?[0].State, "slot state is preserved");
    Equal(92d, completeSummary.Accounts?[0].RemainingPercent, "readable slot percent is preserved");
    Equal(64d, Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(completeSummary), "complete aggregate is renderable");
    Equal("64%", NativeText.For("zh-CN").Sub2ApiAccountAvailabilityCompact(completeSummary), "complete Mini uses the aggregate percent");

    var partialAccounts = "["
        + "{\"slot\":1,\"state\":\"available\",\"remaining_percent\":92},"
        + "{\"slot\":2,\"state\":\"available\",\"remaining_percent\":77},"
        + "{\"slot\":3,\"state\":\"available\",\"remaining_percent\":64},"
        + "{\"slot\":4,\"state\":\"available\",\"remaining_percent\":51},"
        + "{\"slot\":5,\"state\":\"unavailable\",\"remaining_percent\":null}]";
    var partial = Fetch(Payload("available", "partial", 5, 4, null, null, partialAccounts, now));
    Equal(ProviderHealthCode.Current, partial.Code, "partial availability is current");
    var partialSummary = partial.Availability ?? throw new InvalidOperationException("partial state missing");
    Equal(Sub2ApiAccountAvailabilityCoverage.Partial, partialSummary.Coverage, "four of five is partial");
    Equal(4, partialSummary.ReadableAccountCount, "partial readable count is preserved");
    Equal<double?>(null, partialSummary.AggregateRemainingPercent, "partial aggregate percent is null");
    Equal<double?>(null, partialSummary.RemainingAccountEquivalents, "partial aggregate equivalents are null");
    Equal(Sub2ApiAccountAvailabilityState.Unavailable, partialSummary.Accounts?[4].State, "unreadable slot is retained anonymously");
    Equal<double?>(null, partialSummary.Accounts?[4].RemainingPercent, "unreadable slot percent is null");
    Equal<double?>(null, Sub2ApiAccountAvailabilityFormatting.MeanRemainingPercent(partialSummary), "partial never fabricates a percent");
    Equal("4/5", NativeText.For("en").Sub2ApiAccountAvailabilityCompact(partialSummary), "partial Mini uses readable over eligible coverage");

    var knownNoneAccounts = "["
        + "{\"slot\":1,\"state\":\"unavailable\",\"remaining_percent\":null},"
        + "{\"slot\":2,\"state\":\"unavailable\",\"remaining_percent\":null},"
        + "{\"slot\":3,\"state\":\"unavailable\",\"remaining_percent\":null},"
        + "{\"slot\":4,\"state\":\"unavailable\",\"remaining_percent\":null},"
        + "{\"slot\":5,\"state\":\"unavailable\",\"remaining_percent\":null}]";
    var knownNone = Fetch(Payload("available", "none", 5, 0, null, null, knownNoneAccounts, now));
    Equal(ProviderHealthCode.Current, knownNone.Code, "known candidate list with no readable quotas is available");
    Equal(Sub2ApiAccountAvailabilityCoverage.None, knownNone.Availability?.Coverage, "all unreadable coverage is none");
    Equal(5, knownNone.Availability?.EligibleAccountCount, "known-none eligible count is preserved");
    Equal(0, knownNone.Availability?.ReadableAccountCount, "known-none readable count is zero");
    Equal(5, knownNone.Availability?.Accounts?.Count, "known-none retains every anonymous slot");
    Equal("0/5", NativeText.For("zh-CN").Sub2ApiAccountAvailabilityCompact(knownNone.Availability!), "known-none Mini never renders zero percent");

    var noCandidates = Fetch(Payload("available", "none", 0, 0, null, null, "[]", now));
    Equal(ProviderHealthCode.Current, noCandidates.Code, "empty candidate list is an available known-none state");
    Equal(0, noCandidates.Availability?.EligibleAccountCount, "empty candidate list has zero eligible accounts");
    Equal(0, noCandidates.Availability?.Accounts?.Count, "empty candidate list remains an empty array");

    var globalUnavailable = Fetch(Payload("unavailable", "none", null, null, null, null, "null", null));
    Equal(ProviderHealthCode.Unavailable, globalUnavailable.Code, "global observer failure is unavailable");
    Equal(Sub2ApiQuotaStatus.Unavailable, globalUnavailable.Availability?.Status, "global failure freshness is unavailable");
    Equal(Sub2ApiAccountAvailabilityCoverage.None, globalUnavailable.Availability?.Coverage, "global failure coverage is none");
    Equal<int?>(null, globalUnavailable.Availability?.EligibleAccountCount, "global failure hides eligible count");
    Equal<IReadOnlyList<Sub2ApiAccountAvailabilityEntry>?>(null, globalUnavailable.Availability?.Accounts, "global failure hides account slots");
    Equal("额度暂不可用", NativeText.For("zh-CN").Sub2ApiAccountAvailabilityCompact(globalUnavailable.Availability!), "unavailable Mini uses localized unavailable text");
    Equal("Quota unavailable", NativeText.For("en").Sub2ApiAccountAvailabilityCompact(globalUnavailable.Availability!), "unavailable Mini uses English unavailable text");

    var stale = Fetch(Payload("stale", "complete", 5, 5, 64, 3.2, completeAccounts, now.AddMinutes(-14.5)));
    Equal(ProviderHealthCode.Cached, stale.Code, "fresh stale snapshot remains cached");
    Equal(Sub2ApiQuotaStatus.Stale, stale.Availability?.Status, "stale freshness is preserved");
    Equal(Sub2ApiAccountAvailabilityCoverage.Complete, stale.Availability?.Coverage, "stale complete coverage is preserved");
    var expired = Fetch(Payload("stale", "complete", 5, 5, 64, 3.2, completeAccounts, now.AddMinutes(-15.01)));
    Equal(ProviderHealthCode.Unavailable, expired.Code, "expired stale snapshot is unavailable");
    Equal(Sub2ApiQuotaStatus.Unavailable, expired.Availability?.Status, "expired stale state hides old counts");
    Equal<int?>(null, expired.Availability?.EligibleAccountCount, "expired stale state has no fabricated counts");

    Reject(
        Payload("available", "complete", 5, 5, 64, 3.2, completeAccounts, now)
            .Replace("\"observed_at\"", "\"error\"", StringComparison.Ordinal),
        "unknown root field");
    Reject(
        Payload("available", "complete", 5, 5, 64, 3.2,
            "[{\"slot\":1,\"state\":\"available\",\"remaining_percent\":92,\"account_id\":\"private\"}]",
            now),
        "identity-bearing account field");
    Reject(
        Payload("available", "complete", 5, 5, 63.9, 3.2, completeAccounts, now),
        "aggregate mismatch");
    Reject(
        Payload("available", "partial", 5, 4, 64, null, partialAccounts, now),
        "partial aggregate");
    Reject(
        Payload("available", "complete", 5, 5, 64, 3.2,
            completeAccounts.Replace("\"slot\":2", "\"slot\":1", StringComparison.Ordinal),
            now),
        "non-contiguous duplicate slots");
    Reject(
        Payload("available", "partial", 5, 4, null, null,
            partialAccounts.Replace("\"remaining_percent\":null", "\"remaining_percent\":0", StringComparison.Ordinal),
            now),
        "unavailable slot percent");
    Reject(
        Payload("stale", "none", 5, 0, null, null, knownNoneAccounts, now),
        "stale known-none combination");
    Reject(
        Payload("unavailable", "none", 5, 0, null, null, "null", null),
        "unavailable counts");

    var serviceCard = new QuotaCard(
        "codex.sub2api",
        ProviderKind.Codex,
        "API",
        "API key",
        "#10a37f",
        true,
        [new QuotaWindow("API", null, null, TimeSpan.Zero)])
    {
        IsService = true,
        ServiceDisplayName = "sub2api",
        Sub2ApiQuota = new Sub2ApiQuotaSummary(
            Sub2ApiQuotaStatus.Available,
            5,
            null,
            null,
            null,
            5,
            60,
            3,
            now),
        Sub2ApiUsage = new Sub2ApiUsageSummary(
            Sub2ApiUsageStatus.Available,
            2,
            100,
            200,
            0,
            20,
            320,
            4,
            400,
            800,
            0,
            80,
            1280,
            now),
        Sub2ApiPool = new Sub2ApiPoolAvailability(
            Sub2ApiPoolStatus.Available,
            4,
            12,
            0,
            0,
            4,
            4,
            now),
    };
    var completeCard = serviceCard with { Sub2ApiAccountAvailability = completeSummary };
    Equal(
        Sub2ApiServicePresentationKind.CompleteAvailability,
        Sub2ApiServicePresentation.Resolve(completeCard, now).Kind,
        "resolver prefers complete availability");
    Equal(
        Sub2ApiServicePresentationKind.PartialAvailability,
        Sub2ApiServicePresentation.Resolve(
            serviceCard with { Sub2ApiAccountAvailability = partialSummary },
            now).Kind,
        "resolver prefers partial coverage");
    Equal(
        Sub2ApiServicePresentationKind.KnownNoneAvailability,
        Sub2ApiServicePresentation.Resolve(
            serviceCard with { Sub2ApiAccountAvailability = knownNone.Availability },
            now).Kind,
        "resolver prefers known-none coverage");
    Equal(
        Sub2ApiServicePresentationKind.LegacyAggregateQuota,
        Sub2ApiServicePresentation.Resolve(
            serviceCard with { Sub2ApiAccountAvailability = globalUnavailable.Availability },
            now).Kind,
        "resolver falls back to valid legacy aggregate quota");
    Equal(
        Sub2ApiServicePresentationKind.Unavailable,
        Sub2ApiServicePresentation.Resolve(serviceCard with
        {
            Sub2ApiAccountAvailability = globalUnavailable.Availability,
            Sub2ApiQuota = serviceCard.Sub2ApiQuota! with { SevenDayAccountCount = 4 },
            Sub2ApiUsage = null,
            Sub2ApiPool = null,
        }, now).Kind,
        "resolver rejects a legacy aggregate with incomplete account coverage");
    Equal(
        Sub2ApiServicePresentationKind.Usage,
        Sub2ApiServicePresentation.Resolve(serviceCard with
        {
            Sub2ApiAccountAvailability = globalUnavailable.Availability,
            Sub2ApiQuota = null,
        }, now).Kind,
        "resolver prefers explicit usage after quota sources");
    Equal(
        Sub2ApiServicePresentationKind.Pool,
        Sub2ApiServicePresentation.Resolve(serviceCard with
        {
            Sub2ApiAccountAvailability = globalUnavailable.Availability,
            Sub2ApiQuota = null,
            Sub2ApiUsage = null,
        }, now).Kind,
        "resolver prefers explicit pool after usage");
    Equal(
        Sub2ApiServicePresentationKind.Unavailable,
        Sub2ApiServicePresentation.Resolve(serviceCard with
        {
            Sub2ApiAccountAvailability = globalUnavailable.Availability,
            Sub2ApiQuota = null,
            Sub2ApiUsage = null,
            Sub2ApiPool = null,
        }, now).Kind,
        "resolver reports unavailable when no data remains");
    Equal(
        Sub2ApiServicePresentationKind.Unavailable,
        Sub2ApiServicePresentation.Resolve(
            serviceCard with { ServiceDisplayName = "other-api" },
            now).Kind,
        "resolver does not classify generic API services as Sub2API");
    Equal(
        "60%",
        NativeText.For("en").Sub2ApiLegacyQuotaCompact(Sub2ApiQuotaFormatting.PreferredWindow(serviceCard.Sub2ApiQuota!)!),
        "complete legacy aggregate keeps the preferred percent");
    Equal(
        "320",
        NativeText.For("en").Sub2ApiUsageCompact(serviceCard.Sub2ApiUsage!),
        "explicit usage keeps a truthful non-quota Mini value");
    Equal(
        "4/12",
        Sub2ApiPoolFormatting.AccountPair(serviceCard.Sub2ApiPool!),
        "explicit pool keeps a truthful available-account Mini value");
    Equal(
        "此服务不提供 Codex 订阅配额。",
        NativeText.For("zh-CN").ApiServiceNoQuota,
        "generic API service keeps the existing no-quota text");

    var completeSnapshot = new QuotaSnapshot([completeCard], [], now);
    var cacheDirectory = Path.Combine(Path.GetTempPath(), $"wmt-sub2api-cache-{Guid.NewGuid():N}");
    try
    {
        var store = new AppSettingsStore(cacheDirectory, Path.Combine(cacheDirectory, "legacy.json"));
        store.SaveCache(completeSnapshot with
        {
            Cards = [completeCard with { CapturedAt = now }],
        });
        var loaded = store.LoadCache(now.AddMinutes(16));
        Equal(
            Sub2ApiQuotaStatus.Unavailable,
            loaded?.Cards.Single().Sub2ApiAccountAvailability?.Status,
            "expired persisted account availability becomes unavailable");
        Equal<int?>(
            null,
            loaded?.Cards.Single().Sub2ApiAccountAvailability?.EligibleAccountCount,
            "expired persisted account availability hides old counts");
    }
    finally
    {
        if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, true);
    }

    var staleAttached = QuotaCoordinator.AttachSub2ApiAccountAvailability(
        completeSnapshot,
        completeSnapshot,
        new Sub2ApiAccountAvailabilityFetchResult(null, ProviderHealthCode.Timeout));
    Equal(Sub2ApiQuotaStatus.Stale, staleAttached.Cards.Single().Sub2ApiAccountAvailability?.Status, "temporary failure keeps a stale complete cache");
    Equal(Sub2ApiAccountAvailabilityCoverage.Complete, staleAttached.Cards.Single().Sub2ApiAccountAvailability?.Coverage, "stale cache keeps complete coverage");

    var partialCard = completeCard with { Sub2ApiAccountAvailability = partialSummary };
    var partialSnapshot = new QuotaSnapshot([partialCard], [], now);
    var stalePartial = QuotaCoordinator.AttachSub2ApiAccountAvailability(
        partialSnapshot,
        partialSnapshot,
        new Sub2ApiAccountAvailabilityFetchResult(null, ProviderHealthCode.Timeout));
    Equal(Sub2ApiQuotaStatus.Stale, stalePartial.Cards.Single().Sub2ApiAccountAvailability?.Status, "temporary failure keeps a stale partial cache");
    Equal(Sub2ApiAccountAvailabilityCoverage.Partial, stalePartial.Cards.Single().Sub2ApiAccountAvailability?.Coverage, "stale cache keeps partial coverage");

    var knownNoneCard = completeCard with { Sub2ApiAccountAvailability = knownNone.Availability };
    var knownNoneSnapshot = new QuotaSnapshot([knownNoneCard], [], now);
    var noCache = QuotaCoordinator.AttachSub2ApiAccountAvailability(
        knownNoneSnapshot,
        knownNoneSnapshot,
        new Sub2ApiAccountAvailabilityFetchResult(null, ProviderHealthCode.Timeout));
    Equal<Sub2ApiAccountAvailabilitySummary?>(null, noCache.Cards.Single().Sub2ApiAccountAvailability, "known-none is not fabricated after observer transport failure");

    var unavailableAttached = QuotaCoordinator.AttachSub2ApiAccountAvailability(
        completeSnapshot,
        completeSnapshot,
        globalUnavailable);
    Equal(Sub2ApiQuotaStatus.Unavailable, unavailableAttached.Cards.Single().Sub2ApiAccountAvailability?.Status, "explicit global unavailable replaces cached complete state");
    Equal<int?>(null, unavailableAttached.Cards.Single().Sub2ApiAccountAvailability?.EligibleAccountCount, "explicit global unavailable keeps counts null");
}

static void TestAiGatewayUsage()
{
    var now = DateTimeOffset.Parse("2026-08-14T08:00:00Z", CultureInfo.InvariantCulture);
    const string token = "observer-test-token";
    var handler = new RecordingHandler(request =>
    {
        Equal("GET", request.Method.Method, "usage uses GET");
        Equal("/internal/v1/usage", request.RequestUri?.AbsolutePath, "usage endpoint is private");
        Equal("Bearer", request.Headers.Authorization?.Scheme, "usage uses bearer auth");
        Equal(token, request.Headers.Authorization?.Parameter, "usage token is sent only as auth");
        return JsonResponse($$"""
            {
              "schema_version": 1,
              "source": "zgs-ai-gateway",
              "provider": "deepseek",
              "currency": "CNY",
              "status": "available",
              "day_boundary": "UTC",
              "observed_at": "{{now:O}}",
              "today": {
                "request_count": 3,
                "prompt_tokens": 100,
                "completion_tokens": 20,
                "total_tokens": 120,
                "cache_hit_tokens": 40,
                "cache_miss_tokens": 60,
                "cache_unknown_tokens": 0,
                "cache_hit_rate_percent": "40",
                "estimated_cost_cny": "0.0001008"
              },
              "total": {
                "request_count": 18,
                "prompt_tokens": 1000,
                "completion_tokens": 200,
                "total_tokens": 1200,
                "cache_hit_tokens": 400,
                "cache_miss_tokens": 600,
                "cache_unknown_tokens": 0,
                "cache_hit_rate_percent": "40",
                "estimated_cost_cny": "0.001008"
              }
            }
            """);
    });
    using (var http = new HttpClient(handler))
    {
        var service = new AiGatewayUsageService(
            http,
            new MemoryAiGatewayConnectionStore(
                new AiGatewayConnection("http://127.0.0.1:9192", token)));
        var result = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.Current, result.Code, "usage is current");
        Equal("CNY", result.Summary?.Currency, "usage currency is CNY");
        Equal(3L, result.Summary?.Today.RequestCount, "today request count is parsed");
        Equal(120L, result.Summary?.Today.TotalTokens, "today total tokens are parsed");
        Equal(40L, result.Summary?.Today.CacheHitTokens, "today cache hit tokens are parsed");
        Equal(40m, result.Summary?.Today.CacheHitRatePercent, "today cache rate is parsed");
        Equal(18L, result.Summary?.Total.RequestCount, "total request count is parsed");
        Equal(0.001008m, result.Summary?.Total.EstimatedCostCny, "total cost is parsed exactly");
    }

    var staleHandler = new RecordingHandler(_ => JsonResponse($$"""
        {
          "schema_version": 1,
          "source": "zgs-ai-gateway",
          "provider": "deepseek",
          "currency": "CNY",
          "status": "available",
          "day_boundary": "UTC",
          "observed_at": "{{now.AddMinutes(-16):O}}",
          "today": {
            "request_count": 0,
            "prompt_tokens": 0,
            "completion_tokens": 0,
            "total_tokens": 0,
            "cache_hit_tokens": 0,
            "cache_miss_tokens": 0,
            "cache_unknown_tokens": 0,
            "cache_hit_rate_percent": null,
            "estimated_cost_cny": "0"
          },
          "total": {
            "request_count": 0,
            "prompt_tokens": 0,
            "completion_tokens": 0,
            "total_tokens": 0,
            "cache_hit_tokens": 0,
            "cache_miss_tokens": 0,
            "cache_unknown_tokens": 0,
            "cache_hit_rate_percent": null,
            "estimated_cost_cny": "0"
          }
        }
        """));
    using (var http = new HttpClient(staleHandler))
    {
        var service = new AiGatewayUsageService(
            http,
            new MemoryAiGatewayConnectionStore(
                new AiGatewayConnection("http://127.0.0.1:9192", token)));
        var result = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(AiGatewayBalanceStatus.Stale, result.Summary?.Status, "old usage is stale");
    }

    var overflowHandler = new RecordingHandler(_ => JsonResponse($$"""
        {
          "schema_version": 1,
          "source": "zgs-ai-gateway",
          "provider": "deepseek",
          "currency": "CNY",
          "status": "available",
          "day_boundary": "UTC",
          "observed_at": "{{now:O}}",
          "today": {
            "request_count": 1,
            "prompt_tokens": 0,
            "completion_tokens": 0,
            "total_tokens": 0,
            "cache_hit_tokens": 9223372036854775807,
            "cache_miss_tokens": 9223372036854775807,
            "cache_unknown_tokens": 2,
            "cache_hit_rate_percent": "0",
            "estimated_cost_cny": "0"
          },
          "total": {
            "request_count": 1,
            "prompt_tokens": 0,
            "completion_tokens": 0,
            "total_tokens": 0,
            "cache_hit_tokens": 0,
            "cache_miss_tokens": 0,
            "cache_unknown_tokens": 0,
            "cache_hit_rate_percent": null,
            "estimated_cost_cny": "0"
          }
        }
        """));
    using (var http = new HttpClient(overflowHandler))
    {
        var service = new AiGatewayUsageService(
            http,
            new MemoryAiGatewayConnectionStore(
                new AiGatewayConnection("http://127.0.0.1:9192", token)));
        var result = service.FetchAsync(now).GetAwaiter().GetResult();
        Equal(ProviderHealthCode.HttpError, result.Code, "overflowed usage counters fail closed");
        Equal<AiGatewayUsageSummary?>(null, result.Summary, "overflowed usage counters are discarded");
    }
}

static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json"),
};

static void TestClaudeRetryAfterBackoff()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-claude-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var previousConfigDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    try
    {
        var credentialPath = Path.Combine(directory, ".credentials.json");
        WriteCredential(credentialPath, "token-a");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", directory);

        using var handler = new ClaudeRateLimitHandler();
        using var client = new HttpClient(handler);
        var service = new ClaudeQuotaService(client);
        var first = service.FetchAsync(new AppSettings(), CancellationToken.None).GetAwaiter().GetResult();
        Equal(false, first.Health.Connected, "rate-limited Claude is disconnected");
        Equal(true, first.Health.Detail.Contains("Retry in 30m", StringComparison.Ordinal), "Retry-After is visible");

        var second = service.FetchAsync(new AppSettings(), CancellationToken.None).GetAwaiter().GetResult();
        Equal(false, second.Health.Connected, "active backoff remains disconnected");
        Equal(1, handler.Calls, "active Retry-After skips another HTTP request");

        WriteCredential(credentialPath, "token-b");
        var recovered = service.FetchAsync(new AppSettings(), CancellationToken.None).GetAwaiter().GetResult();
        Equal(true, recovered.Health.Connected, "updated credential bypasses stale backoff");
        Equal(2, handler.Calls, "updated credential retries HTTP request");
        Equal(true, handler.SawNoCache, "Claude usage requests bypass HTTP caches");
    }
    finally
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousConfigDirectory);
        Directory.Delete(directory, true);
    }
}

static void TestClaudeExplicitOAuthRefresh()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-claude-refresh-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var previousConfigDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    try
    {
        var credentialPath = Path.Combine(directory, ".credentials.json");
        WriteCredential(credentialPath, "token-a", DateTimeOffset.UtcNow.AddHours(-1));
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", directory);

        using var handler = new ClaudeOAuthRefreshHandler();
        using var client = new HttpClient(handler);
        var service = new ClaudeQuotaService(client);
        var result = service.FetchAsync(
            new AppSettings { AutoRefreshClaudeOAuth = false },
            CancellationToken.None,
            allowOAuthRefresh: true).GetAwaiter().GetResult();

        Equal(true, result.Health.Connected, "explicit refresh recovers expired Claude OAuth");
        Equal(1, handler.RefreshCalls, "explicit refresh rotates OAuth once");
        Equal(1, handler.UsageCalls, "explicit refresh requests usage once");
        Equal(true, handler.SawRefreshedToken, "usage request uses refreshed OAuth token");
        Equal(
            38d,
            result.Cards.Single().Windows.Single(window => window.Label == "Fable").UsedPercent,
            "live Claude card exposes Fable quota");
        using var saved = JsonDocument.Parse(File.ReadAllText(credentialPath));
        var savedOAuth = saved.RootElement.ObjectProperty("claudeAiOauth")!.Value;
        Equal("token-b", savedOAuth.StringProperty("accessToken"), "refreshed access token is persisted");
        Equal("refresh-b", savedOAuth.StringProperty("refreshToken"), "rotating refresh token is persisted");
    }
    finally
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousConfigDirectory);
        Directory.Delete(directory, true);
    }
}

static void WriteCredential(
    string path,
    string accessToken,
    DateTimeOffset? expiresAt = null)
{
    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        claudeAiOauth = new
        {
            accessToken,
            refreshToken = "refresh",
            subscriptionType = "max",
            rateLimitTier = "default_claude_max_20x",
            expiresAt = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeMilliseconds(),
        },
    }));
}

static void TestCodexParser()
{
    const string json = """
        {
          "rate_limit_status": {
            "plan_type": "plus",
            "rate_limit": {
              "primary_window": { "used_percent": 34, "window_minutes": 300, "reset_at": 1784116800 },
              "secondary_window": { "remaining_percent": 72, "limit_window_seconds": 604800, "reset_at": 1784505600000 }
            }
          }
        }
        """;
    var usage = CodexUsageParser.Parse(json);
    Equal(34, usage.Windows.Single(window => window.Label == "5h").UsedPercent, "primary used percent");
    Equal(28, usage.Windows.Single(window => window.Label == "7d").UsedPercent, "secondary remaining conversion");
    Equal("plus", usage.Plan, "plan");

    var extreme = CodexUsageParser.Parse("""
        {
          "rate_limit": {
            "primary": {
              "used_percent": 12,
              "limit_window_seconds": 1e999,
              "reset_after_seconds": 1e999
            }
          }
        }
        """).Windows.Single();
    Equal(TimeSpan.Zero, extreme.Duration, "non-finite duration is ignored");
    Equal<DateTimeOffset?>(null, extreme.ResetsAt, "non-finite reset offset is ignored");
}

static void TestCodexUtilization()
{
    const string json = """
        {
          "rate_limit": {
            "primary": { "utilization": 0.25, "window_minutes": 300 },
            "secondary": { "used_percentage": 6.5, "window_minutes": 10080 }
          }
        }
        """;
    var usage = CodexUsageParser.Parse(json);
    Equal(25, usage.Windows.Single(window => window.Label == "5h").UsedPercent, "fractional utilization");
    Equal(6.5, usage.Windows.Single(window => window.Label == "7d").UsedPercent, "used percentage");
}

static void TestCodexDynamicWindows()
{
    const string json = """
        {
          "rate_limit": {
            "primary": { "used_percent": 12, "limit_window_seconds": 18000 },
            "secondary": { "used_percent": 23, "limit_window_seconds": 2592000 }
          }
        }
        """;
    var usage = CodexUsageParser.Parse(json);
    Equal(2, usage.Windows.Count, "window count");
    Equal("5h", usage.Windows[0].Label, "five-hour label");
    Equal("30d", usage.Windows[1].Label, "thirty-day label");
    Equal(false, usage.Windows.Any(window => window.Label == "7d"), "thirty-day is not mislabeled weekly");
}

static void TestCodexAccountLabels()
{
    Equal("Codex", CodexDisplayFormatting.AccountLabel(1, 0), "single account label");
    Equal("Codex · 1", CodexDisplayFormatting.AccountLabel(2, 0), "first multi-account label");
    Equal("Codex · 2", CodexDisplayFormatting.AccountLabel(2, 1), "second multi-account label");
}

static void TestProviderProcessActivity()
{
    var active = ProviderProcessActivity.DetectFromProcessNames(["ChatGPT", "claude-code"]);
    Equal(true, active.Contains(ProviderKind.Codex), "ChatGPT process activates Codex");
    Equal(true, active.Contains(ProviderKind.Claude), "Claude CLI process activates Claude");

    var inactive = ProviderProcessActivity.DetectFromProcessNames(["ZGSTokenBar"]);
    Equal(false, inactive.Contains(ProviderKind.Codex), "unrelated process does not activate Codex");
    Equal(false, inactive.Contains(ProviderKind.Claude), "unrelated process does not activate Claude");
}

static void TestCockpitCodexInstanceActivity()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-cockpit-instances-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var managedData = Path.Combine(directory, "instances", "codex", "managed");
        Directory.CreateDirectory(managedData);
        File.WriteAllText(
            Path.Combine(directory, "codex_instances.json"),
            JsonSerializer.Serialize(new
            {
                instances = new object[]
                {
                    new { bindAccountId = "managed-plus", lastPid = 101, userDataDir = managedData },
                    new { bindAccountId = "stopped-account", lastPid = 303, userDataDir = (string?)null },
                },
                defaultSettings = new { bindAccountId = "default-plus", lastPid = 202 },
            }));

        var active = CockpitCodexInstanceActivity.ReadActiveAccountIds(
            directory,
            processId => processId is 101 or 202);
        Equal(2, active?.Count, "running managed and default instances are active");
        Equal(true, active?.Contains("managed-plus"), "managed instance account is active");
        Equal(true, active?.Contains("default-plus"), "default instance account is active");
        Equal(false, active?.Contains("stopped-account"), "stopped instance account is inactive");

        var recoveredDefault = CockpitCodexInstanceActivity.ReadActiveAccountIds(
            directory,
            processId => processId == 101,
            () =>
            [
                new CockpitCodexInstanceActivity.ProcessEntry(101, 900, "ChatGPT"),
                new CockpitCodexInstanceActivity.ProcessEntry(102, 101, "ChatGPT"),
                new CockpitCodexInstanceActivity.ProcessEntry(404, 800, "ChatGPT"),
            ]);
        Equal(2, recoveredDefault?.Count, "unmatched Codex root recovers the default account");
        Equal(true, recoveredDefault?.Contains("managed-plus"), "managed binding remains active");
        Equal(true, recoveredDefault?.Contains("default-plus"), "stale default PID follows the unmatched root");

        var rolloutSources = CockpitCodexInstanceActivity.ReadRolloutSources(directory);
        Equal(1, rolloutSources.Count, "managed Cockpit rollout directory is discovered");
        Equal(
            CodexQuotaService.StableCardKey("cockpit:managed-plus"),
            rolloutSources[0].CardKey,
            "instance account maps to the same stable quota card key");
        Equal("managed-plus", rolloutSources[0].AccountId, "rollout source keeps its Cockpit account binding");
        Equal(Path.GetFullPath(managedData), rolloutSources[0].CodexHome, "rollout source keeps the account-scoped directory");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestCockpitCodexAccountDirectory()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-cockpit-directory-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllText(
            Path.Combine(directory, "codex_accounts.json"),
            """
            {
              "current_account_id": "account-current",
              "accounts": [
                {"id":"account-old","email":"old@example.test","plan_type":"plus","last_used":"2026-07-03T00:00:00Z"},
                {"id":"account-current","email":"current@example.test","plan_type":"ChatGPT Pro","last_used":"2026-07-02T00:00:00Z"},
                {"id":"account-free","email":"free@example.test","plan_type":"free","last_used":"2026-07-01T00:00:00Z"},
                {"id":"account-key","email":"key@example.test","plan_type":"api_key","last_used":"2026-07-04T00:00:00Z"},
                {"id":"account-key-second","email":"key-second@example.test","plan_type":"API_KEY","last_used":"2026-07-03T00:00:00Z"}
              ]
            }
            """);

        var accounts = CockpitCodexAccountDirectory.Read(directory);
        Equal(3, accounts.Count, "inactive API key accounts are hidden from the directory");
        Equal("account-current", accounts[0].AccountId, "current account is listed first");
        Equal(true, accounts[0].Active, "current account is marked active");
        Equal("pro", CodexAccountFormatting.PlanLabel(accounts[0].Plan), "ChatGPT Pro plan label is normalized");
        Equal("plus", accounts[1].Plan, "non-Pro account remains visible");
        Equal("f***e@example.test", CodexAccountFormatting.MaskEmail(accounts[2].Email), "email is masked");
        Equal("plus", CodexAccountFormatting.PlanLabel("CHATGPT-PLUS"), "ChatGPT Plus separators are normalized");
        Equal("free", CodexAccountFormatting.PlanLabel("chatgpt_free"), "ChatGPT Free separators are normalized");
        Equal("future-plan", CodexAccountFormatting.PlanLabel("future-plan"), "unknown plans remain explicit");
        File.WriteAllText(
            Path.Combine(directory, "codex_accounts.json"),
            File.ReadAllText(Path.Combine(directory, "codex_accounts.json"))
                .Replace("account-current", "account-key", StringComparison.Ordinal));
        var activeApiAccounts = CockpitCodexAccountDirectory.Read(directory);
        var activeApi = activeApiAccounts.Single(account => account.AccountId == "cockpit:api-services");
        Equal(1, activeApi.AccountCount, "only the active API service is counted");
        Equal(true, activeApi.Active, "active API service directory row is marked active");
        Equal("API key", CodexAccountFormatting.PlanLabel("API_KEY"), "API key plan label");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestCodexActiveAuthRegistryReconciliation()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-codex-active-{Guid.NewGuid():N}");
    var accountsDirectory = Path.Combine(directory, "accounts");
    Directory.CreateDirectory(accountsDirectory);
    var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    var previousRefresh = Environment.GetEnvironmentVariable("ZTB_DISABLE_REFRESH");
    try
    {
        const string currentEmail = "current@example.test";
        var accessToken = TestJwt(new Dictionary<string, object?>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = currentEmail,
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "acct-current",
            },
        });
        File.WriteAllText(
            Path.Combine(directory, "auth.json"),
            JsonSerializer.Serialize(new { tokens = new { access_token = accessToken } }));

        const string matchingEmailKey = "current::old-workspace";
        const string staleActiveKey = "stale::active";
        File.WriteAllText(
            Path.Combine(accountsDirectory, "registry.json"),
            JsonSerializer.Serialize(new
            {
                active_account_key = staleActiveKey,
                accounts = new[]
                {
                    new
                    {
                        account_key = matchingEmailKey,
                        email = currentEmail,
                        plan = "plus",
                        chatgpt_account_id = "acct-old-workspace",
                    },
                    new
                    {
                        account_key = staleActiveKey,
                        email = "stale@example.test",
                        plan = "plus",
                        chatgpt_account_id = "acct-stale",
                    },
                },
            }));
        File.WriteAllText(
            Path.Combine(accountsDirectory, $"{Base64Url(staleActiveKey)}.auth.json"),
            """{"tokens":{"access_token":"stale-token","account_id":"acct-stale"}}""");

        Environment.SetEnvironmentVariable("CODEX_HOME", directory);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", "1");
        using var handler = new CodexUsageHandler(accessToken, 12);
        using var client = new HttpClient(handler);
        var service = new CodexQuotaService(client, Path.Combine(directory, "no-cockpit"));
        var result = service.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();

        Equal(true, result.Health.Connected, "current root auth remains connected");
        Equal(ProviderHealthCode.Current, result.Health.Code, "current root auth health");
        Equal(1, result.Cards.Count, "stale registry account stays hidden");
        Equal(true, result.Cards[0].Active, "root auth overrides stale registry active key");
        Equal("plus", result.Cards[0].Badge, "registry plan is retained for the active account");
        Equal(12d, result.Cards[0].Windows.Single().UsedPercent, "root auth supplies live quota");
        Equal(1, handler.ExpectedCalls, "matching-email registry entry uses root auth");
    }
    finally
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", previousRefresh);
        Directory.Delete(directory, true);
    }
}

static void TestCodexProfileLifetimeCounter()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-codex-profile-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    var previousRefresh = Environment.GetEnvironmentVariable("ZTB_DISABLE_REFRESH");
    try
    {
        const string accountId = "acct-profile";
        const long lifetimeTokens = 123_456;
        var accessToken = TestJwt(new Dictionary<string, object?>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = accountId,
            },
        });
        File.WriteAllText(
            Path.Combine(directory, "auth.json"),
            JsonSerializer.Serialize(new { tokens = new { access_token = accessToken } }));
        Environment.SetEnvironmentVariable("CODEX_HOME", directory);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", "1");

        using var handler = new CodexProfileUsageHandler(lifetimeTokens);
        using var client = new HttpClient(handler);
        var service = new CodexQuotaService(client, Path.Combine(directory, "no-cockpit"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dailyBuckets = new List<(DateOnly Date, long Tokens)>();
        for (var index = 0; index < 28; index++)
        {
            if (index != 5)
            {
                dailyBuckets.Add((
                    today.AddDays(-27 + index),
                    100_000_000L + index + 1));
            }
        }
        dailyBuckets.Add((today.AddDays(-35), 999_999_999L));
        dailyBuckets.Add((today.AddDays(1), 888_888_888L));
        dailyBuckets.Add((today.AddDays(2), 777_777_777L));
        handler.ProfileBody = ProfileBodyWithDailyUsage(lifetimeTokens, dailyBuckets);
        var result = service.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();

        Equal(true, result.Health.Connected, "profile counter keeps quota connected");
        Equal(1, result.CodexQuotaTokenCounters.Count, "successful profile emits one counter");
        var counter = result.CodexQuotaTokenCounters.Single();
        Equal(result.Cards.Single().Key, counter.CardKey, "counter uses the visible stable card key");
        Equal(lifetimeTokens, counter.LifetimeTokens, "profile lifetime token parses as Int64");
        Equal(
            675_000_100L,
            counter.RecentWeeklyAverageTokens,
            "sparse stats daily buckets use UTC today, missing days as zero, and ignore older/future days");
        var serializedCounter = JsonSerializer.Serialize(counter);
        Equal(
            false,
            serializedCounter.Contains(nameof(CodexQuotaTokenCounter.RecentWeeklyAverageTokens), StringComparison.Ordinal),
            "recent weekly average is not serialized into the counter JSON");
        Equal(
            "/backend-api/wham/usage,/backend-api/wham/profiles/me",
            string.Join(',', handler.Requests.Select(request => request.Path)),
            "profile request uses the same backend base URL");
        Equal(true, handler.Requests.All(request => request.Method == HttpMethod.Get), "quota and profile are GET requests");
        Equal(true, handler.Requests.All(request => request.Authorization == accessToken), "quota and profile reuse OAuth");
        Equal(true, handler.Requests.All(request => request.AccountId == accountId), "quota and profile reuse account header");
        Equal(true, handler.Requests.All(request => request.NoCache), "quota and profile bypass HTTP caches");
        Equal(true, handler.Requests.All(request => request.UserAgent == "codex-cli"), "quota and profile reuse user agent");

        var merged = QuotaCoordinator.MergeResults([result], null, DateTimeOffset.UtcNow);
        Equal(counter, merged.CodexQuotaTokenCounters.Single(), "coordinator propagates profile counter");
        var observations = QuotaApplicationContext.CodexQuotaTokenObservations(
            merged,
            DateTimeOffset.UtcNow);
        Equal(1, observations.Count, "fresh profile counter pairs with the live quota window");
        Equal(CodexQuotaTokenTracker.ProfileLifetimeSourceKey, observations[0].SourceKey, "profile observation uses the anonymous source key");
        Equal(lifetimeTokens, observations[0].TotalTokens, "profile observation retains only the cumulative token counter");

        void AssertRecentDailyUsageRejected(string profileBody, string reason)
        {
            handler.ProfileBody = profileBody;
            var rejected = service.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
            Equal(true, rejected.Health.Connected, $"{reason} does not fail quota");
            Equal(1, rejected.CodexQuotaTokenCounters.Count, $"{reason} preserves the lifetime counter");
            Equal(lifetimeTokens, rejected.CodexQuotaTokenCounters.Single().LifetimeTokens, $"{reason} preserves lifetime tokens");
            Equal(null, rejected.CodexQuotaTokenCounters.Single().RecentWeeklyAverageTokens, $"{reason} hides only recent usage");
        }

        string ProfileBodyWithRawBuckets(params object[] buckets) =>
            JsonSerializer.Serialize(new
            {
                stats = new
                {
                    lifetime_tokens = lifetimeTokens,
                    daily_usage_buckets = buckets,
                },
            });

        AssertRecentDailyUsageRejected(
            ProfileBodyWithDailyUsage(lifetimeTokens, [(today, 1), (today, 2)]),
            "duplicate daily date");
        AssertRecentDailyUsageRejected(
            ProfileBodyWithRawBuckets(new { start_date = "2026-02-30", tokens = 1 }),
            "invalid daily date");
        AssertRecentDailyUsageRejected(
            ProfileBodyWithRawBuckets(new { start_date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), tokens = -1 }),
            "negative daily token");
        AssertRecentDailyUsageRejected(
            ProfileBodyWithRawBuckets(new { start_date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), tokens = 1.5 }),
            "non-integer daily token");
        AssertRecentDailyUsageRejected(
            ProfileBodyWithDailyUsage(
                lifetimeTokens,
                [(today, long.MaxValue), (today.AddDays(-1), long.MaxValue)]),
            "daily token sum overflow");

        handler.ProfileStatus = HttpStatusCode.BadGateway;
        var profileFailure = service.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal(true, profileFailure.Health.Connected, "profile HTTP failure does not fail quota");
        Equal(0, profileFailure.CodexQuotaTokenCounters.Count, "profile HTTP failure omits counter");
        var mergedFailure = QuotaCoordinator.MergeResults(
            [profileFailure],
            merged,
            DateTimeOffset.UtcNow);
        Equal(0, mergedFailure.CodexQuotaTokenCounters.Count, "profile failure never reuses a previous counter");
        Equal(
            0,
            QuotaApplicationContext.CodexQuotaTokenObservations(mergedFailure, DateTimeOffset.UtcNow).Count,
            "profile failure produces no capacity observation");

        handler.ProfileStatus = HttpStatusCode.OK;
        handler.ProfileBody = "{\"stats\":{\"lifetime_tokens\":-1}}";
        var invalidProfile = service.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal(true, invalidProfile.Health.Connected, "invalid profile schema does not fail quota");
        Equal(0, invalidProfile.CodexQuotaTokenCounters.Count, "negative lifetime token is rejected");
    }
    finally
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", previousRefresh);
        Directory.Delete(directory, true);
    }
}

static void TestCockpitProCodexAccountMerge()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-cockpit-codex-{Guid.NewGuid():N}");
    var codexHome = Path.Combine(directory, "codex-home");
    var accountDirectory = Path.Combine(directory, "codex_accounts");
    Directory.CreateDirectory(codexHome);
    Directory.CreateDirectory(accountDirectory);
    var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    var previousRefresh = Environment.GetEnvironmentVariable("ZTB_DISABLE_REFRESH");
    var key = RandomNumberGenerator.GetBytes(32);
    try
    {
        const string currentId = "current-pro";
        const string secondProId = "second-pro";
        const string validPlusId = "valid-plus";
        const string inactiveProId = "inactive-pro";
        const string secondProEmail = "plus@example.test";
        var now = DateTimeOffset.UtcNow;
        var activeUntil = now.AddDays(30);
        var expiredAt = now.AddHours(-1);
        var currentAccessToken = TestJwt(new Dictionary<string, object?>
        {
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = "current@example.test",
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "acct-current-pro",
            },
        });
        var staleNativeCurrentAccessToken = TestJwt(new Dictionary<string, object?>
        {
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = "current@example.test",
            ["jti"] = "native-current-stale-plan",
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "acct-current-pro",
            },
        });
        var accessToken = TestJwt(new Dictionary<string, object?>
        {
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = secondProEmail,
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "acct-second-pro",
            },
        });
        var plusAccessToken = TestJwt(new Dictionary<string, object?>
        {
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = "plus@example.test",
            ["jti"] = "cockpit-plus",
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "acct-valid-plus",
            },
        });
        var nativePlusAccessToken = TestJwt(new Dictionary<string, object?>
        {
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = "plus@example.test",
            ["jti"] = "native-plus",
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "acct-valid-plus",
            },
        });
        var staleSnapshotAt = now.AddHours(-2);
        File.WriteAllText(
            Path.Combine(directory, "secure-account-storage.key"),
            Convert.ToBase64String(key));
        File.WriteAllText(
            Path.Combine(directory, "codex_accounts.json"),
            JsonSerializer.Serialize(new
            {
                current_account_id = currentId,
                accounts = new[]
                {
                    new { id = currentId, plan_type = "ChatGPT-Pro", subscription_active_until = activeUntil },
                    new { id = secondProId, plan_type = "pro", subscription_active_until = activeUntil },
                    new { id = validPlusId, plan_type = "PLUS", subscription_active_until = activeUntil },
                    new { id = "free-account", plan_type = "free", subscription_active_until = activeUntil },
                    new { id = "api-account", plan_type = "api_key", subscription_active_until = activeUntil },
                    new { id = "unknown-account", plan_type = "unknown", subscription_active_until = activeUntil },
                    new { id = inactiveProId, plan_type = "pro", subscription_active_until = expiredAt },
                },
            }));
        WriteCockpitCodexAccount(
            Path.Combine(accountDirectory, "current.json"),
            key,
            currentId,
            "current@example.test",
            "chatgpt_pro",
            0,
            staleSnapshotAt,
            currentAccessToken);
        WriteCockpitCodexAccount(
            Path.Combine(accountDirectory, "second.json"),
            key,
            secondProId,
            secondProEmail,
            "pro",
            94,
            staleSnapshotAt,
            accessToken);
        WriteCockpitCodexAccount(
            Path.Combine(accountDirectory, "plus.json"),
            key,
            validPlusId,
            "plus@example.test",
            "PLUS",
            50,
            staleSnapshotAt,
            plusAccessToken,
            subscriptionActiveUntil: activeUntil);
        WriteCockpitApiService(
            Path.Combine(accountDirectory, "api.json"),
            key,
            "api-account",
            "Test API provider");

        var selected = CockpitCodexQuotaReader.Read(directory, now);
        Equal(4, selected.Count, "valid Cockpit subscription and API accounts are selected");
        Equal(1, selected.Count(account => account.Active), "Cockpit current account remains primary");
        Equal(2, selected.Count(account => account.Plan == "pro"), "both valid Pro accounts are selected");
        Equal(1, selected.Count(account => account.Plan == "plus"), "valid Plus account is selected case-insensitively");
        Equal(1, selected.Count(account => account.IsApiService), "API service account is selected without OAuth");
        Equal(
            true,
            selected.Where(account => !account.IsApiService)
                .All(account => !string.IsNullOrWhiteSpace(account.AccessToken)),
            "Cockpit OAuth access tokens remain available only in memory");
        File.WriteAllText(
            Path.Combine(codexHome, "auth.json"),
            JsonSerializer.Serialize(new { tokens = new { access_token = nativePlusAccessToken } }));
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", "1");
        using var handler = new CodexUsageMapHandler(new Dictionary<string, double>
        {
            [accessToken] = 6,
            [currentAccessToken] = 11,
            [plusAccessToken] = 99,
            [nativePlusAccessToken] = 17,
            [staleNativeCurrentAccessToken] = 23,
        }, new Dictionary<string, string>
        {
            [accessToken] = "pro",
            [currentAccessToken] = "ChatGPT Pro",
            [plusAccessToken] = "plus",
            [nativePlusAccessToken] = "plus",
            [staleNativeCurrentAccessToken] = "plus",
        });
        using var client = new HttpClient(handler);
        var service = new CodexQuotaService(client, directory);
        var result = service.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();

        Equal(true, result.Health.Connected, "Cockpit accounts merge with live Codex");
        Equal(ProviderHealthCode.Current, result.Health.Code, "live duplicate keeps current health");
        Equal(2, result.Cards.Count, "inactive API service is not displayed with quota cards");
        Equal("Codex · 1", result.Cards[0].Label, "current native account uses ordinal label");
        Equal("Codex · 2", result.Cards[1].Label, "current Cockpit account uses ordinal label");
        Equal(0, result.Cards.Count(card => card.IsService), "inactive API service cards are hidden");
        Equal(1, result.Cards.Count(card => card.Badge == "pro"), "current Cockpit account retains Pro badge");
        Equal(1, result.Cards.Count(card => card.Badge == "plus"), "current native account retains Plus badge");
        Equal(3, result.CodexAccounts.Count, "all valid Cockpit accounts expose quota summaries");
        Equal(
            "6,11,17",
            string.Join(",", result.CodexAccounts
                .SelectMany(account => account.Windows)
                .Select(window => window.UsedPercent)
                .Order()),
            "all valid Cockpit account quotas refresh live");
        Equal(
            "11,17",
            string.Join(',', result.Cards
                .SelectMany(card => card.Windows)
                .Select(window => window.UsedPercent)
                .Where(value => value is not null)
                .Order()),
            "current native and Cockpit accounts refresh live");
        Equal(3, handler.ExpectedCalls, "native and all distinct Cockpit accounts are fetched once each");
        Equal(3, handler.ProfileCalls, "each distinct account receives at most one profile request");
        Equal(
            true,
            handler.RequestedTokens.ContainsKey(accessToken),
            "same email with a different account id remains a distinct account");

        var nativeAccountDirectory = Path.Combine(codexHome, "accounts");
        Directory.CreateDirectory(nativeAccountDirectory);
        const string nativeCurrentKey = "current::stale-plan";
        File.WriteAllText(
            Path.Combine(codexHome, "auth.json"),
            JsonSerializer.Serialize(new
            {
                tokens = new
                {
                    access_token = staleNativeCurrentAccessToken,
                    refresh_token = "native-refresh-token",
                },
            }));
        File.WriteAllText(
            Path.Combine(nativeAccountDirectory, "registry.json"),
            JsonSerializer.Serialize(new
            {
                active_account_key = nativeCurrentKey,
                accounts = new[]
                {
                    new
                    {
                        account_key = nativeCurrentKey,
                        email = "current@example.test",
                        plan = "plus",
                        chatgpt_account_id = "acct-current-pro",
                    },
                },
            }));

        var staleNativePlan = service.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal(1, staleNativePlan.Cards.Count, "duplicate active account still produces one card");
        Equal(
            CodexQuotaService.StableCardKey(nativeCurrentKey),
            staleNativePlan.Cards.Single().Key,
            "plan correction retains the native stable card key");
        Equal(
            23d,
            staleNativePlan.Cards.Single().Windows.Single().UsedPercent,
            "plan correction retains the native access token");
        Equal(
            "pro",
            staleNativePlan.Cards.Single().Badge,
            "active Cockpit Pro plan overrides a stale native Plus registry value");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", previousRefresh);
        Directory.Delete(directory, true);
    }
}

static void TestFailedActiveCodexAccountRemainsVisible()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-active-codex-failure-{Guid.NewGuid():N}");
    var codexHome = Path.Combine(directory, "codex-home");
    var cockpitAccountDirectory = Path.Combine(directory, "codex_accounts");
    var nativeAccountDirectory = Path.Combine(codexHome, "accounts");
    Directory.CreateDirectory(cockpitAccountDirectory);
    Directory.CreateDirectory(nativeAccountDirectory);
    var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    var previousRefresh = Environment.GetEnvironmentVariable("ZTB_DISABLE_REFRESH");
    var key = RandomNumberGenerator.GetBytes(32);
    try
    {
        const string currentId = "current-pro";
        const string currentEmail = "current@example.test";
        const string failedEmail = "failed@example.test";
        var now = DateTimeOffset.UtcNow;
        var activeUntil = now.AddDays(30);
        var currentToken = TestJwt(new Dictionary<string, object?>
        {
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = currentEmail,
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "acct-current-pro",
            },
        });
        var failedToken = TestJwt(new Dictionary<string, object?>
        {
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = failedEmail,
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "acct-failed-pro",
            },
        });

        File.WriteAllText(
            Path.Combine(directory, "secure-account-storage.key"),
            Convert.ToBase64String(key));
        File.WriteAllText(
            Path.Combine(directory, "codex_accounts.json"),
            JsonSerializer.Serialize(new
            {
                current_account_id = currentId,
                accounts = new[]
                {
                    new { id = currentId, plan_type = "ChatGPT Pro", subscription_active_until = activeUntil },
                },
            }));
        WriteCockpitCodexAccount(
            Path.Combine(cockpitAccountDirectory, "current.json"),
            key,
            currentId,
            currentEmail,
            "CHATGPT_PRO",
            82,
            now.AddMinutes(-2),
            currentToken);

        File.WriteAllText(
            Path.Combine(codexHome, "auth.json"),
            JsonSerializer.Serialize(new { tokens = new { access_token = failedToken } }));
        File.WriteAllText(
            Path.Combine(nativeAccountDirectory, "registry.json"),
            JsonSerializer.Serialize(new
            {
                active_account_key = "failed-pro",
                accounts = new[]
                {
                    new
                    {
                        account_key = "failed-pro",
                        email = failedEmail,
                        plan = "pro",
                        chatgpt_account_id = "acct-failed-pro",
                    },
                },
            }));

        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", "1");
        using var handler = new CodexUsageMapHandler(
            new Dictionary<string, double> { [currentToken] = 18 },
            new Dictionary<string, string> { [currentToken] = "chatgpt-pro" });
        using var client = new HttpClient(handler);
        var result = new CodexQuotaService(client, directory)
            .FetchAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Equal(true, result.Health.Connected, "one successful Pro keeps provider health connected");
        Equal(ProviderHealthCode.Current, result.Health.Code, "one successful Pro keeps provider health current");
        Equal(2, result.Cards.Count, "both active Pro accounts remain visible when one quota request fails");
        Equal(true, result.Cards.All(card => card.Badge == "pro"), "failed active account retains its Pro plan");
        Equal(1, result.Cards.Count(card => card.Windows.Any(window => window.UsedPercent is not null)), "successful Pro keeps its quota value");
        Equal(1, result.Cards.Count(card => card.Windows.All(window => window.UsedPercent is null)), "failed Pro uses an explicit placeholder");
        Equal(2, handler.Calls, "both active Pro accounts are requested once");

        var merged = QuotaCoordinator.MergeResults([result], null, now);
        Equal(2, merged.Cards.Count, "snapshot merge retains the failed active Pro beside the live Pro");
        var groups = TaskbarMiniGrouping.Create(merged.Cards);
        Equal(1, groups.Count, "observer configuration alone does not create a Sub2API Mini module");
        Equal(2, groups[0].Cards.Count, "the first Codex Mini module contains both Pro accounts");
        Equal(true, groups[0].Cards.All(card => !card.IsService && card.Badge == "pro"), "both Pro rows remain ordinary accounts");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", previousRefresh);
        Directory.Delete(directory, true);
    }
}

static void TestCockpitApiServiceCard()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-cockpit-api-{Guid.NewGuid():N}");
    var codexHome = Path.Combine(directory, "codex-home");
    var accountDirectory = Path.Combine(directory, "codex_accounts");
    Directory.CreateDirectory(codexHome);
    Directory.CreateDirectory(accountDirectory);
    var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    var previousRefresh = Environment.GetEnvironmentVariable("ZTB_DISABLE_REFRESH");
    var key = RandomNumberGenerator.GetBytes(32);
    try
    {
        File.WriteAllText(
            Path.Combine(directory, "secure-account-storage.key"),
            Convert.ToBase64String(key));
        File.WriteAllText(
            Path.Combine(directory, "codex_accounts.json"),
            """
            {
              "current_account_id":"api-service",
              "accounts": [
                {"id":"api-service","plan_type":"API_KEY"},
                {"id":"api-service-two","plan_type":"api_key"}
              ]
            }
            """);
        WriteCockpitApiService(
            Path.Combine(accountDirectory, "api.json"),
            key,
            "api-service",
            "Test API provider");
        WriteCockpitApiService(
            Path.Combine(accountDirectory, "api-two.json"),
            key,
            "api-service-two",
            "Another API provider");

        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", "1");
        using var handler = new CodexUsageMapHandler(new Dictionary<string, double>());
        using var client = new HttpClient(handler);
        var result = new CodexQuotaService(client, directory)
            .FetchAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var card = result.Cards.Single();
        Equal(1, result.Cards.Count, "API-only Cockpit configuration returns one aggregate card");
        Equal(true, card.IsService, "API-only card is informational");
        Equal("API · 1", card.Label, "API-only card shows the active service total");
        Equal("Test API provider", card.ServiceDisplayName, "API-only card retains the Cockpit provider name");
        Equal("Test API provider", card.DisplayLabel, "API-only card prefers the Cockpit provider name on surfaces");
        Equal("API key", card.Badge, "API-only card retains its plan badge");
        Equal(1, card.ServiceCount, "API-only card counts active services only");
        Equal<double?>(null, card.Windows.Single().UsedPercent, "API service has no fake percentage");
        Equal<DateTimeOffset?>(null, card.Windows.Single().ResetsAt, "API service has no fake reset");
        Equal(false, result.Health.Connected, "API-only configuration is not quota-connected");
        Equal(ProviderHealthCode.Unavailable, result.Health.Code, "API-only health remains unavailable for quota");
        Equal(0, result.CodexAccounts.Count, "API service does not enter quota summaries");
        Equal(0, handler.Calls, "API-only configuration performs no provider request");
        Equal("API", TaskbarMiniGrouping.CodexRowWindows(card).Single().Label, "Mini preserves the API service row");
        Equal("API 服务已配置", NativeText.For("zh-CN").ApiServiceConfigured, "Chinese service status is explicit");
        Equal("API service configured", NativeText.For("en").ApiServiceConfigured, "English service status is explicit");

        using var mini = new BarForm(
            new AppSettings
            {
                Locale = "zh-CN",
                EnableAnimations = false,
                EnableRadar = false,
            },
            new QuotaSnapshot([card], [result.Health], DateTimeOffset.UtcNow),
            renderOnly: true,
            renderDpi: 96,
            activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
        mini.CreateControl();
        using var miniBitmap = new Bitmap(
            mini.ClientSize.Width,
            mini.ClientSize.Height,
            PixelFormat.Format32bppPArgb);
        mini.DrawToBitmap(miniBitmap, new Rectangle(Point.Empty, miniBitmap.Size));
        var miniArea = mini.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Codex);
        Equal(card.DisplayLabel, miniArea.Title, "API Mini retains the Cockpit provider name");
        Equal(TaskbarMiniLayoutMath.ServiceCardWidth, miniArea.Width, "API Mini keeps the service label lane at compact width");

        var merged = QuotaCoordinator.MergeResults([result], null, DateTimeOffset.UtcNow);
        Equal(1, merged.Cards.Count, "coordinator preserves API service cards without quota cards");
        Equal(true, merged.Cards[0].IsService, "coordinator retains informational card semantics");

        using var popover = new QuotaPopoverForm();
        using var providerLogo = new Bitmap(24, 24);
        using var resetClock = new Bitmap(10, 10);
        using var bitmap = popover.RenderForTest(
            new QuotaPopoverContent(card, card.Windows.Single(), null, DateTimeOffset.UtcNow, null, false),
            NativeText.For("zh-CN"),
            providerLogo,
            resetClock,
            96,
            DateTimeOffset.UtcNow);
        Equal(
            new Size(QuotaPopoverForm.LogicalBodyWidth, QuotaPopoverForm.LogicalBodyHeight + 8),
            bitmap.Size,
            "API service popover uses the existing deterministic size");
        Equal(
            true,
            Enumerable.Range(0, bitmap.Width)
                .Any(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).A > 0)),
            "API service popover renders visible pixels");

        File.WriteAllText(
            Path.Combine(directory, "codex_accounts.json"),
            """
            {
              "accounts": [
                {"id":"api-service","plan_type":"API_KEY"},
                {"id":"api-service-two","plan_type":"api_key"}
              ]
            }
            """);
        var inactiveResult = new CodexQuotaService(client, directory)
            .FetchAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Equal(0, inactiveResult.Cards.Count(card => card.IsService), "inactive API-only configuration is hidden");
        Equal(0, handler.Calls, "inactive API-only configuration performs no provider request");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", previousRefresh);
        Directory.Delete(directory, true);
    }
}

static void TestInactiveCodexAccountsDoNotRestoreCachedCards()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-inactive-codex-{Guid.NewGuid():N}");
    var codexHome = Path.Combine(directory, "codex-home");
    var accountDirectory = Path.Combine(directory, "codex_accounts");
    Directory.CreateDirectory(codexHome);
    Directory.CreateDirectory(accountDirectory);
    var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    var previousRefresh = Environment.GetEnvironmentVariable("ZTB_DISABLE_REFRESH");
    var key = RandomNumberGenerator.GetBytes(32);
    try
    {
        const string accountId = "inactive-account";
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(
            Path.Combine(directory, "secure-account-storage.key"),
            Convert.ToBase64String(key));
        File.WriteAllText(
            Path.Combine(directory, "codex_accounts.json"),
            JsonSerializer.Serialize(new
            {
                current_account_id = accountId,
                accounts = new[]
                {
                    new
                    {
                        id = accountId,
                        plan_type = "plus",
                        subscription_active_until = now.AddDays(30),
                    },
                },
            }));
        File.WriteAllText(
            Path.Combine(directory, "codex_instances.json"),
            JsonSerializer.Serialize(new
            {
                instances = new[]
                {
                    new { bindAccountId = accountId, lastPid = (int?)null },
                },
            }));
        WriteCockpitCodexAccount(
            Path.Combine(accountDirectory, "inactive.json"),
            key,
            accountId,
            "inactive@example.test",
            "plus",
            0,
            now.AddMinutes(-2));

        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", "1");
        using var handler = new CodexUsageMapHandler(new Dictionary<string, double>());
        using var client = new HttpClient(handler);
        var result = new CodexQuotaService(client, directory)
            .FetchAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Equal(0, result.Cards.Count, "inactive Cockpit account does not create a visible Codex card");
        Equal(1, result.CodexAccounts.Count, "inactive account remains available to account summaries");
        Equal(true, result.ReplaceCachedCards, "inactive account refresh explicitly replaces cached cards");

        var previousCard = new QuotaCard(
            "codex.inactive",
            ProviderKind.Codex,
            "Codex",
            "plus",
            "#10a37f",
            false,
            [new QuotaWindow("7d", 100, now.AddDays(1), TimeSpan.FromDays(7))])
        {
            CapturedAt = now.AddMinutes(-1),
        };
        var merged = QuotaCoordinator.MergeResults(
            [result],
            new QuotaSnapshot([previousCard], [result.Health], now.AddMinutes(-1)),
            now);
        Equal(0, merged.Cards.Count, "inactive Codex card is not restored from the previous snapshot");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
        Environment.SetEnvironmentVariable("ZTB_DISABLE_REFRESH", previousRefresh);
        Directory.Delete(directory, true);
    }
}

static void WriteCockpitApiService(
    string path,
    byte[] key,
    string id,
    string providerName)
{
    var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
    {
        id,
        plan_type = "API_KEY",
        account_id = $"acct-{id}",
        api_provider_name = providerName,
        api_base_url = "https://api.example.test/v1",
        openai_api_key = "test-api-key",
        tokens = new
        {
            id_token = "not-used",
            access_token = (string?)null,
        },
    });
    var nonce = RandomNumberGenerator.GetBytes(12);
    var ciphertext = new byte[plaintext.Length];
    var tag = new byte[16];
    var combined = new byte[ciphertext.Length + tag.Length];
    try
    {
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                version = 1,
                kind = "codex",
                algorithm = "AES-256-GCM",
                key_id = "test",
                nonce = Convert.ToBase64String(nonce),
                ciphertext = Convert.ToBase64String(combined),
            }));
    }
    finally
    {
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(nonce);
        CryptographicOperations.ZeroMemory(ciphertext);
        CryptographicOperations.ZeroMemory(tag);
        CryptographicOperations.ZeroMemory(combined);
    }
}

static void WriteCockpitCodexAccount(
    string path,
    byte[] key,
    string id,
    string email,
    string plan,
    double remainingPercent,
    DateTimeOffset capturedAt,
    string? accessToken = null,
    string? idToken = null,
    string? refreshToken = null,
    DateTimeOffset? subscriptionActiveUntil = null)
{
    var effectiveSubscription = subscriptionActiveUntil ?? capturedAt.AddDays(30);
    var effectiveAccessToken = accessToken ?? TestJwt(new Dictionary<string, object?>
    {
        ["exp"] = effectiveSubscription.AddHours(-1).ToUnixTimeSeconds(),
    });
    var effectiveIdToken = idToken ?? TestJwt(new Dictionary<string, object?>
    {
        ["exp"] = effectiveSubscription.AddHours(-1).ToUnixTimeSeconds(),
    });
    var effectiveRefreshToken = refreshToken ?? "refresh-token";
    var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
    {
        id,
        email,
        plan_type = plan,
        account_id = $"acct-{id}",
        subscription_active_until = effectiveSubscription,
        tokens = new
        {
            access_token = effectiveAccessToken,
            id_token = effectiveIdToken,
            refresh_token = effectiveRefreshToken,
        },
        usage_updated_at = capturedAt.ToUnixTimeSeconds(),
        quota = new
        {
            raw_data = new
            {
                account_id = $"acct-{id}",
                email,
                plan_type = plan,
                rate_limit = new
                {
                    primary_window = new
                    {
                        remaining_percent = remainingPercent,
                        limit_window_seconds = 604_800,
                        reset_at = capturedAt.AddDays(7).ToUnixTimeSeconds(),
                    },
                },
            },
        },
    });
    var nonce = RandomNumberGenerator.GetBytes(12);
    var ciphertext = new byte[plaintext.Length];
    var tag = new byte[16];
    var combined = new byte[ciphertext.Length + tag.Length];
    try
    {
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                version = 1,
                kind = "codex",
                algorithm = "AES-256-GCM",
                key_id = "test",
                nonce = Convert.ToBase64String(nonce),
                ciphertext = Convert.ToBase64String(combined),
            }));
    }
    finally
    {
        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(nonce);
        CryptographicOperations.ZeroMemory(ciphertext);
        CryptographicOperations.ZeroMemory(tag);
        CryptographicOperations.ZeroMemory(combined);
    }
}

static string TestJwt(IReadOnlyDictionary<string, object?> payload) =>
    $"{Base64Url("""{"alg":"none","typ":"JWT"}""")}.{Base64Url(JsonSerializer.Serialize(payload))}.test";

static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
    .TrimEnd('=')
    .Replace('+', '-')
    .Replace('/', '_');

static void TestQuotaJumpStabilization()
{
    var now = DateTimeOffset.Parse("2026-07-19T06:00:00Z");
    var reset = now.AddMinutes(20);
    var previous = SingleWindowSnapshot(80, reset);
    var firstCandidate = SingleWindowSnapshot(10, reset.AddHours(5));
    var stabilizer = new QuotaSnapshotStabilizer();

    var held = stabilizer.Apply(previous, firstCandidate, now);
    Equal(true, held.ConfirmationRequired, "first large premature reset requires confirmation");
    Equal(previous, held.Snapshot, "first large premature reset keeps prior snapshot");

    var confirmedCandidate = SingleWindowSnapshot(11, reset.AddHours(5));
    var confirmed = stabilizer.Apply(previous, confirmedCandidate, now.AddSeconds(3));
    Equal(false, confirmed.ConfirmationRequired, "matching second sample confirms jump");
    Equal(confirmedCandidate, confirmed.Snapshot, "confirmed sample becomes visible");

    var ordinary = stabilizer.Apply(previous, SingleWindowSnapshot(75, reset), now);
    Equal(false, ordinary.ConfirmationRequired, "small change is accepted immediately");
}

static void TestQuotaCacheFreshness()
{
    var now = DateTimeOffset.Parse("2026-07-21T08:00:00Z");
    var cachedAt = now.AddDays(-6);
    var cachedClaude = new QuotaCard(
        "claude.account",
        ProviderKind.Claude,
        "Claude",
        "Pro",
        "#d97757",
        true,
        [
            new QuotaWindow("5h", 42, now.AddHours(-1), TimeSpan.FromHours(5)),
            new QuotaWindow("1w", 18, now.AddDays(1), TimeSpan.FromDays(7)),
        ])
    {
        CapturedAt = cachedAt,
    };
    var previous = new QuotaSnapshot(
        [cachedClaude],
        [new ProviderHealth(ProviderKind.Claude, true, "Claude quota is current.")],
        now);
    var unavailableClaude = new ProviderResult(
        ProviderKind.Claude,
        [new QuotaCard(
            "claude.account",
            ProviderKind.Claude,
            "Claude",
            null,
            "#d97757",
            true,
            [
                new QuotaWindow("5h", null, null, TimeSpan.FromHours(5)),
                new QuotaWindow("1w", null, null, TimeSpan.FromDays(7)),
            ])],
        new ProviderHealth(ProviderKind.Claude, false, "Claude quota is unavailable."));
    var liveCodex = new ProviderResult(
        ProviderKind.Codex,
        [new QuotaCard(
            "codex.account",
            ProviderKind.Codex,
            "Codex",
            "Plus",
            "#10a37f",
            true,
            [new QuotaWindow("5h", 17, now.AddHours(3), TimeSpan.FromHours(5))])],
        new ProviderHealth(ProviderKind.Codex, true, "Codex quota is current."));

    var merged = QuotaCoordinator.MergeResults([unavailableClaude, liveCodex], previous, now);
    var mergedClaude = merged.Cards.Single(card => card.Provider == ProviderKind.Claude);
    Equal(now, mergedClaude.CapturedAt, "failed Claude refresh replaces the cached timestamp");
    Equal(true, mergedClaude.Windows.All(window => window.UsedPercent is null), "failed Claude refresh never shows cached percentages");
    Equal(now, merged.Cards.Single(card => card.Provider == ProviderKind.Codex).CapturedAt, "live provider timestamp is current");

    var exhaustedAt = now.AddMinutes(-5);
    var exhaustedClaude = cachedClaude with
    {
        CapturedAt = exhaustedAt,
        Windows =
        [
            new QuotaWindow("5h", 0, null, TimeSpan.FromHours(5)),
            new QuotaWindow("1w", 100, now.AddHours(6), TimeSpan.FromDays(7)),
        ],
    };
    var exhaustedPrevious = previous with { Cards = [exhaustedClaude] };
    var exhaustedFallback = QuotaCoordinator.MergeResults([unavailableClaude], exhaustedPrevious, now);
    var fallbackCard = exhaustedFallback.Cards.Single();
    Equal(exhaustedAt, fallbackCard.CapturedAt, "exhausted Claude keeps its original sample time");
    Equal<double?>(null, fallbackCard.Windows.Single(window => window.Label == "5h").UsedPercent, "non-exhausted stale window is hidden");
    Equal<double?>(100, fallbackCard.Windows.Single(window => window.Label == "1w").UsedPercent, "active exhausted window remains visible");

    var resetFallback = QuotaCoordinator.MergeResults([unavailableClaude], exhaustedPrevious, now.AddHours(7));
    Equal(true, resetFallback.Cards.Single().Windows.All(window => window.UsedPercent is null), "exhausted cache clears after reset");

    var expired = QuotaCoordinator.MergeResults([unavailableClaude], merged, now.AddDays(8));
    Equal(true, expired.Cards.Single().Windows.All(window => window.UsedPercent is null), "expired provider quota is not reused");

    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-cache-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new AppSettingsStore(directory);
        store.SaveCache(merged with
        {
            Cards = merged.Cards
                .Select(card => card.Provider == ProviderKind.Claude
                    ? card with { CapturedAt = now.AddDays(-8) }
                    : card)
                .ToArray(),
        });
        var loaded = store.LoadCache(now);
        Equal(1, loaded?.Cards.Count, "expired provider card is filtered from persisted cache");
        Equal(ProviderKind.Codex, loaded!.Cards.Single().Provider, "fresh provider card remains cached");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestProviderExhaustedRefreshCadence()
{
    var now = DateTimeOffset.Parse("2026-07-28T04:00:00Z");
    var capturedAt = now.AddMinutes(-5);
    var resetAt = now.AddHours(6);
    var exhausted = new QuotaSnapshot(
        [new QuotaCard(
            "claude.account",
            ProviderKind.Claude,
            "Claude",
            "Max",
            "#d97757",
            true,
            [
                new QuotaWindow("5h", 0, null, TimeSpan.FromHours(5)),
                new QuotaWindow("1w", 100, resetAt, TimeSpan.FromDays(7)),
            ])
        {
            CapturedAt = capturedAt,
        }],
        [new ProviderHealth(ProviderKind.Claude, true, "Claude quota is current.")],
        capturedAt);

    Equal(true, QuotaCoordinator.ShouldDeferProviderRefresh(exhausted, ProviderKind.Claude, now), "recent exhausted Claude sample defers polling");
    Equal(false, QuotaCoordinator.ShouldDeferProviderRefresh(exhausted, ProviderKind.Claude, capturedAt.AddMinutes(30)), "exhausted sample refreshes every thirty minutes");
    Equal(true, QuotaCoordinator.ShouldDeferProviderRefresh(exhausted, ProviderKind.Claude, now, now.AddMinutes(-5)), "failed attempt starts a new cooldown");

    var nearReset = exhausted with
    {
        Cards =
        [
            exhausted.Cards.Single() with
            {
                Windows =
                [
                    new QuotaWindow("5h", 0, null, TimeSpan.FromHours(5)),
                    new QuotaWindow("1w", 100, now.AddMinutes(2), TimeSpan.FromDays(7)),
                ],
            },
        ],
    };
    Equal(false, QuotaCoordinator.ShouldDeferProviderRefresh(nearReset, ProviderKind.Claude, now.AddMinutes(2)), "reset bypasses the cooldown");

    var available = exhausted with
    {
        Cards =
        [
            exhausted.Cards.Single() with
            {
                Windows =
                [
                    new QuotaWindow("5h", 0, null, TimeSpan.FromHours(5)),
                    new QuotaWindow("1w", 99, resetAt, TimeSpan.FromDays(7)),
                ],
            },
        ],
    };
    Equal(false, QuotaCoordinator.ShouldDeferProviderRefresh(available, ProviderKind.Claude, now), "remaining quota keeps the configured cadence");

    var exhaustedCodexCard = exhausted.Cards.Single() with
    {
        Key = "codex.account",
        Provider = ProviderKind.Codex,
        Label = "Codex",
    };
    var mixed = exhausted with
    {
        Cards =
        [
            exhausted.Cards.Single(),
            exhaustedCodexCard with
            {
                Windows =
                [
                    new QuotaWindow("5h", 20, now.AddHours(4), TimeSpan.FromHours(5)),
                    new QuotaWindow("7d", 30, resetAt, TimeSpan.FromDays(7)),
                ],
            },
        ],
        Health =
        [
            exhausted.Health.Single(),
            new ProviderHealth(ProviderKind.Codex, true, "Codex quota is current."),
        ],
    };
    Equal(true, QuotaCoordinator.ShouldDeferProviderRefresh(mixed, ProviderKind.Claude, now), "exhausted Claude remains on the slow cadence");
    Equal(false, QuotaCoordinator.ShouldDeferProviderRefresh(mixed, ProviderKind.Codex, now), "available Codex remains on the normal cadence");

    var bothExhausted = mixed with { Cards = [exhausted.Cards.Single(), exhaustedCodexCard] };
    Equal(true, QuotaCoordinator.ShouldDeferProviderRefresh(bothExhausted, ProviderKind.Claude, now), "exhausted Claude defers when both are exhausted");
    Equal(true, QuotaCoordinator.ShouldDeferProviderRefresh(bothExhausted, ProviderKind.Codex, now), "exhausted Codex defers when both are exhausted");

    var secondCodexAccount = exhaustedCodexCard with
    {
        Key = "codex.second",
        Active = false,
        Windows = [new QuotaWindow("7d", 40, resetAt, TimeSpan.FromDays(7))],
    };
    var oneCodexAccountAvailable = mixed with { Cards = [exhaustedCodexCard, secondCodexAccount] };
    Equal(false, QuotaCoordinator.ShouldDeferProviderRefresh(oneCodexAccountAvailable, ProviderKind.Codex, now), "one available Codex account keeps polling at the normal cadence");
}

static void TestQuotaPaceEstimate()
{
    var baselineAt = DateTimeOffset.Parse("2026-07-31T04:00:00Z");
    var latestAt = baselineAt.AddHours(1);
    var resetAt = baselineAt.AddHours(10);
    var tracker = new QuotaPaceTracker();
    var baseline = PaceSnapshot(
        baselineAt,
        [
            PaceCard("codex.first", "Codex · 1", 20, resetAt, baselineAt),
        ]);

    Equal(true, tracker.Observe(baseline, baselineAt), "first live quota sample is recorded");
    Equal(
        QuotaPaceStatus.Learning,
        tracker.Estimate(baseline.Cards.Single(), baseline.Cards.Single().Windows.Single(), baselineAt, 5).Status,
        "one sample cannot produce a pace");

    var latest = PaceSnapshot(
        latestAt,
        [
            PaceCard("codex.first", "Codex · 1", 30, resetAt, latestAt),
            PaceCard("codex.second", "Codex · 2", 42, resetAt, latestAt, active: false),
        ]);
    Equal(true, tracker.Observe(latest, latestAt), "new account samples are recorded independently");

    var firstCard = latest.Cards[0];
    var estimate = tracker.Estimate(firstCard, firstCard.Windows.Single(), latestAt, 5);
    Equal(QuotaPaceStatus.ProjectedExhaustion, estimate.Status, "positive pace projects exhaustion before reset");
    Equal<double?>(10, estimate.Recent?.PercentPerHour, "pace is percentage-point delta per hour");
    Equal<TimeSpan?>(TimeSpan.FromHours(1), estimate.ObservedSpan, "pace exposes the actual observed span");
    Equal<DateTimeOffset?>(baselineAt.AddHours(8), estimate.Recent?.ProjectedExhaustedAt, "ETA starts from the latest sample");
    Equal(QuotaTrendConfidence.Stable, estimate.Recent?.Confidence, "one-hour trend is stable");

    var secondCard = latest.Cards[1];
    Equal(
        QuotaPaceStatus.Learning,
        tracker.Estimate(secondCard, secondCard.Windows.Single(), latestAt, 5).Status,
        "a second Codex account cannot borrow the first account baseline");

    var slowTracker = new QuotaPaceTracker();
    var slowBaseline = PaceSnapshot(
        baselineAt,
        [PaceCard("codex.slow", "Codex", 20, latestAt.AddHours(2), baselineAt)]);
    var slowLatest = PaceSnapshot(
        latestAt,
        [PaceCard("codex.slow", "Codex", 24, latestAt.AddHours(2), latestAt)]);
    slowTracker.Observe(slowBaseline, baselineAt);
    slowTracker.Observe(slowLatest, latestAt);
    Equal(
        QuotaPaceStatus.ResetsBeforeExhaustion,
        slowTracker.Estimate(slowLatest.Cards.Single(), slowLatest.Cards.Single().Windows.Single(), latestAt, 5).Status,
        "reset-first projection does not claim exhaustion");

    var quietTracker = new QuotaPaceTracker();
    quietTracker.Observe(
        PaceSnapshot(baselineAt, [PaceCard("codex.quiet", "Codex", 20, resetAt, baselineAt)]),
        baselineAt);
    var quietLatest = PaceSnapshot(
        latestAt,
        [PaceCard("codex.quiet", "Codex", 20.4, resetAt, latestAt)]);
    quietTracker.Observe(quietLatest, latestAt);
    Equal(
        QuotaPaceStatus.NoMeaningfulConsumption,
        quietTracker.Estimate(quietLatest.Cards.Single(), quietLatest.Cards.Single().Windows.Single(), latestAt, 5).Status,
        "sub-half-point movement is treated as provider rounding noise");

    var aliasCard = PaceCard("codex.alias", "Codex", 10, resetAt, latestAt);
    var weekly = aliasCard.Windows.Single() with { Label = "1w", Duration = TimeSpan.FromDays(7) };
    Equal(
        QuotaPaceTracker.SeriesKey(aliasCard, weekly),
        QuotaPaceTracker.SeriesKey(aliasCard, weekly with { Label = "week" }),
        "weekly aliases share one series");
    Equal(
        false,
        string.Equals(
            QuotaPaceTracker.SeriesKey(aliasCard, weekly),
            QuotaPaceTracker.SeriesKey(aliasCard, weekly with { Label = "Fable" }),
            StringComparison.Ordinal),
        "Fable stays separate from the general weekly window");

    foreach (var (span, expectedStatus) in new[]
             {
                 (TimeSpan.FromMinutes(44), QuotaPaceStatus.ResetsBeforeExhaustion),
                 (TimeSpan.FromMinutes(45), QuotaPaceStatus.ResetsBeforeExhaustion),
                 (TimeSpan.FromMinutes(75), QuotaPaceStatus.ResetsBeforeExhaustion),
                 (TimeSpan.FromMinutes(76), QuotaPaceStatus.ResetsBeforeExhaustion),
             })
    {
        var boundaryTracker = new QuotaPaceTracker();
        boundaryTracker.Observe(
            PaceSnapshot(baselineAt, [PaceCard("codex.boundary", "Codex", 10, resetAt, baselineAt)]),
            baselineAt);
        var boundaryLatest = PaceSnapshot(
            baselineAt + span,
            [PaceCard(
                "codex.boundary",
                "Codex",
                10 + span.TotalHours * 6,
                resetAt,
                baselineAt + span)]);
        boundaryTracker.Observe(boundaryLatest, baselineAt + span);
        Equal(
            expectedStatus,
            boundaryTracker.Estimate(
                boundaryLatest.Cards.Single(),
                boundaryLatest.Cards.Single().Windows.Single(),
                baselineAt + span,
                5).Status,
            $"{span.TotalMinutes:0}-minute baseline boundary");
    }
}

static void TestQuotaPaceWeightedDailyTrend()
{
    var now = DateTimeOffset.Parse("2026-08-02T12:00:00Z");
    var resetAt = now.AddDays(5);

    (QuotaPaceEstimate Estimate, QuotaRateHistory History) Estimate(
        string key,
        int hours,
        Func<int, double> usedAtHour)
    {
        var tracker = new QuotaPaceTracker();
        QuotaCard? latestCard = null;
        for (var hour = 0; hour <= hours; hour++)
        {
            var capturedAt = now.AddHours(hour - hours);
            latestCard = PaceCard(key, "Codex", usedAtHour(hour), resetAt, capturedAt) with
            {
                Windows = [new QuotaWindow("7d", usedAtHour(hour), resetAt, TimeSpan.FromDays(7))],
            };
            tracker.Observe(PaceSnapshot(capturedAt, [latestCard]), capturedAt);
        }

        return (
            tracker.Estimate(latestCard!, latestCard!.Windows.Single(), now, 5),
            tracker.Export(now));
    }

    var stable = Estimate("codex.daily-stable", 24, hour => 20 + hour);
    Equal(TimeSpan.FromHours(24), stable.Estimate.ObservedSpan, "daily trend uses the rolling 24-hour baseline");
    Equal(1d, Math.Round(stable.Estimate.Recent!.PercentPerHour, 3), "matching one-hour and daily rates stay unchanged");

    var accelerating = Estimate(
        "codex.daily-fast",
        24,
        hour => hour < 24 ? 20 + hour : 47);
    Equal(
        2.706d,
        Math.Round(accelerating.Estimate.Recent!.PercentPerHour, 3),
        "recent acceleration receives the larger fifty-five-percent weight");
    Equal(
        now.AddHours(53 / 2.70625d),
        accelerating.Estimate.Recent.ProjectedExhaustedAt,
        "weighted daily rate drives the exhaustion ETA");

    var slowing = Estimate(
        "codex.daily-slow",
        24,
        hour => hour < 24 ? 20 + hour : 43);
    Equal(
        .767d,
        Math.Round(slowing.Estimate.Recent!.PercentPerHour, 3),
        "one quiet hour only receives the smaller twenty-percent weight");

    QuotaPaceEstimate EstimateShortBurst(
        string key,
        params (TimeSpan BeforeLatest, double Used)[] tail)
    {
        var tracker = new QuotaPaceTracker();
        for (var hour = 0; hour <= 22; hour++)
        {
            var capturedAt = now.AddHours(hour - 24);
            var card = PaceCard(key, "Codex", 20 + hour, resetAt, capturedAt) with
            {
                Windows = [new QuotaWindow("7d", 20 + hour, resetAt, TimeSpan.FromDays(7))],
            };
            tracker.Observe(PaceSnapshot(capturedAt, [card]), capturedAt);
        }

        QuotaCard? latest = null;
        foreach (var sample in tail.OrderByDescending(sample => sample.BeforeLatest))
        {
            var capturedAt = now - sample.BeforeLatest;
            latest = PaceCard(key, "Codex", sample.Used, resetAt, capturedAt) with
            {
                Windows = [new QuotaWindow("7d", sample.Used, resetAt, TimeSpan.FromDays(7))],
            };
            tracker.Observe(PaceSnapshot(capturedAt, [latest]), capturedAt);
        }

        return tracker.Estimate(latest!, latest!.Windows.Single(), now, 5);
    }

    var thirtyMinuteBurst = EstimateShortBurst(
        "codex.daily-thirty-minute-burst",
        (TimeSpan.FromMinutes(30), 42),
        (TimeSpan.FromMinutes(15), 43),
        (TimeSpan.Zero, 44));
    Equal(
        2.35d,
        Math.Round(thirtyMinuteBurst.Recent!.PercentPerHour, 3),
        "a qualified thirty-minute acceleration adjusts the daily baseline with normal confidence");
    Equal(
        now.AddHours(56 / 2.35d),
        thirtyMinuteBurst.Recent.ProjectedExhaustedAt,
        "the accelerated account-level quota delta tightens the ETA");
    Equal(
        TimeSpan.FromHours(24),
        thirtyMinuteBurst.ObservedSpan,
        "a responsive correction keeps the stable daily anchor as its disclosed evidence span");
    Equal(
        QuotaTrendConfidence.Stable,
        thirtyMinuteBurst.Recent.Confidence,
        "a responsive correction does not replace the stable baseline confidence");

    var fifteenMinuteBurst = EstimateShortBurst(
        "codex.daily-fifteen-minute-burst",
        (TimeSpan.FromMinutes(15), 42),
        (TimeSpan.FromMinutes(7.5), 43),
        (TimeSpan.Zero, 44));
    Equal(
        3.45d,
        Math.Round(fifteenMinuteBurst.Recent!.PercentPerHour, 3),
        "a qualified fifteen-minute acceleration has a smaller provisional influence");

    var bootstrapBurst = EstimateShortBurst(
        "codex.daily-bootstrap-burst",
        (TimeSpan.FromMinutes(6), 42),
        (TimeSpan.Zero, 43));
    Equal(
        4.123d,
        Math.Round(bootstrapBurst.Recent!.PercentPerHour, 3),
        "a five-to-twelve-minute two-point burst fills the gap before the fifteen-minute window matures");

    Equal(
        TimeSpan.FromMinutes(12),
        QuotaPaceTracker.ResponsiveFallbackMaximumSpan,
        "the responsive two-point fallback has an explicit upper boundary");
    var boundaryBurst = EstimateShortBurst(
        "codex.daily-bootstrap-twelve-minute-boundary",
        (TimeSpan.FromMinutes(12), 42),
        (TimeSpan.Zero, 43));
    Equal(
        2.373d,
        Math.Round(boundaryBurst.Recent!.PercentPerHour, 3),
        "a two-point burst at twelve minutes still adjusts the stable baseline");
    var outsideBoundaryBurst = EstimateShortBurst(
        "codex.daily-bootstrap-thirteen-minute-boundary",
        (TimeSpan.FromMinutes(13), 42),
        (TimeSpan.Zero, 43));
    Equal(
        .958d,
        Math.Round(outsideBoundaryBurst.Recent!.PercentPerHour, 3),
        "a two-point sample older than twelve minutes cannot masquerade as current acceleration");

    var thirtyMinuteSlowdown = EstimateShortBurst(
        "codex.daily-thirty-minute-slowdown",
        (TimeSpan.FromMinutes(30), 42),
        (TimeSpan.FromMinutes(15), 42.1),
        (TimeSpan.Zero, 42.2));
    Equal(
        .846d,
        Math.Round(thirtyMinuteSlowdown.Recent!.PercentPerHour, 3),
        "a normal-confidence slowdown releases more cautiously than acceleration attacks");

    var quietHourWithThirtyMinuteBurst = EstimateShortBurst(
        "codex.daily-quiet-hour-fast-thirty",
        (TimeSpan.FromHours(1), 44.4),
        (TimeSpan.FromMinutes(30), 43),
        (TimeSpan.FromMinutes(15), 43.5),
        (TimeSpan.Zero, 44));
    Equal(
        1.45d,
        Math.Round(quietHourWithThirtyMinuteBurst.Recent!.PercentPerHour, 3),
        "a non-meaningful one-hour window cannot mask a qualified thirty-minute acceleration");

    var sixHour = Estimate("codex.six-hour", 6, hour => 30 + hour);
    Equal(TimeSpan.FromHours(6), sixHour.Estimate.ObservedSpan, "six-hour trend is the long-window fallback");
    Equal(1d, Math.Round(sixHour.Estimate.Recent!.PercentPerHour, 3), "six-hour fallback preserves its average rate");

    var highCadenceSamples = Enumerable.Range(0, 1_561)
        .Select(minute => new QuotaRateSample(
            "codex.one-minute-refresh",
            "7d",
            TimeSpan.FromDays(7).Ticks,
            now.AddHours(-26).AddMinutes(minute),
            10 + minute * .01,
            resetAt,
            QuotaRateSampleSource.Live))
        .ToList();
    var highCadenceTracker = new QuotaPaceTracker(
        new QuotaRateHistory { Samples = highCadenceSamples });
    highCadenceTracker.Export(now);
    var highCadenceCard = PaceCard(
        "codex.one-minute-refresh",
        "Codex",
        25.6,
        resetAt,
        now) with
    {
        Windows = [new QuotaWindow("7d", 25.6, resetAt, TimeSpan.FromDays(7))],
    };
    var highCadence = highCadenceTracker.Estimate(
        highCadenceCard,
        highCadenceCard.Windows.Single(),
        now,
        1);
    Equal(
        TimeSpan.FromHours(24),
        highCadence.ObservedSpan,
        "one-minute refresh history retains the full daily baseline");
    Equal(
        .6d,
        Math.Round(highCadence.Recent!.PercentPerHour, 3),
        "one-minute refresh keeps the stable twenty-four-hour rate");

    var claudeHistory = new QuotaRateHistory
    {
        Samples = accelerating.History.Samples
            .Select(sample => sample with { CardKey = "claude.account" })
            .ToList(),
    };
    var restored = new QuotaPaceTracker(claudeHistory);
    var claude = new QuotaCard(
        "claude.account",
        ProviderKind.Claude,
        "Claude",
        "Max",
        "#d97757",
        true,
        [new QuotaWindow("7d", 47, resetAt, TimeSpan.FromDays(7))])
    {
        CapturedAt = now,
    };
    Equal(
        2.706d,
        Math.Round(restored.Estimate(claude, claude.Windows.Single(), now, 5).Recent!.PercentPerHour, 3),
        "persisted live history restores the same Claude trend without a provider-specific importer");
}

static void TestQuotaCyclePace()
{
    var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
    var resetAt = now.AddHours(2);
    var card = PaceCard("codex.cycle", "Codex", 70, resetAt, now);
    var tracker = new QuotaPaceTracker();
    var estimate = tracker.Estimate(card, card.Windows.Single(), now, 5);

    Equal(QuotaPaceStatus.ProjectedExhaustion, estimate.Status, "cycle pace is available without history");
    Equal(60d, estimate.Cycle?.ExpectedUsedPercent, "cycle expected usage follows elapsed fraction");
    Equal(10d, estimate.Cycle?.DeltaPercent, "cycle budget delta");
    Equal(23.333d, Math.Round(estimate.Cycle!.PercentPerHour!.Value, 3), "cycle average rate");
    Equal(
        now.AddHours(30d / (70d / 3d)),
        estimate.Cycle.ProjectedExhaustedAt,
        "cycle ETA starts at the current capture");
    Equal(false, estimate.Cycle.ResetsBeforeExhaustion, "cycle projection exhausts before reset");
    Equal(.643d, Math.Round(estimate.Cycle.SafeSpeedMultiplier!.Value, 3), "safe speed multiplier");
    Equal<QuotaRecentTrend?>(null, estimate.Recent, "cycle pace does not pretend to be a recent trend");

    var early = PaceCard("codex.early", "Codex", 1, now.AddHours(4).AddMinutes(55), now);
    var earlyEstimate = tracker.Estimate(early, early.Windows.Single(), now, 5);
    Equal(QuotaPaceStatus.Learning, earlyEstimate.Status, "first three percent of a cycle avoids unstable ETA");
    Equal(true, earlyEstimate.Cycle is not null, "early cycle still exposes budget position");
    Equal<double?>(null, earlyEstimate.Cycle?.PercentPerHour, "early cycle hides unstable average rate");

    var zero = PaceCard("codex.zero", "Codex", 0, resetAt, now);
    Equal<double?>(
        null,
        tracker.Estimate(zero, zero.Windows.Single(), now, 5).Cycle?.PercentPerHour,
        "zero usage has no exhaustion projection");

    var provisionalTracker = new QuotaPaceTracker();
    var provisionalReset = now.AddHours(5);
    foreach (var (minutes, used) in new[] { (0, 20d), (6, 20.8d), (15, 22d) })
    {
        var capturedAt = now.AddMinutes(minutes);
        provisionalTracker.Observe(
            PaceSnapshot(capturedAt, [PaceCard("codex.short", "Codex", used, provisionalReset, capturedAt)]),
            capturedAt);
    }
    var provisionalCard = PaceCard("codex.short", "Codex", 22, provisionalReset, now.AddMinutes(15));
    var provisional = provisionalTracker.Estimate(
        provisionalCard,
        provisionalCard.Windows.Single(),
        now.AddMinutes(15),
        5);
    Equal(QuotaTrendConfidence.Provisional, provisional.Recent?.Confidence, "15-minute trend is provisional");
    Equal(8d, Math.Round(provisional.Recent!.PercentPerHour, 3), "short trend uses regression");

    var normalTracker = new QuotaPaceTracker();
    foreach (var (minutes, used) in new[] { (0, 20d), (15, 21.5d), (30, 23d) })
    {
        var capturedAt = now.AddMinutes(minutes);
        normalTracker.Observe(
            PaceSnapshot(capturedAt, [PaceCard("codex.normal", "Codex", used, provisionalReset, capturedAt)]),
            capturedAt);
    }
    var normalCard = PaceCard("codex.normal", "Codex", 23, provisionalReset, now.AddMinutes(30));
    var normal = normalTracker.Estimate(normalCard, normalCard.Windows.Single(), now.AddMinutes(30), 5);
    Equal(QuotaTrendConfidence.Normal, normal.Recent?.Confidence, "30-minute trend is normal confidence");
    Equal(6d, Math.Round(normal.Recent!.PercentPerHour, 3), "30-minute regression rate");
}

static void TestQuotaPaceGuards()
{
    var baselineAt = DateTimeOffset.Parse("2026-07-31T04:00:00Z");
    var latestAt = baselineAt.AddHours(1);
    var resetAt = baselineAt.AddHours(5);
    var tracker = new QuotaPaceTracker();
    tracker.Observe(
        PaceSnapshot(baselineAt, [PaceCard("codex.reset", "Codex", 70, resetAt, baselineAt)]),
        baselineAt);
    var resetLatest = PaceSnapshot(
        latestAt,
        [PaceCard("codex.reset", "Codex", 5, resetAt.AddHours(5), latestAt)]);
    tracker.Observe(resetLatest, latestAt);
    Equal(
        QuotaPaceStatus.Learning,
        tracker.Estimate(resetLatest.Cards.Single(), resetLatest.Cards.Single().Windows.Single(), latestAt, 5).Status,
        "a reset starts a fresh learning period");
    Equal(1, tracker.Export(latestAt).Samples.Count, "old-cycle samples are removed");
    Equal(false, tracker.Observe(resetLatest, latestAt), "duplicate capturedAt is ignored");

    var futureCapturedAt = latestAt.AddMinutes(2);
    var futureCard = PaceCard(
        "codex.future",
        "Codex",
        40,
        futureCapturedAt.AddHours(4),
        futureCapturedAt);
    var futureEstimate = new QuotaPaceTracker().Estimate(
        futureCard,
        futureCard.Windows.Single(),
        latestAt,
        5);
    Equal(
        QuotaPaceStatus.WaitingForFreshData,
        futureEstimate.Status,
        "a future capture waits for a trustworthy provider timestamp");
    Equal<QuotaCyclePace?>(
        null,
        futureEstimate.Cycle,
        "a future capture cannot leak a cycle-derived ETA");

    var staleTracker = new QuotaPaceTracker();
    var oldWithoutHistory = PaceSnapshot(
        baselineAt,
        [PaceCard("codex.old", "Codex", 10, resetAt, baselineAt)]);
    Equal(
        QuotaPaceStatus.WaitingForFreshData,
        staleTracker.Estimate(
            oldWithoutHistory.Cards.Single(),
            oldWithoutHistory.Cards.Single().Windows.Single(),
            latestAt,
            5).Status,
        "an old card does not claim to be learning");
    staleTracker.Observe(
        PaceSnapshot(baselineAt, [PaceCard("codex.stale", "Codex", 10, resetAt, baselineAt)]),
        baselineAt);
    var staleLatest = PaceSnapshot(
        latestAt,
        [PaceCard("codex.stale", "Codex", 20, resetAt, latestAt)]);
    staleTracker.Observe(staleLatest, latestAt);
    Equal(
        QuotaPaceStatus.ResetsBeforeExhaustion,
        staleTracker.Estimate(
            staleLatest.Cards.Single(),
            staleLatest.Cards.Single().Windows.Single(),
            latestAt.AddMinutes(16),
            5).Status,
        "a recent cached estimate keeps a labeled fallback projection");
    Equal(
        QuotaPaceStatus.WaitingForFreshData,
        staleTracker.Estimate(
            staleLatest.Cards.Single(),
            staleLatest.Cards.Single().Windows.Single(),
            latestAt.AddHours(2).AddMinutes(1),
            5).Status,
        "fallback expires after two hours");

    var claudeTracker = new QuotaPaceTracker();
    var claudeFirstAt = DateTimeOffset.Parse("2026-08-02T18:10:59+08:00");
    var claudeLatestAt = DateTimeOffset.Parse("2026-08-02T18:45:02+08:00");
    var claudeReset = DateTimeOffset.Parse("2026-08-02T22:40:00+08:00");
    var claudeFirst = PaceSnapshot(
        claudeFirstAt,
        [PaceCard("claude.account", "Claude", 1, claudeReset, claudeFirstAt)]);
    var claudeLatest = PaceSnapshot(
        claudeLatestAt,
        [PaceCard("claude.account", "Claude", 1, claudeReset, claudeLatestAt)]);
    claudeTracker.Observe(claudeFirst, claudeFirstAt);
    claudeTracker.Observe(claudeLatest, claudeLatestAt);
    var claudeFallback = claudeTracker.Estimate(
        claudeLatest.Cards.Single(),
        claudeLatest.Cards.Single().Windows.Single(),
        claudeLatestAt.AddMinutes(25),
        5);
    Equal(
        QuotaPaceStatus.ResetsBeforeExhaustion,
        claudeFallback.Status,
        "rounded Claude samples fall back to the current-cycle estimate");
    Equal<double?>(
        .923,
        Math.Round(claudeFallback.Cycle!.PercentPerHour!.Value, 3),
        "Claude fallback uses elapsed cycle consumption");
    Equal<QuotaRecentTrend?>(null, claudeFallback.Recent, "flat rounded samples do not invent a recent trend");

    var cachedTracker = new QuotaPaceTracker();
    var cached = PaceSnapshot(
        latestAt,
        [PaceCard("codex.cached", "Codex", 40, resetAt, baselineAt)],
        ProviderHealthCode.Cached);
    Equal(false, cachedTracker.Observe(cached, latestAt), "cached provider data is not sampled");
    Equal(0, cachedTracker.Export(latestAt).Samples.Count, "cached sample leaves history empty");
    var oldLive = PaceSnapshot(
        latestAt,
        [PaceCard("codex.old-live", "Codex", 40, resetAt, baselineAt)]);
    Equal(false, cachedTracker.Observe(oldLive, latestAt), "old card data is not sampled even with current provider health");

    var claudeBlocked = new QuotaCard(
        "claude.account",
        ProviderKind.Claude,
        "Claude",
        "Max",
        "#d97757",
        true,
        [
            new QuotaWindow("5h", 0, null, TimeSpan.FromHours(5)),
            new QuotaWindow("1w", 100, latestAt.AddHours(4), TimeSpan.FromDays(7)),
        ])
    {
        CapturedAt = latestAt,
    };
    Equal(
        QuotaPaceStatus.WeeklyBlocked,
        cachedTracker.Estimate(claudeBlocked, claudeBlocked.Windows[0], latestAt, 5).Status,
        "weekly-blocked Claude five-hour quota has no pace");
    Equal(
        QuotaPaceStatus.Exhausted,
        cachedTracker.Estimate(claudeBlocked, claudeBlocked.Windows[1], latestAt, 5).Status,
        "fully consumed quota is exhausted instead of projected");
}

static void TestQuotaPacePersistence()
{
    var now = DateTimeOffset.Parse("2026-07-31T06:00:00Z");
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-pace-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new AppSettingsStore(directory);
        Equal(2_048, QuotaPaceTracker.MaximumSamplesPerSeries, "daily history covers one-minute refresh plus rollout headroom");
        Equal(32_768, QuotaPaceTracker.MaximumSamples, "global daily history remains explicitly bounded");
        var samples = Enumerable.Range(0, 2_200)
            .Select(index => new QuotaRateSample(
                "codex.account",
                "5h",
                TimeSpan.FromHours(5).Ticks,
                now.AddHours(-25).AddSeconds(index * 40),
                index % 101,
                now.AddHours(4)))
            .ToList();
        var tracker = new QuotaPaceTracker(new QuotaRateHistory { Samples = samples });
        Equal(2_048, tracker.SampleCount, "constructor immediately enforces the per-series history bound");
        var bounded = tracker.Export(now);
        Equal(2_048, bounded.Samples.Count, "per-series daily history is bounded");

        var globalSamples = Enumerable.Range(0, 20)
            .SelectMany(series => Enumerable.Range(0, 2_048)
                .Select(index => new QuotaRateSample(
                    $"codex.{series}",
                    "5h",
                    TimeSpan.FromHours(5).Ticks,
                    now.AddHours(-25).AddSeconds(index * 40),
                    index % 101,
                    now.AddHours(4))))
            .ToList();
        var globallyBoundedTracker = new QuotaPaceTracker(
            new QuotaRateHistory { Samples = globalSamples });
        Equal(32_768, globallyBoundedTracker.SampleCount, "constructor immediately enforces the global history bound");
        var globallyBounded = globallyBoundedTracker.Export(now);
        Equal(32_768, globallyBounded.Samples.Count, "total daily history is bounded");

        store.SaveQuotaRateHistory(bounded);
        var loaded = store.LoadQuotaRateHistory(now);
        Equal(2_048, loaded.Samples.Count, "daily pace history round-trips");
        Equal("codex.account", loaded.Samples[0].CardKey, "pace history keeps only the stable card key");
        Equal(QuotaRateHistory.CurrentSchemaVersion, loaded.SchemaVersion, "pace history saves schema v2");

        store.SaveQuotaRateHistory(new QuotaRateHistory
        {
            Samples =
            [
                new QuotaRateSample(
                    "codex.imported",
                    "7d",
                    TimeSpan.FromDays(7).Ticks,
                    now.AddMinutes(-30),
                    12,
                    now.AddDays(5),
                    QuotaRateSampleSource.CodexRollout),
            ],
        });
        var importedRoundTrip = store.LoadQuotaRateHistory(now);
        Equal(
            QuotaRateSampleSource.CodexRollout,
            importedRoundTrip.Samples.Single().Source,
            "schema v2 preserves sample provenance");

        File.WriteAllText(
            store.QuotaRateHistoryPath,
            $$"""
            {
              "schemaVersion": 1,
              "samples": [
                {
                  "cardKey": "codex.legacy",
                  "windowLabel": "5h",
                  "durationTicks": {{TimeSpan.FromHours(5).Ticks}},
                  "capturedAt": "{{now.AddMinutes(-20):O}}",
                  "usedPercent": 18,
                  "resetsAt": "{{now.AddHours(4):O}}"
                }
              ]
            }
            """);
        var migrated = store.LoadQuotaRateHistory(now);
        Equal(QuotaRateHistory.CurrentSchemaVersion, migrated.SchemaVersion, "schema v1 migrates in memory");
        Equal(QuotaRateSampleSource.Live, migrated.Samples.Single().Source, "schema v1 samples default to live");

        File.WriteAllText(store.QuotaRateHistoryPath, "{\"schemaVersion\":99,\"samples\":[]}");
        Equal(0, store.LoadQuotaRateHistory(now).Samples.Count, "unknown history schema safely starts empty");

        File.WriteAllText(store.QuotaRateHistoryPath, "{invalid");
        var recovered = store.LoadQuotaRateHistory(now);
        Equal(0, recovered.Samples.Count, "corrupt pace history starts empty");
        Equal(true, File.Exists(store.QuotaRateHistoryPath + ".corrupt.bak"), "corrupt pace history is preserved");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestPersistentHistoryIoProtection()
{
    var now = DateTimeOffset.Parse("2026-08-23T08:00:00Z");
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-history-io-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new AppSettingsStore(directory);
        var pace = new QuotaRateHistory
        {
            Samples =
            [
                new(
                    "codex.io",
                    "5h",
                    TimeSpan.FromHours(5).Ticks,
                    now,
                    12,
                    now.AddHours(4)),
            ],
        };
        var tokenIndex = new CodexTokenUsageIndex();
        var quotaTokens = new CodexQuotaTokenHistory();
        var radar = new RadarAlertState { LastSuccessfulFetchAt = now };

        store.SaveQuotaRateHistory(pace);
        store.SaveCodexTokenUsageIndex(tokenIndex);
        store.SaveCodexQuotaTokenHistory(quotaTokens);
        store.SaveRadarState(radar);

        VerifyHistoryIoProtection(
            store.QuotaRateHistoryPath,
            () => store.LoadQuotaRateHistory(now),
            () => store.SaveQuotaRateHistory(pace),
            "quota pace history");
        VerifyHistoryIoProtection(
            store.CodexTokenUsageIndexPath,
            () => store.LoadCodexTokenUsageIndex(),
            () => store.SaveCodexTokenUsageIndex(tokenIndex),
            "Codex token usage index");
        VerifyHistoryIoProtection(
            store.CodexQuotaTokenHistoryPath,
            () => store.LoadCodexQuotaTokenHistory(),
            () => store.SaveCodexQuotaTokenHistory(quotaTokens),
            "Codex quota token history");
        VerifyHistoryIoProtection(
            store.RadarStatePath,
            () => store.LoadRadarState(),
            () => store.SaveRadarState(radar),
            "Radar state");

        var firstBackup = Encoding.UTF8.GetBytes("first recovery copy");
        File.WriteAllBytes(store.QuotaRateHistoryPath + ".corrupt.bak", firstBackup);
        File.WriteAllText(store.QuotaRateHistoryPath, "{invalid");
        Equal(0, store.LoadQuotaRateHistory(now).Samples.Count, "corrupt history still falls back safely");
        Equal(
            true,
            firstBackup.SequenceEqual(File.ReadAllBytes(store.QuotaRateHistoryPath + ".corrupt.bak")),
            "an existing corrupt backup is never overwritten");
        File.WriteAllText(store.QuotaRateHistoryPath, "[]");
        Equal(
            0,
            store.LoadQuotaRateHistory(now).Samples.Count,
            "valid JSON with the wrong root shape is treated as corrupt history");
        Equal(
            true,
            firstBackup.SequenceEqual(File.ReadAllBytes(store.QuotaRateHistoryPath + ".corrupt.bak")),
            "wrong-shape JSON does not overwrite the first recovery copy");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void VerifyHistoryIoProtection(
    string path,
    Action load,
    Action save,
    string label)
{
    var original = File.ReadAllBytes(path);
    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        load();
        Equal(false, File.Exists(path + ".corrupt.bak"), $"{label} transient I/O does not create a corrupt backup");
        var blocked = false;
        try
        {
            save();
        }
        catch (IOException)
        {
            blocked = true;
        }
        Equal(true, blocked, $"{label} save is blocked after a transient read failure");
    }

    Equal(true, original.SequenceEqual(File.ReadAllBytes(path)), $"{label} remains byte-for-byte intact");
    load();
    save();
}

static void TestBoundedHttpBodyReader()
{
    using (var exact = new HttpResponseMessage(HttpStatusCode.OK)
           {
               Content = new ByteArrayContent(new byte[BoundedHttpBodyReader.MaximumBytes]),
           })
    {
        Equal(
            BoundedHttpBodyReader.MaximumBytes,
            BoundedHttpBodyReader.ReadAsync(exact, CancellationToken.None).GetAwaiter().GetResult().Length,
            "the exact HTTP body limit is accepted");
    }

    using (var knownOversize = new HttpResponseMessage(HttpStatusCode.OK)
           {
               Content = new ByteArrayContent(new byte[BoundedHttpBodyReader.MaximumBytes + 1]),
           })
    {
        var rejected = false;
        try
        {
            BoundedHttpBodyReader.ReadAsync(knownOversize, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Equal(true, rejected, "known oversized HTTP bodies are rejected");
    }

    using (var chunkedOversize = new HttpResponseMessage(HttpStatusCode.OK)
           {
               Content = new UnknownLengthContent(new byte[BoundedHttpBodyReader.MaximumBytes + 1]),
           })
    {
        Equal<long?>(null, chunkedOversize.Content.Headers.ContentLength, "chunked fixture has no content length");
        var rejected = false;
        try
        {
            BoundedHttpBodyReader.ReadAsync(chunkedOversize, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Equal(true, rejected, "chunked oversized HTTP bodies are rejected while streaming");
    }

    using var cancelledResponse = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new UnknownLengthContent(new byte[1]),
    };
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var cancellationObserved = false;
    try
    {
        BoundedHttpBodyReader.ReadAsync(cancelledResponse, cancelled.Token).GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
        cancellationObserved = true;
    }
    Equal(true, cancellationObserved, "HTTP body cancellation propagates");
}

static void TestQuotaImportedCompaction()
{
    var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
    var resetAt = now.AddDays(4);
    var card = RolloutCard(
        "codex.dense",
        now,
        new QuotaWindow("7d", 40, resetAt, TimeSpan.FromDays(7)));
    var snapshot = RolloutSnapshot(now, card);
    var tracker = new QuotaPaceTracker();
    tracker.Observe(snapshot, now);
    var imported = Enumerable.Range(0, 2001)
        .Select(index =>
        {
            var fraction = index / 2000d;
            return new QuotaRateSample(
                card.Key,
                "7d",
                TimeSpan.FromDays(7).Ticks,
                now.AddHours(-2).AddSeconds(TimeSpan.FromHours(2).TotalSeconds * fraction),
                20 + 20 * fraction,
                resetAt,
                QuotaRateSampleSource.CodexRollout);
        })
        .ToArray();

    Equal(true, tracker.MergeImported(imported, now), "dense imported history merges behind a live anchor");
    var retained = tracker.Export(now).Samples
        .Where(sample => sample.CardKey == card.Key)
        .OrderBy(sample => sample.CapturedAt)
        .ToArray();
    Equal(true, retained.Length <= 63, "two-minute compaction stays well below the per-series cap");
    Equal(true, retained.Length >= 60, "two-minute compaction retains enough OLS points");
    Equal(
        true,
        retained[^1].CapturedAt - retained[0].CapturedAt >= TimeSpan.FromMinutes(118),
        "dense imported history keeps the two-hour baseline");
    Equal(1, retained.Count(sample => sample.Source == QuotaRateSampleSource.Live), "live anchor is never compacted");
    Equal(
        QuotaRateSampleSource.Live,
        retained[^1].Source,
        "live anchor remains the current authoritative sample");
    Equal(TimeSpan.FromMinutes(2), QuotaPaceTracker.ImportedSampleInterval, "imported bucket interval");

    var estimate = tracker.Estimate(card, card.Windows.Single(), now, 5);
    Equal(QuotaTrendConfidence.Stable, estimate.Recent?.Confidence, "compacted history produces a stable one-hour trend");
    Equal(10d, Math.Round(estimate.Recent!.PercentPerHour, 1), "compacted OLS preserves the underlying rate");

    var resetVariants = new QuotaPaceTracker(new QuotaRateHistory
    {
        Samples =
        [
            new QuotaRateSample(
                "codex.reset-buckets",
                "7d",
                TimeSpan.FromDays(7).Ticks,
                now.AddMinutes(-10),
                10,
                resetAt,
                QuotaRateSampleSource.CodexRollout),
            new QuotaRateSample(
                "codex.reset-buckets",
                "7d",
                TimeSpan.FromDays(7).Ticks,
                now.AddMinutes(-9).AddSeconds(30),
                1,
                resetAt.AddDays(7),
                QuotaRateSampleSource.CodexRollout),
        ],
    }).Export(now);
    Equal(2, resetVariants.Samples.Count, "different reset cycles never share one imported bucket");

    var resetClockDrift = new QuotaPaceTracker(new QuotaRateHistory
    {
        Samples =
        [
            new QuotaRateSample(
                "codex.reset-drift",
                "7d",
                TimeSpan.FromDays(7).Ticks,
                now.AddMinutes(-10),
                10,
                resetAt,
                QuotaRateSampleSource.CodexRollout),
            new QuotaRateSample(
                "codex.reset-drift",
                "7d",
                TimeSpan.FromDays(7).Ticks,
                now.AddMinutes(-9).AddSeconds(30),
                10,
                resetAt.AddSeconds(1),
                QuotaRateSampleSource.CodexRollout),
        ],
    }).Export(now);
    Equal(1, resetClockDrift.Samples.Count, "same-cycle reset clock drift shares one imported bucket");
}

static QuotaCard PaceCard(
    string key,
    string label,
    double used,
    DateTimeOffset? reset,
    DateTimeOffset capturedAt,
    bool active = true) => new(
    key,
    ProviderKind.Codex,
    label,
    "Pro",
    "#10a37f",
    active,
    [new QuotaWindow("5h", used, reset, TimeSpan.FromHours(5))])
{
    CapturedAt = capturedAt,
};

static QuotaSnapshot PaceSnapshot(
    DateTimeOffset capturedAt,
    IReadOnlyList<QuotaCard> cards,
    ProviderHealthCode healthCode = ProviderHealthCode.Current) => new(
    cards,
    [new ProviderHealth(
        ProviderKind.Codex,
        healthCode == ProviderHealthCode.Current,
        healthCode.ToString(),
        healthCode)],
    capturedAt);

static void TestCodexRolloutQuotaOwnership()
{
    var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
    var firstFiveReset = now.AddHours(3);
    var firstWeekReset = now.AddDays(4);
    var secondFiveReset = now.AddHours(4);
    var secondWeekReset = now.AddDays(5);
    var first = RolloutCard(
        "codex.first",
        now,
        new QuotaWindow("5h", 30, firstFiveReset, TimeSpan.FromHours(5)),
        new QuotaWindow("7d", 40, firstWeekReset, TimeSpan.FromDays(7)));
    var second = RolloutCard(
        "codex.second",
        now,
        new QuotaWindow("5h", 50, secondFiveReset, TimeSpan.FromHours(5)),
        new QuotaWindow("7d", 60, secondWeekReset, TimeSpan.FromDays(7)));
    var snapshot = RolloutSnapshot(now, first, second);
    var events = new[]
    {
        RolloutEvent(now.AddHours(-1), 25, firstFiveReset, 38, firstWeekReset),
        RolloutEvent(now.AddHours(-1), 45, secondFiveReset, 58, secondWeekReset),
        RolloutEvent(now, 30, firstFiveReset, 40, firstWeekReset),
        RolloutEvent(now, 50, secondFiveReset, 60, secondWeekReset),
    };

    var matched = CodexRolloutQuotaImporter.Match(events, snapshot, now);
    Equal(2, matched.AcceptedChains, "two uniquely anchored accounts are imported");
    Equal(8, matched.Samples.Count, "each chain contributes both windows without cross-account mixing");
    Equal(
        4,
        matched.Samples.Count(sample => sample.CardKey == "codex.first"),
        "first account owns only its chain");
    Equal(
        4,
        matched.Samples.Count(sample => sample.CardKey == "codex.second"),
        "second account owns only its chain");

    var unanchoredTracker = new QuotaPaceTracker();
    Equal(false, unanchoredTracker.MergeImported(matched.Samples, now), "imported samples require live anchors");
    Equal(0, unanchoredTracker.Export(now).Samples.Count, "unanchored import persists nothing");

    var tracker = new QuotaPaceTracker();
    tracker.Observe(snapshot, now);
    Equal(true, tracker.MergeImported(matched.Samples, now), "older matched rollout samples merge");
    var merged = tracker.Export(now).Samples;
    Equal(8, merged.Count, "live anchors win timestamp collisions");
    Equal(
        4,
        merged.Count(sample => sample.Source == QuotaRateSampleSource.Live),
        "all current anchors remain live-owned");

    var identicalA = RolloutCard(
        "codex.same-a",
        now,
        new QuotaWindow("5h", 30, firstFiveReset, TimeSpan.FromHours(5)),
        new QuotaWindow("7d", 40, firstWeekReset, TimeSpan.FromDays(7)));
    var identicalB = identicalA with { Key = "codex.same-b", Active = false };
    var ambiguous = CodexRolloutQuotaImporter.Match(
        [events[0], events[2]],
        RolloutSnapshot(now, identicalA, identicalB),
        now);
    Equal(0, ambiguous.Samples.Count, "ambiguous account ownership imports nothing");
    Equal(1, ambiguous.AmbiguousChains, "ambiguous chain is audited");

    var weeklyOnly = RolloutCard(
        "codex.weekly",
        now,
        new QuotaWindow("7d", 11, firstWeekReset, TimeSpan.FromDays(7)));
    var weeklyMatched = CodexRolloutQuotaImporter.Match(
        [
            new CodexRolloutRateLimitEvent(
                now.AddMinutes(-30),
                null,
                new CodexRolloutRateLimitWindow(10, 10080, firstWeekReset)),
            new CodexRolloutRateLimitEvent(
                now,
                null,
                new CodexRolloutRateLimitWindow(11, 10080, firstWeekReset)),
        ],
        RolloutSnapshot(now, weeklyOnly),
        now);
    Equal(2, weeklyMatched.Samples.Count, "7d-only live cards can own secondary-only chains");

    var weeklyPrimaryMatched = CodexRolloutQuotaImporter.Match(
        [
            new CodexRolloutRateLimitEvent(
                now.AddMinutes(-30),
                new CodexRolloutRateLimitWindow(10, 10080, firstWeekReset),
                null),
            new CodexRolloutRateLimitEvent(
                now,
                new CodexRolloutRateLimitWindow(11, 10080, firstWeekReset),
                null),
        ],
        RolloutSnapshot(now, weeklyOnly),
        now);
    Equal(2, weeklyPrimaryMatched.Samples.Count, "7d-only chains may occupy the primary rollout lane");

    var swappedLanes = CodexRolloutQuotaImporter.Match(
        [
            new CodexRolloutRateLimitEvent(
                now.AddMinutes(-30),
                new CodexRolloutRateLimitWindow(39, 10080, firstWeekReset),
                new CodexRolloutRateLimitWindow(29, 300, firstFiveReset)),
            new CodexRolloutRateLimitEvent(
                now,
                new CodexRolloutRateLimitWindow(40, 10080, firstWeekReset),
                new CodexRolloutRateLimitWindow(30, 300, firstFiveReset)),
        ],
        RolloutSnapshot(now, first),
        now);
    Equal(4, swappedLanes.Samples.Count, "rollout lanes are identified by duration and reset, not field position");

    var replayed = CodexRolloutQuotaImporter.Match(
        [
            RolloutEvent(now.AddMinutes(-30), 29, firstFiveReset.AddMinutes(10), 39, firstWeekReset),
            RolloutEvent(now, 30, firstFiveReset.AddMinutes(10), 40, firstWeekReset),
        ],
        RolloutSnapshot(now, first),
        now);
    Equal(0, replayed.Samples.Count, "mismatched reset cycles are rejected");

    var windowJump = CodexRolloutQuotaImporter.Match(
        [
            RolloutEvent(now.AddMinutes(-30), 29, firstFiveReset, 39, firstWeekReset),
            new CodexRolloutRateLimitEvent(
                now,
                new CodexRolloutRateLimitWindow(30, 600, firstFiveReset),
                new CodexRolloutRateLimitWindow(40, 10080, firstWeekReset)),
        ],
        RolloutSnapshot(now, first),
        now);
    Equal(0, windowJump.Samples.Count, "window-duration jumps cannot form an import chain");

    var noReset = RolloutCard(
        "codex.no-reset",
        now,
        new QuotaWindow("5h", 30, null, TimeSpan.FromHours(5)));
    Equal(
        0,
        CodexRolloutQuotaImporter.Match([events[0], events[2]], RolloutSnapshot(now, noReset), now).Samples.Count,
        "missing live reset cannot anchor imported history");

    Equal(true, CodexRolloutQuotaImporter.TryParseLine(RolloutJsonLine(events[0]), out _), "strict rollout line parses");
    Equal(false, CodexRolloutQuotaImporter.TryParseLine("{invalid", out _), "malformed rollout line is ignored");
    Equal(
        false,
        CodexRolloutQuotaImporter.TryParseLine(
            RolloutJsonLine(new CodexRolloutRateLimitEvent(
                now,
                new CodexRolloutRateLimitWindow(101, 300, firstFiveReset),
                null)),
            out _),
        "out-of-range usage is ignored");
    Equal(
        false,
        CodexRolloutQuotaImporter.TryParseLine(
            JsonSerializer.Serialize(new
            {
                timestamp = now,
                type = "event_msg",
                payload = new { type = "token_count", rate_limits = (object?)null },
            }),
            out _),
        "null rate_limits is ignored");
}

static void TestCodexRolloutBoundedScanner()
{
    var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-rollout-scan-{Guid.NewGuid():N}");
    var sessions = Path.Combine(directory, "sessions", "2026", "07", "31");
    Directory.CreateDirectory(sessions);
    try
    {
        var resetAt = now.AddDays(4);
        var weeklyOnly = RolloutCard(
            "codex.weekly",
            now,
            new QuotaWindow("7d", 11, resetAt, TimeSpan.FromDays(7)));
        var firstPath = Path.Combine(sessions, "000.jsonl");
        File.WriteAllLines(
            firstPath,
            [
                "{invalid",
                new string('x', CodexRolloutQuotaImporter.MaximumLineBytes + 1),
                RolloutJsonLine(new CodexRolloutRateLimitEvent(
                    now.AddMinutes(-30),
                    null,
                    new CodexRolloutRateLimitWindow(10, 10080, resetAt))),
                RolloutJsonLine(new CodexRolloutRateLimitEvent(
                    now,
                    null,
                    new CodexRolloutRateLimitWindow(11, 10080, resetAt))),
                RolloutJsonLine(new CodexRolloutRateLimitEvent(
                    now,
                    null,
                    new CodexRolloutRateLimitWindow(11, 10080, resetAt))),
                RolloutJsonLine(new CodexRolloutRateLimitEvent(
                    now.AddMinutes(5),
                    null,
                    new CodexRolloutRateLimitWindow(12, 10080, resetAt))),
            ]);
        for (var index = 1; index < 65; index++)
        {
            File.WriteAllText(Path.Combine(sessions, $"{index:000}.jsonl"), "{invalid}\n");
        }
        foreach (var path in Directory.EnumerateFiles(sessions, "*.jsonl"))
        {
            File.SetLastWriteTimeUtc(path, now.UtcDateTime);
        }

        var result = CodexRolloutQuotaImporter.Import(
            directory,
            RolloutSnapshot(now, weeklyOnly),
            now);
        Equal(64, result.CandidateFiles, "scanner caps candidate files");
        Equal(64, result.ScannedFiles, "scanner opens at most 64 files");
        Equal(2, result.AcceptedEvents, "scanner accepts only strict recent rate-limit events");
        Equal(true, result.ParsedLines >= 5, "duplicate and future lines are parsed before filtering");
        Equal(1, result.AcceptedChains, "bounded scan still finds the unique chain");
        Equal(2, result.Samples.Count, "bounded scan emits the matched weekly samples");
        Equal(1, result.OversizedLines, "oversized JSONL line is skipped");
        Equal(8L * 1024 * 1024, CodexRolloutQuotaImporter.MaximumBytesPerFile, "per-file byte cap");
        Equal(64L * 1024 * 1024, CodexRolloutQuotaImporter.MaximumTotalBytes, "total byte cap");

        var scopedHome = Path.Combine(directory, "cockpit-scoped");
        var scopedSessions = Path.Combine(scopedHome, "sessions");
        Directory.CreateDirectory(scopedSessions);
        var scopedCard = RolloutCard(
            "codex.scoped",
            now,
            new QuotaWindow("7d", 11, resetAt, TimeSpan.FromDays(7)));
        var scopedPath = Path.Combine(scopedSessions, "scoped.jsonl");
        File.WriteAllLines(
            scopedPath,
            [
                RolloutTokenJsonLine(
                    new CodexRolloutRateLimitEvent(
                        now.AddMinutes(-20),
                        null,
                        new CodexRolloutRateLimitWindow(10, 10080, resetAt)),
                    100),
                RolloutTokenJsonLine(
                    new CodexRolloutRateLimitEvent(
                        now.AddMinutes(-10),
                        null,
                        new CodexRolloutRateLimitWindow(11, 10080, resetAt)),
                    140),
            ]);
        File.SetLastWriteTimeUtc(scopedPath, now.UtcDateTime);
        var adaptive = CodexRolloutQuotaImporter.Import(
            Path.Combine(directory, "empty-default"),
            [new CockpitCodexRolloutSource("scoped-account", scopedCard.Key, scopedHome)],
            RolloutSnapshot(now, scopedCard),
            now);
        Equal(2, adaptive.Observations.Count, "account-scoped Cockpit sessions feed token fallback");
        Equal(
            true,
            adaptive.Observations.All(observation => observation.CardKey == scopedCard.Key),
            "account-scoped session data cannot attach to another card");

        var nativeCard = scopedCard with { Key = "codex.native" };
        var nativeSnapshot = RolloutSnapshot(now, nativeCard) with
        {
            CodexAccounts =
            [
                new CodexAccountQuota("scoped-account", nativeCard.Windows, now),
            ],
        };
        var nativeMapped = CodexRolloutQuotaImporter.Import(
            Path.Combine(directory, "empty-default"),
            [new CockpitCodexRolloutSource(
                "scoped-account",
                CodexQuotaService.StableCardKey("cockpit:scoped-account"),
                scopedHome)],
            nativeSnapshot,
            now);
        Equal(2, nativeMapped.Observations.Count, "Cockpit account quota uniquely maps scoped sessions to a native card");
        Equal(
            true,
            nativeMapped.Observations.All(observation => observation.CardKey == nativeCard.Key),
            "native card identity wins after unique account quota matching");

        var ambiguousSnapshot = RolloutSnapshot(
            now,
            nativeCard,
            nativeCard with { Key = "codex.native-duplicate" }) with
        {
            CodexAccounts = nativeSnapshot.CodexAccounts,
        };
        var ambiguous = CodexRolloutQuotaImporter.Import(
            Path.Combine(directory, "empty-default"),
            [new CockpitCodexRolloutSource(
                "scoped-account",
                CodexQuotaService.StableCardKey("cockpit:scoped-account"),
                scopedHome)],
            ambiguousSnapshot,
            now);
        Equal(0, ambiguous.Observations.Count, "ambiguous native card mapping fails closed");

        var staleAccount = nativeSnapshot with
        {
            CodexAccounts =
            [
                new CodexAccountQuota(
                    "scoped-account",
                    nativeCard.Windows,
                    now.AddMinutes(-5)),
            ],
        };
        var staleMapped = CodexRolloutQuotaImporter.Import(
            Path.Combine(directory, "empty-default"),
            [new CockpitCodexRolloutSource(
                "scoped-account",
                CodexQuotaService.StableCardKey("cockpit:scoped-account"),
                scopedHome)],
            staleAccount,
            now);
        Equal(0, staleMapped.Observations.Count, "stale Cockpit account quota cannot remap scoped sessions");

        var failedAccount = nativeSnapshot with
        {
            CodexAccounts =
            [
                new CodexAccountQuota("scoped-account", nativeCard.Windows, now, "cached failure"),
            ],
        };
        var failedMapped = CodexRolloutQuotaImporter.Import(
            Path.Combine(directory, "empty-default"),
            [new CockpitCodexRolloutSource(
                "scoped-account",
                CodexQuotaService.StableCardKey("cockpit:scoped-account"),
                scopedHome)],
            failedAccount,
            now);
        Equal(0, failedMapped.Observations.Count, "failed Cockpit account quota cannot remap scoped sessions");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestCodexQuotaTokenTracker()
{
    var now = DateTimeOffset.Parse("2026-08-21T12:00:00Z");
    var reset = now.AddHours(3);
    var lifetimeTracker = new CodexQuotaTokenTracker();
    lifetimeTracker.Merge(
        [
            TokenObservation("codex.account-a", "5h", TimeSpan.FromHours(5), now.AddMinutes(-10), 10, reset, CodexQuotaTokenTracker.ProfileLifetimeSourceKey, 1_000),
            TokenObservation("codex.account-a", "7d", TimeSpan.FromDays(7), now.AddMinutes(-9), 20, now.AddDays(6), CodexQuotaTokenTracker.ProfileLifetimeSourceKey, 1_200),
            TokenObservation("codex.account-b", "7d", TimeSpan.FromDays(7), now.AddMinutes(-8), 30, now.AddDays(6), CodexQuotaTokenTracker.ProfileLifetimeSourceKey, 700),
            TokenObservation("codex.account-c", "7d", TimeSpan.FromDays(7), now.AddMinutes(-7), 40, now.AddDays(6), "rollout", 9_999),
        ],
        now);
    Equal(1_900L, lifetimeTracker.GetProfileLifetimeTotal(), "profile lifetime total deduplicates windows and sums accounts");
    Equal<long?>(null, new CodexQuotaTokenTracker().GetProfileLifetimeTotal(), "missing profile lifetime remains unknown");

    var tracker = new CodexQuotaTokenTracker();
    var observations = new[]
    {
        TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-50), 10, reset, "source-a", 100),
        TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-40), 16, reset, "source-a", 160),
        TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-40), 30, reset, "source-a", 160),
        TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-30), 12, reset, "source-a", 20),
        TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-20), 14, reset, "source-a", 40),
    };
    Equal(true, tracker.Merge(observations, now), "token observations merge");
    var current = tracker.GetSummary("codex.token", "7d", TimeSpan.FromDays(7).Ticks);
    Equal(1333L, current!.CurrentCapacityTokens, "current capacity uses positive deltas and observed span");
    Equal(80L, current.CurrentObservedTokens, "current summary exposes source-deduplicated observed tokens");
    Equal(6d, current.CurrentObservedSpanPercent, "current summary exposes the observed percentage span");
    Equal(false, current.CoversCycleStart, "a mid-cycle baseline is not presented as full current-cycle usage");
    Equal<long?>(null, current.CurrentCycleTokens, "mid-cycle observations do not fabricate tokens before the baseline");
    Equal(0, current.CompletedCycleCount, "current cycle does not pollute history");
    Equal(
        false,
        tracker.Observe(
            TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-45), 30, reset, "source-a", 200),
            now),
        "older source observations are ignored");

    var cycleStartTracker = new CodexQuotaTokenTracker();
    cycleStartTracker.Merge(
        [
            TokenObservation(
                "codex.cycle-start",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-20),
                0,
                reset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                1_000_000),
            TokenObservation(
                "codex.cycle-start",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-10),
                16,
                reset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                500_200_000),
        ],
        now);
    var cycleStart = cycleStartTracker.GetSummary(
        "codex.cycle-start",
        "7d",
        TimeSpan.FromDays(7).Ticks)!;
    Equal(499_200_000L, cycleStart.CurrentObservedTokens, "zero-to-sixteen span reports the directly observed token delta");
    Equal(499_200_000L, cycleStart.CurrentCycleTokens, "cycle-start coverage makes the observed delta available as current used tokens");
    Equal(16d, cycleStart.CurrentObservedSpanPercent, "zero-to-sixteen span retains its quota coverage");
    Equal(true, cycleStart.CoversCycleStart, "zero-percent baseline proves current-cycle coverage");
    Equal(3_120_000_000L, cycleStart.CurrentCapacityTokens, "the same sixteen-percent delta normalizes to one hundred percent");

    var migrated = new CodexQuotaTokenTracker();
    migrated.Merge(
        [
            TokenObservation("codex.migration", "7d", TimeSpan.FromDays(7), now.AddMinutes(-20), 10, reset, "rollout", 100),
            TokenObservation("codex.migration", "7d", TimeSpan.FromDays(7), now.AddMinutes(-10), 16, reset, "rollout", 160),
        ],
        now);
    migrated.Merge(
        [
            TokenObservation(
                "codex.migration",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-5),
                18,
                reset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                1_000),
        ],
        now);
    var migratedSummary = migrated.GetSummary("codex.migration", "7d", TimeSpan.FromDays(7).Ticks);
    Equal<long?>(null, migratedSummary!.CurrentCapacityTokens, "profile source migration starts a clean baseline");
    Equal(0, migratedSummary.CompletedCycleCount, "profile source migration preserves only completed history");
    migrated.Merge(
        [
            TokenObservation(
                "codex.migration",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-1),
                20,
                reset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                1_100),
        ],
        now);
    Equal(
        5_000L,
        migrated.GetSummary("codex.migration", "7d", TimeSpan.FromDays(7).Ticks)!.CurrentCapacityTokens,
        "profile source advances from the migrated baseline");

    var profilePositive = new CodexQuotaTokenTracker();
    var priorityReset = reset.AddDays(1);
    profilePositive.Merge(
        [
            TokenObservation(
                "codex.priority",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-30),
                10,
                priorityReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                100),
            TokenObservation(
                "codex.priority",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-20),
                11,
                priorityReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                110),
        ],
        now);
    var rolloutCandidate = TokenObservation(
        "codex.priority",
        "7d",
        TimeSpan.FromDays(7),
        now.AddMinutes(-10),
        11.5,
        priorityReset,
        "anonymous-priority",
        500);
    Equal(false, profilePositive.IsRolloutFallbackEligible(rolloutCandidate, now), "fresh profile increment keeps priority below the stall span");
    var stalledRolloutCandidate = rolloutCandidate with
    {
        CapturedAt = now.AddMinutes(-9),
        UsedPercent = 12,
    };
    Equal(true, profilePositive.IsRolloutFallbackEligible(stalledRolloutCandidate, now), "profile switches after a full point without progress");
    Equal(
        true,
        profilePositive.Merge(
            [
                stalledRolloutCandidate with
                {
                    SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(stalledRolloutCandidate.SourceKey),
                },
                stalledRolloutCandidate with
                {
                    CapturedAt = now.AddMinutes(-8),
                    UsedPercent = 13,
                    TotalTokens = 550,
                    SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(stalledRolloutCandidate.SourceKey),
                },
            ],
            now),
        "stalled profile switches to local fallback");
    Equal(
        2_000L,
        profilePositive.GetSummary("codex.priority", "7d", TimeSpan.FromDays(7).Ticks)!.CurrentCapacityTokens,
        "fallback preserves completed official segments");
    Equal(
        true,
        profilePositive.GetSummary("codex.priority", "7d", TimeSpan.FromDays(7).Ticks)!.IsCurrentLocalFallback,
        "summary exposes the active local fallback quality");
    Equal(
        true,
        profilePositive.Merge(
            [TokenObservation(
                "codex.priority",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-7),
                14,
                priorityReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                140)],
            now),
        "advancing official counter resumes within the same cycle");
    Equal(
        1_000L,
        profilePositive.GetSummary("codex.priority", "7d", TimeSpan.FromDays(7).Ticks)!.CurrentCapacityTokens,
        "official recovery replaces the overlapping fallback segment");
    Equal(
        false,
        profilePositive.GetSummary("codex.priority", "7d", TimeSpan.FromDays(7).Ticks)!.IsCurrentLocalFallback,
        "summary clears local fallback quality after Profile recovery");

    var zeroProfile = new CodexQuotaTokenTracker();
    var zeroReset = reset.AddDays(2);
    zeroProfile.Merge(
        [TokenObservation(
            "codex.zero-profile",
            "7d",
            TimeSpan.FromDays(7),
            now.AddMinutes(-30),
            8,
            zeroReset,
            CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
            0)],
        now);
    var zeroFallbackFirst = TokenObservation(
        "codex.zero-profile",
        "7d",
        TimeSpan.FromDays(7),
        now.AddMinutes(-20),
        10,
        zeroReset,
        "anonymous-zero",
        200);
    var zeroFallbackSecond = zeroFallbackFirst with
    {
        CapturedAt = now.AddMinutes(-10),
        UsedPercent = 12,
        TotalTokens = 260,
    };
    Equal(true, zeroProfile.IsRolloutFallbackEligible(zeroFallbackFirst, now), "zero profile baseline allows fallback");
    var preBaselineFallback = zeroFallbackFirst with
    {
        CapturedAt = now.AddMinutes(-35),
        UsedPercent = 6,
    };
    Equal(false, zeroProfile.IsRolloutFallbackEligible(preBaselineFallback, now), "fallback cannot start before the profile source segment");
    Equal(false, zeroProfile.IsRolloutFallbackReplayObservation(preBaselineFallback, now), "profile baseline prevents replaying older local events");
    Equal(
        true,
        zeroProfile.Merge(
            [
                zeroFallbackFirst with
                {
                    SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(zeroFallbackFirst.SourceKey),
                },
                zeroFallbackSecond with
                {
                    SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(zeroFallbackSecond.SourceKey),
                },
            ],
            now),
        "fallback source establishes a zero-profile cycle");
    Equal(
        3_000L,
        zeroProfile.GetSummary("codex.zero-profile", "7d", TimeSpan.FromDays(7).Ticks)!.CurrentCapacityTokens,
        "fallback capacity uses its own cumulative cursor");
    Equal(
        true,
        zeroProfile.Merge(
            [TokenObservation(
                "codex.zero-profile",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-5),
                14,
                zeroReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                100)],
            now),
        "official counter can recover after a zero-profile fallback");
    Equal(
        2_500L,
        zeroProfile.GetSummary("codex.zero-profile", "7d", TimeSpan.FromDays(7).Ticks)!.CurrentCapacityTokens,
        "recovered official delta replaces the local fallback estimate");

    var noProfile = new CodexQuotaTokenTracker();
    var noProfileCandidate = TokenObservation(
        "codex.no-profile",
        "5h",
        TimeSpan.FromHours(5),
        now.AddMinutes(-10),
        5,
        reset,
        "anonymous-no-profile",
        50);
    Equal(true, noProfile.IsRolloutFallbackEligible(noProfileCandidate, now), "missing profile source allows fallback");
    Equal(
        true,
        noProfile.Merge(
            [noProfileCandidate with
            {
                SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(noProfileCandidate.SourceKey),
            }],
            now),
        "fallback without a profile baseline is accepted");
    noProfile.Merge(
        [noProfileCandidate with
        {
            CapturedAt = now.AddMinutes(-8),
            UsedPercent = 6,
            TotalTokens = 70,
            SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(noProfileCandidate.SourceKey),
        }],
        now);
    Equal(
        true,
        noProfile.Merge(
            [TokenObservation(
                "codex.no-profile",
                "5h",
                TimeSpan.FromHours(5),
                now.AddMinutes(-7),
                7,
                reset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                1_000)],
            now),
        "first official value freezes completed fallback tokens and establishes a baseline");
    Equal(
        false,
        noProfile.IsRolloutFallbackReplayObservation(
            noProfileCandidate with { CapturedAt = now.AddMinutes(-8) },
            now),
        "a later fallback cannot replay across a frozen local segment");
    noProfile.Merge(
        [TokenObservation(
            "codex.no-profile",
            "5h",
            TimeSpan.FromHours(5),
            now.AddMinutes(-6),
            8,
            reset,
            CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
            1_010)],
        now);
    Equal(
        1_000L,
        noProfile.GetSummary("codex.no-profile", "5h", TimeSpan.FromHours(5).Ticks)!.CurrentCapacityTokens,
        "official continuation adds only post-baseline tokens after a missing-profile fallback");

    var zeroIncrement = new CodexQuotaTokenTracker();
    var zeroIncrementReset = reset.AddDays(3);
    zeroIncrement.Merge(
        [
            TokenObservation(
                "codex.zero-increment",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-30),
                10,
                zeroIncrementReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                100),
            TokenObservation(
                "codex.zero-increment",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-20),
                11.5,
                zeroIncrementReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                100),
        ],
        now);
    var zeroIncrementCandidate = TokenObservation(
        "codex.zero-increment",
        "7d",
        TimeSpan.FromDays(7),
        now.AddMinutes(-10),
        12,
        zeroIncrementReset,
        "anonymous-zero-increment",
        300);
    Equal(true, zeroIncrement.IsRolloutFallbackEligible(zeroIncrementCandidate, now), "one-point profile span with zero delta allows fallback");
    Equal(
        true,
        zeroIncrement.Merge(
            [zeroIncrementCandidate with
            {
                SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(zeroIncrementCandidate.SourceKey),
            }],
            now),
        "one-point zero-delta profile switches to fallback once");
    Equal(
        true,
        zeroIncrement.Merge(
            [TokenObservation(
                "codex.zero-increment",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-5),
                13,
                zeroIncrementReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                120)],
            now),
        "one-point fallback returns to an advancing official counter");
    Equal(
        2_000L,
        zeroIncrement.GetSummary("codex.zero-increment", "7d", TimeSpan.FromDays(7).Ticks)!.CurrentCapacityTokens,
        "official recovery is not added on top of fallback tokens");

    var regressedProfile = new CodexQuotaTokenTracker();
    var regressedReset = reset.AddDays(5);
    regressedProfile.Merge(
        [
            TokenObservation("codex.regressed", "7d", TimeSpan.FromDays(7), now.AddMinutes(-30), 10, regressedReset, CodexQuotaTokenTracker.ProfileLifetimeSourceKey, 100),
            TokenObservation("codex.regressed", "7d", TimeSpan.FromDays(7), now.AddMinutes(-25), 11, regressedReset, CodexQuotaTokenTracker.ProfileLifetimeSourceKey, 120),
            TokenObservation("codex.regressed", "7d", TimeSpan.FromDays(7), now.AddMinutes(-20), 12, regressedReset, CodexQuotaTokenTracker.ProfileLifetimeSourceKey, 0),
        ],
        now);
    var regressedFallback = TokenObservation(
        "codex.regressed",
        "7d",
        TimeSpan.FromDays(7),
        now.AddMinutes(-15),
        13,
        regressedReset,
        "anonymous-regressed",
        500);
    Equal(true, regressedProfile.IsRolloutFallbackEligible(regressedFallback, now), "regressed official counter can fall back safely");
    regressedProfile.Merge(
        [
            regressedFallback with { SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(regressedFallback.SourceKey) },
            regressedFallback with
            {
                CapturedAt = now.AddMinutes(-10),
                UsedPercent = 14,
                TotalTokens = 550,
                SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(regressedFallback.SourceKey),
            },
        ],
        now);
    Equal(
        false,
        regressedProfile.Merge(
            [TokenObservation("codex.regressed", "7d", TimeSpan.FromDays(7), now.AddMinutes(-5), 15, regressedReset, CodexQuotaTokenTracker.ProfileLifetimeSourceKey, 130)],
            now),
        "regressed official counter cannot overwrite fallback in the same cycle");
    Equal(
        1_750L,
        regressedProfile.GetSummary("codex.regressed", "7d", TimeSpan.FromDays(7).Ticks)!.CurrentCapacityTokens,
        "regressed official counter does not duplicate or erase fallback tokens");

    var belowThreshold = new CodexQuotaTokenTracker();
    var belowReset = reset.AddDays(4);
    belowThreshold.Merge(
        [
            TokenObservation(
                "codex.below-threshold",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-30),
                10,
                belowReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                100),
            TokenObservation(
                "codex.below-threshold",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-20),
                10.5,
                belowReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                100),
        ],
        now);
    var belowCandidate = TokenObservation(
        "codex.below-threshold",
        "7d",
        TimeSpan.FromDays(7),
        now.AddMinutes(-10),
        10.75,
        belowReset,
        "anonymous-below-threshold",
        300);
    Equal(false, belowThreshold.IsRolloutFallbackEligible(belowCandidate, now), "sub-threshold profile baseline does not switch on a miss");
    Equal(
        false,
        belowThreshold.Merge(
            [belowCandidate with
            {
                SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(belowCandidate.SourceKey),
            }],
            now),
        "sub-threshold profile baseline rejects fallback merge");

    var restoredReset = zeroReset.AddDays(7);
    Equal(
        true,
        zeroProfile.Merge(
            [TokenObservation(
                "codex.zero-profile",
                "7d",
                TimeSpan.FromDays(7),
                now.AddMinutes(-2),
                2,
                restoredReset,
                CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
                0)],
            now),
        "new reset clears fallback and restores profile priority");
    zeroProfile.Merge(
        [TokenObservation(
            "codex.zero-profile",
            "7d",
            TimeSpan.FromDays(7),
            now.AddMinutes(-1),
            4,
            restoredReset,
            CodexQuotaTokenTracker.ProfileLifetimeSourceKey,
            100)],
        now);
    Equal(
        5_000L,
        zeroProfile.GetSummary("codex.zero-profile", "7d", TimeSpan.FromDays(7).Ticks)!.CurrentCapacityTokens,
        "profile source resumes after a new reset");
    Equal(
        false,
        zeroProfile.Merge(
            [zeroFallbackSecond with
            {
                CapturedAt = now,
                ResetsAt = zeroReset,
                SourceKey = CodexQuotaTokenTracker.ToRolloutFallbackSourceKey(zeroFallbackSecond.SourceKey),
            }],
            now),
        "old fallback reset cannot displace restored profile cycle");

    var isolatedFallback = new CodexQuotaTokenTracker();
    isolatedFallback.Merge(
        [
            TokenObservation("codex.account-a", "5h", TimeSpan.FromHours(5), now.AddMinutes(-20), 10, reset, "rollout-fallback:a5", 0),
            TokenObservation("codex.account-a", "5h", TimeSpan.FromHours(5), now.AddMinutes(-10), 12, reset, "rollout-fallback:a5", 20),
            TokenObservation("codex.account-a", "7d", TimeSpan.FromDays(7), now.AddMinutes(-20), 10, reset, "rollout-fallback:a7", 0),
            TokenObservation("codex.account-a", "7d", TimeSpan.FromDays(7), now.AddMinutes(-10), 12, reset, "rollout-fallback:a7", 20),
            TokenObservation("codex.account-b", "5h", TimeSpan.FromHours(5), now.AddMinutes(-20), 20, reset, "rollout-fallback:b5", 0),
            TokenObservation("codex.account-b", "5h", TimeSpan.FromHours(5), now.AddMinutes(-10), 22, reset, "rollout-fallback:b5", 20),
        ],
        now);
    Equal(1_000L, isolatedFallback.GetSummary("codex.account-a", "5h", TimeSpan.FromHours(5).Ticks)!.CurrentCapacityTokens, "fallback keeps account 5h series isolated");
    Equal(1_000L, isolatedFallback.GetSummary("codex.account-a", "7d", TimeSpan.FromDays(7).Ticks)!.CurrentCapacityTokens, "fallback keeps account 7d series isolated");
    Equal(1_000L, isolatedFallback.GetSummary("codex.account-b", "5h", TimeSpan.FromHours(5).Ticks)!.CurrentCapacityTokens, "fallback keeps second account isolated");
    Equal(null, isolatedFallback.GetSummary("codex.account-b", "7d", TimeSpan.FromDays(7).Ticks), "unobserved account window stays empty");

    var secondReset = reset.AddDays(7);
    tracker.Merge(
        [
            TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-10), 2, secondReset, "source-a", 0),
            TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-9), 3, secondReset, "source-a", 10),
            TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-8), 4, secondReset, "source-a", 20),
        ],
        now);
    current = tracker.GetSummary("codex.token", "7d", TimeSpan.FromDays(7).Ticks);
    Equal(1000L, current!.CurrentCapacityTokens, "new reset starts a fresh current cycle");
    Equal(1, current.CompletedCycleCount, "qualified reset enters history");
    Equal(1333L, current.MaxCapacityTokens, "history keeps the highest completed capacity");
    Equal(1333d, current.AverageCapacityTokens, "average uses completed cycles only");

    var thirdReset = secondReset.AddDays(7);
    tracker.Merge(
        [
            TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-3), 1, thirdReset, "source-a", 0),
            TokenObservation("codex.token", "7d", TimeSpan.FromDays(7), now.AddMinutes(-2), 2, thirdReset, "source-a", 1),
        ],
        now);
    current = tracker.GetSummary("codex.token", "7d", TimeSpan.FromDays(7).Ticks);
    Equal(1, current!.CompletedCycleCount, "short reset cycle is excluded from history");
    Equal(100L, current.CurrentCapacityTokens, "one-point current estimate is available");

    var otherSeries = new CodexQuotaTokenTracker();
    otherSeries.Merge(
        [
            TokenObservation("codex.other", "5h", TimeSpan.FromHours(5), now.AddMinutes(-2), 1, reset, "source-b", 0),
            TokenObservation("codex.other", "5h", TimeSpan.FromHours(5), now.AddMinutes(-1), 2, reset, "source-b", 10),
        ],
        now);
    Equal(null, otherSeries.GetSummary("codex.token", "7d", TimeSpan.FromDays(7).Ticks), "series keys stay isolated");

    var sharedReset = now.AddDays(4);
    var multiSource = CodexRolloutQuotaImporter.Match(
        [
            new CodexRolloutRateLimitEvent(
                now.AddMinutes(-30),
                null,
                new CodexRolloutRateLimitWindow(10, 10080, sharedReset),
                100,
                "source-a"),
            new CodexRolloutRateLimitEvent(
                now.AddMinutes(-25),
                null,
                new CodexRolloutRateLimitWindow(10.5, 10080, sharedReset),
                200,
                "source-b"),
            new CodexRolloutRateLimitEvent(
                now.AddMinutes(-10),
                null,
                new CodexRolloutRateLimitWindow(11, 10080, sharedReset),
                130,
                "source-a"),
            new CodexRolloutRateLimitEvent(
                now.AddMinutes(-5),
                null,
                new CodexRolloutRateLimitWindow(11, 10080, sharedReset),
                240,
                "source-b"),
        ],
        RolloutSnapshot(
            now,
            RolloutCard(
                "codex.multi-source",
                now,
                new QuotaWindow("7d", 11, sharedReset, TimeSpan.FromDays(7)))),
        now);
    Equal(1, multiSource.AcceptedChains, "parallel sessions share one logical quota ownership chain");
    Equal(4, multiSource.Observations.Count, "parallel session token cursors survive quota-chain normalization");
    Equal(2, multiSource.Observations.Select(item => item.SourceKey).Distinct().Count(), "parallel source keys remain isolated");

    var dataDirectory = Path.Combine(Path.GetTempPath(), $"wmt-codex-token-history-{Guid.NewGuid():N}");
    try
    {
        var store = new AppSettingsStore(dataDirectory);
        store.SaveCodexQuotaTokenHistory(tracker.Export());
        var loaded = store.LoadCodexQuotaTokenHistory();
        var resumed = new CodexQuotaTokenTracker(loaded)
            .GetSummary("codex.token", "7d", TimeSpan.FromDays(7).Ticks);
        Equal(100L, resumed!.CurrentCapacityTokens, "history load preserves current source cursor");
        Equal(1, resumed.CompletedCycleCount, "history load preserves completed aggregates");
        Equal(true, File.Exists(store.CodexQuotaTokenHistoryPath), "token history uses an independent file");

        store.SaveCodexQuotaTokenHistory(profilePositive.Export());
        var resumedAdaptive = new CodexQuotaTokenTracker(store.LoadCodexQuotaTokenHistory())
            .GetSummary("codex.priority", "7d", TimeSpan.FromDays(7).Ticks);
        Equal(1_000L, resumedAdaptive!.CurrentCapacityTokens, "adaptive source state survives history reload");

        File.WriteAllText(store.CodexQuotaTokenHistoryPath, "{\"schemaVersion\":99}");
        Equal(0, store.LoadCodexQuotaTokenHistory().Series.Count, "unknown token history schema fails closed");
        Equal(true, File.Exists(store.CodexQuotaTokenHistoryPath + ".corrupt.bak"), "invalid token history is preserved");
    }
    finally
    {
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
    }

    var fixtureDirectory = Path.Combine(Path.GetTempPath(), $"wmt-codex-token-import-{Guid.NewGuid():N}");
    var sessions = Path.Combine(fixtureDirectory, "sessions");
    Directory.CreateDirectory(sessions);
    try
    {
        var fixtureReset = now.AddDays(4);
        var path = Path.Combine(sessions, "anonymous-source.jsonl");
        File.WriteAllLines(
            path,
            [
                RolloutTokenJsonLine(
                    new CodexRolloutRateLimitEvent(
                        now.AddMinutes(-30),
                        null,
                        new CodexRolloutRateLimitWindow(10, 10080, fixtureReset)),
                    100),
                RolloutTokenJsonLine(
                    new CodexRolloutRateLimitEvent(
                        now,
                        null,
                        new CodexRolloutRateLimitWindow(11, 10080, fixtureReset)),
                    130),
                RolloutTokenJsonLine(
                    new CodexRolloutRateLimitEvent(
                        now,
                        null,
                        new CodexRolloutRateLimitWindow(11, 10080, fixtureReset)),
                    140),
            ]);
        File.SetLastWriteTimeUtc(path, now.UtcDateTime);
        var result = CodexRolloutQuotaImporter.Import(
            fixtureDirectory,
            RolloutSnapshot(
                now,
                RolloutCard(
                    "codex.token-fixture",
                    now,
                    new QuotaWindow("7d", 11, fixtureReset, TimeSpan.FromDays(7)))),
            now);
        Equal(2, result.Observations.Count, "unique ownership emits token observations");
        Equal(140L, result.Observations[^1].TotalTokens, "same timestamp keeps the larger cumulative token event");
        var expectedSource = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(path))))
            .ToLowerInvariant();
        Equal(expectedSource, result.Observations[0].SourceKey, "source key hashes only the rollout filename");
    }
    finally
    {
        if (Directory.Exists(fixtureDirectory)) Directory.Delete(fixtureDirectory, true);
    }
}

static void TestCodexTokenUsageAggregation()
{
    var floorTime = DateTimeOffset.Parse("2026-08-24T12:00:00Z");
    var localSummary = new CodexTokenUsageSummary(100, 500, 2, floorTime, 400, 300, 80, 60);
    var flooredSummary = CodexTokenUsageSummary.ApplyCumulativeFloor(localSummary, 1_000, floorTime)!;
    Equal(1_000L, flooredSummary.LocalTokens, "account lifetime raises the cumulative local floor");
    Equal(100L, flooredSummary.TodayTokens, "account lifetime does not change today's local tokens");
    Equal(75d, flooredSummary.TotalCacheHitPercent, "account lifetime does not change local cache rates");
    Equal(500L, CodexTokenUsageSummary.ApplyCumulativeFloor(localSummary, 400, floorTime)!.LocalTokens, "local total wins above a lower account lifetime");
    var lifetimeOnlySummary = CodexTokenUsageSummary.ApplyCumulativeFloor(null, 1_000, floorTime)!;
    Equal(1_000L, lifetimeOnlySummary.LocalTokens, "account lifetime remains visible without local logs");
    Equal(0L, lifetimeOnlySummary.TodayTokens, "account-only cumulative starts with zero local today tokens");
    Equal<CodexTokenUsageSummary?>(null, CodexTokenUsageSummary.ApplyCumulativeFloor(null, null, floorTime), "missing lifetime and logs remain unavailable");

    var directory = Path.Combine(Path.GetTempPath(), $"zgstokenbar-token-usage-{Guid.NewGuid():N}");
    var sessions = Path.Combine(directory, "sessions", "2026", "08", "02");
    var archived = Path.Combine(directory, "archived_sessions");
    Directory.CreateDirectory(sessions);
    Directory.CreateDirectory(archived);
    try
    {
        var localClock = DateTime.SpecifyKind(new DateTime(2026, 8, 2, 12, 0, 0), DateTimeKind.Unspecified);
        var now = new DateTimeOffset(localClock, TimeZoneInfo.Local.GetUtcOffset(localClock));
        var todayPath = Path.Combine(sessions, "today.jsonl");
        File.WriteAllText(
            todayPath,
            string.Join('\n',
            [
                TokenSessionMetaJsonLine(now.AddHours(-4)),
                new string('x', CodexTokenUsageReader.MaximumLineBytes + 1),
                "{invalid",
                TokenUsageJsonLine(now.AddHours(-3), 100, 80, 60),
                TokenUsageJsonLine(now.AddHours(-2), 150, 120, 90),
            ]) + "\n");

        var overnightPath = Path.Combine(sessions, "overnight.jsonl");
        File.WriteAllText(
            overnightPath,
            string.Join('\n',
            [
                TokenSessionMetaJsonLine(now.AddHours(-16)),
                TokenUsageJsonLine(now.AddHours(-13), 200, 150, 100),
                TokenUsageJsonLine(now.AddHours(-11), 260, 200, 150),
                TokenUsageJsonLine(now.AddHours(-10), 300, 240, 180),
            ]) + "\n");

        var oldPath = Path.Combine(archived, "old.jsonl");
        var oldContents = string.Join('\n',
        [
            TokenSessionMetaJsonLine(now.AddDays(-2)),
            TokenUsageJsonLine(now.AddDays(-2).AddHours(1), 500, 400, 300),
        ]) + "\n";
        File.WriteAllText(oldPath, oldContents);
        File.WriteAllText(Path.Combine(sessions, "old.jsonl"), oldContents);

        Equal(true, CodexTokenUsageReader.TryParseLine(TokenUsageJsonLine(now, 42, 40, 30), out var parsed), "token event parses");
        Equal(42L, parsed!.TotalTokens, "token event total");
        Equal(40L, parsed.InputTokens, "token event input total");
        Equal(30L, parsed.CachedInputTokens, "token event cached input total");
        Equal(
            true,
            CodexTokenUsageReader.TryParseLine(
                TokenUsageJsonLine(now, 1_042, 940, 830, 42, 40, 30),
                out var inherited),
            "token event parses the last request usage");
        Equal(42L, inherited!.LastTotalTokens, "token event last request total");
        Equal(40L, inherited.LastInputTokens, "token event last request input");
        Equal(30L, inherited.LastCachedInputTokens, "token event last request cached input");
        Equal(true, CodexTokenUsageReader.TryParseLine(TokenUsageJsonLine(now, 42, 20, 21), out var invalidCache), "invalid cache totals do not discard token event");
        Equal<long?>(null, invalidCache!.InputTokens, "invalid cache totals degrade cache stats");
        Equal(false, CodexTokenUsageReader.TryParseLine("{invalid", out _), "malformed token event is ignored");
        Equal(false, CodexTokenUsageReader.TryParseLine(TokenUsageJsonLine(now, -1), out _), "negative token event is ignored");

        var reader = new CodexTokenUsageReader();
        var initial = reader.Refresh(directory, now);
        Equal(true, initial.Changed, "first token index changes");
        Equal(3, initial.Summary!.SessionCount, "active/archive duplicate is counted once");
        Equal(950L, initial.Summary.LocalTokens, "retained local session totals");
        Equal(250L, initial.Summary.TodayTokens, "local-day total includes only post-midnight delta");
        Equal(760L, initial.Summary.InputTokens, "input tokens aggregate across sessions");
        Equal(570L, initial.Summary.CachedInputTokens, "cached input tokens aggregate across sessions");
        Equal(210L, initial.Summary.TodayInputTokens, "today input tokens use the local-day baseline");
        Equal(170L, initial.Summary.TodayCachedInputTokens, "today cached input tokens use the local-day baseline");
        Equal(170d * 100 / 210, initial.Summary.TodayCacheHitPercent, "today cache hit rate uses daily cached over input tokens");
        Equal(75d, initial.Summary.TotalCacheHitPercent, "total cache hit rate uses cached over input tokens");

        File.AppendAllText(overnightPath, TokenUsageJsonLine(now.AddMinutes(-10), 350, 280, 220) + "\n");
        var appended = reader.Refresh(directory, now);
        Equal(1_000L, appended.Summary!.LocalTokens, "append advances local total once");
        Equal(300L, appended.Summary.TodayTokens, "append advances today's total once");
        Equal(800L, appended.Summary.InputTokens, "append advances input tokens once");
        Equal(610L, appended.Summary.CachedInputTokens, "append advances cached input tokens once");
        Equal(250L, appended.Summary.TodayInputTokens, "append advances today input tokens once");
        Equal(210L, appended.Summary.TodayCachedInputTokens, "append advances today cached input tokens once");
        Equal(84d, appended.Summary.TodayCacheHitPercent, "append updates today cache hit rate");
        Equal(76.25d, appended.Summary.TotalCacheHitPercent, "append updates total cache hit rate");

        var forkHome = Path.Combine(directory, "fork-home");
        var forkSessions = Path.Combine(forkHome, "sessions");
        Directory.CreateDirectory(forkSessions);
        var forkPath = Path.Combine(forkSessions, "fork.jsonl");
        File.WriteAllText(
            forkPath,
            string.Join('\n',
            [
                TokenSessionMetaJsonLine(now.AddHours(-2)),
                new string('x', CodexTokenUsageReader.MaximumLineBytes + 1),
                TokenUsageJsonLine(now.AddHours(-1), 1_000_100, 900_080, 850_060, 100, 80, 60),
                TokenUsageJsonLine(now.AddMinutes(-30), 1_000_300, 900_240, 850_180, 200, 160, 120),
            ]) + "\n");
        var forkReader = new CodexTokenUsageReader();
        var forkInitial = forkReader.Refresh(forkHome, now);
        Equal(300L, forkInitial.Summary!.LocalTokens, "fork initial import removes the inherited total baseline");
        Equal(300L, forkInitial.Summary.TodayTokens, "fork initial import removes today's inherited baseline");
        Equal(240L, forkInitial.Summary.InputTokens, "fork initial import removes inherited input");
        Equal(180L, forkInitial.Summary.CachedInputTokens, "fork initial import removes inherited cached input");
        Equal(
            CodexTokenUsageIndex.CurrentAccountingVersion,
            forkInitial.Index.Files.Single().AccountingVersion,
            "fork initial import records the current accounting version");
        File.AppendAllText(
            forkPath,
            TokenUsageJsonLine(now.AddMinutes(-10), 1_000_450, 900_360, 850_270, 150, 120, 90) + "\n");
        var forkAppended = forkReader.Refresh(forkHome, now);
        Equal(450L, forkAppended.Summary!.LocalTokens, "fork append advances the corrected total once");
        Equal(450L, forkAppended.Summary.TodayTokens, "fork append advances the corrected daily total once");
        Equal(360L, forkAppended.Summary.InputTokens, "fork append advances corrected input once");
        Equal(270L, forkAppended.Summary.CachedInputTokens, "fork append advances corrected cache input once");

        var replayHome = Path.Combine(directory, "fork-replay-home");
        var replaySessions = Path.Combine(replayHome, "sessions");
        Directory.CreateDirectory(replaySessions);
        var parentId = "11111111-1111-1111-1111-111111111111";
        var firstChildId = "22222222-2222-2222-2222-222222222222";
        var secondChildId = "33333333-3333-3333-3333-333333333333";
        var nestedChildId = "44444444-4444-4444-4444-444444444444";
        var forkedAt = now.AddHours(-2);
        var parentPath = Path.Combine(
            replaySessions,
            $"rollout-2026-08-02T08-00-00-{parentId}.jsonl");
        File.WriteAllText(
            parentPath,
            string.Join('\n',
            [
                TokenSessionMetaJsonLine(now.AddHours(-4)),
                TokenUsageJsonLine(now.AddHours(-3), 1_000, 800, 600, 100, 80, 60),
                TokenUsageJsonLine(forkedAt.AddMinutes(-1), 1_500, 1_200, 900, 500, 400, 300),
                TokenUsageJsonLine(forkedAt.AddMinutes(20), 1_700, 1_350, 1_000, 200, 150, 100),
            ]) + "\n");
        var firstChildPath = Path.Combine(
            replaySessions,
            $"rollout-2026-08-02T10-00-00-{firstChildId}.jsonl");
        File.WriteAllText(
            firstChildPath,
            string.Join('\n',
            [
                TokenForkSessionMetaJsonLine(forkedAt, firstChildId, parentId),
                TokenUsageJsonLine(forkedAt, 1_000, 800, 600, 100, 80, 60),
                TokenUsageJsonLine(forkedAt.AddMilliseconds(1), 1_500, 1_200, 900, 500, 400, 300),
                TokenInterAgentMetadataJsonLine(forkedAt.AddMilliseconds(2)),
                new string('y', CodexTokenUsageReader.InitialTailBytes + 1),
                TokenUsageJsonLine(forkedAt.AddMinutes(10), 1_600, 1_300, 980, 100, 100, 80),
                TokenUsageJsonLine(forkedAt.AddMinutes(30), 1_750, 1_400, 1_050, 150, 100, 70),
            ]) + "\n");
        var secondForkAt = forkedAt.AddMinutes(25);
        File.WriteAllText(
            Path.Combine(
                replaySessions,
                $"rollout-2026-08-02T10-25-00-{secondChildId}.jsonl"),
            string.Join('\n',
            [
                TokenForkSessionMetaJsonLine(secondForkAt, secondChildId, parentId),
                TokenUsageJsonLine(secondForkAt, 1_700, 1_350, 1_000, 200, 150, 100),
                TokenUsageJsonLine(secondForkAt.AddMinutes(10), 1_800, 1_430, 1_060, 100, 80, 60),
            ]) + "\n");
        var nestedForkAt = forkedAt.AddMinutes(15);
        File.WriteAllText(
            Path.Combine(
                replaySessions,
                $"rollout-2026-08-02T10-15-00-{nestedChildId}.jsonl"),
            string.Join('\n',
            [
                TokenForkSessionMetaJsonLine(nestedForkAt, nestedChildId, firstChildId),
                TokenUsageJsonLine(nestedForkAt, 1_600, 1_300, 980, 100, 100, 80),
                TokenUsageJsonLine(nestedForkAt.AddMinutes(5), 1_680, 1_360, 1_020, 80, 60, 40),
            ]) + "\n");
        var replayReader = new CodexTokenUsageReader();
        var replay = replayReader.Refresh(replayHome, now);
        Equal(4, replay.Summary!.SessionCount, "parent and replay forks are counted as four producing sessions");
        Equal(1_230L, replay.Summary.LocalTokens, "copied parent histories are removed from sibling and nested forks");
        Equal(1_230L, replay.Summary.TodayTokens, "copied parent histories do not inflate today's total");
        Equal(970L, replay.Summary.InputTokens, "fork baselines correct copied input history");
        Equal(710L, replay.Summary.CachedInputTokens, "fork baselines correct copied cache history");
        Equal(
            true,
            replay.Index.Files.All(file =>
                file.AccountingVersion == CodexTokenUsageIndex.CurrentAccountingVersion),
            "resolved replay forks use the current accounting version");
        File.AppendAllText(
            firstChildPath,
            TokenUsageJsonLine(
                forkedAt.AddMinutes(40),
                1_800,
                1_440,
                1_080,
                50,
                40,
                30) + "\n");
        var replayAppended = replayReader.Refresh(replayHome, now);
        Equal(1_280L, replayAppended.Summary!.LocalTokens, "resolved fork append advances once without replaying parent history");
        Equal(1_010L, replayAppended.Summary.InputTokens, "resolved fork append advances corrected input once");
        Equal(740L, replayAppended.Summary.CachedInputTokens, "resolved fork append advances corrected cache once");

        var missingParentHome = Path.Combine(directory, "missing-fork-parent-home");
        var missingParentSessions = Path.Combine(missingParentHome, "sessions");
        Directory.CreateDirectory(missingParentSessions);
        var missingParentId = "55555555-5555-5555-5555-555555555555";
        var orphanId = "66666666-6666-6666-6666-666666666666";
        File.WriteAllText(
            Path.Combine(
                missingParentSessions,
                $"rollout-2026-08-02T10-00-00-{orphanId}.jsonl"),
            string.Join('\n',
            [
                TokenForkSessionMetaJsonLine(forkedAt, orphanId, missingParentId),
                TokenUsageJsonLine(forkedAt, 10_000, 9_000, 8_000, 100, 80, 60),
                TokenUsageJsonLine(forkedAt.AddMinutes(10), 10_200, 9_160, 8_120, 200, 160, 120),
            ]) + "\n");
        var unresolvedReader = new CodexTokenUsageReader();
        var unresolved = unresolvedReader.Refresh(missingParentHome, now);
        Equal<CodexTokenUsageSummary?>(null, unresolved.Summary, "fork with a missing parent is excluded");
        Equal(0, unresolved.Index.Files.Single().AccountingVersion, "missing-parent fork remains uncorrected");
        Equal(false, unresolved.Index.Files.Single().LegacyLifetimeOnly, "unresolved live fork cannot become legacy lifetime history");
        File.WriteAllText(
            Path.Combine(
                missingParentSessions,
                $"rollout-2026-08-02T08-00-00-{missingParentId}.jsonl"),
            string.Join('\n',
            [
                TokenSessionMetaJsonLine(now.AddHours(-4)),
                TokenUsageJsonLine(forkedAt.AddMinutes(-1), 10_000, 9_000, 8_000, 100, 80, 60),
            ]) + "\n");
        var recoveredFork = unresolvedReader.Refresh(missingParentHome, now);
        Equal(300L, recoveredFork.Summary!.LocalTokens, "missing-parent fork recovers when its parent appears");
        Equal(240L, recoveredFork.Summary.InputTokens, "recovered fork uses the parent input baseline");
        Equal(180L, recoveredFork.Summary.CachedInputTokens, "recovered fork uses the parent cache baseline");

        var rolloverHome = Path.Combine(directory, "rollover-home");
        var rolloverSessions = Path.Combine(rolloverHome, "sessions");
        Directory.CreateDirectory(rolloverSessions);
        var beforeMidnightClock = DateTime.SpecifyKind(
            new DateTime(2026, 8, 2, 23, 55, 0),
            DateTimeKind.Unspecified);
        var afterMidnightClock = DateTime.SpecifyKind(
            new DateTime(2026, 8, 3, 0, 15, 0),
            DateTimeKind.Unspecified);
        var beforeMidnight = new DateTimeOffset(
            beforeMidnightClock,
            TimeZoneInfo.Local.GetUtcOffset(beforeMidnightClock));
        var afterMidnight = new DateTimeOffset(
            afterMidnightClock,
            TimeZoneInfo.Local.GetUtcOffset(afterMidnightClock));
        var rolloverPath = Path.Combine(rolloverSessions, "rollover.jsonl");
        File.WriteAllText(
            rolloverPath,
            TokenUsageJsonLine(beforeMidnight, 1_000_100, 900_080, 850_060, 100, 80, 60) + "\n");
        var rolloverReader = new CodexTokenUsageReader();
        rolloverReader.Refresh(rolloverHome, beforeMidnight);
        File.AppendAllText(
            rolloverPath,
            TokenUsageJsonLine(beforeMidnight.AddMinutes(4), 1_000_150, 900_120, 850_090, 50, 40, 30) + "\n"
            + TokenUsageJsonLine(afterMidnight.AddMinutes(-5), 1_000_200, 900_160, 850_130, 50, 40, 40) + "\n");
        var rollover = rolloverReader.Refresh(rolloverHome, afterMidnight);
        Equal(200L, rollover.Summary!.LocalTokens, "cross-midnight append preserves local total");
        Equal(50L, rollover.Summary.TodayTokens, "cross-midnight append starts a new local day");
        Equal(40L, rollover.Summary.TodayInputTokens, "cross-midnight append resets today input baseline");
        Equal(40L, rollover.Summary.TodayCachedInputTokens, "cross-midnight append resets today cached baseline");
        Equal(100d, rollover.Summary.TodayCacheHitPercent, "cross-midnight today cache hit rate uses only the new day");
        Equal(81.25d, rollover.Summary.TotalCacheHitPercent, "cross-midnight total cache hit rate remains cumulative");

        var replayRolloverHome = Path.Combine(directory, "fork-replay-rollover-home");
        var replayRolloverSessions = Path.Combine(replayRolloverHome, "sessions");
        Directory.CreateDirectory(replayRolloverSessions);
        var rolloverParentId = "77777777-7777-7777-7777-777777777777";
        var rolloverChildId = "88888888-8888-8888-8888-888888888888";
        var rolloverForkAt = beforeMidnight.AddMinutes(-15);
        File.WriteAllText(
            Path.Combine(replayRolloverSessions, $"rollout-parent-{rolloverParentId}.jsonl"),
            string.Join('\n',
            [
                TokenSessionMetaJsonLine(rolloverForkAt.AddMinutes(-5)),
                TokenUsageJsonLine(rolloverForkAt.AddMinutes(-1), 1_000, 800, 600, 100, 80, 60),
            ]) + "\n");
        File.WriteAllText(
            Path.Combine(replayRolloverSessions, $"rollout-child-{rolloverChildId}.jsonl"),
            string.Join('\n',
            [
                TokenForkSessionMetaJsonLine(rolloverForkAt, rolloverChildId, rolloverParentId),
                TokenUsageJsonLine(rolloverForkAt, 1_000, 800, 600, 100, 80, 60),
                TokenInterAgentMetadataJsonLine(rolloverForkAt.AddMilliseconds(1)),
                TokenUsageJsonLine(beforeMidnight, 1_100, 880, 660, 100, 80, 60),
                TokenUsageJsonLine(afterMidnight.AddMinutes(-5), 1_150, 920, 700, 50, 40, 40),
            ]) + "\n");
        var replayRollover = new CodexTokenUsageReader().Refresh(replayRolloverHome, afterMidnight);
        Equal(250L, replayRollover.Summary!.LocalTokens, "fork replay rollover keeps root and child production only");
        Equal(50L, replayRollover.Summary.TodayTokens, "fork replay rollover excludes copied and prior-day tokens");
        Equal(40L, replayRollover.Summary.TodayInputTokens, "fork replay rollover uses the child's prior-day input baseline");
        Equal(40L, replayRollover.Summary.TodayCachedInputTokens, "fork replay rollover uses the child's prior-day cache baseline");

        var missingCacheHome = Path.Combine(directory, "missing-cache-home");
        var missingCacheSessions = Path.Combine(missingCacheHome, "sessions");
        Directory.CreateDirectory(missingCacheSessions);
        File.WriteAllText(
            Path.Combine(missingCacheSessions, "missing.jsonl"),
            TokenUsageJsonLine(now, 10) + "\n");
        var missingCache = new CodexTokenUsageReader().Refresh(missingCacheHome, now);
        Equal<double?>(null, missingCache.Summary!.TodayCacheHitPercent, "missing cache fields hide the today rate");
        Equal<double?>(null, missingCache.Summary.TotalCacheHitPercent, "missing cache fields hide the total rate");

        var partialCacheHome = Path.Combine(directory, "partial-cache-home");
        var partialCacheSessions = Path.Combine(partialCacheHome, "sessions");
        Directory.CreateDirectory(partialCacheSessions);
        File.WriteAllText(
            Path.Combine(partialCacheSessions, "legacy.jsonl"),
            TokenUsageJsonLine(now.AddDays(-1), 10) + "\n");
        File.WriteAllText(
            Path.Combine(partialCacheSessions, "current.jsonl"),
            TokenUsageJsonLine(now, 100, 80, 72) + "\n");
        var partialCache = new CodexTokenUsageReader().Refresh(partialCacheHome, now);
        Equal(90d, partialCache.Summary!.TodayCacheHitPercent, "complete current-day cache counters remain visible beside an older unknown source");
        Equal(90d, partialCache.Summary.TotalCacheHitPercent, "cumulative cache rate uses all cache-capable current sources without one legacy source blanking the result");

        var dataDirectory = Path.Combine(directory, "app-data");
        var store = new AppSettingsStore(dataDirectory);
        store.SaveCodexTokenUsageIndex(appended.Index);
        var indexJson = File.ReadAllText(store.CodexTokenUsageIndexPath);
        Equal(false, indexJson.Contains("today.jsonl", StringComparison.Ordinal), "token index omits source file names");
        Equal(false, indexJson.Contains("overnight", StringComparison.Ordinal), "token index omits session identity");
        var legacyFiles = appended.Index.Files.Select(file => new
        {
            file.Key,
            file.Length,
            file.LastWriteTimeUtcTicks,
            file.HasTokenData,
            file.TotalTokens,
            file.LastTotalTokens,
            file.LatestLocalDate,
            file.LatestDayTokens,
        });
        File.WriteAllText(
            store.CodexTokenUsageIndexPath,
            JsonSerializer.Serialize(
                new { SchemaVersion = 1, Files = legacyFiles },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var migrated = store.LoadCodexTokenUsageIndex();
        Equal(CodexTokenUsageIndex.CurrentSchemaVersion, migrated.SchemaVersion, "v1 token index migrates in memory");
        Equal(true, migrated.Files.All(file => file.InputTokens is null), "v1 cache stats start unknown");
        Equal(true, migrated.Files.All(file => file.AccountingVersion == 0), "v1 token entries require accounting migration");
        var resumed = new CodexTokenUsageReader(migrated).Refresh(directory, now);
        Equal(1_000L, resumed.Summary!.LocalTokens, "persisted index resumes without duplication");
        Equal(300L, resumed.Summary.TodayTokens, "persisted index keeps local-day total");
        Equal(84d, resumed.Summary.TodayCacheHitPercent, "v1 index backfills today cache totals from source logs");
        Equal(76.25d, resumed.Summary.TotalCacheHitPercent, "v1 index backfills total cache values from source logs");
        Equal(
            true,
            resumed.Index.Files.All(file =>
                file.AccountingVersion == CodexTokenUsageIndex.CurrentAccountingVersion),
            "v1 live entries migrate to corrected accounting");

        File.WriteAllText(
            store.CodexTokenUsageIndexPath,
            JsonSerializer.Serialize(
                new { SchemaVersion = 2, Files = legacyFiles },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var migratedV2 = store.LoadCodexTokenUsageIndex();
        Equal(CodexTokenUsageIndex.CurrentSchemaVersion, migratedV2.SchemaVersion, "v2 token index migrates in memory");
        Equal(true, migratedV2.Files.All(file => file.AccountingVersion == 0), "v2 token entries require accounting migration");
        var resumedV2 = new CodexTokenUsageReader(migratedV2).Refresh(directory, now);
        Equal(1_000L, resumedV2.Summary!.LocalTokens, "v2 live entries rebuild without inherited baselines");
        Equal(
            true,
            resumedV2.Index.Files.All(file =>
                file.AccountingVersion == CodexTokenUsageIndex.CurrentAccountingVersion),
            "v2 live entries migrate to corrected accounting");

        File.WriteAllText(
            store.CodexTokenUsageIndexPath,
            JsonSerializer.Serialize(
                new
                {
                    SchemaVersion = 3,
                    Files = appended.Index.Files.Select(file => file with { AccountingVersion = 1 }),
                },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var migratedV3 = store.LoadCodexTokenUsageIndex();
        Equal(CodexTokenUsageIndex.CurrentSchemaVersion, migratedV3.SchemaVersion, "v3 token index migrates in memory");
        Equal(true, migratedV3.Files.All(file => file.AccountingVersion == 1), "v3 entries retain their stale accounting marker");
        var resumedV3 = new CodexTokenUsageReader(migratedV3).Refresh(directory, now);
        Equal(1_000L, resumedV3.Summary!.LocalTokens, "v3 live entries rebuild with fork-history accounting");
        Equal(
            true,
            resumedV3.Index.Files.All(file =>
                file.AccountingVersion == CodexTokenUsageIndex.CurrentAccountingVersion),
            "v3 live entries migrate to fork-history accounting");

        File.WriteAllText(
            store.CodexTokenUsageIndexPath,
            JsonSerializer.Serialize(
                new { SchemaVersion = 4, appended.Index.Files },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var migratedV4 = store.LoadCodexTokenUsageIndex();
        Equal(CodexTokenUsageIndex.CurrentSchemaVersion, migratedV4.SchemaVersion, "v4 token index migrates in memory");
        Equal(true, migratedV4.Files.All(file => file.LegacyLifetimeOnly is null), "v4 entries await lifetime classification");

        File.Delete(todayPath);
        var deleted = reader.Refresh(directory, now);
        Equal(1_000L, deleted.Summary!.LocalTokens, "deleted local logs preserve the observed total");
        Equal(300L, deleted.Summary.TodayTokens, "deleted local logs preserve today's observed total");
        Equal(120d * 100 / 130, deleted.Summary.TodayCacheHitPercent, "deleted logs leave the today rate scoped to current sources");
        Equal(520d * 100 / 680, deleted.Summary.TotalCacheHitPercent, "deleted logs leave the total rate scoped to current sources");
        var resumedAfterDelete = new CodexTokenUsageReader(deleted.Index).Refresh(directory, now);
        Equal(1_000L, resumedAfterDelete.Summary!.LocalTokens, "persisted index preserves deleted session totals");
        Equal(300L, resumedAfterDelete.Summary.TodayTokens, "persisted index preserves deleted daily totals");
        Equal(120d * 100 / 130, resumedAfterDelete.Summary.TodayCacheHitPercent, "persisted index keeps the today rate scoped to current sources");
        Equal(520d * 100 / 680, resumedAfterDelete.Summary.TotalCacheHitPercent, "persisted index keeps the total rate scoped to current sources");

        var legacyDeletedEntry = new CodexTokenUsageIndex
        {
            Files = deleted.Index.Files.Select(file => file.TotalTokens == 150
                ? file with
                {
                    InputTokens = null,
                    CachedInputTokens = null,
                    LastInputTokens = null,
                    LastCachedInputTokens = null,
                    LatestDayInputTokens = null,
                    LatestDayCachedInputTokens = null,
                    AccountingVersion = 0,
                }
                : file).ToList(),
        };
        var resumedWithUnknownDeletedStats = new CodexTokenUsageReader(legacyDeletedEntry)
            .Refresh(directory, now);
        Equal(1_000L, resumedWithUnknownDeletedStats.Summary!.LocalTokens, "unrecoverable legacy deleted total remains in lifetime history");
        Equal(150L, resumedWithUnknownDeletedStats.Summary.TodayTokens, "unrecoverable legacy deleted daily total is excluded");
        Equal(3, resumedWithUnknownDeletedStats.Summary.SessionCount, "unrecoverable legacy deleted session remains in lifetime history");
        Equal(
            0,
            resumedWithUnknownDeletedStats.Index.Files.Single(file => file.TotalTokens == 150).AccountingVersion,
            "unrecoverable legacy deleted entry remains marked uncorrected");
        Equal(
            true,
            resumedWithUnknownDeletedStats.Index.Files.Single(file => file.TotalTokens == 150).LegacyLifetimeOnly,
            "unrecoverable legacy deleted entry is marked lifetime-only");
        store.SaveCodexTokenUsageIndex(resumedWithUnknownDeletedStats.Index);
        var reloadedLegacyLifetime = store.LoadCodexTokenUsageIndex();
        Equal(
            true,
            reloadedLegacyLifetime.Files.Single(file => file.TotalTokens == 150).LegacyLifetimeOnly,
            "lifetime-only classification persists across reload");
        Equal(
            1_000L,
            new CodexTokenUsageReader(reloadedLegacyLifetime).Refresh(directory, now).Summary!.LocalTokens,
            "persisted lifetime-only history remains in the cumulative total");
        Equal(
            120d * 100 / 130,
            resumedWithUnknownDeletedStats.Summary.TodayCacheHitPercent,
            "unrecoverable deleted history does not hide the today cache rate");
        Equal(
            520d * 100 / 680,
            resumedWithUnknownDeletedStats.Summary.TotalCacheHitPercent,
            "unrecoverable deleted history does not hide the total cache rate");

        File.WriteAllText(store.CodexTokenUsageIndexPath, "{invalid");
        Equal(0, store.LoadCodexTokenUsageIndex().Files.Count, "corrupt token index rebuilds empty");
        Equal(true, File.Exists(store.CodexTokenUsageIndexPath + ".corrupt.bak"), "corrupt token index is preserved");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestCodexRolloutFixtureCli()
{
    var now = DateTimeOffset.Parse("2026-07-31T12:00:00Z");
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-rollout-cli-{Guid.NewGuid():N}");
    var sessions = Path.Combine(directory, "sessions");
    Directory.CreateDirectory(sessions);
    try
    {
        var resetAt = now.AddDays(4);
        var card = RolloutCard(
            "codex.fixture",
            now,
            new QuotaWindow("7d", 11, resetAt, TimeSpan.FromDays(7)));
        var snapshot = RolloutSnapshot(now, card);
        File.WriteAllText(
            Path.Combine(directory, "live-snapshot.json"),
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
        var rolloutPath = Path.Combine(sessions, "fixture.jsonl");
        File.WriteAllLines(
            rolloutPath,
            [
                RolloutJsonLine(new CodexRolloutRateLimitEvent(
                    now.AddMinutes(-30),
                    null,
                    new CodexRolloutRateLimitWindow(10, 10080, resetAt))),
                RolloutJsonLine(new CodexRolloutRateLimitEvent(
                    now,
                    null,
                    new CodexRolloutRateLimitWindow(11, 10080, resetAt))),
            ]);
        File.SetLastWriteTimeUtc(rolloutPath, now.UtcDateTime);

        using var output = new StringWriter();
        using var error = new StringWriter();
        Equal(0, RunQuotaImportFixture(directory, output, error), "fixture CLI succeeds");
        Equal(string.Empty, error.ToString(), "fixture CLI keeps stderr empty");
        using var document = JsonDocument.Parse(output.ToString());
        Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32(), "fixture CLI schema");
        Equal(2, document.RootElement.GetProperty("sampleCount").GetInt32(), "fixture CLI sample count");
        Equal(1, document.RootElement.GetProperty("acceptedChains").GetInt32(), "fixture CLI matched chain count");
        Equal(1, document.RootElement.GetProperty("cards").GetArrayLength(), "fixture CLI anonymizes each card");
        Equal(
            false,
            output.ToString().Contains("codex.fixture", StringComparison.Ordinal),
            "fixture CLI does not print card keys");

        using var missingOutput = new StringWriter();
        using var missingError = new StringWriter();
        Equal(
            1,
            RunQuotaImportFixture(Path.Combine(directory, "missing"), missingOutput, missingError),
            "fixture CLI rejects missing directory");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static int RunQuotaImportFixture(string fixtureDirectory, TextWriter output, TextWriter error)
{
    try
    {
        var directory = Path.GetFullPath(fixtureDirectory);
        var snapshotPath = Path.Combine(directory, "live-snapshot.json");
        if (!Directory.Exists(directory) || !File.Exists(snapshotPath))
        {
            error.WriteLine("quota-import-fixture: invalid_fixture");
            return 1;
        }

        var snapshot = JsonSerializer.Deserialize<QuotaSnapshot>(
            File.ReadAllText(snapshotPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (snapshot is null)
        {
            error.WriteLine("quota-import-fixture: invalid_fixture");
            return 1;
        }

        var result = CodexRolloutQuotaImporter.Import(directory, snapshot, snapshot.CapturedAt);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            result.CandidateFiles,
            result.ScannedFiles,
            result.ParsedLines,
            result.AcceptedEvents,
            result.AcceptedChains,
            result.AmbiguousChains,
            result.OversizedLines,
            sampleCount = result.Samples.Count,
            cards = result.Samples
                .GroupBy(sample => sample.CardKey, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select((group, index) => new
                {
                    fixtureCard = index + 1,
                    sampleCount = group.Count(),
                }),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
    {
        error.WriteLine("quota-import-fixture: invalid_fixture");
        return 1;
    }
}

static QuotaCard RolloutCard(
    string key,
    DateTimeOffset capturedAt,
    params QuotaWindow[] windows) => new(
    key,
    ProviderKind.Codex,
    key,
    "Pro",
    "#10a37f",
    true,
    windows)
{
    CapturedAt = capturedAt,
};

static QuotaSnapshot RolloutSnapshot(DateTimeOffset capturedAt, params QuotaCard[] cards) => new(
    cards,
    [new ProviderHealth(ProviderKind.Codex, true, "Current", ProviderHealthCode.Current)],
    capturedAt);

static CodexRolloutRateLimitEvent RolloutEvent(
    DateTimeOffset capturedAt,
    double primaryUsed,
    DateTimeOffset primaryReset,
    double secondaryUsed,
    DateTimeOffset secondaryReset) => new(
    capturedAt,
    new CodexRolloutRateLimitWindow(primaryUsed, 300, primaryReset),
    new CodexRolloutRateLimitWindow(secondaryUsed, 10080, secondaryReset));

static string RolloutJsonLine(CodexRolloutRateLimitEvent item) => JsonSerializer.Serialize(new
{
    timestamp = item.CapturedAt,
    type = "event_msg",
    payload = new
    {
        type = "token_count",
        rate_limits = new
        {
            primary = RolloutWindowPayload(item.Primary),
            secondary = RolloutWindowPayload(item.Secondary),
        },
    },
});

static string RolloutTokenJsonLine(CodexRolloutRateLimitEvent item, long totalTokens) => JsonSerializer.Serialize(new
{
    timestamp = item.CapturedAt,
    type = "event_msg",
    payload = new
    {
        type = "token_count",
        info = new
        {
            total_token_usage = new { total_tokens = totalTokens },
        },
        rate_limits = new
        {
            primary = RolloutWindowPayload(item.Primary),
            secondary = RolloutWindowPayload(item.Secondary),
        },
    },
});

static CodexQuotaTokenObservation TokenObservation(
    string cardKey,
    string windowLabel,
    TimeSpan duration,
    DateTimeOffset capturedAt,
    double usedPercent,
    DateTimeOffset reset,
    string sourceKey,
    long totalTokens) => new(
        cardKey,
        windowLabel,
        duration.Ticks,
        capturedAt,
        usedPercent,
        reset,
        sourceKey,
        totalTokens);

static string TokenSessionMetaJsonLine(DateTimeOffset timestamp) => JsonSerializer.Serialize(new
{
    timestamp = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    type = "session_meta",
    payload = new { type = "session_meta" },
});

static string TokenForkSessionMetaJsonLine(
    DateTimeOffset timestamp,
    string sessionId,
    string parentThreadId) => JsonSerializer.Serialize(new
{
    timestamp = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    type = "session_meta",
    payload = new
    {
        id = sessionId,
        forked_from_id = parentThreadId,
        parent_thread_id = parentThreadId,
        timestamp = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        source = new
        {
            subagent = new
            {
                thread_spawn = new { parent_thread_id = parentThreadId },
            },
        },
    },
});

static string TokenInterAgentMetadataJsonLine(DateTimeOffset timestamp) => JsonSerializer.Serialize(new
{
    timestamp = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    type = "inter_agent_communication_metadata",
    payload = new { trigger_turn = true },
});

static string TokenUsageJsonLine(
    DateTimeOffset timestamp,
    long totalTokens,
    long? inputTokens = null,
    long? cachedInputTokens = null,
    long? lastTotalTokens = null,
    long? lastInputTokens = null,
    long? lastCachedInputTokens = null) => JsonSerializer.Serialize(new
{
    timestamp = timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    type = "event_msg",
    payload = new
    {
        type = "token_count",
        info = new
        {
            total_token_usage = new
            {
                total_tokens = totalTokens,
                input_tokens = inputTokens,
                cached_input_tokens = cachedInputTokens,
            },
            last_token_usage = lastTotalTokens is null
                ? null
                : new
                {
                    total_tokens = lastTotalTokens,
                    input_tokens = lastInputTokens,
                    cached_input_tokens = lastCachedInputTokens,
                },
        },
    },
});

static object? RolloutWindowPayload(CodexRolloutRateLimitWindow? window) => window is null
    ? null
    : new
    {
        used_percent = window.UsedPercent,
        window_minutes = window.WindowMinutes,
        resets_at = window.ResetsAt?.ToUnixTimeSeconds(),
    };

static void TestRadarParser()
{
    const string summary = """
        {
          "monitored_at": "2026-07-19T12:00:00+08:00",
          "window": {
            "open": false,
            "closed_at": "2026-08-01T11:32:37+08:00",
            "target_at": "2026-08-03T09:00:00+08:00",
            "scope": "周一表演式重置预告",
            "source_url": "https://x.com/example/status/123"
          },
          "model_iq": {
            "updated_at": "2026-07-19T11:30:00+08:00",
            "latest": {
              "date": "2026-07-19",
              "model": "gpt-5.6-sol",
              "reasoning_effort": "max",
              "score": 106.3,
              "status": "green",
              "passed": 79,
              "valid_tasks": 112,
              "average_cost_usd": 10.2,
              "average_task_seconds": 2340,
              "average_task_time_human": "39m"
            },
            "recent_days": [
              { "date": "2026-07-19T08:00:00+08:00", "score": 100 },
              { "date": "2026-07-19T10:00:00+08:00", "score": 104 },
              { "date": "invalid", "score": 999 }
            ],
            "comparisons": {
              "terra": {
                "label": "GPT-5.6 Terra high",
                "latest": { "date": "2026-07-19", "model": "gpt-5.6-terra", "reasoning_effort": "high", "score": 69.1 },
                "recent_days": [
                  { "date": "2026-07-19T08:00:00+08:00", "score": 68 },
                  { "date": "2026-07-19T10:00:00+08:00", "score": 70 }
                ]
              },
              "totals": {
                "latest": {
                  "date": "2026-07-19",
                  "model": "totals-only",
                  "score": 60,
                  "cost_usd": 99,
                  "wall_time_human": "3h"
                }
              },
              "stale": {
                "latest": { "date": "2026-07-18", "model": "old-model", "score": 55 }
              }
            }
          }
        }
        """;
    var snapshot = RadarParser.Parse(summary, DateTimeOffset.Parse("2026-07-19T04:00:00Z"));
    Equal(ProviderKind.Codex, snapshot.Provider, "Radar provider");
    Equal("model_iq:2026-07-19", snapshot.EventId, "Radar event id");
    Equal("gpt-5.6-sol", snapshot.Primary.Model, "primary model");
    Equal("max", snapshot.Primary.ReasoningEffort, "primary effort");
    Equal("green", snapshot.Primary.Status, "primary status");
    Equal(2340d, snapshot.Primary.AverageTaskSeconds, "primary average task seconds");
    Equal(2, snapshot.Primary.IqHistory.Count, "primary recent IQ history");
    Equal(104d, snapshot.Primary.IqHistory[1].Score, "primary history preserves score order");
    Equal(false, snapshot.ResetWindow?.Open, "closed global reset window");
    Equal(
        DateTimeOffset.Parse("2026-08-01T11:32:37+08:00"),
        snapshot.ResetWindow?.ClosedAt,
        "global reset close time");
    Equal(
        "https://x.com/example/status/123",
        snapshot.ResetWindow?.SourceUrl,
        "global reset source");
    Equal(
        DateTimeOffset.Parse("2026-08-03T09:00:00+08:00"),
        snapshot.ResetWindow?.TargetAt,
        "global reset target time");
    Equal("周一表演式重置预告", snapshot.ResetWindow?.Scope, "global reset target scope");
    Equal(2, snapshot.Comparisons.Count, "only same-date comparison rows");
    Equal("GPT-5.6 Terra high", snapshot.Comparisons[0].Label, "comparison label");
    Equal(2, snapshot.Comparisons[0].IqHistory.Count, "comparison recent IQ history");
    var totalsOnly = snapshot.Comparisons.Single(model => model.Model == "totals-only");
    Equal<double?>(null, totalsOnly.CostUsd, "total cost is not displayed as per-task cost");
    Equal<string?>(null, totalsOnly.WallTime, "total wall time is not displayed as per-task average");

    var pageTarget = DateTimeOffset.Parse("2030-04-05T06:07:08+08:00");
    Equal(
        pageTarget,
        RadarHomePageParser.ParseResetTarget("""
            <div data-window-closes-at="2099-01-01T00:00:00Z"></div>
            <div data-window-closes-at='2030-04-05T06:07:08&#43;08:00' data-window-clock></div>
            """),
        "Radar homepage parser reads only the marked reset clock target");
    Equal<DateTimeOffset?>(
        null,
        RadarHomePageParser.ParseResetTarget("<div data-window-closes-at='2030-04-05T06:07:08+08:00'></div>"),
        "unmarked homepage dates cannot become a reset target");
    Equal<DateTimeOffset?>(
        null,
        RadarHomePageParser.ParseResetTarget("<div data-window-clock data-window-closes-at='not-a-date'></div>"),
        "malformed homepage clock target is ignored");

    var extreme = RadarParser.Parse("""
        {
          "model_iq": {
            "latest": {
              "date": "2026-07-19",
              "model": "extreme",
              "score": 1e999,
              "passed": 1e999,
              "valid_tasks": 9223372036854775808,
              "average_cost_usd": -1,
              "average_task_seconds": 1e999
            }
          }
        }
        """, DateTimeOffset.Parse("2026-07-19T04:00:00Z"));
    Equal<double?>(null, extreme.Primary.Score, "non-finite Radar score is ignored");
    Equal<long?>(null, extreme.Primary.Passed, "non-finite Radar count is ignored");
    Equal<long?>(null, extreme.Primary.ValidTasks, "out-of-range Radar count is ignored");
    Equal<double?>(null, extreme.Primary.CostUsd, "negative Radar cost is ignored");
    Equal<double?>(null, extreme.Primary.AverageTaskSeconds, "non-finite Radar duration is ignored");

    var comparisonRows = string.Join(
        ",",
        Enumerable.Range(0, 15).Select(index => $$"""
            "row_{{index}}": {
              "label": "row {{index}}",
              "latest": {
                "date": "2026-07-30",
                "model": "{{(index == 13 ? "gpt-5.6-sol" : $"test-model-{index}")}}",
                "reasoning_effort": "{{(index == 13 ? "ultra" : $"level-{index}")}}",
                "score": {{100 - index}}
              }
            }
            """));
    var expandedSummary = $$"""
        {
          "model_iq": {
            "latest": {
              "date": "2026-07-30",
              "model": "gpt-5.6-sol",
              "reasoning_effort": "max",
              "score": 100
            },
            "comparisons": {
              {{comparisonRows}}
            }
          }
        }
        """;
    var expanded = RadarParser.Parse(expandedSummary, DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
    Equal<RadarResetWindow?>(null, expanded.ResetWindow, "missing reset window remains absent");
    Equal(15, expanded.Comparisons.Count, "parser preserves the complete dynamic comparison set");
    Equal(true, expanded.Comparisons.Any(model => model.ReasoningEffort == "ultra"), "trailing Ultra comparison is preserved");
    Equal(true, expanded.Comparisons.Any(model => model.Model == "test-model-14"), "future comparison is not dropped by source order");

    var oversizedComparisonRows = string.Join(
        ",",
        Enumerable.Range(0, 200).Select(index => $$"""
            "row_{{index}}": {
              "latest": {
                "date": "2026-07-30",
                "model": "oversized-model-{{index}}",
                "reasoning_effort": "level-{{index}}",
                "score": {{100 - index / 10d}}
              }
            }
            """));
    var oversized = RadarParser.Parse($$"""
        {
          "model_iq": {
            "latest": {
              "date": "2026-07-30",
              "model": "gpt-5.6-sol",
              "reasoning_effort": "max",
              "score": 100
            },
            "comparisons": { {{oversizedComparisonRows}} }
          }
        }
        """, DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
    Equal(
        RadarSnapshotLimits.MaxComparisonModels,
        oversized.Comparisons.Count,
        "dynamic comparison parsing has a generous persistence safety bound");
}

static void TestRadarResetCountdownSupplement()
{
    var now = DateTimeOffset.Parse("2030-04-04T20:00:00Z");
    var firstTarget = DateTimeOffset.Parse("2030-04-05T06:00:00+08:00");
    using var handler = new RadarResetCountdownRequestHandler
    {
        HomeTarget = firstTarget,
        OpenedAt = DateTimeOffset.Parse("2030-04-04T14:00:00+08:00"),
    };
    using var service = new RadarService(handler, () => now);

    var first = service.FetchAsync().GetAwaiter().GetResult();
    Equal<DateTimeOffset?>(firstTarget, first.ResetWindow?.TargetAt, "homepage clock enriches an open reset window without a JSON target");
    Equal(1, handler.PageCalls, "open reset window fetches the homepage clock once");
    Equal(true, handler.PageAcceptsHtml, "homepage clock requests HTML explicitly");
    Equal(true, handler.PageSawNoCache, "homepage clock revalidates the public page");
    Equal(true, handler.PageHasProductUserAgent, "homepage clock identifies the product");
    Equal(false, handler.HasAuthorization, "homepage clock sends no authorization");
    Equal(false, handler.HasCookie, "homepage clock sends no cookie");

    service.FetchAsync().GetAwaiter().GetResult();
    Equal(1, handler.PageCalls, "homepage clock target uses the existing ten-minute supplemental cache");

    var secondTarget = firstTarget.AddHours(3);
    handler.OpenedAt = handler.OpenedAt!.Value.AddMinutes(1);
    handler.HomeTarget = secondTarget;
    now = now.AddMinutes(1);
    var changedWindow = service.FetchAsync().GetAwaiter().GetResult();
    Equal<DateTimeOffset?>(secondTarget, changedWindow.ResetWindow?.TargetAt, "new reset-window identity cannot reuse the previous target");
    Equal(2, handler.PageCalls, "new reset-window identity refreshes the homepage clock immediately");

    var jsonTarget = firstTarget.AddHours(6);
    using var jsonHandler = new RadarResetCountdownRequestHandler
    {
        HomeTarget = firstTarget,
        JsonTarget = jsonTarget,
    };
    using var jsonService = new RadarService(jsonHandler, () => now);
    var fromJson = jsonService.FetchAsync().GetAwaiter().GetResult();
    Equal<DateTimeOffset?>(jsonTarget, fromJson.ResetWindow?.TargetAt, "current.json reset target has priority over homepage metadata");
    Equal(0, jsonHandler.PageCalls, "current.json target avoids the supplemental homepage request");

    using var incompleteHandler = new RadarResetCountdownRequestHandler
    {
        HomeTarget = firstTarget,
        OpenedAt = null,
    };
    using var incompleteService = new RadarService(incompleteHandler, () => now);
    var incompleteFirst = incompleteService.FetchAsync().GetAwaiter().GetResult();
    Equal<DateTimeOffset?>(firstTarget, incompleteFirst.ResetWindow?.TargetAt, "an incomplete window identity still uses the current homepage target");
    incompleteHandler.HomeTarget = secondTarget;
    now = now.AddMinutes(1);
    var incompleteSecond = incompleteService.FetchAsync().GetAwaiter().GetResult();
    Equal<DateTimeOffset?>(secondTarget, incompleteSecond.ResetWindow?.TargetAt, "an incomplete window identity cannot reuse a previous target");
    Equal(2, incompleteHandler.PageCalls, "an incomplete window identity disables cross-refresh target caching");

    using var failingHandler = new RadarResetCountdownRequestHandler { FailPage = true };
    using var failingService = new RadarService(failingHandler, () => now);
    var withoutClock = failingService.FetchAsync().GetAwaiter().GetResult();
    Equal(true, withoutClock.ResetWindow?.Open, "homepage failure preserves the primary open-window snapshot");
    Equal<DateTimeOffset?>(null, withoutClock.ResetWindow?.TargetAt, "homepage failure does not invent a countdown target");
    now = now.AddMinutes(1);
    failingService.FetchAsync().GetAwaiter().GetResult();
    Equal(1, failingHandler.PageCalls, "homepage failure uses the supplemental retry interval");
    now = now.AddMinutes(9);
    failingService.FetchAsync().GetAwaiter().GetResult();
    Equal(2, failingHandler.PageCalls, "homepage failure retries when the supplemental interval expires");
}

static void TestRadarRecommendationParser()
{
    const string insights = """
        {
          "schema": 1,
          "mode": "latest_valid_per_task",
          "generated_at": "2026-08-01T13:39:13+08:00",
          "source_updated_at": "2026-08-01T13:39:08+08:00",
          "recommendations": [
            {
              "key": "daily_development",
              "title": "日常开发",
              "rule": "上游分组规则",
              "items": [
                {
                  "model": "gpt-5.5",
                  "effort": "xhigh",
                  "iq": 91.07,
                  "passed": 68,
                  "samples": 112,
                  "average_cost_usd": 5.870495,
                  "cost_samples": 111,
                  "average_duration_minutes": 23.41,
                  "duration_samples": 112,
                  "combined_cost_index": 7877.209,
                  "trend_48h": [
                    { "timestamp": "2026-07-31T12:00:00+08:00", "iq": 90 },
                    { "timestamp": "2026-08-01T12:00:00+08:00", "iq": 92 }
                  ],
                  "rule": "上游速度位规则",
                  "slot": "speed"
                },
                {
                  "model": "gpt-5.6-sol",
                  "effort": "xhigh",
                  "iq": 96.43,
                  "samples": 112,
                  "average_cost_usd": 6.635433,
                  "average_duration_minutes": 25.2,
                  "slot": "smart"
                }
              ]
            },
            {
              "key": "future_category",
              "title": "未来类别",
              "rule": "未来规则",
              "items": [
                {
                  "model": "future-model",
                  "effort": "adaptive",
                  "iq": 88,
                  "samples": 10
                }
              ]
            },
            {
              "key": "invalid",
              "title": "无有效条目",
              "items": [
                { "model": "", "effort": "max", "iq": 100 }
              ]
            }
          ]
        }
        """;

    var feed = RadarRecommendationsParser.Parse(insights);
    Equal<int?>(1, feed.Schema, "upstream recommendation schema");
    Equal("latest_valid_per_task", feed.Mode, "upstream recommendation mode");
    Equal(DateTimeOffset.Parse("2026-08-01T13:39:13+08:00"), feed.GeneratedAt, "upstream generated time");
    Equal(DateTimeOffset.Parse("2026-08-01T13:39:08+08:00"), feed.SourceUpdatedAt, "upstream source time");
    Equal(2, feed.Groups.Count, "invalid empty groups are rejected");
    Equal("daily_development", feed.Groups[0].Key, "group key order is preserved");
    Equal("日常开发", feed.Groups[0].Title, "group title is preserved");
    Equal("上游分组规则", feed.Groups[0].Rule, "group rule is preserved");
    Equal("gpt-5.5", feed.Groups[0].Items[0].Model.Model, "item order is preserved");
    Equal("speed", feed.Groups[0].Items[0].Slot, "item slot is preserved");
    Equal("上游速度位规则", feed.Groups[0].Items[0].Rule, "item rule is preserved");
    Equal<long?>(111, feed.Groups[0].Items[0].CostSamples, "cost sample count is preserved");
    Equal<long?>(112, feed.Groups[0].Items[0].DurationSamples, "duration sample count is preserved");
    Equal(7877.209, feed.Groups[0].Items[0].CombinedCostIndex, "combined cost index is preserved");
    Equal(1404.6, feed.Groups[0].Items[0].Model.AverageTaskSeconds, "duration is mapped to seconds");
    Equal(2, feed.Groups[0].Items[0].Model.IqHistory.Count, "48-hour IQ trend is preserved");
    Equal("future_category", feed.Groups[1].Key, "unknown group keys remain data-driven");
    Equal("adaptive", feed.Groups[1].Items[0].Model.ReasoningEffort, "unknown efforts remain data-driven");

    var expandedInsights = JsonSerializer.Serialize(new
    {
        recommendations = Enumerable.Range(0, 10).Select(groupIndex => new
        {
            key = $"group_{groupIndex}",
            title = $"Group {groupIndex}",
            items = Enumerable.Range(0, 5).Select(itemIndex => new
            {
                model = $"model-{groupIndex}-{itemIndex}",
                effort = $"effort-{itemIndex}",
            }),
        }),
    });
    var expandedFeed = RadarRecommendationsParser.Parse(expandedInsights);
    Equal(10, expandedFeed.Groups.Count, "recommendation group count follows the upstream feed");
    Equal(5, expandedFeed.Groups[9].Items.Count, "recommendation item count follows the upstream feed");

    var oversizedInsights = JsonSerializer.Serialize(new
    {
        recommendations = Enumerable.Range(0, 40).Select(groupIndex => new
        {
            key = $"oversized_group_{groupIndex}",
            title = $"Oversized Group {groupIndex}",
            items = Enumerable.Range(0, 5).Select(itemIndex => new
            {
                model = $"oversized-model-{groupIndex}-{itemIndex}",
                effort = $"effort-{itemIndex}",
            }),
        }),
    });
    var boundedFeed = RadarRecommendationsParser.Parse(oversizedInsights);
    Equal(
        RadarSnapshotLimits.MaxRecommendationItems,
        boundedFeed.Groups.Sum(group => group.Items.Count),
        "recommendation feed has a total item safety bound");
    Equal(
        true,
        boundedFeed.Groups.Count <= RadarSnapshotLimits.MaxRecommendationGroups,
        "recommendation feed has a group safety bound");
}

static void TestRadarMeasurementParser()
{
    const string measurements = """
        {
          "schema": 2,
          "source_updated_at": "2026-08-02T12:31:38+08:00",
          "points": [
            {
              "model": "gpt-5.6-sol",
              "effort": "max",
              "iq": 100.45,
              "passed": 75,
              "valid_tasks": 112,
              "average_price_usd": 9.57,
              "incomplete_cost_samples": 39,
              "average_minutes": 34.4
            },
            {
              "model": "deepseek-v4-flash",
              "effort": "max",
              "iq": 79.02,
              "passed": 59,
              "valid_tasks": 112,
              "average_price_usd": 0.10,
              "average_minutes": 22.29
            },
            {
              "model": "deepseek-v4-flash",
              "effort": "high",
              "iq": 50.89,
              "passed": 38,
              "valid_tasks": 112,
              "average_price_usd": 0.098,
              "average_minutes": 24.18
            }
          ],
          "history": [
            {
              "at": "2026-08-01T12:00:00+08:00",
              "points": [
                { "model": "deepseek-v4-flash", "effort": "max", "iq": 80 },
                { "model": "deepseek-v4-flash", "effort": "high", "iq": 48 }
              ]
            },
            {
              "at": "2026-08-02T12:00:00+08:00",
              "points": [
                { "model": "deepseek-v4-flash", "effort": "max", "iq": 78 },
                { "model": "deepseek-v4-flash", "effort": "high", "iq": 52 }
              ]
            }
          ]
        }
        """;

    var feed = RadarMeasurementsParser.Parse(measurements);
    Equal(DateTimeOffset.Parse("2026-08-02T12:31:38+08:00"), feed.SourceUpdatedAt, "measurement source time");
    Equal(3, feed.Models.Count, "every measured configuration is parsed");
    var deepSeekMax = feed.Models.Single(model =>
        model.Model == "deepseek-v4-flash" && model.ReasoningEffort == "max");
    Equal(79.02, deepSeekMax.Score, "DeepSeek Max IQ");
    Equal(2, deepSeekMax.IqHistory.Count, "DeepSeek Max history");
    Equal(1337.4, Math.Round(deepSeekMax.AverageTaskSeconds!.Value, 1), "measurement duration is mapped to seconds");
    Equal<long?>(39, feed.Models[0].IncompleteCostSamples, "incomplete cost samples are preserved");

    var baseline = RadarSnapshot("2026-08-02", "gpt-5.6-sol", "max", 99, "green");
    var merged = RadarMeasurementsParser.Merge(baseline, feed);
    Equal(100.45, merged.Primary.Score, "full measurement overlays the primary IQ");
    Equal("green", merged.Primary.Status, "summary status survives measurement overlay");
    Equal(2, merged.Comparisons.Count(model => model.Model == "deepseek-v4-flash"), "both DeepSeek efforts are added");

    var oversizedMeasurements = JsonSerializer.Serialize(new
    {
        points = Enumerable.Range(0, 200).Select(index => new
        {
            model = $"measurement-model-{index}",
            effort = $"effort-{index}",
            iq = 100 - index / 10d,
        }),
    });
    var boundedMeasurements = RadarMeasurementsParser.Parse(oversizedMeasurements);
    Equal(
        RadarSnapshotLimits.MaxTrackedModels,
        boundedMeasurements.Models.Count,
        "measurement parsing has a tracked-model safety bound");
    Equal(
        RadarSnapshotLimits.MaxComparisonModels,
        RadarMeasurementsParser.Merge(baseline, boundedMeasurements).Comparisons.Count,
        "measurement merge reserves one tracked-model slot for the primary");

    var crowdedModels = RadarScenarioFixtureModels()
        .Concat(
        [
            ScenarioRadarModel("deepseek-v4-flash", "max", 79.02, 0.10, 22.29),
            ScenarioRadarModel("deepseek-v4-flash", "high", 50.89, 0.098, 24.18),
            ScenarioRadarModel("gpt-5.6-sol", "low", 72.3, 2.14, 11.3),
            ScenarioRadarModel("gpt-5.6-terra", "low", 37.5, 0.49, 8.6),
            ScenarioRadarModel("gpt-5.6-luna", "medium", 33.5, 0.08, 8.7),
            ScenarioRadarModel("gpt-5.6-luna", "low", 5.4, 0.03, 5.2),
        ])
        .ToArray();
    var crowdedSnapshot = baseline with
    {
        Primary = crowdedModels[0],
        Comparisons = crowdedModels[1..],
    };
    var visible = RadarPresentation.Build(crowdedSnapshot).Rows;
    Equal(crowdedModels.Length, visible.Count, "Radar keeps the complete dynamic model set");
    Equal(2, visible.Count(row => row.Model.Model == "deepseek-v4-flash"), "compact Radar keeps both members of a two-effort family");
    var expectedModelGroups = new (string Model, string?[] Efforts)[]
    {
        ("gpt-5.6-sol", ["ultra", "max", "xhigh", "high", "medium", "low", "low"]),
        ("gpt-5.6-terra", ["max", "xhigh", "high", "medium", "low"]),
        ("gpt-5.6-luna", ["max", "high", "medium", "low"]),
        ("gpt-5.5", ["xhigh", "high"]),
        ("deepseek-v4-flash", ["max", "high"]),
    };
    var actualModelGroups = visible
        .GroupBy(row => row.Model.Model, StringComparer.OrdinalIgnoreCase)
        .Select(group => (Model: group.Key, Efforts: group.Select(row => row.Model.ReasoningEffort).ToArray()))
        .ToArray();
    Equal(expectedModelGroups.Length, actualModelGroups.Length, "Radar keeps distinct model groups");
    for (var groupIndex = 0; groupIndex < expectedModelGroups.Length; groupIndex++)
    {
        Equal(expectedModelGroups[groupIndex].Model, actualModelGroups[groupIndex].Model, "Radar model group order");
        Equal(
            true,
            actualModelGroups[groupIndex].Efforts.SequenceEqual(expectedModelGroups[groupIndex].Efforts),
            $"Radar {expectedModelGroups[groupIndex].Model} effort order");
    }
}

static ProviderRadarSnapshot RadarSnapshot(
    string date,
    string model,
    string effort,
    double? score,
    string status)
{
    var capturedAt = DateTimeOffset.Parse($"{date}T12:00:00Z");
    return new ProviderRadarSnapshot(
        ProviderKind.Codex,
        $"model_iq:{date}",
        capturedAt,
        capturedAt,
        new RadarModel(model, model, effort, score, status, 80, 100, 1.5, 1800, "30m"),
        []);
}

static void TestRadarRequestPrivacy()
{
    using var handler = new RadarRequestHandler { RequireConcurrentSupplementalRequests = true };
    using var service = new RadarService(handler);
    var snapshot = service.FetchAsync().GetAwaiter().GetResult();
    Equal("model_iq:2026-07-19", snapshot.EventId, "Radar request response parsed");
    Equal(3, handler.Calls, "Radar requests primary, full measurement, and recommendation feeds");
    Equal(true, handler.RequestUris.Contains(RadarService.SummaryUri), "Radar uses current.json");
    Equal(true, handler.RequestUris.Contains(RadarService.MeasurementsUri), "Radar uses full intelligence measurements");
    Equal(true, handler.RequestUris.Contains(RadarService.RecommendationsUri), "Radar uses public recommendations");
    Equal(true, handler.AcceptsJson, "Radar requests JSON");
    Equal(true, handler.SawNoCache, "Radar revalidates requested snapshots");
    Equal(true, handler.HasProductUserAgent, "Radar identifies the product");
    Equal(false, handler.HasAuthorization, "Radar sends no authorization");
    Equal(false, handler.HasCookie, "Radar sends no cookie");
    Equal(true, handler.ConcurrentSupplementalRequestsObserved, "Radar fetches supplemental feeds concurrently");

    var feed = snapshot.RecommendationFeed!;
    Equal(4, feed.Groups.Count, "every upstream recommendation group is mapped");
    Equal("daily_development", feed.Groups[0].Key, "upstream group order is preserved");
    Equal("future_category", feed.Groups[3].Key, "unknown upstream groups are preserved");
    Equal("gpt-5.6-luna", feed.Groups[2].Items[0].Model.Model, "upstream item order is preserved");
    Equal("max", feed.Groups[2].Items[0].Model.ReasoningEffort, "Luna recommendation effort");
    Equal(0.457463, feed.Groups[2].Items[0].Model.CostUsd, "Luna recommendation cost");
    Equal(1783.8, feed.Groups[2].Items[0].Model.AverageTaskSeconds, "Luna recommendation duration");
    Equal<long?>(112, feed.Groups[2].Items[0].CostSamples, "upstream cost samples");
    Equal(1274.144, feed.Groups[2].Items[0].CombinedCostIndex, "upstream combined cost index");

    Equal(103.12, snapshot.Primary.Score, "recommended primary metrics overlay current.json");
    Equal("green", snapshot.Primary.Status, "primary status remains sourced from current.json");
    Equal(8, snapshot.Comparisons.Count, "full measurements and missing recommended configurations are added");
    Equal(2, snapshot.Comparisons.Count(model => model.Model == "deepseek-v4-flash"), "both DeepSeek configurations survive assembly");
    var solUltra = snapshot.Comparisons.Single(model =>
        model.Model == "gpt-5.6-sol" && model.ReasoningEffort == "ultra");
    Equal(109.82, solUltra.Score, "Sol Ultra recommendation IQ");
    Equal<long?>(82, solUltra.Passed, "Sol Ultra passed tasks");
    Equal<long?>(112, solUltra.ValidTasks, "Sol Ultra samples");
    Equal(18.804978, solUltra.CostUsd, "Sol Ultra measured cost");
    Equal(3127.2, solUltra.AverageTaskSeconds, "Sol Ultra measured duration");
    Equal(true, snapshot.Comparisons.Any(model =>
        model.Model == "gpt-5.6-terra" && model.ReasoningEffort == "medium"), "unknown-category item is added");
    Equal(DateTimeOffset.Parse("2026-08-01T13:39:08+08:00"), snapshot.SourceUpdatedAt, "newer recommendation source time is preserved");

    service.FetchAsync().GetAwaiter().GetResult();
    Equal(4, handler.Calls, "supplemental feeds are cached for ten minutes");
}
static void TestRadarRecommendationContinuity()
{
    var now = DateTimeOffset.Parse("2026-08-01T06:00:00Z");
    using var handler = new RadarRequestHandler { ReturnEmptyRecommendationsAfterFirst = true };
    using var service = new RadarService(handler, () => now);

    var initial = service.FetchAsync().GetAwaiter().GetResult();
    Equal(4, initial.RecommendationFeed?.Groups.Count, "initial upstream groups are present");
    Equal(8, initial.Comparisons.Count, "initial full measurement rows are present");

    now = now.AddMinutes(11);
    var afterEmptyRefresh = service.FetchAsync().GetAwaiter().GetResult();
    Equal(4, afterEmptyRefresh.RecommendationFeed?.Groups.Count, "empty refresh retains last upstream groups");
    Equal(8, afterEmptyRefresh.Comparisons.Count, "empty refresh retains full measurement rows");

    now = now.AddMinutes(1);
    service.FetchAsync().GetAwaiter().GetResult();
    Equal(8, handler.Calls, "empty recommendation refresh is not cached and is retried");

    using var restartedHandler = new RadarRequestHandler { ReturnEmptyRecommendations = true };
    using var restartedService = new RadarService(restartedHandler, () => now);
    restartedService.RestoreRecommendationCache(afterEmptyRefresh);
    var afterRestart = restartedService.FetchAsync().GetAwaiter().GetResult();
    Equal(4, afterRestart.RecommendationFeed?.Groups.Count, "restart retains persisted upstream groups");
    Equal(8, afterRestart.Comparisons.Count, "restart retains persisted full measurement rows");
    Equal(3, restartedHandler.Calls, "restart probes both supplemental sources immediately");
}
static void TestRadarScenarioEvaluator()
{
    var models = RadarScenarioFixtureModels();
    var evaluation = RadarScenarioEvaluator.Evaluate(models);

    Equal(RadarScenarioEvaluator.PolicyVersion, evaluation.PolicyVersion, "local scenario policy version");
    Equal("gpt-5.6-sol", evaluation.DailyDevelopment?.Model, "daily development model");
    Equal("xhigh", evaluation.DailyDevelopment?.ReasoningEffort, "daily development balances IQ, time, and cost");
    Equal("gpt-5.6-sol", evaluation.HardProblems?.Model, "hard-problem planning model");
    Equal("ultra", evaluation.HardProblems?.ReasoningEffort, "planning follows the highest guarded IQ");
    Equal("gpt-5.6-sol", evaluation.TaskExecution?.Model, "task execution model");
    Equal("xhigh", evaluation.TaskExecution?.ReasoningEffort, "task execution balances IQ and cost");
    Equal("gpt-5.6-luna", evaluation.BackgroundAutomation?.Model, "background automation model");
    Equal("max", evaluation.BackgroundAutomation?.ReasoningEffort, "lower background IQ admits Luna Max value");
    var rankings = RadarScenarioEvaluator.Rank(models);
    Equal(evaluation.HardProblems, rankings.HardProblems[0], "ranked planning leader matches the existing pick");
    Equal(evaluation.TaskExecution, rankings.TaskExecution[0], "ranked execution leader matches the existing pick");
    var lunaRankings = RadarScenarioEvaluator.Rank(
        models.Where(RadarScenarioEvaluator.IsLunaModel).ToArray());
    Equal("gpt-5.6-luna", lunaRankings.HardProblems[0].Model, "Luna-only planning stays inside the exact Luna family");
    Equal("max", lunaRankings.HardProblems[0].ReasoningEffort, "Luna-only planning is recalculated on the filtered set");

    var routing = RadarRoutingSnapshotBuilder.Build(new ProviderRadarSnapshot(
        ProviderKind.Codex,
        "routing-fixture",
        DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
        DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
        models[0],
        models[1..]));
    Equal(3, routing.Scenarios.Count, "routing snapshot contains the three automatic scenarios");
    Equal("ultra", routing.Scenarios[RadarScenarioEvaluator.HardProblemsKey].Overall[0].Effort, "routing snapshot preserves the measured planning leader");
    Equal("max", routing.Scenarios[RadarScenarioEvaluator.HardProblemsKey].LunaOnly[0].Effort, "routing snapshot exposes the recalculated Luna leader");

    var withoutUltra = RadarScenarioEvaluator.Evaluate(models
        .Where(model => !string.Equals(model.ReasoningEffort, "ultra", StringComparison.OrdinalIgnoreCase))
        .ToArray());
    Equal("max", withoutUltra.HardProblems?.ReasoningEffort, "planning follows the measured leader instead of a fixed top effort");

    var repriced = RadarScenarioEvaluator.Evaluate(models
        .Select(model => string.Equals(model.Model, "gpt-5.6-luna", StringComparison.OrdinalIgnoreCase)
            && string.Equals(model.ReasoningEffort, "max", StringComparison.OrdinalIgnoreCase)
                ? model with { CostUsd = 2.50 }
                : model)
        .ToArray());
    Equal("gpt-5.6-terra", repriced.BackgroundAutomation?.Model, "background reacts to Luna price changes");
    Equal("high", repriced.BackgroundAutomation?.ReasoningEffort, "repriced background winner");

    var planningQualityPriority = RadarScenarioEvaluator.Evaluate(
    [
        ScenarioRadarModel("leader", "high", 100, 100, 10),
        ScenarioRadarModel("eligible", "high", 90, 2, 10),
        ScenarioRadarModel("below-threshold", "high", 89.9, 0.01, 1),
    ]);
    Equal("leader", planningQualityPriority.HardProblems?.Model, "planning never trades guarded IQ for lower cost");

    var planningTie = RadarScenarioEvaluator.Evaluate(
    [
        ScenarioRadarModel("expensive-leader", "high", 100, 100, 10),
        ScenarioRadarModel("efficient-leader", "high", 100, 2, 10),
        ScenarioRadarModel("cheaper-lower-iq", "high", 99, 0.01, 1),
    ]);
    Equal("efficient-leader", planningTie.HardProblems?.Model, "planning uses efficiency only to break an IQ tie");

    var executionCostPriority = RadarScenarioEvaluator.Evaluate(
    [
        ScenarioRadarModel("leader", "high", 100, 100, 10),
        ScenarioRadarModel("surplus-iq", "high", 99, 1.05, 10),
        ScenarioRadarModel("threshold-iq", "high", 70, 1, 10),
    ]);
    Equal("surplus-iq", executionCostPriority.BackgroundAutomation?.Model, "background preserves continuous IQ value above the quality floor");

    var deepSeekCostOutlier = RadarScenarioEvaluator.Evaluate(
    [
        ScenarioRadarModel("gpt-5.6-sol", "ultra", 107.14, 18.926747, 53.04),
        ScenarioRadarModel("gpt-5.6-sol", "max", 99.11, 9.712145, 34.35),
        ScenarioRadarModel("gpt-5.6-sol", "xhigh", 97.77, 6.624371, 25.18),
        ScenarioRadarModel("gpt-5.6-sol", "high", 88.8, 5.44678, 22.05),
        ScenarioRadarModel("gpt-5.6-sol", "medium", 88.39, 3.96539, 18.01),
        ScenarioRadarModel("gpt-5.5", "xhigh", 92.41, 5.853857, 23.51),
        ScenarioRadarModel("deepseek-v4-flash", "max", 84, 0.097974, 23.85),
        ScenarioRadarModel("gpt-5.6-terra", "medium", 57.59, 0.637281, 9.04),
    ]);
    Equal("gpt-5.6-sol", deepSeekCostOutlier.DailyDevelopment?.Model, "daily quality floor rejects an extreme low-cost IQ outlier");
    Equal("xhigh", deepSeekCostOutlier.DailyDevelopment?.ReasoningEffort, "daily keeps the balanced xhigh configuration");
    Equal("ultra", deepSeekCostOutlier.HardProblems?.ReasoningEffort, "planning keeps the highest guarded-IQ configuration");
    Equal("deepseek-v4-flash", deepSeekCostOutlier.BackgroundAutomation?.Model, "background automation can still choose the extreme low-cost candidate");

    var reference = RadarScenarioEvaluator.Evaluate(RadarScenarioReferenceModels());
    Equal("gpt-5.6-sol", reference.DailyDevelopment?.Model, "reference daily model");
    Equal("xhigh", reference.DailyDevelopment?.ReasoningEffort, "reference daily effort");
    Equal("gpt-5.6-sol", reference.HardProblems?.Model, "reference planning model");
    Equal("max", reference.HardProblems?.ReasoningEffort, "reference planning effort");
    Equal("gpt-5.6-luna", reference.TaskExecution?.Model, "reference task-execution model");
    Equal("max", reference.TaskExecution?.ReasoningEffort, "reference task-execution effort");
    Equal("deepseek-v4-flash", reference.BackgroundAutomation?.Model, "reference background model");
    Equal("max", reference.BackgroundAutomation?.ReasoningEffort, "reference background effort");

    var confidenceGuard = RadarScenarioEvaluator.Evaluate(
    [
        ScenarioRadarModel("trusted", "high", 100.45, 2, 10) with { Passed = 75, ValidTasks = 112 },
        ScenarioRadarModel("sparse-outlier", "high", 150, 0.01, 1) with { Passed = 1, ValidTasks = 1 },
    ]);
    Equal("trusted", confidenceGuard.BackgroundAutomation?.Model, "confidence guard rejects a one-sample IQ outlier");

    var sampleQualification = RadarScenarioEvaluator.Evaluate(
    [
        ScenarioRadarModel("trusted-boundary", "high", 100, 2, 10) with
        {
            Status = "red",
            Passed = 34,
            ValidTasks = RadarScenarioEvaluator.MinimumValidTasks,
        },
        ScenarioRadarModel("k3-sparse-outlier", "max", 150, 0.01, 1) with
        {
            Status = "green",
            Passed = 23,
            ValidTasks = 23,
        },
        ScenarioRadarModel("missing-samples", "max", 160, 0.01, 1) with
        {
            Status = "green",
            ValidTasks = null,
        },
    ]);
    Equal("trusted-boundary", sampleQualification.DailyDevelopment?.Model, "sample gate includes the exact 50-task boundary");
    Equal("trusted-boundary", sampleQualification.HardProblems?.Model, "planning excludes sparse and missing-sample outliers");
    Equal("trusted-boundary", sampleQualification.TaskExecution?.Model, "task execution excludes sparse and missing-sample outliers");
    Equal("trusted-boundary", sampleQualification.BackgroundAutomation?.Model, "background excludes sparse and missing-sample outliers");
    Equal(
        false,
        RadarScenarioEvaluator.HasSufficientSamples(ScenarioRadarModel("sparse", "high", 100, 1, 10) with { ValidTasks = 49 }),
        "49 valid tasks are insufficient");
    Equal(
        true,
        RadarScenarioEvaluator.HasSufficientSamples(ScenarioRadarModel("boundary", "high", 100, 1, 10) with { ValidTasks = 50 }),
        "50 valid tasks are sufficient");

    var observedAt = DateTimeOffset.Parse("2026-08-02T12:00:00+08:00");
    var volatilityGuard = RadarScenarioEvaluator.Evaluate(
    [
        ScenarioRadarModel("stable", "high", 100.45, 2, 10) with
        {
            Passed = 75,
            ValidTasks = 112,
            IqHistory =
            [
                new RadarIqSample(observedAt.AddHours(-24), 100),
                new RadarIqSample(observedAt, 100),
            ],
        },
        ScenarioRadarModel("volatile", "high", 99.11, 0.01, 1) with
        {
            Passed = 74,
            ValidTasks = 112,
            IqHistory =
            [
                new RadarIqSample(observedAt.AddHours(-24), 20),
                new RadarIqSample(observedAt, 120),
            ],
        },
    ]);
    Equal("stable", volatilityGuard.BackgroundAutomation?.Model, "24-hour downside guard rejects an unstable cheap outlier");

    var mixedHistoryCoverage = RadarScenarioEvaluator.Evaluate(
    [
        ScenarioRadarModel("dense-history", "high", 100.45, 1, 10) with
        {
            Passed = 75,
            ValidTasks = 112,
            IqHistory =
            [
                new RadarIqSample(observedAt.AddHours(-24), 20),
                new RadarIqSample(observedAt, 120),
            ],
        },
        ScenarioRadarModel("sparse-history", "high", 100.45, 2, 10) with
        {
            Passed = 75,
            ValidTasks = 112,
            IqHistory = [new RadarIqSample(observedAt, 100.45)],
        },
    ]);
    Equal(
        "dense-history",
        mixedHistoryCoverage.BackgroundAutomation?.Model,
        "mixed history coverage never penalizes only the densely measured candidate");

    var invalid = RadarScenarioEvaluator.Evaluate(
    [
        ScenarioRadarModel("invalid", "high", 100, 0, 10),
    ]);
    Equal<RadarModel?>(null, invalid.DailyDevelopment, "invalid costs produce no daily pick");
    Equal<RadarModel?>(null, invalid.HardProblems, "invalid costs produce no planning pick");
    Equal<RadarModel?>(null, invalid.TaskExecution, "invalid costs produce no task-execution pick");
    Equal<RadarModel?>(null, invalid.BackgroundAutomation, "invalid costs produce no background pick");
}

static RadarModel[] RadarScenarioReferenceModels() =>
[
    ScenarioRadarModel("gpt-5.6-sol", "ultra", 107.14, 21.85, 53.14) with { Passed = 80 },
    ScenarioRadarModel("gpt-5.6-sol", "max", 107.14, 9.49, 35.22) with { Passed = 80 },
    ScenarioRadarModel("gpt-5.6-sol", "xhigh", 104.46, 6.29, 25.52) with { Passed = 78 },
    ScenarioRadarModel("gpt-5.5", "xhigh", 100.45, 5.76, 24.00) with { Passed = 75 },
    ScenarioRadarModel("gpt-5.6-terra", "ultra", 97.77, 9.90, 43.00) with { Passed = 73 },
    ScenarioRadarModel("gpt-5.6-terra", "max", 95.09, 4.02, 31.00) with { Passed = 71 },
    ScenarioRadarModel("gpt-5.6-luna", "max", 97.77, 0.46, 32.00) with { Passed = 73 },
    ScenarioRadarModel("deepseek-v4-flash", "max", 89.73, 0.10, 27.00) with { Passed = 67 },
];

static RadarModel[] RadarScenarioFixtureModels() =>
[
    ScenarioRadarModel("gpt-5.6-sol", "max", 101.79, 9.50, 33.63),
    ScenarioRadarModel("gpt-5.6-sol", "xhigh", 97.77, 6.65, 25.26),
    ScenarioRadarModel("gpt-5.6-sol", "high", 90.10, 5.45, 21.89),
    ScenarioRadarModel("gpt-5.6-sol", "medium", 87.05, 3.88, 17.51),
    ScenarioRadarModel("gpt-5.6-sol", "low", 74.00, 2.24, 11.76),
    ScenarioRadarModel("gpt-5.6-terra", "max", 87.05, 4.11, 30.67),
    ScenarioRadarModel("gpt-5.6-terra", "xhigh", 74.00, 2.44, 18.41),
    ScenarioRadarModel("gpt-5.6-terra", "high", 78.00, 1.37, 13.76),
    ScenarioRadarModel("gpt-5.6-luna", "max", 79.40, 1.24, 29.86),
    ScenarioRadarModel("gpt-5.6-luna", "high", 66.96, 0.21, 17.45),
    ScenarioRadarModel("gpt-5.5", "xhigh", 91.07, 5.87, 23.41),
    ScenarioRadarModel("gpt-5.5", "high", 79.40, 3.70, 17.42),
    ScenarioRadarModel("gpt-5.6-sol", "ultra", 109.82, 18.79, 52.14),
    ScenarioRadarModel("gpt-5.6-terra", "medium", 57.59, 0.64, 9.06),
];

static RadarModel ScenarioRadarModel(
    string model,
    string effort,
    double iq,
    double cost,
    double minutes) =>
    new(model, $"{model} {effort}", effort, iq, "unknown", null, 112, cost, minutes * 60, $"{minutes:0.##}m");

static void TestRadarPresentation()
{
    var capturedAt = DateTimeOffset.Parse("2026-07-28T13:09:56+08:00");
    var primary = new RadarModel(
        "gpt-5.6-sol",
        "gpt-5.6-sol max",
        "max",
        103.6,
        "green",
        77,
        112,
        9.064097,
        1939.321,
        "32分钟");
    var faster = new RadarModel(
        "gpt-5.6-sol",
        "GPT-5.6 Sol high",
        "high",
        91.5,
        "yellow",
        68,
        112,
        6,
        1000,
        "17分钟");
    var cheapest = new RadarModel(
        "gpt-5.6-luna",
        "GPT-5.6 Luna high",
        "high",
        74,
        "red",
        55,
        112,
        0.004,
        2100,
        "35分钟");
    var unselected = cheapest with
    {
        Model = "gpt-5.6-terra",
        Label = "GPT-5.6 Terra medium",
        ReasoningEffort = "medium",
        Score = 80,
    };
    var snapshot = new ProviderRadarSnapshot(
        ProviderKind.Codex,
        "model_iq:2026-07-28",
        capturedAt,
        capturedAt,
        primary,
        [faster, cheapest, unselected])
    {
        RecommendationFeed = new RadarRecommendationFeed(
            1,
            "latest_valid_per_task",
            capturedAt,
            capturedAt,
            [
                new RadarRecommendationGroup(
                    "daily_development",
                    "日常开发",
                    "上游日常规则",
                    [
                        RecommendationItem(faster, "speed", "速度位"),
                        RecommendationItem(primary, "smart", "聪明位"),
                    ]),
                new RadarRecommendationGroup(
                    "hard_problems",
                    "难题攻坚",
                    "上游攻坚规则",
                    [RecommendationItem(primary)]),
                new RadarRecommendationGroup(
                    "background_automation",
                    "后台自动化",
                    "上游后台规则",
                    [RecommendationItem(cheapest)]),
                new RadarRecommendationGroup(
                    "lobster_tasks",
                    "跑龙虾类任务",
                    "不在产品中展示",
                    [RecommendationItem(cheapest)]),
            ]),
    };
    var presentation = RadarPresentation.Build(snapshot);
    var local = RadarScenarioEvaluator.Evaluate([primary, faster, cheapest, unselected]);

    Equal(4, presentation.Rows.Count, "presentation preserves the complete comparison table");
    Equal(true, presentation.Rows.Any(row =>
        string.Equals(row.Model.Model, unselected.Model, StringComparison.OrdinalIgnoreCase)
        && string.Equals(row.Model.ReasoningEffort, unselected.ReasoningEffort, StringComparison.OrdinalIgnoreCase)), "unrecommended rows stay visible");
    Equal(primary.Label, presentation.Rows[0].Model.Label, "full table keeps model order");
    Equal(1, presentation.Rows[0].Rank, "full-table IQ leader rank");
    Equal<int?>(null, presentation.Rows[1].Rank, "second visible row is not ranked");
    Equal<int?>(null, presentation.Rows[2].Rank, "third IQ rank is not displayed");
    Equal<int?>(null, presentation.Rows[3].Rank, "fourth visible row is not ranked");
    Equal(primary.Label, presentation.IqLeader?.Label, "full-table IQ leader summary");
    Equal(4, presentation.Recommendations.Count, "four local scenario groups are exposed");
    Equal(true, presentation.Recommendations.All(group => group.Items.Count == 1), "each scenario exposes one local pick");
    Equal(local.DailyDevelopment?.Model, presentation.Recommendations[0].Items[0].Model.Model, "daily local selection is mapped");
    Equal(local.HardProblems?.Model, presentation.Recommendations[1].Items[0].Model.Model, "planning local selection is mapped");
    Equal(local.TaskExecution?.Model, presentation.Recommendations[2].Items[0].Model.Model, "task-execution local selection is mapped");
    Equal(local.BackgroundAutomation?.Model, presentation.Recommendations[3].Items[0].Model.Model, "background local selection is mapped");
    Equal<string?>(null, presentation.Recommendations[0].Items[0].Slot, "local picks do not impersonate upstream slots");
    Equal<long?>(null, presentation.Recommendations[0].Items[0].CostSamples, "local picks do not impersonate upstream cost sample counts");
    Equal<long?>(null, presentation.Recommendations[0].Items[0].DurationSamples, "local picks do not impersonate upstream duration sample counts");
    Equal(RadarScenarioEvaluator.RuleFor("daily_development"), presentation.Recommendations[0].Rule, "local rule replaces upstream rule");
    Equal(2, snapshot.RecommendationFeed!.Groups[0].Items.Count, "the raw upstream feed stays complete");
    Equal("daily_development", presentation.Recommendations[0].Key, "local scenario order starts with daily development");
    Equal("task_execution", presentation.Recommendations[2].Key, "task-execution group is exposed");
    Equal("background_automation", presentation.Recommendations[3].Key, "background group is exposed");
    Equal(false, presentation.Recommendations.Any(group => group.Key == "lobster_tasks"), "lobster task group is hidden");
    Equal(true, presentation.Rows[0].RecommendationGroupIndexes.SequenceEqual([0, 1]), "one model can serve daily and planning scenarios");
    Equal(true, presentation.Rows[1].RecommendationGroupIndexes.SequenceEqual([2, 3]), "confidence-qualified task and background picks are marked");
    Equal(true, presentation.Rows[2].RecommendationGroupIndexes.Count == 0, "unselected comparison stays unmarked");
    Equal(true, presentation.Rows[3].RecommendationGroupIndexes.Count == 0, "below-confidence comparison stays unmarked");
    var withoutFeed = RadarPresentation.Build(snapshot with { RecommendationFeed = null });
    Equal(4, withoutFeed.Rows.Count, "missing upstream recommendations keep every source row");
    Equal(4, withoutFeed.Recommendations.Count, "local scenario picks do not depend on upstream groups");
    var trustedBoundary = ScenarioRadarModel("trusted-boundary", "high", 100, 2, 10) with
    {
        Status = "red",
        Passed = 34,
        ValidTasks = RadarScenarioEvaluator.MinimumValidTasks,
    };
    var sparseK3 = ScenarioRadarModel("k3", "max", 150, 0.01, 1) with
    {
        Status = "green",
        Passed = 23,
        ValidTasks = 23,
    };
    var sparsePresentation = RadarPresentation.Build(new ProviderRadarSnapshot(
        ProviderKind.Codex,
        "sample-qualification",
        capturedAt,
        capturedAt,
        trustedBoundary,
        [sparseK3]));
    Equal(2, sparsePresentation.Rows.Count, "sparse rows remain visible");
    Equal(trustedBoundary.Label, sparsePresentation.IqLeader?.Label, "strongest ignores a higher-IQ sparse K3 row");
    Equal(1, sparsePresentation.Rows.Single(row => row.Model.Model == trustedBoundary.Model).Rank, "sample-qualified row receives strongest rank");
    Equal<int?>(null, sparsePresentation.Rows.Single(row => row.Model.Model == sparseK3.Model).Rank, "sparse K3 row has no strongest rank");
    Equal(4, sparsePresentation.Rows.Single(row => row.Model.Model == trustedBoundary.Model).RecommendationGroupIndexes.Count, "sample-qualified row can receive all scenario markers");
    Equal(0, sparsePresentation.Rows.Single(row => row.Model.Model == sparseK3.Model).RecommendationGroupIndexes.Count, "sparse K3 row receives no scenario marker");
    var deepSeek = new RadarModel(
        "deepseek-v4-flash",
        "deepseek-v4-flash max",
        "max",
        79,
        "green",
        42,
        50,
        0.10,
        1800,
        "30分钟");
    var dshDeepSeek = deepSeek with
    {
        Model = "dsh-deepseek-v4-pro",
        Label = "dsh-deepseek-v4-pro max",
        Score = 112,
    };
    var deepSeekPresentation = RadarPresentation.DeepSeekOnly(
        RadarPresentation.Build(snapshot with
        {
            Comparisons = [.. snapshot.Comparisons, deepSeek, dshDeepSeek],
        }));
    Equal(2, deepSeekPresentation.Rows.Count, "DeepSeek Radar keeps only DeepSeek model rows");
    Equal(true, deepSeekPresentation.Rows.All(row => RadarPresentation.IsDeepSeekModel(row.Model)), "DeepSeek filter excludes non-DeepSeek rows");
    Equal(dshDeepSeek.Label, deepSeekPresentation.IqLeader?.Label, "filtered view preserves a DeepSeek leader");
    Equal("dsh-deepseek-v4-pro", deepSeekPresentation.Rows[0].Model.Model, "DeepSeek Radar puts Pro before Flash");
    Equal(true, RadarPresentation.IsDeepSeekModel(dshDeepSeek), "dsh-prefixed DeepSeek model is included");
    RadarModel DeepSeekVariant(string model, string effort, double score) => deepSeek with
    {
        Model = model,
        Label = $"{model} {effort}",
        ReasoningEffort = effort,
        Score = score,
    };
    var deepSeekOrderingRows = new[]
    {
        DeepSeekVariant("deepseek-v4-pro", "max", 90),
        DeepSeekVariant("deepseek-v4-pro", "high", 89),
        DeepSeekVariant("deepseek-v4-pro", "off", 88),
        DeepSeekVariant("dsh-deepseek-v4-pro", "max", 100),
        DeepSeekVariant("dsh-deepseek-v4-pro", "high", 99),
        DeepSeekVariant("dsh-deepseek-v4-pro", "off", 98),
        DeepSeekVariant("deepseek-v4-flash", "max", 80),
        DeepSeekVariant("deepseek-v4-flash", "high", 79),
        DeepSeekVariant("deepseek-v4-flash", "off", 78),
        DeepSeekVariant("dsh-deepseek-v4-flash", "max", 87),
        DeepSeekVariant("dsh-deepseek-v4-flash", "high", 86),
        DeepSeekVariant("dsh-deepseek-v4-flash", "off", 85),
    };
    var deepSeekOrderingPresentation = RadarPresentation.DeepSeekOnly(
        RadarPresentation.Build(new ProviderRadarSnapshot(
            ProviderKind.Codex,
            "deepseek-ordering",
            capturedAt,
            capturedAt,
            deepSeekOrderingRows[0],
            deepSeekOrderingRows.Skip(1).ToArray())));
    var deepSeekOrdering = deepSeekOrderingPresentation.Rows
        .Select(row => $"{row.Model.Model}:{row.Model.ReasoningEffort}")
        .ToArray();
    Equal(
        true,
        deepSeekOrdering.SequenceEqual(
            [
                "deepseek-v4-pro:max",
                "deepseek-v4-pro:high",
                "deepseek-v4-pro:off",
                "dsh-deepseek-v4-pro:max",
                "dsh-deepseek-v4-pro:high",
                "dsh-deepseek-v4-pro:off",
                "deepseek-v4-flash:max",
                "deepseek-v4-flash:high",
                "deepseek-v4-flash:off",
                "dsh-deepseek-v4-flash:max",
                "dsh-deepseek-v4-flash:high",
                "dsh-deepseek-v4-flash:off",
            ],
            StringComparer.Ordinal),
        "DeepSeek Radar groups base and DSH lanes within each model family");
    var codexPresentation = RadarPresentation.CodexOnly(
        RadarPresentation.Build(snapshot with
        {
            Comparisons = [.. snapshot.Comparisons, deepSeek, dshDeepSeek],
        }));
    Equal(4, codexPresentation.Rows.Count, "Codex Radar keeps only non-DeepSeek model rows");
    Equal(true, codexPresentation.Rows.All(row => !RadarPresentation.IsDeepSeekModel(row.Model)), "Codex filter excludes all DeepSeek rows");
    Equal("DSH deepseek-v4-pro max", RadarPresentation.FormatModelLabel(dshDeepSeek), "DSH lane is labeled instead of looking like a duplicate");
    Equal("77/112 (69%)", RadarPresentation.FormatPass(primary), "pass/valid percentage");
    Equal("$9.06", RadarPresentation.FormatAverageCost(primary), "average cost");
    Equal("<$0.01", RadarPresentation.FormatAverageCost(cheapest), "small average cost");
    Equal(RadarStatusIndicator.Stable, RadarStatus.Indicator(primary.Status), "green shape");
    Equal(RadarStatusIndicator.Watch, RadarStatus.Indicator(faster.Status), "yellow shape");
    Equal(RadarStatusIndicator.Degraded, RadarStatus.Indicator(cheapest.Status), "red shape");
    Equal(RadarStatusIndicator.Unknown, RadarStatus.Indicator("future"), "unknown shape");
    Equal("GPT-5.6 Sol Max", presentation.Rows[0].ModelText, "row model display");
    Equal("GPT-5.6 Sol XHigh", RadarPresentation.FormatModelLabel(faster with
    {
        ReasoningEffort = "xhigh",
    }), "reasoning effort display casing");
    Equal("103.6", presentation.Rows[0].ScoreText, "row score display");
    Equal("112", presentation.Rows[0].SampleCountText, "row valid sample count display");
    Equal<RadarIqComparison?>(null, presentation.Rows[0].IqComparison, "row omits unavailable recent average");
    Equal<RadarIqComparison?>(
        null,
        RadarPresentation.FormatIqComparison(primary with
        {
            IqHistory = [new RadarIqSample(capturedAt, primary.Score!.Value)],
        }),
        "one history point does not manufacture a flat current-versus-average arrow");
    var withIqHistory = primary with
    {
        IqHistory =
        [
            new RadarIqSample(DateTimeOffset.Parse("2026-07-20T12:00:00+08:00"), 1000),
            new RadarIqSample(DateTimeOffset.Parse("2026-07-31T12:00:00+08:00"), 98),
            new RadarIqSample(DateTimeOffset.Parse("2026-08-01T12:00:00+08:00"), 102),
        ],
    };
    Equal<RadarIqComparison?>(
        new RadarIqComparison("↑", "100.0"),
        RadarPresentation.FormatIqComparison(withIqHistory),
        "row places an upward arrow after current IQ when above the recent average");
    Equal<RadarIqComparison?>(
        new RadarIqComparison("↓", "100.0"),
        RadarPresentation.FormatIqComparison(withIqHistory with { Score = 97.4 }),
        "row places a downward arrow after current IQ when below the recent average");
    Equal<RadarIqComparison?>(
        new RadarIqComparison("→", "100.0"),
        RadarPresentation.FormatIqComparison(withIqHistory with { Score = 100.0 }),
        "row places a flat arrow after current IQ when matching the recent average");
    Equal(
        "≈$9.06",
        RadarPresentation.FormatAverageCost(primary with { IncompleteCostSamples = 1 }),
        "partial cost coverage is marked as approximate");
    Equal("77/112 (69%)", presentation.Rows[0].PassText, "row pass display");

    var missing = primary with
    {
        Passed = null,
        ValidTasks = 0,
        CostUsd = null,
        AverageTaskSeconds = null,
        WallTime = null,
    };
    Equal("—", RadarPresentation.FormatPass(missing), "invalid pass denominator");
    Equal("—", RadarPresentation.FormatSampleCount(missing), "missing sample count");
    Equal("—", RadarPresentation.FormatAverageCost(missing), "missing average cost");
}

static void TestRadarDeveloperCli()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-radar-cli-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var validPath = Path.Combine(directory, "current.json");
        File.WriteAllText(validPath, RadarCliFixtureJson());
        var first = RunRadarCli(["--radar-evaluate", validPath]);
        var second = RunRadarCli(["--RADAR-EVALUATE", validPath]);
        Equal(0, first.ExitCode, "offline CLI succeeds");
        Equal(string.Empty, first.Error, "offline CLI keeps stderr empty");
        Equal(first.Output, second.Output, "offline CLI output is deterministic");
        using (var document = JsonDocument.Parse(first.Output))
        {
            var root = document.RootElement;
            Equal(6, root.GetProperty("schemaVersion").GetInt32(), "CLI schema version");
            Equal(JsonValueKind.Null, root.GetProperty("recommendationFeed").ValueKind, "current.json does not synthesize recommendations");
            Equal(2, root.GetProperty("rows").GetArrayLength(), "CLI emits every row");
            Equal(0L, root.GetProperty("rows")[0].GetProperty("sourceIndex").GetInt64(), "CLI preserves source order");
            Equal("gpt-5.6-sol", root.GetProperty("rows")[0].GetProperty("model").GetString(), "CLI exposes raw model data");
            var localRecommendations = root.GetProperty("localRecommendations");
            Equal(RadarScenarioEvaluator.PolicyVersion, localRecommendations.GetProperty("policyVersion").GetString(), "CLI exposes local policy");
            Equal("max", localRecommendations.GetProperty("dailyDevelopment").GetProperty("effort").GetString(), "CLI daily selection");
            Equal("max", localRecommendations.GetProperty("hardProblems").GetProperty("effort").GetString(), "CLI planning selection");
            Equal("medium", localRecommendations.GetProperty("taskExecution").GetProperty("effort").GetString(), "CLI task-execution selection");
            Equal("medium", localRecommendations.GetProperty("backgroundAutomation").GetProperty("effort").GetString(), "CLI background selection");
        }

        var liveCalls = 0;
        var live = RunRadarCli(
            ["--radar-live"],
            _ =>
            {
                liveCalls++;
                var snapshot = RadarParser.Parse(RadarCliFixtureJson(), DateTimeOffset.UnixEpoch);
                return Task.FromResult(snapshot with
                {
                    RecommendationFeed = new RadarRecommendationFeed(
                        1,
                        "latest_valid_per_task",
                        DateTimeOffset.Parse("2026-08-01T13:39:13+08:00"),
                        DateTimeOffset.Parse("2026-08-01T13:39:08+08:00"),
                        [
                            new RadarRecommendationGroup(
                                "background_automation",
                                "后台自动化",
                                "上游规则",
                                [RecommendationItem(snapshot.Comparisons[0], rule: "上游条目规则")]),
                        ]),
                });
            });
        Equal(0, live.ExitCode, "live CLI succeeds");
        Equal(1, liveCalls, "live CLI fetches exactly once");
        using (var liveDocument = JsonDocument.Parse(live.Output))
        {
            var root = liveDocument.RootElement;
            Equal(6, root.GetProperty("schemaVersion").GetInt32(), "live CLI shares schema");
            var feed = root.GetProperty("recommendationFeed");
            Equal("latest_valid_per_task", feed.GetProperty("mode").GetString(), "CLI exposes upstream mode");
            var group = feed.GetProperty("recommendations")[0];
            Equal("background_automation", group.GetProperty("key").GetString(), "CLI exposes upstream group key");
            Equal("后台自动化", group.GetProperty("title").GetString(), "CLI exposes upstream title");
            Equal("上游规则", group.GetProperty("rule").GetString(), "CLI exposes upstream rule");
            Equal("上游条目规则", group.GetProperty("items")[0].GetProperty("rule").GetString(), "CLI exposes upstream item rule");
            Equal(RadarScenarioEvaluator.PolicyVersion, root.GetProperty("localRecommendations").GetProperty("policyVersion").GetString(), "live CLI exposes local policy");
        }

        var noRecommendationPath = Path.Combine(directory, "none.json");
        File.WriteAllText(noRecommendationPath, RadarCliFixtureJson("\"score\": null", "\"score\": null"));
        var none = RunRadarCli(["--radar-evaluate", noRecommendationPath]);
        using (var noneDocument = JsonDocument.Parse(none.Output))
        {
            Equal(JsonValueKind.Null, noneDocument.RootElement.GetProperty("recommendationFeed").ValueKind, "offline input remains recommendation-free");
            Equal(JsonValueKind.Null, noneDocument.RootElement.GetProperty("rows")[0].GetProperty("iq").ValueKind, "invalid IQ remains null");
            Equal(JsonValueKind.Null, noneDocument.RootElement.GetProperty("localRecommendations").GetProperty("dailyDevelopment").ValueKind, "invalid IQ has no local selection");
        }

        var invalidPath = Path.Combine(directory, "invalid.json");
        File.WriteAllText(invalidPath, "{}");
        var oversizedPath = Path.Combine(directory, "oversized.json");
        File.WriteAllBytes(oversizedPath, new byte[1024 * 1024 + 1]);
        AssertRadarCliError(["--radar-evaluate"], "invalid_arguments");
        AssertRadarCliError(["--radar-evaluate", Path.Combine(directory, "missing.json")], "input_not_found");
        AssertRadarCliError(["--radar-evaluate", directory], "input_not_file");
        AssertRadarCliError(["--radar-evaluate", oversizedPath], "input_too_large");
        AssertRadarCliError(["--radar-evaluate", invalidPath], "invalid_payload");
        AssertRadarCliError(["--unknown"], "invalid_arguments");
        AssertRadarCliError(["--help", "extra"], "invalid_arguments");

        var help = RunRadarCli(["--help"]);
        Equal(0, help.ExitCode, "help succeeds");
        Equal(true, help.Output.Contains("--radar-evaluate", StringComparison.Ordinal), "help documents offline evaluation");
        Equal(
            true,
            help.Output.Contains("--taskbar-mini-captures", StringComparison.Ordinal),
            "help documents headless Mini captures");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static (int ExitCode, string Output, string Error) RunRadarCli(
    string[] arguments,
    Func<CancellationToken, Task<ProviderRadarSnapshot>>? fetchLive = null)
{
    using var output = new StringWriter();
    using var error = new StringWriter();
    var result = RadarDeveloperCli.TryRunAsync(arguments, output, error, fetchLive).GetAwaiter().GetResult();
    Equal(true, result.Handled, "Radar CLI handles command");
    return (result.ExitCode, output.ToString(), error.ToString());
}

static void AssertRadarCliError(string[] arguments, string category)
{
    var result = RunRadarCli(arguments);
    Equal(1, result.ExitCode, $"CLI {category} exits one");
    Equal(string.Empty, result.Output, $"CLI {category} keeps stdout clean");
    Equal(true, result.Error.Contains(category, StringComparison.Ordinal), $"CLI {category} is stable");
    Equal(false, result.Error.Contains(" at ", StringComparison.Ordinal), $"CLI {category} has no stack trace");
}

static string RadarCliFixtureJson(
    string primaryScore = "\"score\": 104.9",
    string comparisonScore = "\"score\": 92.8") =>
    $$"""
      {
        "model_iq": {
          "updated_at": "2026-07-28T17:33:00+08:00",
          "latest": {
            "date": "2026-07-28",
            "model": "gpt-5.6-sol",
            "reasoning_effort": "max",
            {{primaryScore}},
            "status": "green",
            "passed": 78,
            "valid_tasks": 112,
            "average_cost_usd": 9.06,
            "average_task_seconds": 1920
          },
          "comparisons": {
            "medium": {
              "label": "GPT-5.6 Sol medium",
              "latest": {
                "date": "2026-07-28",
                "model": "gpt-5.6-sol",
                "reasoning_effort": "medium",
                {{comparisonScore}},
                "status": "yellow",
                "passed": 69,
                "valid_tasks": 112,
                "average_cost_usd": 3.56,
                "average_task_seconds": 1020
              }
            }
          }
        }
      }
      """;

static void RenderQuotaTokenEstimateCaptures(string outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    var now = DateTimeOffset.Parse("2026-08-23T16:00:00+08:00");
    var card = new QuotaCard(
        "codex.mid-cycle",
        ProviderKind.Codex,
        "Codex · 1",
        "pro",
        "#10a37f",
        true,
        [new QuotaWindow("7d", 62, now.AddDays(4), TimeSpan.FromDays(7))])
    {
        AccountHint = "q***w@example.test",
        CapturedAt = now,
    };
    var pace = new QuotaPaceEstimate(
        QuotaPaceStatus.ResetsBeforeExhaustion,
        new QuotaCyclePace(62, 12, .9, now.AddDays(5), true, 1.4),
        new QuotaRecentTrend(
            TimeSpan.FromHours(1),
            .9,
            now.AddDays(1).AddHours(15),
            false,
            QuotaTrendConfidence.Stable),
        TimeSpan.FromHours(1),
        now.AddMinutes(15));
    var content = new QuotaPopoverContent(
        card,
        card.Windows[0],
        null,
        now,
        pace,
        false,
        null,
        new CodexQuotaTokenSummary(
            card.Key,
            "7d",
            TimeSpan.FromDays(7).Ticks,
            1_575_299_235,
            null,
            null,
            0,
            true,
            976_685_526,
            62,
            true,
            true,
            5_000_000_000));
    using var providerLogo = LoadNativeAsset(
        "ZGSTokenBar.App.Assets.openai-official-ios-icon.png");
    using var resetClockIcon = LoadNativeAsset(
        "ZGSTokenBar.App.Assets.fluent-clock-20-regular.png");
    foreach (var locale in new[] { "zh-CN", "en" })
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var popover = new QuotaPopoverForm();
            using var bitmap = popover.RenderForTest(
                content,
                NativeText.For(locale),
                providerLogo,
                resetClockIcon,
                dpi,
                now);
            var path = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"quota-token-current-used-{locale}-{dpi}dpi.png"));
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height}");
        }
    }
}

static void RenderTaskbarMiniCaptures(string outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    var systemUsage = new SystemUsageSnapshot(
        78,
        42UL * 1_073_741_824,
        22UL * 1_073_741_824,
        64UL * 1_073_741_824,
        41,
        "3D",
        6,
        32,
        DateTimeOffset.Parse("2026-08-03T14:02:03+08:00"))
    {
        DiskActivePercent = 68,
        DiskReadBytesPerSecond = 125 * 1_048_576d,
        DiskWriteBytesPerSecond = 12 * 1_048_576d,
        TopProcesses =
        [
            new SystemProcessUsage("Unity", 3, 31, 5UL * 1_073_741_824, 38),
            new SystemProcessUsage("chrome", 12, 18, 3_382_312_960, 24),
            new SystemProcessUsage("Code", 7, 12, 1_932_735_283, 3),
            new SystemProcessUsage("MsMpEng", 1, 8, 734_003_200, 0),
            new SystemProcessUsage("SearchHost", 2, null, 398_458_880, 0),
        ],
    };
    var codexAccounts = new[]
    {
        new CodexAccountInfo("account-current", "current@example.test", "pro", true),
        new CodexAccountInfo("account-second", "second@example.test", "pro", false),
        new CodexAccountInfo("account-plus", "plus@example.test", "plus", false),
        new CodexAccountInfo("account-plus-second", "plus-second@example.test", "plus", false),
        new CodexAccountInfo("account-key", null, "api_key", false),
        new CodexAccountInfo("account-key-second", null, "api_key", false),
        new CodexAccountInfo("account-free", "free@example.test", "free", false),
    };
    foreach (var scenario in new[]
             {
                 (Name: "claude-and-two-codex", IncludeClaude: true, CodexCount: 2),
                 (Name: "two-codex-only", IncludeClaude: false, CodexCount: 2),
                 (Name: "three-codex-only", IncludeClaude: false, CodexCount: 3),
                 (Name: "four-codex-only", IncludeClaude: false, CodexCount: 4),
             })
    {
        var snapshot = TaskbarMiniCaptureSnapshot(scenario.IncludeClaude, scenario.CodexCount);
        foreach (var locale in new[] { "en", "zh-CN" })
        {
            IEnumerable<string> paletteIds = scenario.Name == "two-codex-only" && locale == "en"
                ? QuotaBackgroundPalette.All.Select(theme => theme.Id)
                : [AppSettings.DefaultBackgroundPalette];
            foreach (var paletteId in paletteIds)
            {
                IEnumerable<int> dpis = scenario.IncludeClaude
                    && locale == "zh-CN"
                    && paletteId == AppSettings.DefaultBackgroundPalette
                        ? [96, 144, 192]
                        : [192];
                foreach (var dpi in dpis)
                {
                    var settings = new AppSettings
                    {
                        Locale = locale,
                        UseTaskbarRings = true,
                        EnableAnimations = false,
                        EnableRadar = false,
                        BackgroundPalette = paletteId,
                    };
                    using var form = new BarForm(
                        settings,
                        snapshot,
                        renderOnly: true,
                        renderDpi: dpi,
                        codexAccounts: codexAccounts);
                    form.SetCodexEconomyStatus(new CodexEconomyStatus(
                        CodexEconomyMode.Ask,
                        new CodexEconomyProfile(
                            "Codex default",
                            Path.Combine(Path.GetTempPath(), "wmt-mini-capture-codex"),
                            true,
                            "capture"),
                        true,
                        false,
                        null));
                    form.SetQuotaPaceEstimates(snapshot.Cards
                        .SelectMany(card => card.Windows.Select(window => new
                        {
                            Key = QuotaPaceTracker.SeriesKey(card, window),
                            Estimate = new QuotaPaceEstimate(
                                QuotaPaceStatus.Learning,
                                window.UsedPercent is { } used
                                    ? new QuotaCyclePace(Math.Max(0, used - 8), 8)
                                    : null),
                        }))
                        .ToDictionary(item => item.Key, item => item.Estimate, StringComparer.Ordinal));
                    form.SetSystemUsage(systemUsage);
                    form.CreateControl();
                    using var bitmap = new Bitmap(
                        form.ClientSize.Width,
                        form.ClientSize.Height,
                        PixelFormat.Format32bppPArgb);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    var localeSuffix = locale == "en" ? string.Empty : "-zh-CN";
                    var paletteSuffix = paletteId == AppSettings.DefaultBackgroundPalette
                        ? string.Empty
                        : $"-{paletteId}";
                    var path = Path.GetFullPath(Path.Combine(
                        outputDirectory,
                        $"taskbar-mini-{scenario.Name}{localeSuffix}{paletteSuffix}-{dpi}dpi.png"));
                    bitmap.Save(path, ImageFormat.Png);
                    var groups = TaskbarMiniGrouping.Create(snapshot.Cards);
                    Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} slots={groups.Count} palette={paletteId}");
                }
            }
        }
    }

    var fourCodexPoolSnapshot = FourCodexPoolSnapshot(DateTimeOffset.UtcNow);
    var fourCodexPoolAccounts = Enumerable.Range(1, 4)
        .Select(index => new CodexAccountInfo(
            $"pool-account-{index}",
            $"pool-{index}@example.test",
            "pro",
            index == 1))
        .ToArray();
    var poolCaptureScenarios = new[]
    {
        (Suffix: string.Empty, Snapshot: fourCodexPoolSnapshot),
        (Suffix: "-7d-only", Snapshot: fourCodexPoolSnapshot with
        {
            Cards = fourCodexPoolSnapshot.Cards
                .Select(card => card with
                {
                    Windows = card.Windows
                        .Where(window => window.Duration != TimeSpan.FromHours(5))
                        .ToArray(),
                })
                .ToArray(),
            CodexAccounts = fourCodexPoolSnapshot.CodexAccounts
                .Select(quota => quota with
                {
                    Windows = quota.Windows
                        .Where(window => window.Duration != TimeSpan.FromHours(5))
                        .ToArray(),
                })
                .ToArray(),
        }),
        (Suffix: "-7d-partial", Snapshot: fourCodexPoolSnapshot with
        {
            Cards = fourCodexPoolSnapshot.Cards
                .Take(3)
                .Select(card => card with
                {
                    Windows = card.Windows
                        .Where(window => window.Duration != TimeSpan.FromHours(5))
                        .ToArray(),
                })
                .ToArray(),
            CodexAccounts = fourCodexPoolSnapshot.CodexAccounts
                .Take(3)
                .Select(quota => quota with
                {
                    Windows = quota.Windows
                        .Where(window => window.Duration != TimeSpan.FromHours(5))
                        .ToArray(),
                })
                .ToArray(),
        }),
    };
    foreach (var scenario in poolCaptureScenarios)
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            using var form = new BarForm(
                new AppSettings
                {
                    Locale = "zh-CN",
                    UseTaskbarRings = true,
                    EnableAnimations = false,
                    EnableRadar = false,
                    CodexMiniDisplayMode = CodexMiniDisplayModes.Pool,
                },
                scenario.Snapshot,
                renderOnly: true,
                renderDpi: dpi,
                codexAccounts: fourCodexPoolAccounts);
            form.SetSystemUsage(systemUsage);
            form.CreateControl();
            using var bitmap = new Bitmap(
                form.ClientSize.Width,
                form.ClientSize.Height,
                PixelFormat.Format32bppPArgb);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var path = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"taskbar-mini-four-codex-pool{scenario.Suffix}-zh-CN-{dpi}dpi.png"));
            bitmap.Save(path, ImageFormat.Png);
            var groups = TaskbarMiniGrouping.Create(
                scenario.Snapshot.Cards,
                CodexMiniDisplayModes.Pool);
            Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} slots={groups.Count} codex=pool accounts=4");
        }
    }

    foreach (var dpi in new[] { 96, 192 })
    {
        var snapshot = TaskbarMiniCaptureSnapshot(includeClaude: true);
        using var form = new BarForm(
            new AppSettings
            {
                Locale = "zh-CN",
                UseTaskbarRings = true,
                EnableAnimations = false,
                EnableRadar = false,
                MiniAreaOrder = [
                    MiniAreaIds.SystemMetrics,
                    MiniAreaIds.Codex,
                    MiniAreaIds.Claude,
                ],
            },
            snapshot,
            renderOnly: true,
            renderDpi: dpi,
            codexAccounts: codexAccounts);
        form.SetSystemUsage(systemUsage);
        form.CreateControl();
        using var bitmap = new Bitmap(
            form.ClientSize.Width,
            form.ClientSize.Height,
            PixelFormat.Format32bppPArgb);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var path = Path.GetFullPath(Path.Combine(
            outputDirectory,
            $"taskbar-mini-system-first-zh-CN-{dpi}dpi.png"));
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} system=first");
    }

    var aiGatewayNow = DateTimeOffset.Parse("2026-08-14T15:00:00+08:00");
    var aiGatewayCard = new QuotaCard(
        "ai-gateway.balance",
        ProviderKind.AiGateway,
        "AI 网关",
        null,
        "#8b5cf6",
        true,
        [new QuotaWindow("AI", null, null, TimeSpan.Zero)])
    {
        CapturedAt = aiGatewayNow,
        IsService = true,
        Balance = new AiGatewayBalance(
            AiGatewayBalanceStatus.Available,
            "CNY",
            42.36m,
            42.36m,
            0m,
            aiGatewayNow),
    };
    var aiGatewayUsage = new AiGatewayUsageSummary(
        "CNY",
        AiGatewayBalanceStatus.Available,
        new AiGatewayUsagePeriod(8, 7_000, 1_500, 8_500, 5_600, 1_400, 0, 80m, 0.0043m),
        new AiGatewayUsagePeriod(91, 120_000, 24_000, 144_000, 98_000, 20_000, 2_000, 83.05m, 0.0723m),
        aiGatewayNow);
    foreach (var locale in new[] { "en", "zh-CN" })
    {
        foreach (var dpi in new[] { 96, 192 })
        {
            foreach (var collapsed in new[] { false, true })
            {
                var settings = new AppSettings
                {
                    Locale = locale,
                    UseTaskbarRings = true,
                    EnableAnimations = false,
                    EnableRadar = false,
                    MiniAreaLayouts = collapsed
                        ? new(StringComparer.Ordinal) { [MiniAreaIds.AiGateway] = new(true) }
                        : new(StringComparer.Ordinal),
                };
                using var form = new BarForm(
                    settings,
                    new QuotaSnapshot(
                        [aiGatewayCard],
                        [new ProviderHealth(ProviderKind.AiGateway, true, "current", ProviderHealthCode.Current)],
                        aiGatewayNow),
                    renderOnly: true,
                    renderDpi: dpi,
                    activeProviders: new HashSet<ProviderKind> { ProviderKind.AiGateway });
                form.CreateControl();
                using var bitmap = new Bitmap(
                    form.ClientSize.Width,
                    form.ClientSize.Height,
                    PixelFormat.Format32bppPArgb);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                var collapseSuffix = collapsed ? "-collapsed" : string.Empty;
                var path = Path.GetFullPath(Path.Combine(
                    outputDirectory,
                    $"taskbar-mini-ai-gateway-{locale}{collapseSuffix}-{dpi}dpi.png"));
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} service=ai-gateway collapsed={collapsed}");
            }
        }
    }

    var sub2ApiCard = new QuotaCard(
        "codex.sub2api",
        ProviderKind.Codex,
        "API · 5",
        "API key",
        "#10a37f",
        true,
        [new QuotaWindow("API", null, null, TimeSpan.Zero)])
    {
        CapturedAt = aiGatewayNow,
        IsService = true,
        ServiceCount = 5,
        ServiceDisplayName = "sub2api",
        Sub2ApiPool = new Sub2ApiPoolAvailability(
            Sub2ApiPoolStatus.Available,
            4,
            12,
            0,
            0,
            4,
            4,
            aiGatewayNow),
        Sub2ApiUsage = new Sub2ApiUsageSummary(
            Sub2ApiUsageStatus.Available,
            19,
            1200,
            340,
            0,
            80,
            1540,
            2430,
            120000,
            34000,
            0,
            8000,
            154000,
            aiGatewayNow),
        Sub2ApiQuota = new Sub2ApiQuotaSummary(
            Sub2ApiQuotaStatus.Available,
            5,
            null,
            null,
            null,
            5,
            60,
            3,
            aiGatewayNow),
        Sub2ApiAccountAvailability = new Sub2ApiAccountAvailabilitySummary(
            Sub2ApiQuotaStatus.Available,
            Sub2ApiAccountAvailabilityCoverage.Complete,
            5,
            5,
            64,
            3.2,
            [
                new Sub2ApiAccountAvailabilityEntry(1, Sub2ApiAccountAvailabilityState.Available, 92),
                new Sub2ApiAccountAvailabilityEntry(2, Sub2ApiAccountAvailabilityState.Available, 77),
                new Sub2ApiAccountAvailabilityEntry(3, Sub2ApiAccountAvailabilityState.Available, 64),
                new Sub2ApiAccountAvailabilityEntry(4, Sub2ApiAccountAvailabilityState.Available, 51),
                new Sub2ApiAccountAvailabilityEntry(5, Sub2ApiAccountAvailabilityState.Available, 36),
            ],
            aiGatewayNow),
    };
    var partialSub2ApiCard = sub2ApiCard with
    {
        Sub2ApiAccountAvailability = new Sub2ApiAccountAvailabilitySummary(
            Sub2ApiQuotaStatus.Available,
            Sub2ApiAccountAvailabilityCoverage.Partial,
            5,
            4,
            null,
            null,
            [
                new Sub2ApiAccountAvailabilityEntry(1, Sub2ApiAccountAvailabilityState.Available, 92),
                new Sub2ApiAccountAvailabilityEntry(2, Sub2ApiAccountAvailabilityState.Available, 77),
                new Sub2ApiAccountAvailabilityEntry(3, Sub2ApiAccountAvailabilityState.Available, 64),
                new Sub2ApiAccountAvailabilityEntry(4, Sub2ApiAccountAvailabilityState.Available, 51),
                new Sub2ApiAccountAvailabilityEntry(5, Sub2ApiAccountAvailabilityState.Unavailable, null),
            ],
            aiGatewayNow),
    };
    var knownNoneSub2ApiCard = sub2ApiCard with
    {
        Sub2ApiAccountAvailability = new Sub2ApiAccountAvailabilitySummary(
            Sub2ApiQuotaStatus.Available,
            Sub2ApiAccountAvailabilityCoverage.None,
            5,
            0,
            null,
            null,
            [
                new Sub2ApiAccountAvailabilityEntry(1, Sub2ApiAccountAvailabilityState.Unavailable, null),
                new Sub2ApiAccountAvailabilityEntry(2, Sub2ApiAccountAvailabilityState.Unavailable, null),
                new Sub2ApiAccountAvailabilityEntry(3, Sub2ApiAccountAvailabilityState.Unavailable, null),
                new Sub2ApiAccountAvailabilityEntry(4, Sub2ApiAccountAvailabilityState.Unavailable, null),
                new Sub2ApiAccountAvailabilityEntry(5, Sub2ApiAccountAvailabilityState.Unavailable, null),
            ],
            aiGatewayNow),
    };
    var unavailableSub2ApiCard = sub2ApiCard with
    {
        Sub2ApiAccountAvailability = new Sub2ApiAccountAvailabilitySummary(
            Sub2ApiQuotaStatus.Unavailable,
            Sub2ApiAccountAvailabilityCoverage.None,
            null,
            null,
            null,
            null,
            null,
            null),
        Sub2ApiQuota = null,
        Sub2ApiUsage = null,
        Sub2ApiPool = null,
    };
    var legacySub2ApiCard = sub2ApiCard with { Sub2ApiAccountAvailability = null };
    var genericApiCard = new QuotaCard(
        "codex.generic-api",
        ProviderKind.Codex,
        "API · 1",
        "API key",
        "#10a37f",
        true,
        [new QuotaWindow("API", null, null, TimeSpan.Zero)])
    {
        CapturedAt = aiGatewayNow,
        IsService = true,
        ServiceCount = 1,
        ServiceDisplayName = "other-api",
    };
    void RenderSub2ApiMiniScenarios()
    {
        var scenarios = new[]
        {
            (Name: "complete", Card: sub2ApiCard),
            (Name: "partial", Card: partialSub2ApiCard),
            (Name: "known-none", Card: knownNoneSub2ApiCard),
            (Name: "unavailable", Card: unavailableSub2ApiCard),
            (Name: "legacy-complete", Card: legacySub2ApiCard),
            (Name: "generic-api", Card: genericApiCard),
        };
        foreach (var locale in new[] { "en", "zh-CN" })
        {
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                foreach (var scenario in scenarios)
                {
                    using var form = new BarForm(
                        new AppSettings
                        {
                            Locale = locale,
                            EnableAnimations = false,
                            EnableRadar = false,
                        },
                        new QuotaSnapshot(
                            [scenario.Card],
                            [new ProviderHealth(ProviderKind.Codex, true, "synthetic", ProviderHealthCode.Current)],
                            aiGatewayNow),
                        renderOnly: true,
                        renderDpi: dpi,
                        activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
                    form.CreateControl();
                    using var bitmap = new Bitmap(
                        form.ClientSize.Width,
                        form.ClientSize.Height,
                        PixelFormat.Format32bppPArgb);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    var path = Path.GetFullPath(Path.Combine(
                        outputDirectory,
                        $"taskbar-mini-sub2api-{scenario.Name}-{locale}-{dpi}dpi.png"));
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} service={scenario.Name}");
                }
            }
        }
    }
    RenderSub2ApiMiniScenarios();
    foreach (var locale in new[] { "en", "zh-CN" })
    {
        foreach (var dpi in new[] { 96, 192 })
        {
            using var form = new BarForm(
                new AppSettings
                {
                    Locale = locale,
                    EnableAnimations = false,
                    EnableRadar = false,
                },
                new QuotaSnapshot(
                    [sub2ApiCard],
                    [new ProviderHealth(ProviderKind.Codex, false, "API service", ProviderHealthCode.Unavailable)],
                    aiGatewayNow),
                renderOnly: true,
                renderDpi: dpi,
                activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
            form.CreateControl();
            using var bitmap = new Bitmap(
                form.ClientSize.Width,
                form.ClientSize.Height,
                PixelFormat.Format32bppPArgb);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var path = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"taskbar-mini-sub2api-{locale}-{dpi}dpi.png"));
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} service=sub2api availability=64 pool=4/12");
        }
    }

    var stackedSub2ApiSnapshot = new QuotaSnapshot(
        [
            new QuotaCard(
                "codex.plus",
                ProviderKind.Codex,
                "Codex · 1",
                "plus",
                "#10a37f",
                true,
                [new QuotaWindow("7d", 31, aiGatewayNow.AddDays(4), TimeSpan.FromDays(7))]),
            sub2ApiCard,
        ],
        [new ProviderHealth(ProviderKind.Codex, true, "current", ProviderHealthCode.Current)],
        aiGatewayNow);
    foreach (var locale in new[] { "en", "zh-CN" })
    {
        foreach (var dpi in new[] { 96, 192 })
        {
            using var form = new BarForm(
                new AppSettings
                {
                    Locale = locale,
                    EnableAnimations = false,
                    EnableRadar = false,
                },
                stackedSub2ApiSnapshot,
                renderOnly: true,
                renderDpi: dpi,
                activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex });
            form.CreateControl();
            using var bitmap = new Bitmap(
                form.ClientSize.Width,
                form.ClientSize.Height,
                PixelFormat.Format32bppPArgb);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            var path = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"taskbar-mini-sub2api-stacked-{locale}-{dpi}dpi.png"));
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} service=sub2api-stacked availability=64 pool=4/12");
        }
    }

    using (var sub2ApiLogo = LoadNativeAsset("ZGSTokenBar.App.Assets.openai-official-ios-icon.png"))
    using (var sub2ApiResetClock = LoadNativeAsset("ZGSTokenBar.App.Assets.fluent-clock-20-regular.png"))
    {
        var popoverScenarios = new[]
        {
            (Name: "complete", Card: sub2ApiCard),
            (Name: "partial", Card: partialSub2ApiCard),
            (Name: "known-none", Card: knownNoneSub2ApiCard),
            (Name: "unavailable", Card: unavailableSub2ApiCard),
            (Name: "legacy-complete", Card: legacySub2ApiCard),
            (Name: "generic-api", Card: genericApiCard),
        };
        foreach (var locale in new[] { "en", "zh-CN" })
        {
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                foreach (var scenario in popoverScenarios)
                {
                    using var popover = new QuotaPopoverForm();
                    using var bitmap = popover.RenderForTest(
                        new QuotaPopoverContent(
                            scenario.Card,
                            scenario.Card.Windows.Single(),
                            null,
                            aiGatewayNow,
                            null,
                            false),
                        NativeText.For(locale),
                        sub2ApiLogo,
                        sub2ApiResetClock,
                        dpi,
                        aiGatewayNow);
                    var path = Path.GetFullPath(Path.Combine(
                        outputDirectory,
                        $"taskbar-mini-sub2api-popover-{scenario.Name}-{locale}-{dpi}dpi.png"));
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} service={scenario.Name}");
                }
            }
        }
    }

    var radarSurfaceSnapshot = LocalizationRadarSnapshot();
    var radarSurfaceCards = TaskbarMiniCaptureSnapshot(includeClaude: false).Cards
        .Append(aiGatewayCard)
        .ToArray();
    foreach (var dpi in new[] { 96, 192 })
    {
        using var form = new BarForm(
            new AppSettings
            {
                Locale = "zh-CN",
                EnableRadar = true,
                EnableAnimations = false,
            },
            new QuotaSnapshot(
                radarSurfaceCards,
                [new ProviderHealth(ProviderKind.Codex, true, "current", ProviderHealthCode.Current)],
                aiGatewayNow),
            radarProviders: [ProviderKind.Codex],
            renderOnly: true,
            renderDpi: dpi,
            codexAccounts: codexAccounts,
            activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex, ProviderKind.AiGateway },
            utcNow: () => aiGatewayNow);
        form.SetRadarState(new RadarViewState(
            radarSurfaceSnapshot,
            radarSurfaceSnapshot.CapturedAt,
            false,
            null,
            true,
            new HashSet<string>(StringComparer.Ordinal) { RadarSurfaceIds.DeepSeek }));
        form.CreateControl();
        using var bitmap = new Bitmap(
            form.ClientSize.Width,
            form.ClientSize.Height,
            PixelFormat.Format32bppPArgb);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var path = Path.GetFullPath(Path.Combine(
            outputDirectory,
            $"taskbar-mini-radar-surface-unread-zh-CN-{dpi}dpi.png"));
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} codex=read deepseek=unread");
    }

    var resetCaptureNow = DateTimeOffset.Parse("2026-08-23T16:00:00+08:00");
    var resetCaptureQuota = TaskbarMiniCaptureSnapshot(includeClaude: false);
    var resetCaptureScenarios = new[]
    {
        (
            Name: "exact",
            Window: new RadarResetWindow(
                true,
                resetCaptureNow.AddHours(-2),
                null,
                "https://x.com/example/status/123")
            {
                TargetAt = resetCaptureNow.AddHours(15).AddMinutes(42).AddSeconds(17),
            }),
        (
            Name: "estimated",
            Window: new RadarResetWindow(
                true,
                resetCaptureNow.AddHours(-2),
                null,
                "https://x.com/example/status/123")),
    };
    foreach (var locale in new[] { "en", "zh-CN" })
    {
        foreach (var dpi in new[] { 96, 192 })
        {
            foreach (var scenario in resetCaptureScenarios)
            {
                using var form = new BarForm(
                    new AppSettings
                    {
                        Locale = locale,
                        EnableRadar = true,
                        EnableAnimations = false,
                    },
                    resetCaptureQuota,
                    radarProviders: [ProviderKind.Codex],
                    renderOnly: true,
                    renderDpi: dpi,
                    codexAccounts: codexAccounts,
                    activeProviders: new HashSet<ProviderKind> { ProviderKind.Codex },
                    utcNow: () => resetCaptureNow);
                var snapshot = radarSurfaceSnapshot with { ResetWindow = scenario.Window };
                form.SetRadarState(new RadarViewState(
                    snapshot,
                    resetCaptureNow,
                    false,
                    null));
                form.CreateControl();
                using var bitmap = new Bitmap(
                    form.ClientSize.Width,
                    form.ClientSize.Height,
                    PixelFormat.Format32bppPArgb);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                var path = Path.GetFullPath(Path.Combine(
                    outputDirectory,
                    $"taskbar-mini-radar-reset-{scenario.Name}-{locale}-{dpi}dpi.png"));
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} reset={scenario.Name}");
            }
        }
    }

    var collapsedProvidersSnapshot = TaskbarMiniCaptureSnapshot(includeClaude: true);
    var allCollapsedProvidersSnapshot = new QuotaSnapshot(
        collapsedProvidersSnapshot.Cards.Append(aiGatewayCard).ToArray(),
        [
            new ProviderHealth(ProviderKind.Claude, true, "current", ProviderHealthCode.Current),
            new ProviderHealth(ProviderKind.Codex, true, "current", ProviderHealthCode.Current),
            new ProviderHealth(ProviderKind.AiGateway, true, "current", ProviderHealthCode.Current),
        ],
        aiGatewayNow);
    foreach (var dpi in new[] { 96, 192 })
    {
        var settings = new AppSettings
        {
            Locale = "zh-CN",
            UseTaskbarRings = true,
            EnableAnimations = false,
            EnableRadar = false,
            MiniAreaLayouts = new(StringComparer.Ordinal)
            {
                [MiniAreaIds.Claude] = new(true),
                [MiniAreaIds.Codex] = new(true),
                [MiniAreaIds.AiGateway] = new(true),
                [MiniAreaIds.SystemMetrics] = new(true),
            },
        };
        using var form = new BarForm(
            settings,
            allCollapsedProvidersSnapshot,
            renderOnly: true,
            renderDpi: dpi,
            codexAccounts: codexAccounts,
            activeProviders: new HashSet<ProviderKind>
            {
                ProviderKind.Claude,
                ProviderKind.Codex,
                ProviderKind.AiGateway,
            });
        form.SetSystemUsage(systemUsage);
        form.CreateControl();
        using var bitmap = new Bitmap(
            form.ClientSize.Width,
            form.ClientSize.Height,
            PixelFormat.Format32bppPArgb);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var path = Path.GetFullPath(Path.Combine(
            outputDirectory,
            $"taskbar-mini-all-provider-cards-collapsed-zh-CN-{dpi}dpi.png"));
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} collapsed=all-provider-areas");
    }

    foreach (var dpi in new[] { 96, 192 })
    {
        var settings = new AppSettings
        {
            Locale = "zh-CN",
            UseTaskbarRings = true,
            EnableAnimations = false,
            EnableRadar = false,
            MiniAreaLayouts = new(StringComparer.Ordinal)
            {
                [MiniAreaIds.Claude] = new(true),
                [MiniAreaIds.Codex] = new(false, 200),
                [MiniAreaIds.SystemMetrics] = new(false, 120),
            },
        };
        using var form = new BarForm(
            settings,
            collapsedProvidersSnapshot,
            renderOnly: true,
            renderDpi: dpi,
            codexAccounts: codexAccounts);
        form.SetSystemUsage(systemUsage);
        form.CreateControl();
        using var bitmap = new Bitmap(
            form.ClientSize.Width,
            form.ClientSize.Height,
            PixelFormat.Format32bppPArgb);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var path = Path.GetFullPath(Path.Combine(
            outputDirectory,
            $"taskbar-mini-independent-area-layout-zh-CN-{dpi}dpi.png"));
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} claude=collapsed codex=200 system=120");
    }

    var pluginIcon = LoadNativeAssetBytes("ZGSTokenBar.App.Assets.deepseek-whale-icon.png");
    foreach (var locale in new[] { "en", "zh-CN" })
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            foreach (var collapsed in new[] { false, true })
            {
                var settings = new AppSettings
                {
                    Locale = locale,
                    EnableAnimations = false,
                    MiniAreaLayouts = collapsed
                        ? new(StringComparer.Ordinal) { ["test.local-plugin"] = new(true) }
                        : new(StringComparer.Ordinal),
                };
                using var form = new BarForm(
                    settings,
                    new QuotaSnapshot([], [], aiGatewayNow),
                    renderOnly: true,
                    renderDpi: dpi,
                    activeProviders: new HashSet<ProviderKind>());
                var text = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["test.plugin.value"] = locale == "zh-CN" ? "可用余额" : "Available balance",
                };
                form.SetPluginMiniCards(
                [
                    new PluginMiniCardView(
                        "test.local-plugin",
                        locale == "zh-CN" ? "本地插件" : "Local plugin",
                        new(
                            "card.test.local-plugin",
                            "test.local-plugin",
                            "test",
                            ContributionKind.Balance,
                            0,
                            "test.plugin.title",
                            "test.plugin.icon",
                            "accent.test",
                            [new("test.plugin.value", new("currency", Text: "CNY", Decimal: 42.36m))]),
                        text,
                        pluginIcon),
                ]);
                form.CreateControl();
                using var bitmap = new Bitmap(
                    form.ClientSize.Width,
                    form.ClientSize.Height,
                    PixelFormat.Format32bppPArgb);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                var collapseSuffix = collapsed ? "-collapsed" : string.Empty;
                var path = Path.GetFullPath(Path.Combine(
                    outputDirectory,
                    $"taskbar-mini-plugin-{locale}{collapseSuffix}-{dpi}dpi.png"));
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} generic-plugin collapsed={collapsed}");
            }
        }
    }

    using (var aiGatewayLogo = LoadNativeAsset("ZGSTokenBar.App.Assets.deepseek-whale-icon.png"))
    using (var aiGatewayResetClock = LoadNativeAsset("ZGSTokenBar.App.Assets.fluent-clock-20-regular.png"))
    {
        foreach (var locale in new[] { "en", "zh-CN" })
        {
            using var popover = new QuotaPopoverForm();
            using var bitmap = popover.RenderForTest(
                new QuotaPopoverContent(
                    aiGatewayCard,
                    aiGatewayCard.Windows.Single(),
                    null,
                    aiGatewayNow,
                    null,
                    false,
                    aiGatewayUsage),
                NativeText.For(locale),
                aiGatewayLogo,
                aiGatewayResetClock,
                96,
                aiGatewayNow);
            var path = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"taskbar-mini-ai-gateway-popover-{locale}-96dpi.png"));
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height} service=ai-gateway-popover");
        }
    }

    var accountCard = TaskbarMiniCaptureSnapshot(false).Cards[0] with
    {
        AccountHint = "c***t@example.test",
    };
    var quotas = new[]
    {
        new CodexAccountQuota(
            "account-current",
            [
                new QuotaWindow("5h", 12, DateTimeOffset.UtcNow.AddHours(2), TimeSpan.FromHours(5)),
                new QuotaWindow("7d", 70, DateTimeOffset.UtcNow.AddDays(4), TimeSpan.FromDays(7)),
            ]),
        new CodexAccountQuota(
            "account-second",
            [new QuotaWindow("5h", 45, DateTimeOffset.UtcNow.AddHours(2), TimeSpan.FromHours(5))]),
        new CodexAccountQuota(
            "account-plus",
            [new QuotaWindow("7d", 2, DateTimeOffset.UtcNow.AddDays(4), TimeSpan.FromDays(7))]),
        new CodexAccountQuota(
            "account-plus-second",
            [new QuotaWindow("7d", 81, DateTimeOffset.UtcNow.AddDays(4), TimeSpan.FromDays(7))]),
        new CodexAccountQuota(
            "account-free",
            [new QuotaWindow("30d", 0, DateTimeOffset.UtcNow.AddDays(20), TimeSpan.FromDays(30))]),
    };
    foreach (var locale in new[] { "en", "zh-CN" })
    {
        foreach (var dpi in new[] { 96, 192 })
        {
            using var accountPopover = new CodexAccountsPopoverForm();
            using var accountBitmap = accountPopover.RenderForTest(
                codexAccounts,
                accountCard,
                NativeText.For(locale),
                QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette),
                dpi,
                quotas);
            var accountPath = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"taskbar-mini-codex-accounts-popover-{locale}-{dpi}dpi.png"));
            accountBitmap.Save(accountPath, ImageFormat.Png);
            Console.WriteLine($"{accountPath} {accountBitmap.Width}x{accountBitmap.Height}");
        }
    }

    foreach (var locale in new[] { "en", "zh-CN" })
    {
        var text = NativeText.For(locale);
        foreach (var dpi in new[] { 96, 192 })
        {
            using var hintPopover = new TaskbarHintPopoverForm();
            var detail = text.RefreshUpdatedDetail(TimeSpan.FromMinutes(1));
            using var hintBitmap = hintPopover.RenderForTest(
                text.RefreshNow,
                detail,
                QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette),
                dpi);
            var hintPath = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"taskbar-mini-hint-popover-{locale}-{dpi}dpi.png"));
            hintBitmap.Save(hintPath, ImageFormat.Png);
            Console.WriteLine($"{hintPath} {hintBitmap.Width}x{hintBitmap.Height}");

            using var pluginPopover = new TaskbarHintPopoverForm();
            var pluginTitle = locale == "zh-CN" ? "本地插件" : "Local plugin";
            var pluginDetail = locale == "zh-CN"
                ? "可用余额: ¥42.36\n缓存命中率: 83.1%"
                : "Available balance: ¥42.36\nCache hit rate: 83.1%";
            using var pluginBitmap = pluginPopover.RenderForTest(
                pluginTitle,
                pluginDetail,
                QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette),
                dpi);
            var pluginPath = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"taskbar-mini-plugin-popover-{locale}-{dpi}dpi.png"));
            pluginBitmap.Save(pluginPath, ImageFormat.Png);
            Console.WriteLine($"{pluginPath} {pluginBitmap.Width}x{pluginBitmap.Height}");
        }
    }

    var now = DateTimeOffset.Parse("2026-07-31T18:00:00+08:00");
    var card = new QuotaCard(
        "codex.1",
        ProviderKind.Codex,
        "Codex · 1",
        "pro",
        "#10a37f",
        true,
        [new QuotaWindow("7d", 29, now.AddDays(4), TimeSpan.FromDays(7))])
    {
        AccountHint = "c***t@example.test",
        CapturedAt = now,
    };
    var stablePace = new QuotaPaceEstimate(
        QuotaPaceStatus.ProjectedExhaustion,
        new QuotaCyclePace(
            21,
            8,
            .82,
            now.AddHours(86),
            false,
            .9),
        new QuotaRecentTrend(
            TimeSpan.FromHours(1),
            8.4,
            now.AddHours(8).AddMinutes(27),
            false,
            QuotaTrendConfidence.Stable),
        TimeSpan.FromHours(1),
        now.AddMinutes(15));
    var provisionalProjectedAt = now.AddHours((100 - 29) / 8d);
    var provisionalResetsFirst = provisionalProjectedAt >= card.Windows[0].ResetsAt;
    var provisionalPace = new QuotaPaceEstimate(
        provisionalResetsFirst
            ? QuotaPaceStatus.ResetsBeforeExhaustion
            : QuotaPaceStatus.ProjectedExhaustion,
        new QuotaCyclePace(40, -11, .6, now.AddDays(5), true, 1.4),
        new QuotaRecentTrend(
            TimeSpan.FromMinutes(15),
            8,
            provisionalProjectedAt,
            provisionalResetsFirst,
            QuotaTrendConfidence.Provisional),
        TimeSpan.FromMinutes(15),
        now.AddMinutes(15));
    var cyclePace = new QuotaPaceEstimate(
        QuotaPaceStatus.ProjectedExhaustion,
        new QuotaCyclePace(24, 5, 4, now.AddHours(17).AddMinutes(45), false, .8),
        ValidUntil: now.AddMinutes(15));
    var quietPace = new QuotaPaceEstimate(
        QuotaPaceStatus.NoMeaningfulConsumption,
        new QuotaCyclePace(29, 0, .8, now.AddHours(100), true, 1.1),
        ObservedSpan: TimeSpan.FromHours(1),
        ValidUntil: now.AddMinutes(15));
    var noResetCard = card with
    {
        Key = "codex.no-reset",
        Windows = [new QuotaWindow("7d", 29, null, TimeSpan.FromDays(7))],
    };
    var midCycleCard = card with
    {
        Key = "codex.mid-cycle",
        Windows = [new QuotaWindow("7d", 66, now.AddDays(4), TimeSpan.FromDays(7))],
    };
    var midCyclePace = new QuotaPaceTracker().Estimate(
        midCycleCard,
        midCycleCard.Windows[0],
        now,
        5);
    var blockedCard = new QuotaCard(
        "claude.blocked",
        ProviderKind.Claude,
        "Claude",
        "pro",
        "#d97757",
        true,
        [new QuotaWindow("5h", 29, now.AddHours(2), TimeSpan.FromHours(5))])
    {
        CapturedAt = now,
    };
    var scenarios = new (string Name, QuotaPopoverContent Content)[]
    {
        ("stable-over", new QuotaPopoverContent(card, card.Windows[0], null, now, stablePace, false)),
        ("provisional-reserve", new QuotaPopoverContent(card, card.Windows[0], null, now, provisionalPace, false)),
        ("cycle-fallback", new QuotaPopoverContent(card, card.Windows[0], null, now, cyclePace, false)),
        ("quiet", new QuotaPopoverContent(card, card.Windows[0], null, now, quietPace, false)),
        ("token-capacity", new QuotaPopoverContent(
            card,
            card.Windows[0],
            null,
            now,
            stablePace,
            false,
            null,
            new CodexQuotaTokenSummary(
                card.Key,
                "7d",
                TimeSpan.FromDays(7).Ticks,
                12_400_000,
                15_800_000,
                13_100_000,
                3,
                true,
                3_596_000,
                29,
                true,
                RecentWeeklyAverageTokens: 5_000_000_000))),
        ("token-capacity-mid-cycle", new QuotaPopoverContent(
            midCycleCard,
            midCycleCard.Windows[0],
            null,
            now,
            midCyclePace,
            false,
            null,
            new CodexQuotaTokenSummary(
                midCycleCard.Key,
                "7d",
                TimeSpan.FromDays(7).Ticks,
                4_311_063_479,
                null,
                null,
                0,
                true,
                1_638_204_122,
                38,
                false,
                RecentWeeklyAverageTokens: 5_000_000_000))),
        ("no-reset", new QuotaPopoverContent(
            noResetCard,
            noResetCard.Windows[0],
            null,
            now,
            new QuotaPaceEstimate(QuotaPaceStatus.Learning),
            false)),
        ("weekly-blocked", new QuotaPopoverContent(
            blockedCard,
            blockedCard.Windows[0],
            now.AddDays(3),
            now,
            new QuotaPaceEstimate(QuotaPaceStatus.WeeklyBlocked),
            false)),
    };
    using var providerLogo = LoadNativeAsset(
        "ZGSTokenBar.App.Assets.openai-official-ios-icon.png");
    using var claudeLogo = LoadNativeAsset(
        "ZGSTokenBar.App.Assets.claude-icon-rounded.png");
    using var resetClockIcon = LoadNativeAsset(
        "ZGSTokenBar.App.Assets.fluent-clock-20-regular.png");
    foreach (var scenario in scenarios)
    {
        ValidateCapturePaceScenario(scenario.Content);
    }
    foreach (var locale in new[] { "zh-CN", "en" })
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            foreach (var scenario in scenarios)
            {
                using var popover = new QuotaPopoverForm();
                using var bitmap = popover.RenderForTest(
                    scenario.Content,
                    NativeText.For(locale),
                    scenario.Content.Card.Provider == ProviderKind.Claude
                        ? claudeLogo
                        : providerLogo,
                    resetClockIcon,
                    dpi,
                    now);
                var suffix = scenario.Name == "stable-over" ? string.Empty : $"-{scenario.Name}";
                var path = Path.GetFullPath(Path.Combine(
                    outputDirectory,
                    $"taskbar-mini-quota-popover-{locale}-{dpi}dpi{suffix}.png"));
                bitmap.Save(path, ImageFormat.Png);
                Console.WriteLine($"{path} {bitmap.Width}x{bitmap.Height}");
            }

            var tokenUsage = new CodexTokenUsageSummary(
                12_345_678,
                172_600_000_000,
                2_179,
                now,
                337_671_064,
                332_691_712,
                12_000_000,
                11_400_000);
            var tokenLayout = RadarPopoverLayout.CreateTokenUsage(dpi);
            using var tokenBitmap = new Bitmap(
                tokenLayout.BodySize.Width,
                tokenLayout.BodySize.Height + tokenLayout.TailSize,
                PixelFormat.Format32bppPArgb);
            using (var tokenGraphics = Graphics.FromImage(tokenBitmap))
            using (var renderer = new RadarPopoverRenderer())
            {
                tokenGraphics.Clear(Color.FromArgb(2, 6, 23));
                renderer.Draw(
                    tokenGraphics,
                    tokenLayout,
                    PopoverTailSide.Bottom,
                    tokenLayout.BodySize.Width / 2,
                    new RadarViewState(null, null, false, null),
                    null,
                    providerLogo,
                    NativeText.For(locale),
                    tokenUsage);
            }
            var tokenPath = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"taskbar-mini-codex-token-popover-{locale}-{dpi}dpi.png"));
            tokenBitmap.Save(tokenPath, ImageFormat.Png);
            Console.WriteLine($"{tokenPath} {tokenBitmap.Width}x{tokenBitmap.Height}");

            using var systemPopover = new SystemUsagePopoverForm();
            using var systemBitmap = systemPopover.RenderForTest(
                new SystemUsagePopoverContent(systemUsage, false),
                NativeText.For(locale),
                QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette),
                dpi);
            var systemPath = Path.GetFullPath(Path.Combine(
                outputDirectory,
                $"taskbar-mini-system-usage-popover-{locale}-{dpi}dpi.png"));
            systemBitmap.Save(systemPath, ImageFormat.Png);
            Console.WriteLine($"{systemPath} {systemBitmap.Width}x{systemBitmap.Height}");
        }
    }

    var economySnapshot = TaskbarMiniCaptureSnapshot(includeClaude: true);
    using var economyForm = new BarForm(
        new AppSettings
        {
            Locale = "zh-CN",
            UseTaskbarRings = true,
            EnableAnimations = false,
            EnableRadar = false,
        },
        economySnapshot,
        renderOnly: true,
        renderDpi: 96,
        codexAccounts: codexAccounts);
    economyForm.SetCodexEconomyStatus(new CodexEconomyStatus(
        CodexEconomyMode.Ask,
        new CodexEconomyProfile(
            "Codex default",
            Path.Combine(Path.GetTempPath(), "wmt-mini-economy-menu-capture"),
            true,
            "capture"),
        true,
        false,
        null));
    economyForm.SetSystemUsage(systemUsage);
    economyForm.CreateControl();
    using var economyBarBitmap = new Bitmap(
        economyForm.ClientSize.Width,
        economyForm.ClientSize.Height,
        PixelFormat.Format32bppPArgb);
    economyForm.DrawToBitmap(economyBarBitmap, new Rectangle(Point.Empty, economyBarBitmap.Size));
    using var economyMenu = economyForm.CreateCodexEconomyMenuForAcceptance();
    economyMenu.CreateControl();
    economyMenu.PerformLayout();
    using var economyMenuBitmap = new Bitmap(
        economyMenu.Width,
        economyMenu.Height,
        PixelFormat.Format32bppPArgb);
    economyMenu.DrawToBitmap(economyMenuBitmap, new Rectangle(Point.Empty, economyMenuBitmap.Size));
    using var economyComposite = new Bitmap(
        economyBarBitmap.Width,
        economyMenuBitmap.Height + 4 + economyBarBitmap.Height,
        PixelFormat.Format32bppPArgb);
    using (var graphics = Graphics.FromImage(economyComposite))
    {
        graphics.Clear(Color.FromArgb(2, 6, 23));
        graphics.DrawImageUnscaled(
            economyMenuBitmap,
            Math.Max(0, economyBarBitmap.Width - economyMenuBitmap.Width - 32),
            0);
        graphics.DrawImageUnscaled(economyBarBitmap, 0, economyMenuBitmap.Height + 4);
    }
    var economyPath = Path.GetFullPath(Path.Combine(
        outputDirectory,
        "taskbar-mini-codex-economy-menu-zh-CN-96dpi.png"));
    economyComposite.Save(economyPath, ImageFormat.Png);
    Console.WriteLine($"{economyPath} {economyComposite.Width}x{economyComposite.Height}");
}

static void ValidateCapturePaceScenario(QuotaPopoverContent content)
{
    if (content.Window.ResetsAt is not { } reset || content.Pace is not { } pace) return;

    if (pace.Recent is { ProjectedExhaustedAt: { } recentProjected } recent)
    {
        var resetsFirst = recentProjected >= reset;
        if (recent.ResetsBeforeExhaustion != resetsFirst)
        {
            throw new InvalidOperationException("capture recent trend contradicts its reset time");
        }

        var expectedStatus = resetsFirst
            ? QuotaPaceStatus.ResetsBeforeExhaustion
            : QuotaPaceStatus.ProjectedExhaustion;
        if (pace.Status != expectedStatus)
        {
            throw new InvalidOperationException("capture pace status contradicts its recent projection");
        }

        if (content.Window.UsedPercent is { } used && recent.PercentPerHour > 0)
        {
            var projectedFromRate = content.CapturedAt.AddHours((100 - used) / recent.PercentPerHour);
            if ((projectedFromRate - recentProjected).Duration() > TimeSpan.FromMinutes(1))
            {
                throw new InvalidOperationException("capture recent rate contradicts its exhaustion time");
            }
        }
    }

    if (pace.Cycle is { ProjectedExhaustedAt: { } cycleProjected } cycle
        && cycle.ResetsBeforeExhaustion != (cycleProjected >= reset))
    {
        throw new InvalidOperationException("capture cycle pace contradicts its reset time");
    }
}

static Image LoadNativeAsset(string logicalName)
{
    using var stream = typeof(BarForm).Assembly.GetManifestResourceStream(logicalName)
        ?? throw new InvalidOperationException($"Missing embedded asset: {logicalName}");
    using var image = Image.FromStream(stream);
    return new Bitmap(image);
}

static byte[] LoadNativeAssetBytes(string logicalName)
{
    using var stream = typeof(BarForm).Assembly.GetManifestResourceStream(logicalName)
        ?? throw new InvalidOperationException($"Missing embedded asset: {logicalName}");
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    return buffer.ToArray();
}

static QuotaSnapshot FourCodexPoolSnapshot(DateTimeOffset now)
{
    var weeklyUsed = new[] { 10d, 30d, 50d, 70d };
    var fiveHourUsed = new[] { 5d, 15d, 25d, 35d };
    var cards = Enumerable.Range(0, 4)
        .Select(index => new QuotaCard(
            $"codex.{index + 1}",
            ProviderKind.Codex,
            $"Codex · {index + 1}",
            "pro",
            "#10a37f",
            index == 0,
            [
                new QuotaWindow(
                    "5h",
                    fiveHourUsed[index],
                    now.AddHours(4 - index),
                    TimeSpan.FromHours(5)),
                new QuotaWindow(
                    "7d",
                    weeklyUsed[index],
                    now.AddDays(4 - index),
                    TimeSpan.FromDays(7)),
            ])
        {
            CapturedAt = now,
        })
        .ToArray();
    return new QuotaSnapshot(
        cards,
        [new ProviderHealth(ProviderKind.Codex, true, "current", ProviderHealthCode.Current)],
        now)
    {
        CodexAccounts = cards
            .Select((card, index) => new CodexAccountQuota(
                $"pool-account-{index + 1}",
                card.Windows,
                now)
            {
                CardKey = card.Key,
            })
            .ToArray(),
    };
}

static QuotaSnapshot TaskbarMiniCaptureSnapshot(bool includeClaude, int codexCount = 2)
{
    var now = DateTimeOffset.UtcNow;
    var cards = new List<QuotaCard>();
    if (includeClaude)
    {
        cards.Add(new QuotaCard(
            "claude",
            ProviderKind.Claude,
            "Claude",
            "pro",
            "#d97757",
            true,
            [
                new QuotaWindow("5h", 29, now.AddHours(3), TimeSpan.FromHours(5)),
                new QuotaWindow("1w", 30, now.AddDays(5), TimeSpan.FromDays(7)),
                new QuotaWindow("Fable", 48, now.AddDays(5), TimeSpan.FromDays(7)),
            ]));
    }

    cards.Add(new QuotaCard(
        "codex.1",
        ProviderKind.Codex,
        "Codex · 1",
        "pro",
        "#10a37f",
        true,
        [new QuotaWindow("7d", 7, now.AddDays(4), TimeSpan.FromDays(7))]));
    cards.Add(new QuotaCard(
        "codex.2",
        ProviderKind.Codex,
        "Codex · 2",
        "pro",
        "#10a37f",
        false,
        [
            new QuotaWindow("5h", 12, now.AddHours(2), TimeSpan.FromHours(5)),
            new QuotaWindow("7d", 100, now.AddDays(2), TimeSpan.FromDays(7)),
        ]));
    if (codexCount >= 3)
    {
        cards.Add(new QuotaCard(
            "codex.3",
            ProviderKind.Codex,
            "Codex · 3",
            "plus",
            "#10a37f",
            false,
            [new QuotaWindow("7d", 24, now.AddDays(6), TimeSpan.FromDays(7))]));
    }
    if (codexCount >= 4)
    {
        cards.Add(new QuotaCard(
            "codex.4",
            ProviderKind.Codex,
            "Codex · 4",
            "plus",
            "#10a37f",
            false,
            [new QuotaWindow("7d", 48, now.AddDays(3), TimeSpan.FromDays(7))]));
    }
    return new QuotaSnapshot(
        cards,
        [
            new ProviderHealth(ProviderKind.Codex, true, "current", ProviderHealthCode.Current),
        ],
        now);
}

static void RenderLocalizationCaptures(string outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    var snapshot = LocalizationRadarSnapshot();
    var state = new RadarViewState(snapshot, snapshot.CapturedAt, false, null);
    var fullPresentation = RadarPresentation.Build(snapshot);
    var presentation = RadarPresentation.CodexOnly(fullPresentation);
    var deepSeekPresentation = RadarPresentation.DeepSeekOnly(fullPresentation);
    using var deepSeekLogo = LoadNativeAsset("ZGSTokenBar.App.Assets.deepseek-whale-icon.png");

    foreach (var locale in new[] { "zh-CN", "en" })
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            Bitmap? settingsBitmap = null;
            if (dpi == 96)
            {
                var settings = new AppSettings
                {
                    Locale = locale,
                    EnableRadar = true,
                    EnableRadarAlerts = true,
                };
                using var settingsForm = new SettingsForm(settings, dpi);
                settingsForm.Location = new Point(-20_000, -20_000);
                settingsForm.Show();
                System.Windows.Forms.Application.DoEvents();
                settingsBitmap = new Bitmap(
                    settingsForm.Width,
                    settingsForm.Height,
                    PixelFormat.Format32bppPArgb);
                settingsForm.DrawToBitmap(
                    settingsBitmap,
                    new Rectangle(Point.Empty, settingsBitmap.Size));
                settingsForm.Hide();
            }

            var layout = RadarPopoverLayout.Create(
                dpi,
                presentation.Rows.Select(row => row.Model.Model).ToArray(),
                false,
                snapshot.ResetWindow?.Open == true);
            var radarSize = new Size(
                layout.BodySize.Width,
                layout.BodySize.Height + layout.TailSize);
            foreach (var pinned in new[] { false, true })
            {
                using var radarBitmap = new Bitmap(
                    radarSize.Width,
                    radarSize.Height,
                    PixelFormat.Format32bppPArgb);
                using (var radarGraphics = Graphics.FromImage(radarBitmap))
                using (var renderer = new RadarPopoverRenderer())
                {
                    radarGraphics.Clear(Color.FromArgb(2, 6, 23));
                    renderer.Draw(
                        radarGraphics,
                        layout,
                        PopoverTailSide.Bottom,
                        layout.BodySize.Width / 2,
                        state,
                        presentation,
                        null,
                        NativeText.For(locale),
                        new CodexTokenUsageSummary(
                            12_345_678,
                            172_600_000_000,
                            2_179,
                            snapshot.CapturedAt,
                            337_671_064,
                            332_691_712,
                            12_000_000,
                            11_400_000),
                        null,
                        pinned);
                }

                var gap = settingsBitmap is null ? 0 : Math.Max(16, dpi / 6);
                var settingsWidth = settingsBitmap?.Width ?? 0;
                var settingsHeight = settingsBitmap?.Height ?? 0;
                using var composite = new Bitmap(
                    settingsWidth + gap + radarBitmap.Width,
                    Math.Max(settingsHeight, radarBitmap.Height),
                    PixelFormat.Format32bppPArgb);
                using (var graphics = Graphics.FromImage(composite))
                {
                    graphics.Clear(Color.FromArgb(2, 6, 23));
                    if (settingsBitmap is not null)
                    {
                        graphics.DrawImageUnscaled(settingsBitmap, 0, 0);
                    }
                    graphics.DrawImageUnscaled(radarBitmap, settingsWidth + gap, 0);
                }

                var suffix = pinned ? "-pinned" : string.Empty;
                var path = Path.Combine(
                    outputDirectory,
                    $"native-localization-{locale}-{dpi}dpi{suffix}.png");
                composite.Save(path, ImageFormat.Png);
                Console.WriteLine(path);

                var deepSeekLayout = RadarPopoverLayout.Create(
                    dpi,
                    deepSeekPresentation.Rows.Select(row => row.Model.Model).ToArray(),
                    false,
                    snapshot.ResetWindow?.Open == true);
                var deepSeekSize = new Size(
                    deepSeekLayout.BodySize.Width,
                    deepSeekLayout.BodySize.Height + deepSeekLayout.TailSize);
                using var deepSeekBitmap = new Bitmap(
                    deepSeekSize.Width,
                    deepSeekSize.Height,
                    PixelFormat.Format32bppPArgb);
                using (var deepSeekGraphics = Graphics.FromImage(deepSeekBitmap))
                using (var renderer = new RadarPopoverRenderer())
                {
                    deepSeekGraphics.Clear(Color.FromArgb(2, 6, 23));
                    renderer.Draw(
                        deepSeekGraphics,
                        deepSeekLayout,
                        PopoverTailSide.Bottom,
                        deepSeekLayout.BodySize.Width / 2,
                        state,
                        deepSeekPresentation,
                        deepSeekLogo,
                        NativeText.For(locale),
                        null,
                        new AiGatewayUsageSummary(
                            "CNY",
                            AiGatewayBalanceStatus.Available,
                            new AiGatewayUsagePeriod(8, 7_000, 1_500, 8_500, 5_600, 1_400, 0, 80m, 0.0043m),
                            new AiGatewayUsagePeriod(91, 120_000, 24_000, 144_000, 98_000, 20_000, 2_000, 83.05m, 0.0723m),
                            snapshot.CapturedAt),
                        pinned,
                        NativeText.For(locale).DeepSeekRadarTitle);
                }
                var deepSeekPath = Path.Combine(
                    outputDirectory,
                    $"native-localization-deepseek-{locale}-{dpi}dpi{suffix}.png");
                deepSeekBitmap.Save(deepSeekPath, ImageFormat.Png);
                Console.WriteLine(deepSeekPath);
            }
            settingsBitmap?.Dispose();
        }
    }
}

static void RenderSettingsCaptures(string outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    foreach (var locale in new[] { "zh-CN", "en" })
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            foreach (var viewport in new[] { "default", "constrained" })
            {
                var constrained = string.Equals(viewport, "constrained", StringComparison.Ordinal);
                var settings = new AppSettings
                {
                    Locale = locale,
                    EnableRadar = true,
                    EnableRadarAlerts = true,
                    OpenAtLogin = true,
                    EnableAnimations = true,
                    BackgroundPalette = "midnight",
                };
                using var form = new SettingsForm(
                    settings,
                    dpi,
                    renderOnly: true,
                    renderWorkingArea: constrained ? new Rectangle(0, 0, 1024, 720) : null,
                    codexEconomyStatus: new CodexEconomyStatus(
                        CodexEconomyMode.Ask,
                        new CodexEconomyProfile(
                            "Codex default",
                            Path.Combine(Path.GetTempPath(), "wmt-settings-capture-codex"),
                            true,
                            "capture"),
                        true,
                        false,
                        null));
                form.Show();
                System.Windows.Forms.Application.DoEvents();
                LayoutControlTree(form);

                form.ShowDirtyStateForRendering();
                form.SelectPageForRendering("general");
                form.ScrollViewport.AutoScrollPosition = Point.Empty;
                using var top = CaptureSettingsForm(form);

                form.SelectPageForRendering("advanced");
                form.ScrollViewport.AutoScrollPosition = Point.Empty;
                System.Windows.Forms.Application.DoEvents();
                using var advanced = CaptureSettingsForm(form);

                form.SelectPageForRendering("about");
                System.Windows.Forms.Application.DoEvents();
                using var bottom = CaptureSettingsForm(form);
                form.Hide();

                var gap = Math.Max(16, dpi / 6);
                using var composite = new Bitmap(
                    top.Width + gap + advanced.Width + gap + bottom.Width,
                    Math.Max(top.Height, Math.Max(advanced.Height, bottom.Height)),
                    PixelFormat.Format32bppPArgb);
                using (var graphics = Graphics.FromImage(composite))
                {
                    graphics.Clear(Color.FromArgb(2, 6, 23));
                    graphics.DrawImageUnscaled(top, 0, 0);
                    graphics.DrawImageUnscaled(advanced, top.Width + gap, 0);
                    graphics.DrawImageUnscaled(bottom, top.Width + gap + advanced.Width + gap, 0);
                }

                var path = Path.GetFullPath(Path.Combine(
                    outputDirectory,
                    $"settings-{locale}-{dpi}dpi-{viewport}.png"));
                composite.Save(path, ImageFormat.Png);
                Console.WriteLine($"{path} {composite.Width}x{composite.Height}");
            }
        }
    }
}

static Bitmap CaptureSettingsForm(SettingsForm form)
{
    var bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb);
    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    return bitmap;
}

static ProviderRadarSnapshot LocalizationRadarSnapshot()
{
    var capturedAt = DateTimeOffset.Parse("2026-07-28T16:40:00+08:00");
    var rows = new[]
    {
        new RadarModel("gpt-5.6-sol", "gpt-5.6-sol max", "max", 103.6, "green", 77, 112, 9.06, 1920, "32分钟"),
        new RadarModel("gpt-5.6-sol", "GPT-5.6 Sol ultra", "ultra", 101.79, "green", 76, 112, 23.46, 2975.4, "50分钟"),
        new RadarModel("gpt-5.6-sol", "GPT-5.6 Sol xhigh", "xhigh", 90.1, "yellow", 67, 112, 7.34, 1620, "27分钟"),
        new RadarModel("gpt-5.6-sol", "GPT-5.6 Sol high", "high", 91.5, "yellow", 68, 112, 5.14, 1320, "22分钟"),
        new RadarModel("gpt-5.6-sol", "GPT-5.6 Sol medium", "medium", 94.2, "yellow", 70, 112, 3.66, 1020, "17分钟"),
        new RadarModel("gpt-5.6-sol", "GPT-5.6 Sol low", "low", 72.6, "red", 54, 112, 1.98, 600, "10分钟"),
        new RadarModel("gpt-5.6-terra", "GPT-5.6 Terra ultra", "ultra", 100.45, "green", 75, 112, 13.47, 2559, "43分钟"),
        new RadarModel("gpt-5.6-terra", "GPT-5.6 Terra max", "max", 90.1, "yellow", 67, 112, 4.76, 1800, "30分钟"),
        new RadarModel("gpt-5.6-terra", "GPT-5.6 Terra xhigh", "xhigh", 80.7, "yellow", 60, 112, 2.60, 1140, "19分钟"),
        new RadarModel("gpt-5.6-terra", "GPT-5.6 Terra high", "high", 65.9, "red", 49, 112, 1.34, 720, "12分钟"),
        new RadarModel("gpt-5.6-terra", "GPT-5.6 Terra medium", "medium", 57.6, null, 43, 112, 0.64, 544, "9分钟"),
        new RadarModel("gpt-5.6-luna", "GPT-5.6 Luna max", "max", 92.8, "yellow", 69, 112, 2.47, 1920, "32分钟"),
        new RadarModel("gpt-5.6-luna", "GPT-5.6 Luna high", "high", 74.0, "red", 55, 112, 1.06, 1020, "17分钟"),
        new RadarModel("gpt-5.5", "GPT-5.5 xhigh", "xhigh", 87.4, "yellow", 65, 112, 5.90, 1380, "23分钟"),
        new RadarModel("gpt-5.5", "GPT-5.5 high", "high", 79.4, "yellow", 59, 112, 3.73, 1038, "17分钟"),
        new RadarModel("deepseek-v4-flash", "DeepSeek V4 Flash max", "max", 79.0, null, 59, 112, 0.10, 1337, "22分钟"),
        new RadarModel("deepseek-v4-flash", "DeepSeek V4 Flash high", "high", 50.9, null, 38, 112, 0.098, 1451, "24分钟"),
        new RadarModel("dsh-deepseek-v4-flash", "dsh-deepseek-v4-flash max", "max", 150.0, null, 2, 2, null, 1199, null),
        new RadarModel("dsh-deepseek-v4-pro", "dsh-deepseek-v4-pro max", "max", 112.5, null, 3, 4, null, 2315, null),
        new RadarModel("gpt-5.6-luna", "GPT-5.6 Luna xhigh", "xhigh", 79.0, "yellow", 59, 112, 0.31, 1437, "24分钟"),
        new RadarModel("gpt-5.6-luna", "GPT-5.6 Luna medium", "medium", 33.5, "red", 25, 112, 0.08, 524, "9分钟"),
        new RadarModel("gpt-5.6-luna", "GPT-5.6 Luna low", "low", 5.4, "red", 4, 112, 0.03, 312, "5分钟"),
        new RadarModel("gpt-5.6-terra", "GPT-5.6 Terra low", "low", 37.5, "red", 28, 112, 0.49, 517, "9分钟"),
    };
    rows = rows
        .Select((model, index) => model with
        {
            IncompleteCostSamples = string.Equals(
                model.ReasoningEffort,
                "ultra",
                StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0,
            IqHistory = model.Score is { } score
                ? (index % 3) switch
                {
                    0 =>
                    new[]
                    {
                        new RadarIqSample(capturedAt.AddHours(-4), score - 2.0),
                        new RadarIqSample(capturedAt.AddHours(-2), score - 1.0),
                        new RadarIqSample(capturedAt, score),
                    },
                    1 =>
                    new[]
                    {
                        new RadarIqSample(capturedAt.AddHours(-4), score + 2.0),
                        new RadarIqSample(capturedAt.AddHours(-2), score + 1.0),
                        new RadarIqSample(capturedAt, score),
                    },
                    _ =>
                    new[]
                    {
                        new RadarIqSample(capturedAt.AddHours(-4), score - 1.0),
                        new RadarIqSample(capturedAt.AddHours(-2), score),
                        new RadarIqSample(capturedAt, score + 1.0),
                    },
                }
                : [],
        })
        .ToArray();
    return new ProviderRadarSnapshot(
        ProviderKind.Codex,
        "model_iq:2026-07-28",
        capturedAt.AddHours(-3),
        capturedAt,
        rows[0],
        rows[1..])
    {
        ResetWindow = new RadarResetWindow(
            true,
            DateTimeOffset.Parse("2026-08-01T09:15:00+08:00"),
            null,
            "https://x.com/example/status/123")
        {
            Scope = "周一表演式重置预告",
        },
        RecommendationFeed = new RadarRecommendationFeed(
            1,
            "latest_valid_per_task",
            capturedAt,
            capturedAt,
            [
                new RadarRecommendationGroup(
                    "daily_development",
                    "日常开发",
                    "速度位与聪明位均直接来自 Codex Radar。",
                    [RecommendationItem(rows[13], "speed"), RecommendationItem(rows[2], "smart")]),
                new RadarRecommendationGroup(
                    "hard_problems",
                    "难题攻坚",
                    "按上游 IQ 排名直接展示。",
                    [RecommendationItem(rows[1]), RecommendationItem(rows[0])]),
                new RadarRecommendationGroup(
                    "background_automation",
                    "后台自动化",
                    "成本优先的上游推荐。",
                    [RecommendationItem(rows[11]), RecommendationItem(rows[4])]),
                new RadarRecommendationGroup(
                    "lobster_tasks",
                    "跑龙虾类任务",
                    "费用与耗时综合成本最低的上游推荐。",
                    [RecommendationItem(rows[10]), RecommendationItem(rows[12])]),
            ]),
    };
}

static RadarRecommendationItem RecommendationItem(
    RadarModel model,
    string? slot = null,
    string? rule = null) =>
    new(model, slot, rule, model.ValidTasks, model.ValidTasks, null);

static void TestNativeLocalization()
{
    var zh = NativeText.For("zh-CN");
    var en = NativeText.For("en");
    var model = new RadarModel(
        "gpt-5.6-sol",
        "GPT-5.6 Sol max",
        "max",
        103.6,
        "green",
        77,
        112,
        9.06,
        1920,
        "32分钟");
    var health = new ProviderHealth(
        ProviderKind.Codex,
        false,
        "legacy detail must not leak",
        ProviderHealthCode.MissingCredentials);
    var now = DateTimeOffset.Parse("2026-07-28T16:00:00+08:00");

    Equal("zh-CN", zh.Locale, "Chinese locale");
    Equal("en", en.Locale, "English locale");
    Equal("设置", zh.Settings, "Chinese settings");
    Equal("Settings", en.Settings, "English settings");
    Equal("常驻运行", zh.KeepRunning, "Chinese keep-running setting");
    Equal("Keep running", en.KeepRunning, "English keep-running setting");
    Equal("最强", zh.RadarStrongestTitle, "Chinese strongest title");
    Equal("STRONGEST", en.RadarStrongestTitle, "English strongest title");
    Equal("IQ", zh.RadarIqHeader, "Chinese current IQ header");
    Equal("IQ", en.RadarIqHeader, "English current IQ header");
    Equal("24H均", zh.RadarIqAverageHeader, "Chinese IQ average header");
    Equal("24H AVG", en.RadarIqAverageHeader, "English IQ average header");
    Equal("样本", zh.RadarSampleHeader, "Chinese sample-count header");
    Equal("N", en.RadarSampleHeader, "English sample-count header");
    Equal("状态未知", zh.RadarUnknownStatusLegend, "Chinese unknown-status legend");
    Equal("status unknown", en.RadarUnknownStatusLegend, "English unknown-status legend");
    Equal("N≥50 · 95%下界评分", zh.RadarConfidenceNote, "Chinese confidence note");
    Equal("N≥50 · 95% lower-bound picks", en.RadarConfidenceNote, "English confidence note");
    Equal("日常", zh.RadarDailyScenarioTitle, "Chinese daily scenario label");
    Equal("DAILY", en.RadarDailyScenarioTitle, "English daily scenario label");
    Equal("规划", zh.RadarPlanningScenarioTitle, "Chinese planning scenario label");
    Equal("PLAN", en.RadarPlanningScenarioTitle, "English planning scenario label");
    Equal("执行", zh.RadarExecutionScenarioTitle, "Chinese execution scenario label");
    Equal("EXEC", en.RadarExecutionScenarioTitle, "English execution scenario label");
    Equal("后台", zh.RadarBackgroundScenarioTitle, "Chinese background scenario label");
    Equal("BG", en.RadarBackgroundScenarioTitle, "English background scenario label");
    Equal("非官方 · 点击固定", zh.RadarPopoverSubtitle(false), "Chinese Radar preview subtitle");
    Equal("PINNED · ESC / CLICK OUTSIDE", en.RadarPopoverSubtitle(true), "English Radar pinned subtitle");
    Equal("本机日志 · 点击固定", zh.CodexTokenPopoverSubtitle(false), "Chinese token preview subtitle");
    Equal("PINNED · ESC / CLICK OUTSIDE", en.CodexTokenPopoverSubtitle(true), "English token pinned subtitle");
    Equal("关闭", zh.CodexEconomyModeName(CodexEconomyMode.Off), "Chinese economy Off mode");
    Equal("询问", zh.CodexEconomyModeName(CodexEconomyMode.Ask), "Chinese economy Ask mode");
    Equal("开启", zh.CodexEconomyModeName(CodexEconomyMode.On), "Chinese economy On mode");
    Equal("Off", en.CodexEconomyModeName(CodexEconomyMode.Off), "English economy Off mode");
    Equal("Ask", en.CodexEconomyModeName(CodexEconomyMode.Ask), "English economy Ask mode");
    Equal("On", en.CodexEconomyModeName(CodexEconomyMode.On), "English economy On mode");
    Equal("应用", zh.CodexEconomyApply, "Chinese economy Apply action");
    Equal("Apply", en.CodexEconomyApply, "English economy Apply action");
    Equal(
        false,
        zh.CodexEconomyBarHint.Contains("Off", StringComparison.Ordinal),
        "Chinese economy Bar hint does not leak English mode names");
    Equal("受周额度限制", zh.WeeklyQuotaBlocked, "Chinese weekly quota block");
    Equal("blocked by weekly limit", en.WeeklyQuotaBlocked, "English weekly quota block");
    Equal(
        now.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
        en.RadarChecked(now, now),
        "compact checked time");
    var closedReset = new RadarResetWindow(
        false,
        null,
        DateTimeOffset.Parse("2026-08-01T11:32:37+08:00"),
        null);
    var localReset = closedReset.ClosedAt!.Value.ToLocalTime();
    Equal($"硬重置 · {localReset:M/d HH:mm}", zh.RadarResetWindow(closedReset, now), "Chinese reset time");
    Equal($"RESET · {localReset:MM-dd HH:mm}", en.RadarResetWindow(closedReset, now), "English reset time");
    var openedReset = closedReset with
    {
        Open = true,
        OpenedAt = DateTimeOffset.Parse("2026-08-01T09:15:00+08:00"),
    };
    var resetNow = DateTimeOffset.Parse("2026-08-01T12:00:00+08:00");
    Equal(
        "重置窗口已开启 · 推测重置 8/2 · 约1天",
        zh.RadarResetWindow(openedReset, resetNow),
        "Chinese open reset window fallback date");
    Equal(
        "RESET WINDOW OPEN · ESTIMATED RESET 08-02 · ~1d",
        en.RadarResetWindow(openedReset, resetNow),
        "English open reset window fallback date");
    Equal(
        "重置窗口已开启 · 时间未定",
        zh.RadarResetWindow(openedReset with { OpenedAt = null }, resetNow),
        "Chinese open reset window without time");
    var scopedReset = openedReset with { Scope = "周一表演式重置预告" };
    Equal(
        "重置窗口已开启 · 推测重置 8/3 · 约2天",
        zh.RadarResetWindow(scopedReset, resetNow),
        "Chinese scoped reset target");
    Equal(
        "RESET WINDOW OPEN · ESTIMATED RESET 08-03 · ~2d",
        en.RadarResetWindow(scopedReset, resetNow),
        "English scoped reset target");
    var exactTarget = DateTimeOffset.Parse("2026-08-03T09:00:00+08:00");
    var countdownNow = exactTarget.AddHours(-26).AddMinutes(-3).AddSeconds(-4);
    var targetedReset = scopedReset with { TargetAt = exactTarget };
    Equal(
        "重置窗口已开启 · 距离预计重置 26:03:04",
        zh.RadarResetWindow(targetedReset, countdownNow),
        "exact reset target becomes a cumulative-hour Chinese countdown");
    Equal(
        "RESET WINDOW OPEN · EXPECTED RESET IN 26:03:04",
        en.RadarResetWindow(targetedReset, countdownNow),
        "exact reset target becomes a cumulative-hour English countdown");
    var justBeforeTarget = exactTarget.AddMilliseconds(-500);
    Equal(
        "重置窗口已开启 · 距离预计重置 00:00:00",
        zh.RadarResetWindow(targetedReset, justBeforeTarget),
        "a positive subsecond remainder does not expire the countdown early");
    Equal(true, ProviderRadarPopoverForm.HasActiveResetCountdown(targetedReset, justBeforeTarget), "a positive subsecond remainder keeps the countdown timer active");
    Equal(
        "重置窗口已开启 · 等待官方确认重置完成",
        zh.RadarResetWindow(targetedReset, exactTarget),
        "countdown expiry waits for official reset confirmation");
    Equal(
        "RESET WINDOW OPEN · AWAITING OFFICIAL RESET CONFIRMATION",
        en.RadarResetWindow(targetedReset, exactTarget.AddSeconds(1)),
        "expired English countdown does not claim reset completion");
    Equal(true, ProviderRadarPopoverForm.HasActiveResetCountdown(targetedReset, countdownNow), "future reset target runs the countdown timer");
    Equal(false, ProviderRadarPopoverForm.HasActiveResetCountdown(targetedReset, exactTarget), "expired reset target stops the countdown timer");
    Equal(false, ProviderRadarPopoverForm.HasActiveResetCountdown(targetedReset with { Open = false }, countdownNow), "closed reset window has no active countdown");
    Equal("重置窗口已开启", zh.RadarResetWindowTitle(scopedReset), "Chinese reset banner title");
    Equal("RESET WINDOW OPEN", en.RadarResetWindowTitle(scopedReset), "English reset banner title");
    Equal("推测重置 8/3 · 约2天", zh.RadarResetWindowDetail(scopedReset, resetNow), "Chinese reset banner detail");
    Equal("ESTIMATED RESET 08-03 · ~2d", en.RadarResetWindowDetail(scopedReset, resetNow), "English reset banner detail");
    Equal("32分", zh.RadarAverageTime(model), "Chinese average time");
    Equal("32m", en.RadarAverageTime(model), "English average time");
    Equal("未找到 Codex OAuth 凭据。", zh.Health(health, now), "semantic Chinese health");
    Equal("Codex OAuth credentials were not found.", en.Health(health, now), "semantic English health");
    Equal("模型 5.6 Sol → 5.7 Sol", zh.RadarChange(
        new RadarAlertChange(RadarAlertChangeKind.Model, "5.6 Sol", "5.7 Sol")), "Chinese alert change");
    Equal("model 5.6 Sol → 5.7 Sol", en.RadarChange(
        new RadarAlertChange(RadarAlertChangeKind.Model, "5.6 Sol", "5.7 Sol")), "English alert change");
    Equal("😀…", NativeText.TruncateTextElements("😀中文", 3), "surrogate-safe truncation");

    var previous = new AppSettings { Locale = "zh-CN", EnableRadar = true };
    var localeOnly = new AppSettings { Locale = "en", EnableRadar = true };
    var localeAndRefresh = new AppSettings { Locale = "en", EnableRadar = true, RefreshMinutes = 10 };
    Equal(true, QuotaApplicationContext.IsLocaleOnlyChange(previous, localeOnly), "locale-only save avoids refresh");
    Equal(false, QuotaApplicationContext.IsLocaleOnlyChange(previous, localeAndRefresh), "other settings still refresh");
}

static void TestRadarPersistentResetMini()
{
    var now = DateTimeOffset.Parse("2026-08-23T16:00:00+08:00");
    var openedAt = DateTimeOffset.Parse("2026-08-23T14:11:36+08:00");
    var estimatedWindow = new RadarResetWindow(
        true,
        openedAt,
        null,
        "https://x.com/example/status/123");
    var estimated = RadarResetTiming.Resolve(estimatedWindow);
    Equal(RadarResetTimingKind.EstimatedDate, estimated.Kind, "missing exact reset time uses a date estimate");
    Equal(new DateOnly(2026, 8, 24), estimated.EstimatedDate, "date-only fallback is anchored to the Beijing opening date");
    Equal(false, estimated.EstimatedFromWeekday, "next-day fallback is distinguished from a scoped weekday");

    var mondayWindow = estimatedWindow with { Scope = "All plans expected Monday" };
    var monday = RadarResetTiming.Resolve(mondayWindow);
    Equal(new DateOnly(2026, 8, 24), monday.EstimatedDate, "English weekday scope maps to the first matching date");
    Equal(true, monday.EstimatedFromWeekday, "weekday-derived estimate is explicit");
    var tuesday = RadarResetTiming.Resolve(estimatedWindow with { Scope = "预计星期二重置" });
    Equal(new DateOnly(2026, 8, 25), tuesday.EstimatedDate, "Chinese weekday scope maps from the stable opening date");
    var sameDay = RadarResetTiming.Resolve(estimatedWindow with { Scope = "周日重置" });
    Equal(new DateOnly(2026, 8, 23), sameDay.EstimatedDate, "opening-day weekday does not roll to the following week");
    Equal(-1, sameDay.CalendarDaysUntil(now.AddDays(1)), "an overdue inferred date stays overdue instead of rolling forward");

    var exactTarget = now.AddHours(12).AddMinutes(34).AddSeconds(56);
    var exactWindow = mondayWindow with { TargetAt = exactTarget };
    var exact = RadarResetTiming.Resolve(exactWindow);
    Equal(RadarResetTimingKind.Exact, exact.Kind, "exact target has priority over inferred scope");
    Equal(exactTarget, exact.ExactTargetAt, "exact target is preserved without offset conversion");
    Equal(
        RadarResetTiming.ExactRefreshIntervalMilliseconds,
        RadarResetTiming.RefreshIntervalMilliseconds(exactWindow, now),
        "future exact target refreshes every second");
    Equal(
        RadarResetTiming.EstimatedDateRefreshIntervalMilliseconds,
        RadarResetTiming.RefreshIntervalMilliseconds(estimatedWindow, now),
        "date estimate refreshes at low frequency");
    Equal<int?>(null, RadarResetTiming.RefreshIntervalMilliseconds(exactWindow, exactTarget), "expired exact target stops ticking");
    Equal<int?>(null, RadarResetTiming.RefreshIntervalMilliseconds(estimatedWindow, now.AddDays(2)), "overdue date estimate stops ticking");
    Equal(RadarResetTimingKind.Unknown, RadarResetTiming.Resolve(estimatedWindow with { OpenedAt = null }).Kind, "missing opening anchor stays unknown");
    Equal(RadarResetTimingKind.Unknown, RadarResetTiming.Resolve(estimatedWindow with { Open = false }).Kind, "closed window has no active timing");
    Equal(
        RadarResetTimingKind.Unknown,
        RadarResetTiming.Resolve(estimatedWindow with { OpenedAt = DateTimeOffset.MaxValue }).Kind,
        "an opening instant outside the Beijing DateTime range degrades safely");
    Equal(
        RadarResetTimingKind.Unknown,
        RadarResetTiming.Resolve(estimatedWindow with
        {
            OpenedAt = DateTimeOffset.Parse("9999-12-31T00:00:00+08:00"),
        }).Kind,
        "a fallback date past DateOnly max degrades safely");

    var zh = NativeText.For("zh-CN");
    var en = NativeText.For("en");
    Equal("重置", zh.RadarResetMiniTitle(exactWindow, now), "exact Chinese Mini title");
    Equal("12:34:56", zh.RadarResetMiniValue(exactWindow, now), "exact Mini keeps cumulative-hour precision");
    Equal("推测 8/24", zh.RadarResetMiniTitle(estimatedWindow, now), "estimated Chinese Mini title includes the date");
    Equal("约1天", zh.RadarResetMiniValue(estimatedWindow, now), "estimated Chinese Mini uses calendar-day precision");
    Equal("EST 08-24", en.RadarResetMiniTitle(estimatedWindow, now), "estimated English Mini title includes the date");
    Equal("~1d", en.RadarResetMiniValue(estimatedWindow, now), "estimated English Mini uses calendar-day precision");
    Equal("今天", zh.RadarResetMiniValue(estimatedWindow with { Scope = "周日重置" }, now), "estimated date shows today without fake time precision");
    Equal("待确认", zh.RadarResetMiniValue(estimatedWindow, now.AddDays(2)), "overdue estimated date waits for confirmation");
    Equal("时间未定", zh.RadarResetMiniValue(estimatedWindow with { OpenedAt = null }, now), "missing date anchor is explicit");
    Equal("~1d", zh.RadarResetMiniCompact(estimatedWindow, now), "collapsed estimated Mini keeps an explicit approximation marker");
    Equal("~0d", zh.RadarResetMiniCompact(estimatedWindow with { Scope = "周日重置" }, now), "collapsed same-day Chinese estimate stays explicit and compact");
    Equal("~0d", en.RadarResetMiniCompact(estimatedWindow with { Scope = "Sunday" }, now), "collapsed same-day English estimate stays explicit and compact");

    var quotaSnapshot = TaskbarMiniCaptureSnapshot(includeClaude: true);
    RadarViewState StateFor(RadarResetWindow window)
    {
        var snapshot = RadarSnapshot("2026-08-23", "gpt-5.6-sol", "max", 100, "green") with
        {
            ResetWindow = window,
        };
        return new RadarViewState(snapshot, now, false, null);
    }

    using var form = new BarForm(
        new AppSettings
        {
            Locale = "zh-CN",
            EnableRadar = true,
            EnableAnimations = false,
        },
        quotaSnapshot,
        radarProviders: [ProviderKind.Codex],
        renderOnly: true,
        renderDpi: 96,
        utcNow: () => now);
    form.SetRadarState(StateFor(exactWindow));
    var areas = form.GetMiniAreaStates();
    var codexIndex = areas.ToList().FindIndex(area => area.AreaId == MiniAreaIds.Codex);
    Equal(MiniAreaIds.RadarReset, areas[codexIndex + 1].AreaId, "new reset area defaults immediately after Codex");
    Equal(TaskbarMiniLayoutMath.RadarResetContentWidth, areas.Single(area => area.AreaId == MiniAreaIds.RadarReset).Width, "reset area uses its tighter Codex status width");
    Equal(true, form.IsRadarResetTimerEnabled, "future exact Mini starts its timer");
    Equal(RadarResetTiming.ExactRefreshIntervalMilliseconds, form.RadarResetTimerInterval, "exact Mini timer uses one-second cadence");
    Equal(true, form.SetMiniAreaFromCommand(MiniAreaIds.RadarReset, true, null), "reset area supports the shared collapse command");
    Equal(true, form.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.RadarReset).Collapsed, "reset area collapse state is reported");

    var pluginCards = Enumerable.Range(0, 5)
        .Select(index => new PluginMiniCardView(
            $"test.reset-plugin.{index}",
            $"Plugin {index}",
            new MiniCardContribution(
                $"card.test.reset-plugin.{index}",
                $"test.reset-plugin.{index}",
                "test",
                ContributionKind.Metric,
                index,
                "test.title",
                "test.icon",
                "test.accent",
                []),
            new Dictionary<string, string>(StringComparer.Ordinal)))
        .ToArray();
    form.SetPluginMiniCards(pluginCards);
    Equal(true, form.GetMiniAreaStates().Any(area => area.AreaId == MiniAreaIds.RadarReset), "reset area survives the ordinary Mini card limit");
    Equal(
        TaskbarMiniLayoutMath.MaximumCards,
        form.GetMiniAreaStates().Count(area =>
            !string.Equals(area.AreaId, MiniAreaIds.RadarReset, StringComparison.Ordinal)
            && !string.Equals(area.AreaId, MiniAreaIds.CodexEconomy, StringComparison.Ordinal)
            && !string.Equals(area.AreaId, MiniAreaIds.SystemMetrics, StringComparison.Ordinal)),
        "persistent reset area does not consume an ordinary card slot");

    form.SetRadarState(StateFor(estimatedWindow));
    Equal(true, form.IsRadarResetTimerEnabled, "estimated date Mini keeps a low-frequency timer");
    Equal(RadarResetTiming.EstimatedDateRefreshIntervalMilliseconds, form.RadarResetTimerInterval, "estimated Mini timer uses minute cadence");
    form.SetRadarState(StateFor(estimatedWindow with { OpenedAt = null }));
    Equal(true, form.GetMiniAreaStates().Any(area => area.AreaId == MiniAreaIds.RadarReset), "unknown open window keeps a persistent status area");
    Equal(false, form.IsRadarResetTimerEnabled, "unknown date does not waste timer ticks");
    now = exactTarget;
    form.SetRadarState(StateFor(exactWindow));
    Equal(true, form.GetMiniAreaStates().Any(area => area.AreaId == MiniAreaIds.RadarReset), "expired exact target waits visibly for confirmation");
    Equal(false, form.IsRadarResetTimerEnabled, "expired exact target stops the Mini timer");
    form.SetRadarState(StateFor(exactWindow with { Open = false }));
    Equal(false, form.GetMiniAreaStates().Any(area => area.AreaId == MiniAreaIds.RadarReset), "closed reset window removes the persistent area");
    Equal(false, form.IsRadarResetTimerEnabled, "closed reset window stops the Mini timer");

    now = DateTimeOffset.Parse("2026-08-23T16:00:00+08:00");
    using var explicitlyOrdered = new BarForm(
        new AppSettings
        {
            EnableRadar = true,
            EnableAnimations = false,
            MiniAreaOrder =
            [
                MiniAreaIds.SystemMetrics,
                MiniAreaIds.RadarReset,
                MiniAreaIds.Codex,
                MiniAreaIds.Claude,
            ],
        },
        quotaSnapshot,
        radarProviders: [ProviderKind.Codex],
        renderOnly: true,
        renderDpi: 96,
        utcNow: () => now);
    explicitlyOrdered.SetRadarState(StateFor(exactWindow));
    Equal(MiniAreaIds.RadarReset, explicitlyOrdered.GetMiniAreaStates()[1].AreaId, "explicit reset-area order overrides the default Codex adjacency");

    using var radarDisabled = new BarForm(
        new AppSettings { EnableRadar = false, EnableAnimations = false },
        quotaSnapshot,
        radarProviders: [ProviderKind.Codex],
        renderOnly: true,
        renderDpi: 96,
        utcNow: () => now);
    radarDisabled.SetRadarState(StateFor(exactWindow));
    Equal(false, radarDisabled.GetMiniAreaStates().Any(area => area.AreaId == MiniAreaIds.RadarReset), "disabled Radar never creates the reset area");
    Equal(false, radarDisabled.IsRadarResetTimerEnabled, "disabled Radar never starts the reset timer");
}

static void TestQuotaPacePopoverPresentation()
{
    var zh = NativeText.For("zh-CN");
    var en = NativeText.For("en");
    var now = DateTimeOffset.Parse("2026-07-31T18:00:00+08:00");
    var projected = new QuotaPaceEstimate(
        QuotaPaceStatus.ProjectedExhaustion,
        new QuotaCyclePace(
            60,
            10,
            23.333,
            now.AddHours(1).AddMinutes(17),
            false,
            .643),
        new QuotaRecentTrend(
            TimeSpan.FromHours(1),
            8.4,
            now.AddHours(8).AddMinutes(27),
            false,
            QuotaTrendConfidence.Stable),
        TimeSpan.FromHours(1),
        now.AddMinutes(15));

    Equal((240, 144), (QuotaPopoverForm.LogicalBodyWidth, QuotaPopoverForm.LogicalBodyHeight), "two pace rows fit the relaxed popover");
    Equal(207, QuotaPopoverForm.LogicalCodexTokenBodyHeight, "Codex token references fit six metrics in three rows");
    Equal("Token · 原始用量", zh.CodexQuotaCapacityTitle, "Chinese token title avoids fixed-capacity wording");
    Equal("Tokens · raw usage", en.CodexQuotaCapacityTitle, "English token title avoids fixed-capacity wording");
    var capacity = new CodexQuotaTokenSummary(
        "codex.capacity",
        "7d",
        TimeSpan.FromDays(7).Ticks,
        12_400_000,
        15_800_000,
        13_100_000,
        3,
        true,
        3_596_000,
        29,
        true,
        RecentWeeklyAverageTokens: 5_000_000_000);
    Equal(
        "已用29% 3.60M|样本100% ≈12.4M|近4周/周 ≈5.00B|周期平均 ≈13.1M|周期最高 ≈15.8M|完整周期 3",
        string.Join('|', zh.CodexQuotaCapacityMetrics(capacity, 29)),
        "Chinese token metrics keep current, recent, and historical references separate");
    Equal("账号实录", zh.CodexQuotaObservationEvidence(capacity), "cycle-start Profile coverage is source-qualified");
    var screenshotRegression = capacity with
    {
        CurrentCapacityTokens = 4_311_063_479,
        MaxCapacityTokens = null,
        AverageCapacityTokens = null,
        CompletedCycleCount = 0,
        CurrentObservedTokens = 1_638_204_122,
        CurrentObservedSpanPercent = 38,
        CoversCycleStart = false,
    };
    Equal(
        2_845_301_896L,
        screenshotRegression.EstimateUsedTokens(66),
        "mid-cycle current used tokens scale the normalized capacity to live utilization");
    Equal(
        "已用66% ≈2.85B|样本100% ≈4.31B|近4周/周 ≈5.00B|周期平均 —|周期最高 —|完整周期 0",
        string.Join('|', zh.CodexQuotaCapacityMetrics(screenshotRegression, 66)),
        "mid-cycle current estimate aligns with the live used percentage");
    Equal(
        "账号估算",
        zh.CodexQuotaObservationEvidence(screenshotRegression),
        "mid-cycle Profile observation is labeled as an account estimate");
    Equal(
        "Account est.",
        en.CodexQuotaObservationEvidence(screenshotRegression),
        "English mid-cycle Profile source quality is explicit");
    var localFallback = screenshotRegression with
    {
        CurrentCapacityTokens = 1_575_299_235,
        CurrentObservedTokens = 976_685_526,
        CurrentObservedSpanPercent = 62,
        CoversCycleStart = true,
        IsCurrentLocalFallback = true,
    };
    Equal(
        "已用62% ≥977M|样本100% ≈1.58B|近4周/周 ≈5.00B|周期平均 —|周期最高 —|完整周期 0",
        string.Join('|', zh.CodexQuotaCapacityMetrics(localFallback, 62)),
        "fallback current value stays a local lower bound instead of a back-estimated account total");
    Equal("本机下限", zh.CodexQuotaObservationEvidence(localFallback), "fallback source quality is explicit");
    Equal(
        "Used 29% collecting|Sample 100% collecting|4wk/week —|Cycle avg —|Cycle max —|Full cycles 0",
        string.Join('|', en.CodexQuotaCapacityMetrics(capacity with
        {
            CurrentCapacityTokens = null,
            MaxCapacityTokens = null,
            AverageCapacityTokens = null,
            CompletedCycleCount = 0,
            CurrentObservedTokens = null,
            CurrentObservedSpanPercent = null,
            CoversCycleStart = false,
            RecentWeeklyAverageTokens = null,
        }, 29)),
        "missing capacity data is explicit without fabricating values");
    Equal(
        "已用29% 记录中|样本100% 记录中|近4周/周 —|周期平均 —|周期最高 —|完整周期 0",
        string.Join('|', zh.CodexQuotaCapacityMetrics(null, 29)),
        "Codex quota hover advertises collection before the first attributable observation");
    Equal("Click to pin", en.PinHint, "English pin hint stays concise");
    Equal("Esc / 点击外部", zh.ClosePinnedHint, "Chinese pinned hint stays concise");
    Equal(
        ("1h · 8.4%/时", "约8时27分用完"),
        zh.QuotaPace(projected, now),
        "Chinese projected exhaustion");
    Equal(
        ("1h · 8.4%/h", "~8h27m empty"),
        en.QuotaPace(projected, now),
        "English projected exhaustion");
    var daily = projected with
    {
        Recent = projected.Recent! with { ObservedSpan = TimeSpan.FromHours(24) },
        ObservedSpan = TimeSpan.FromHours(24),
    };
    Equal(
        ("24h趋势 · 8.4%/时", "约8时27分用完"),
        zh.QuotaPace(daily, now),
        "Chinese weighted daily trend is explicit");
    Equal(
        ("24h trend · 8.4%/h", "~8h27m empty"),
        en.QuotaPace(daily, now),
        "English weighted daily trend is explicit");
    Equal(
        ("6h趋势 · 平稳", string.Empty),
        zh.QuotaPace(new QuotaPaceEstimate(
            QuotaPaceStatus.NoMeaningfulConsumption,
            ObservedSpan: TimeSpan.FromHours(6)), now),
        "quiet six-hour fallback keeps its horizon");
    Equal(
        ("1h · 10%/时", "可撑到重置"),
        zh.QuotaPace(new QuotaPaceEstimate(
            QuotaPaceStatus.ResetsBeforeExhaustion,
            Recent: new QuotaRecentTrend(
                TimeSpan.FromHours(1),
                10.4,
                now.AddHours(10),
                true,
                QuotaTrendConfidence.Stable)), now),
        "double-digit pace and reset-first state");
    Equal(
        ("趋势样本收集中", string.Empty),
        zh.QuotaPace(new QuotaPaceEstimate(QuotaPaceStatus.Learning), now),
        "learning state");
    Equal(
        ("1h · steady", string.Empty),
        en.QuotaPace(new QuotaPaceEstimate(
            QuotaPaceStatus.NoMeaningfulConsumption,
            ObservedSpan: TimeSpan.FromHours(1)), now),
        "no-consumption state");
    Equal(
        ("等待新数据", string.Empty),
        zh.QuotaPace(new QuotaPaceEstimate(QuotaPaceStatus.WaitingForFreshData), now),
        "fresh-data state");
    Equal(
        ("Quota exhausted", string.Empty),
        en.QuotaPace(new QuotaPaceEstimate(QuotaPaceStatus.Exhausted), now),
        "exhausted state");
    Equal(
        ("周额度受限", string.Empty),
        zh.QuotaPace(new QuotaPaceEstimate(QuotaPaceStatus.WeeklyBlocked), now),
        "weekly-blocked state");
    Equal(
        ("Returns after weekly reset", string.Empty),
        en.QuotaCycle(new QuotaPaceEstimate(QuotaPaceStatus.WeeklyBlocked)),
        "weekly-blocked cycle guidance");
    Equal(
        ("最近预估 · 8.4%/时", "约8时11分用完"),
        zh.QuotaPace(projected, now.AddMinutes(16)),
        "recent cached pace remains available with an explicit fallback label");
    Equal(
        ("周期均速 · 0.9%/时", "可撑到重置"),
        zh.QuotaPace(new QuotaPaceEstimate(
            QuotaPaceStatus.ResetsBeforeExhaustion,
            new QuotaCyclePace(
                21,
                -20,
                .923,
                now.AddDays(4),
                true),
            ValidUntil: now.AddMinutes(-10)), now),
        "cycle-only fallback never pretends to be a recent estimate");

    var screenshotAt = DateTimeOffset.Parse("2026-08-23T15:55:20+08:00");
    var screenshotReset = DateTimeOffset.Parse("2026-08-27T11:29:00+08:00");
    var screenshotCard = PaceCard(
        "codex.screenshot-regression",
        "Codex",
        65,
        screenshotReset,
        screenshotAt) with
    {
        Windows = [new QuotaWindow("7d", 65, screenshotReset, TimeSpan.FromDays(7))],
    };
    var screenshotEstimate = new QuotaPaceTracker().Estimate(
        screenshotCard,
        screenshotCard.Windows.Single(),
        screenshotAt,
        5);
    Equal(
        .85d,
        Math.Round(screenshotEstimate.Cycle!.PercentPerHour!.Value, 2),
        "screenshot regression is the seven-day cycle average");
    Equal(
        ("周期均速 · 0.9%/时", "约1天17时用完"),
        zh.QuotaPace(screenshotEstimate, screenshotAt),
        "the screenshot arithmetic stays intact while its fallback basis is honest");
    Equal(
        ("Cycle avg · 0.9%/h", "~1d17h empty"),
        en.QuotaPace(screenshotEstimate, screenshotAt),
        "English cycle fallback uses the same explicit basis");

    var mismatchTracker = new QuotaPaceTracker();
    var mismatchReset = screenshotAt.AddHours(4);
    var observedMismatchCard = PaceCard(
        "codex.fresh-mismatch",
        "Codex",
        20,
        mismatchReset,
        screenshotAt);
    mismatchTracker.Observe(
        PaceSnapshot(screenshotAt, [observedMismatchCard]),
        screenshotAt);
    var displayedMismatchCard = PaceCard(
        "codex.fresh-mismatch",
        "Codex",
        21,
        mismatchReset,
        screenshotAt);
    var waitingWithFreshCycle = mismatchTracker.Estimate(
        displayedMismatchCard,
        displayedMismatchCard.Windows.Single(),
        screenshotAt,
        5);
    Equal(
        QuotaPaceStatus.WaitingForFreshData,
        waitingWithFreshCycle.Status,
        "a fresh card/history mismatch takes the production waiting path");
    var waitingWithFreshCycleText = zh.QuotaPace(waitingWithFreshCycle, screenshotAt);
    Equal(
        "周期均速 · 21%/时",
        waitingWithFreshCycleText.Left,
        "a fresh card/history mismatch may degrade to an explicitly labeled cycle average");
    Equal(true, waitingWithFreshCycleText.Right.Length > 0, "the valid cycle fallback keeps its ETA detail");
    Equal(
        ("等待新数据", string.Empty),
        zh.QuotaPace(waitingWithFreshCycle with
        {
            ValidUntil = screenshotAt.AddMinutes(-1),
        }, screenshotAt),
        "an expired waiting state does not expose an old cycle ETA");
    Equal(
        ("周期超额 10%", "近期过快"),
        zh.QuotaCycle(projected),
        "Chinese cycle budget and recent warning");
    Equal(
        ("Cycle 10% over", "Recent too fast"),
        en.QuotaCycle(projected),
        "English cycle budget and recent warning");
    Equal(
        ("Cycle 10% over", "Need ≤0.6×"),
        en.QuotaCycle(projected with { Recent = null }),
        "cycle-only fallback keeps its throttle guidance");
    Equal(
        ("今晚目标 54%", "余量 17%"),
        zh.QuotaDailyGoal(54, 71),
        "Chinese daily goal explains the long-window marker");
    Equal(
        ("Midnight goal 54%", "8% over"),
        en.QuotaDailyGoal(54, 46),
        "English daily goal reports an exceeded target");
    Equal(
        ("Midnight goal 54%", "Recent too fast"),
        en.QuotaDailyGoal(54, 71, recentTooFast: true),
        "recent exhaustion warning overrides daily spare");
    Equal(
        ("15m early · 8.0%/h", "until reset"),
        en.QuotaPace(new QuotaPaceEstimate(
            QuotaPaceStatus.ResetsBeforeExhaustion,
            Recent: new QuotaRecentTrend(
                TimeSpan.FromMinutes(15),
                8,
                now.AddHours(10),
                true,
                QuotaTrendConfidence.Provisional)), now),
        "provisional short trend is labeled");
}

static void TestCodexAccountPopoverPresentation()
{
    var accounts = new[]
    {
        new CodexAccountInfo("current", "current@example.test", "pro", true),
        new CodexAccountInfo("plus", "plus@example.test", "plus", false),
        new CodexAccountInfo("free", "free@example.test", "free", false),
    };
    var card = new QuotaCard(
        "codex.1",
        ProviderKind.Codex,
        "Codex · 1",
        "pro",
        "#10a37f",
        true,
        [new QuotaWindow("7d", 29, DateTimeOffset.UtcNow.AddDays(4), TimeSpan.FromDays(7))])
    {
        AccountHint = "c***t@example.test",
    };
    var quotas = new[]
    {
        new CodexAccountQuota(
            "current",
            [
                new QuotaWindow("5h", 12, DateTimeOffset.UtcNow.AddHours(2), TimeSpan.FromHours(5)),
                new QuotaWindow("7d", 70, DateTimeOffset.UtcNow.AddDays(4), TimeSpan.FromDays(7)),
            ]),
        new CodexAccountQuota(
            "plus",
            [new QuotaWindow("5h", 45, DateTimeOffset.UtcNow.AddHours(2), TimeSpan.FromHours(5))]),
    };
    var zh = NativeText.For("zh-CN");
    Equal("Codex 账号", zh.CodexAccountsHeading, "account popover heading is localized");
    Equal("c***t@example.test", CodexAccountFormatting.MaskEmail(accounts[0].Email), "account popover masks the active email");
    Equal(
        "pro · c***t@example.test",
        QuotaPopoverForm.AccountSubtitle(card, zh),
        "quota popover shows the masked account beside the plan");
    Equal("PRO", QuotaPopoverForm.PlanBadgeLabel(card), "Pro quota uses a plan badge");
    Equal("PLUS", QuotaPopoverForm.PlanBadgeLabel(card with { Badge = "plus" }), "Plus quota uses a plan badge");
    Equal("PRO", PlanBadgePresentation.Label("pro"), "account list uses the shared Pro badge label");
    Equal("PLUS", PlanBadgePresentation.Label("plus"), "account list uses the shared Plus badge label");
    Equal(true, PlanBadgePresentation.TryGetStyle("PRO", out _), "Pro badge has a shared style");
    Equal(true, PlanBadgePresentation.TryGetStyle("PLUS", out _), "Plus badge has a shared style");

    using var popover = new CodexAccountsPopoverForm();
    using var bitmap = popover.RenderForTest(
        accounts,
        card,
        zh,
        QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette),
        96,
        quotas);
    Equal(
        new Size(
            popover.CurrentLogicalBodyWidth,
            CodexAccountsPopoverForm.LogicalBodyHeightFor(accounts.Length) + 8),
        bitmap.Size,
        "account popover renders at its deterministic logical size");
    Equal(
        true,
        popover.CurrentLogicalBodyWidth < CodexAccountsPopoverForm.LogicalBodyWidth,
        "account popover contracts around its content");
    Equal(
        true,
        Enumerable.Range(0, bitmap.Width)
            .Any(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).A > 0)),
        "account popover produces visible pixels");
}

static void TestTaskbarHintPopoverPresentation()
{
    var detail = NativeText.For("zh-CN").RefreshUpdatedDetail(TimeSpan.FromMinutes(1));
    using var popover = new TaskbarHintPopoverForm();
    using var bitmap = popover.RenderForTest(
        NativeText.For("zh-CN").RefreshNow,
        detail,
        QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette),
        96);
    Equal(
        new Size(
            popover.CurrentLogicalBodyWidth,
            TaskbarHintPopoverForm.LogicalBodyHeightFor(detail) + 8),
        bitmap.Size,
        "hint popover renders at its deterministic logical size");
    Equal(
        true,
        popover.CurrentLogicalBodyWidth < TaskbarHintPopoverForm.LogicalBodyWidth,
        "short hint popover contracts around its content");
    Equal(
        true,
        Enumerable.Range(0, bitmap.Width)
            .Any(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).A > 0)),
        "hint popover produces visible pixels");
}

static void TestRadarStableRanking()
{
    var capturedAt = DateTimeOffset.Parse("2026-07-28T13:09:56+08:00");
    var sourceFirst = new RadarModel("a", "Source first", "max", 100, "green", 80, 100, 10, 30, "30s");
    var lowerCost = new RadarModel("b", "Lower cost", "max", 100, "green", 80, 100, 9, 35, "35s");
    var fasterTie = new RadarModel("c", "Faster tie", "max", 100, "green", 80, 100, 9, 25, "25s");
    var missingTieBreakers = new RadarModel("d", "Missing", "max", 100, "green", null, 100, null, null, null);
    var snapshot = new ProviderRadarSnapshot(
        ProviderKind.Codex,
        "model_iq:tie",
        capturedAt,
        capturedAt,
        sourceFirst,
        [lowerCost, fasterTie, missingTieBreakers]);
    var presentation = RadarPresentation.Build(snapshot);

    Equal("Source first", presentation.Rows[0].Model.Label, "ranking does not reorder rows");
    Equal<int?>(null, presentation.Rows[0].Rank, "non-leader has no visible rank");
    Equal<int?>(null, presentation.Rows[1].Rank, "runner-up has no visible rank");
    Equal(1, presentation.Rows[2].Rank, "faster exact tie ranks first");
    Equal<int?>(null, presentation.Rows[3].Rank, "missing tie breakers rank after awarded rows");
}

static void TestRadarPopoverLayout()
{
    var zh = NativeText.For("zh-CN");
    var en = NativeText.For("en");
    var renderNow = DateTimeOffset.Parse("2026-08-01T08:00:00+08:00");
    var resetLabel = NativeText.For("en").RadarResetWindow(new RadarResetWindow(
        false,
        null,
        DateTimeOffset.Parse("2026-08-01T11:32:37+08:00"),
        null), renderNow);
    var openResetWindow = new RadarResetWindow(
        true,
        DateTimeOffset.Parse("2026-08-01T09:15:00+08:00"),
        null,
        null)
    {
        Scope = "周一表演式重置预告",
    };
    foreach (var dpi in new[] { 96, 144, 192 })
    {
        var scale = dpi / 96d;
        var layout = RadarPopoverLayout.Create(dpi, 11, false);
        Equal(false, layout.TokenOnly, $"{dpi} DPI Radar layout is not token-only");
        Equal((int)Math.Round(476 * scale, MidpointRounding.AwayFromZero), layout.BodySize.Width, $"{dpi} DPI body width");
        Equal((int)Math.Round(329 * scale, MidpointRounding.AwayFromZero), layout.BodySize.Height, $"{dpi} DPI 11-row height");
        Equal(11, layout.RowBounds.Count, $"{dpi} DPI row count");
        Equal(false, layout.SubtitleBounds.IntersectsWith(layout.ResetBounds), $"{dpi} DPI reset status clears subtitle");
        Equal(false, layout.ResetBounds.IntersectsWith(layout.TableHeaderBounds), $"{dpi} DPI reset status clears table header");
        Equal(false, layout.SubtitleBounds.IntersectsWith(layout.TableHeaderBounds), $"{dpi} DPI table header clears product header");
        using var badgeFont = new Font(
            "Segoe UI",
            Math.Max(1, (float)Math.Round(7 * scale, MidpointRounding.AwayFromZero)),
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        using var metaFont = new Font(
            "Segoe UI",
            Math.Max(1, (float)Math.Round(8 * scale, MidpointRounding.AwayFromZero)),
            FontStyle.Regular,
            GraphicsUnit.Pixel);
        using var emphasizedModelFont = new Font(
            "Segoe UI",
            Math.Max(1, (float)Math.Round(8.5 * scale, MidpointRounding.AwayFromZero)),
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        using var numberFont = new Font(
            "Cascadia Mono",
            Math.Max(1, (float)Math.Round(8.5 * scale, MidpointRounding.AwayFromZero)),
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        var resetTextWidth = System.Windows.Forms.TextRenderer.MeasureText(
            resetLabel,
            badgeFont,
            Size.Empty,
            System.Windows.Forms.TextFormatFlags.NoPadding
            | System.Windows.Forms.TextFormatFlags.NoPrefix
            | System.Windows.Forms.TextFormatFlags.SingleLine).Width;
        Equal(true, resetTextWidth <= layout.ResetBounds.Width, $"{dpi} DPI reset label fits its header cell");
        var openLayout = RadarPopoverLayout.Create(dpi, 11, false, true);
        Equal(true, openLayout.HasOpenResetWindow, $"{dpi} DPI open reset layout flag");
        Equal(
            (int)Math.Round(365 * scale, MidpointRounding.AwayFromZero),
            openLayout.BodySize.Height,
            $"{dpi} DPI open reset banner height");
        Equal(false, openLayout.SubtitleBounds.IntersectsWith(openLayout.ResetBounds), $"{dpi} DPI reset banner clears subtitle");
        Equal(false, openLayout.ResetBounds.IntersectsWith(openLayout.TableHeaderBounds), $"{dpi} DPI reset banner clears table header");
        Equal(
            (int)Math.Round(36 * scale, MidpointRounding.AwayFromZero),
            openLayout.RowBounds[0].Top - layout.RowBounds[0].Top,
            $"{dpi} DPI reset banner shifts table rows");
        using var titleFont = new Font(
            "Segoe UI",
            Math.Max(1, (float)Math.Round(11 * scale, MidpointRounding.AwayFromZero)),
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        var resetBannerLabelWidth = System.Windows.Forms.TextRenderer.MeasureText(
            en.RadarResetWindow(openResetWindow, renderNow),
            titleFont,
            Size.Empty,
            System.Windows.Forms.TextFormatFlags.NoPadding
            | System.Windows.Forms.TextFormatFlags.NoPrefix
            | System.Windows.Forms.TextFormatFlags.SingleLine).Width;
        Equal(
            true,
            resetBannerLabelWidth
                + (int)Math.Round((12 * 2 + 7 + 7) * scale, MidpointRounding.AwayFromZero)
                <= openLayout.ResetBounds.Width,
            $"{dpi} DPI centered open reset title and target fit");
        foreach (var text in new[] { zh, en })
        {
            foreach (var pinned in new[] { false, true })
            {
                var subtitleWidth = System.Windows.Forms.TextRenderer.MeasureText(
                    text.RadarPopoverSubtitle(pinned),
                    badgeFont,
                    Size.Empty,
                    System.Windows.Forms.TextFormatFlags.NoPadding
                    | System.Windows.Forms.TextFormatFlags.NoPrefix
                    | System.Windows.Forms.TextFormatFlags.SingleLine).Width;
                Equal(true, subtitleWidth <= layout.SubtitleBounds.Width, $"{dpi} DPI {text.Locale} Radar pin subtitle fits");
            }
        }
        var markerWidth = Math.Max(1, (int)Math.Round(8 * scale, MidpointRounding.AwayFromZero));
        var markerGap = Math.Max(1, (int)Math.Round(3 * scale, MidpointRounding.AwayFromZero));
        var itemGap = Math.Max(1, (int)Math.Round(4 * scale, MidpointRounding.AwayFromZero));
        foreach (var text in new[] { zh, en })
        {
            var groupGap = Math.Max(1, (int)Math.Round(16 * scale, MidpointRounding.AwayFromZero));
            var groupWidth = (layout.FooterSourceBounds.Width - groupGap) / 2;
            var cardPadding = Math.Max(1, (int)Math.Round(8 * scale, MidpointRounding.AwayFromZero));
            var metricWidth = Math.Max(1, (int)Math.Round(36 * scale, MidpointRounding.AwayFromZero));
            var fieldGap = Math.Max(1, (int)Math.Round(6 * scale, MidpointRounding.AwayFromZero));
            var interFieldGap = Math.Max(1, (int)Math.Round(14 * scale, MidpointRounding.AwayFromZero));
            var fieldWidth = (groupWidth - cardPadding * 2 - metricWidth - fieldGap - interFieldGap) / 2;
            foreach (var metric in new[] { text.CodexTokenRadarMetricTitle, text.CodexCacheRadarMetricTitle })
            {
                Equal(true, MeasureSingleLine(metric, emphasizedModelFont) <= metricWidth, $"{dpi} DPI {text.Locale} Radar metric label fits");
            }
            var valueGap = Math.Max(1, (int)Math.Round(3 * scale, MidpointRounding.AwayFromZero));
            foreach (var value in new[] { "12.3M", "95.0%" })
            {
                Equal(
                    true,
                    MeasureSingleLine(text.CodexTodayMetricLabel, metaFont)
                        + valueGap
                        + MeasureSingleLine(value, numberFont)
                        <= fieldWidth,
                    $"{dpi} DPI {text.Locale} Radar today field fits");
            }
            foreach (var value in new[] { "173B", "98.5%" })
            {
                Equal(
                    true,
                    MeasureSingleLine(text.CodexTotalMetricLabel, metaFont)
                        + valueGap
                        + MeasureSingleLine(value, numberFont)
                        <= fieldWidth,
                    $"{dpi} DPI {text.Locale} Radar total field fits");
            }
            Equal(
                (int)Math.Round(24 * scale, MidpointRounding.AwayFromZero),
                layout.FooterSourceBounds.Height,
                $"{dpi} DPI token footer has one-line height");
            Equal(
                (int)Math.Round(14 * scale, MidpointRounding.AwayFromZero),
                layout.FooterLegendBounds.Height,
                $"{dpi} DPI Radar legend has one-line height");
            var labels = new[]
            {
                text.RadarUnknownStatusLegend,
                text.RadarStrongestTitle,
                text.RadarDailyScenarioTitle,
                text.RadarPlanningScenarioTitle,
                text.RadarExecutionScenarioTitle,
                text.RadarBackgroundScenarioTitle,
            };
            var legendWidth = labels.Sum(label => markerWidth
                + markerGap
                + System.Windows.Forms.TextRenderer.MeasureText(
                    label,
                    metaFont,
                    Size.Empty,
                    System.Windows.Forms.TextFormatFlags.NoPadding
                    | System.Windows.Forms.TextFormatFlags.NoPrefix
                    | System.Windows.Forms.TextFormatFlags.SingleLine).Width)
                + itemGap * (labels.Length - 1);
            var confidenceNoteWidth = System.Windows.Forms.TextRenderer.MeasureText(
                text.RadarConfidenceNote,
                metaFont,
                Size.Empty,
                System.Windows.Forms.TextFormatFlags.NoPadding
                | System.Windows.Forms.TextFormatFlags.NoPrefix
                | System.Windows.Forms.TextFormatFlags.SingleLine).Width;
            var noteGap = Math.Max(1, (int)Math.Round(8 * scale, MidpointRounding.AwayFromZero));
            Equal(
                true,
                confidenceNoteWidth + noteGap + legendWidth <= layout.FooterLegendBounds.Width,
                $"{dpi} DPI {text.Locale} confidence note and six-item legend fit");
        }
        Equal(false, layout.TableHeaderBounds.IntersectsWith(layout.RowBounds[0]), $"{dpi} DPI table header clears first row");
        Equal(false, layout.FooterSourceBounds.IntersectsWith(layout.FooterLegendBounds), $"{dpi} DPI footer zones");
        Equal(
            (int)Math.Round(4 * scale, MidpointRounding.AwayFromZero),
            layout.FooterSourceBounds.Top - layout.FooterLegendBounds.Bottom,
            $"{dpi} DPI legend clears metric cards");
        Equal(false, layout.RowBounds[^1].IntersectsWith(layout.FooterLegendBounds), $"{dpi} DPI rows clear footer legend");
        Equal(false, layout.RowBounds[^1].IntersectsWith(layout.FooterSourceBounds), $"{dpi} DPI rows clear footer");

        var row = layout.RowBounds[0];
        Equal(
            true,
            MeasureSingleLine("99999", numberFont) <= layout.Columns.Samples.Width,
            $"{dpi} DPI five-digit sample count fits");
        foreach (var text in new[] { zh, en })
        {
            Equal(
                true,
                MeasureSingleLine(text.RadarSampleHeader, badgeFont) <= layout.Columns.Samples.Width,
                $"{dpi} DPI {text.Locale} sample header fits");
        }
        var columns = new[]
        {
            layout.Columns.Marker,
            layout.Columns.Model,
            layout.Columns.Status,
            layout.Columns.IqCurrent,
            layout.Columns.IqAverage,
            layout.Columns.Samples,
            layout.Columns.AverageTime,
            layout.Columns.Cost,
        };
        for (var index = 0; index < columns.Length; index++)
        {
            Equal(true, columns[index].Width > 0, $"{dpi} DPI column {index} has width");
            var cell = columns[index].InRow(row);
            Equal(row.Top, cell.Top, $"{dpi} DPI column {index} row alignment");
            if (index > 0)
            {
                Equal(true, columns[index - 1].Right <= columns[index].Left, $"{dpi} DPI columns {index - 1}/{index} do not overlap");
            }
        }
        var withError = RadarPopoverLayout.Create(dpi, 11, true);
        Equal((int)Math.Round(343 * scale, MidpointRounding.AwayFromZero), withError.BodySize.Height, $"{dpi} DPI error height");
        Equal(false, withError.ErrorBounds.IntersectsWith(withError.FooterSourceBounds), $"{dpi} DPI error clears footer");

        var grouped = RadarPopoverLayout.Create(
            dpi,
            ["gpt-5.6-sol", "gpt-5.6-sol", "gpt-5.6-terra"],
            false);
        Equal(
            (int)Math.Round((120 + 3 * 19 + RadarPopoverLayout.LogicalModelGroupGap) * scale, MidpointRounding.AwayFromZero),
            grouped.BodySize.Height,
            $"{dpi} DPI model group gap adds to body height");
        Equal(
            (int)Math.Round(RadarPopoverLayout.LogicalModelGroupGap * scale, MidpointRounding.AwayFromZero),
            grouped.RowBounds[2].Top - grouped.RowBounds[1].Bottom,
            $"{dpi} DPI model groups have a visual gap");

        var tokenLayout = RadarPopoverLayout.CreateTokenUsage(dpi);
        Equal(true, tokenLayout.TokenOnly, $"{dpi} DPI token-only layout");
        Equal(
            (int)Math.Round(240 * scale, MidpointRounding.AwayFromZero),
            tokenLayout.BodySize.Width,
            $"{dpi} DPI token body width");
        Equal(
            (int)Math.Round(144 * scale, MidpointRounding.AwayFromZero),
            tokenLayout.BodySize.Height,
            $"{dpi} DPI token body height");
        foreach (var text in new[] { zh, en })
        {
            foreach (var pinned in new[] { false, true })
            {
                var subtitleWidth = System.Windows.Forms.TextRenderer.MeasureText(
                    text.CodexTokenPopoverSubtitle(pinned),
                    badgeFont,
                    Size.Empty,
                    System.Windows.Forms.TextFormatFlags.NoPadding
                    | System.Windows.Forms.TextFormatFlags.NoPrefix
                    | System.Windows.Forms.TextFormatFlags.SingleLine).Width;
                Equal(true, subtitleWidth <= tokenLayout.SubtitleBounds.Width, $"{dpi} DPI {text.Locale} token pin subtitle fits");
            }
        }
    }

    Equal(180, RadarPopoverLayout.Create(96, 0, false).BodySize.Height, "empty layout minimum height");
    var tokenUsage = new CodexTokenUsageSummary(
        12_345_678,
        1_234_567_890,
        42,
        DateTimeOffset.Parse("2026-08-03T12:00:00+08:00"),
        800,
        610,
        200,
        180);
    Equal(("今日 Token", "12.3M"), zh.CodexTodayTokens(tokenUsage.TodayTokens), "Chinese daily token summary");
    Equal(("Local total", "1.23B"), en.CodexLocalTokens(tokenUsage.LocalTokens), "English local token summary");
    Equal(("今日命中率", "90.0%"), zh.CodexTodayCacheHitRate(tokenUsage.TodayCacheHitPercent), "Chinese today cache hit rate");
    Equal(("Total cache hit", "76.3%"), en.CodexTotalCacheHitRate(tokenUsage.TotalCacheHitPercent), "English total cache hit rate");
    Equal(("Today cache hit", "—"), en.CodexTodayCacheHitRate(null), "missing today cache hit rate is explicit");
    Equal("Token 用量", zh.CodexTokenMetricTitle, "Chinese token metric group title");
    Equal("Cache hit", en.CodexCacheMetricTitle, "English cache metric group title");
    Equal("今日", zh.CodexTodayMetricLabel, "Chinese metric scope label");
    Equal("Total", en.CodexTotalMetricLabel, "English metric scope label");
    Equal("Token", zh.CodexTokenRadarMetricTitle, "Chinese Radar token metric title");
    Equal("Cache", en.CodexCacheRadarMetricTitle, "English Radar cache metric title");
    Equal("42 sessions · not split by account", en.CodexTokenScope(42), "token scope names local account limitation");
    var cockpitAccounts = new[]
    {
        new CodexAccountInfo("current", "current@example.test", "pro", true),
        new CodexAccountInfo("second", "second@example.test", "plus", false),
    };
    Equal("Codex accounts", en.CodexAccountsHeading, "account popover heading is formal");
    Equal("c***t@example.test", CodexAccountFormatting.MaskEmail(cockpitAccounts[0].Email), "account popover masks email");
    Equal("999", NativeText.FormatTokenCount(999), "small token count stays exact");
    Equal(true, BarForm.HasProviderOverview(ProviderKind.Codex, false, tokenUsage), "Codex tokens enable logo hover");
    Equal(false, BarForm.HasProviderOverview(ProviderKind.Codex, false, null), "empty Codex overview stays inert");
    Equal(false, BarForm.HasProviderOverview(ProviderKind.Claude, false, tokenUsage), "Codex tokens do not enable Claude hover");
    Equal(true, BarForm.HasProviderOverview(ProviderKind.Claude, true, null), "Radar enables provider logo hover");
}

static int MeasureSingleLine(string text, Font font) =>
    System.Windows.Forms.TextRenderer.MeasureText(
        text,
        font,
        Size.Empty,
        System.Windows.Forms.TextFormatFlags.NoPadding
        | System.Windows.Forms.TextFormatFlags.NoPrefix
        | System.Windows.Forms.TextFormatFlags.SingleLine).Width;

static void TestRadarAlertContract()
{
    var baseline = RadarSnapshot(
        "2026-07-19",
        "gpt-5.6-sol",
        "max",
        100,
        "green");
    var empty = new RadarAlertState();
    var first = RadarAlertTracker.Evaluate(empty, baseline);
    Equal(false, first.ShouldNotify, "first snapshot is silent");
    Equal(true, first.ShouldSeedBaseline, "first snapshot seeds baseline");

    var state = RadarAlertTracker.RecordBaseline(empty, baseline, baseline.CapturedAt);
    Equal(false, RadarAlertTracker.Evaluate(state, baseline).ShouldNotify, "same event is deduplicated");

    var belowThreshold = RadarSnapshot("2026-07-20", "gpt-5.6-sol", "max", 104.9, "green");
    Equal(false, RadarAlertTracker.Evaluate(state, belowThreshold).ShouldNotify, "score delta below five is silent");
    var unread = RadarAlertTracker.RecordFetch(state, belowThreshold);
    Equal(true, RadarAlertTracker.HasUnread(unread, belowThreshold, RadarSurfaceIds.Codex), "new Radar event is unread on Codex");
    Equal(true, RadarAlertTracker.HasUnread(unread, belowThreshold, RadarSurfaceIds.DeepSeek), "new Radar event is unread on DeepSeek");
    RadarAlertTracker.RecordViewed(unread, belowThreshold, RadarSurfaceIds.Codex);
    Equal(false, RadarAlertTracker.HasUnread(unread, belowThreshold, RadarSurfaceIds.Codex), "viewing Codex clears only its unread event");
    Equal(true, RadarAlertTracker.HasUnread(unread, belowThreshold, RadarSurfaceIds.DeepSeek), "DeepSeek remains unread after viewing Codex");
    Equal(true, RadarAlertTracker.HasUnread(unread, belowThreshold), "aggregate unread remains while one surface is unread");
    RadarAlertTracker.RecordViewed(unread, belowThreshold, RadarSurfaceIds.DeepSeek);
    Equal(false, RadarAlertTracker.HasUnread(unread, belowThreshold), "viewing both surfaces clears aggregate unread");
    var nextUnreadSnapshot = RadarSnapshot("2026-07-21", "gpt-5.6-sol", "max", 105, "green");
    var nextUnread = RadarAlertTracker.RecordFetch(unread, nextUnreadSnapshot);
    Equal(true, RadarAlertTracker.HasUnread(nextUnread, nextUnreadSnapshot, RadarSurfaceIds.Codex), "new event restores Codex unread");
    Equal(true, RadarAlertTracker.HasUnread(nextUnread, nextUnreadSnapshot, RadarSurfaceIds.DeepSeek), "new event restores DeepSeek unread");
    Equal(
        false,
        RadarAlertTracker.HasUnread(
            RadarAlertTracker.RecordFetch(new RadarAlertState(), baseline),
            baseline),
        "first Radar fetch does not create a false unread dot");

    var cumulativeThreshold = RadarSnapshot("2026-07-21", "gpt-5.6-sol", "max", 105, "green");
    var scoreDecision = RadarAlertTracker.Evaluate(state, cumulativeThreshold);
    Equal(true, scoreDecision.ShouldNotify, "score delta of five notifies");
    Equal(RadarAlertChangeKind.Score, scoreDecision.Changes.Single().Kind, "score change is semantic");

    var effortChange = RadarSnapshot("2026-07-22", "gpt-5.6-sol", "high", 100, "green");
    var effortDecision = RadarAlertTracker.Evaluate(state, effortChange);
    Equal(true, effortDecision.ShouldNotify, "effort change notifies");
    Equal(RadarAlertChangeKind.Effort, effortDecision.Changes.Single().Kind, "effort change is semantic");

    var nullabilityChange = RadarSnapshot("2026-07-23", "gpt-5.6-sol", "max", null, "green");
    Equal(true, RadarAlertTracker.Evaluate(state, nullabilityChange).ShouldNotify, "score nullability change notifies");

    var comparisonsOnly = baseline with
    {
        EventId = "model_iq:2026-07-24",
        Comparisons =
        [
            new RadarModel("other", "Other", "max", 999, "red", null, null, null, null, null),
        ],
    };
    Equal(false, RadarAlertTracker.Evaluate(state, comparisonsOnly).ShouldNotify, "comparison-only change is silent");

    var notified = RadarAlertTracker.RecordNotification(
        state,
        cumulativeThreshold,
        cumulativeThreshold.CapturedAt);
    Equal(true, notified.NotifiedEventIds.Contains(cumulativeThreshold.EventId), "notified event retained");
    Equal(false, RadarAlertTracker.Evaluate(notified, cumulativeThreshold).ShouldNotify, "notified event is deduplicated");
}

static void TestRadarStatePersistence()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-radar-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new AppSettingsStore(directory);
        var snapshot = RadarSnapshot("2026-07-19", "gpt-5.6-sol", "max", 100, "green");
        var state = RadarAlertTracker.RecordBaseline(new RadarAlertState(), snapshot, snapshot.CapturedAt);
        var unreadBase = RadarSnapshot("2026-07-20", "gpt-5.6-sol", "max", 104.9, "green");
        var unreadSnapshot = unreadBase with
        {
            ResetWindow = new RadarResetWindow(
                false,
                null,
                DateTimeOffset.Parse("2026-08-01T11:32:37+08:00"),
                "https://x.com/example/status/123")
            {
                TargetAt = DateTimeOffset.Parse("2026-08-03T09:00:00+08:00"),
                Scope = "周一表演式重置预告",
            },
            RecommendationFeed = new RadarRecommendationFeed(
                1,
                "latest_valid_per_task",
                unreadBase.CapturedAt,
                unreadBase.SourceUpdatedAt,
                [
                    new RadarRecommendationGroup(
                        "future_category",
                        "未来类别",
                        "未来规则",
                        [RecommendationItem(unreadBase.Primary)]),
                ]),
        };
        state = RadarAlertTracker.RecordFetch(state, unreadSnapshot);
        RadarAlertTracker.RecordViewed(state, unreadSnapshot, RadarSurfaceIds.Codex);
        store.SaveRadarState(state);
        var loaded = store.LoadRadarState();
        Equal(snapshot.EventId, loaded.LastNotifiedEventId, "Radar baseline round trip");
        Equal(unreadSnapshot.Primary.Model, loaded.LastSnapshot?.Primary.Model, "Radar snapshot round trip");
        Equal(
            unreadSnapshot.ResetWindow?.ClosedAt,
            loaded.LastSnapshot?.ResetWindow?.ClosedAt,
            "Radar reset window round trip");
        Equal(
            unreadSnapshot.ResetWindow?.TargetAt,
            loaded.LastSnapshot?.ResetWindow?.TargetAt,
            "Radar reset target time round trip");
        Equal(
            unreadSnapshot.ResetWindow?.Scope,
            loaded.LastSnapshot?.ResetWindow?.Scope,
            "Radar reset target scope round trip");
        Equal("future_category", loaded.LastSnapshot?.RecommendationFeed?.Groups[0].Key, "Radar recommendation feed round trip");
        Equal(unreadSnapshot.EventId, loaded.UnreadEventId, "Radar unread event round trip");
        Equal(false, RadarAlertTracker.HasUnread(loaded, loaded.LastSnapshot, RadarSurfaceIds.Codex), "Codex viewed state round trip");
        Equal(true, RadarAlertTracker.HasUnread(loaded, loaded.LastSnapshot, RadarSurfaceIds.DeepSeek), "DeepSeek unread survives Codex view and restart");
        Equal(unreadSnapshot.EventId, loaded.ViewedEventIdsBySurface[RadarSurfaceIds.Codex], "Radar surface event acknowledgement round trip");
        using (var routingDocument = JsonDocument.Parse(File.ReadAllText(store.RadarRoutingPath)))
        {
            var root = routingDocument.RootElement;
            Equal(1, root.GetProperty("schemaVersion").GetInt32(), "Radar routing schema is versioned");
            Equal(RadarScenarioEvaluator.PolicyVersion, root.GetProperty("evaluatorPolicyVersion").GetString(), "Radar routing policy is explicit");
            Equal(unreadSnapshot.EventId, root.GetProperty("eventId").GetString(), "Radar routing event matches the saved snapshot");
            Equal(3, root.GetProperty("scenarios").EnumerateObject().Count(), "Radar routing persists every automatic scenario");
            var serialized = root.GetRawText();
            Equal(false, serialized.Contains("token", StringComparison.OrdinalIgnoreCase), "Radar routing contains no token fields");
            Equal(false, serialized.Contains("email", StringComparison.OrdinalIgnoreCase), "Radar routing contains no email fields");
        }

        var oversizedSnapshot = unreadSnapshot with
        {
            Primary = unreadSnapshot.Primary with
            {
                IqHistory = Enumerable.Range(0, 120)
                    .Select(index => new RadarIqSample(unreadSnapshot.CapturedAt.AddHours(index), 100 + index))
                    .ToArray(),
            },
            Comparisons = Enumerable.Range(0, 200)
                .Select(index => ScenarioRadarModel($"persisted-model-{index}", "high", 100, 1, 10))
                .ToArray(),
            RecommendationFeed = new RadarRecommendationFeed(
                1,
                "oversized",
                unreadSnapshot.CapturedAt,
                unreadSnapshot.SourceUpdatedAt,
                Enumerable.Range(0, 40)
                    .Select(groupIndex => new RadarRecommendationGroup(
                        $"persisted-group-{groupIndex}",
                        $"Persisted group {groupIndex}",
                        null,
                        Enumerable.Range(0, 5)
                            .Select(itemIndex => RecommendationItem(ScenarioRadarModel(
                                $"persisted-feed-model-{groupIndex}-{itemIndex}",
                                "high",
                                100,
                                1,
                                10)))
                            .ToArray()))
                    .ToArray()),
        };
        store.SaveRadarState(RadarAlertTracker.RecordFetch(state, oversizedSnapshot));
        var bounded = store.LoadRadarState().LastSnapshot!;
        Equal(
            RadarSnapshotLimits.MaxIqHistorySamples,
            bounded.Primary.IqHistory.Count,
            "persisted Radar IQ history is bounded");
        Equal(
            RadarSnapshotLimits.MaxComparisonModels,
            bounded.Comparisons.Count,
            "persisted Radar comparisons are bounded");
        Equal(
            RadarSnapshotLimits.MaxRecommendationItems,
            bounded.RecommendationFeed!.Groups.Sum(group => group.Items.Count),
            "persisted Radar recommendation items are bounded");

        store.SaveRadarState(new RadarAlertState());
        using (var missingRoutingDocument = JsonDocument.Parse(File.ReadAllText(store.RadarRoutingPath)))
        {
            Equal(
                "radar_missing",
                missingRoutingDocument.RootElement.GetProperty("error").GetString(),
                "missing Radar snapshot invalidates the routing artifact");
        }

        File.WriteAllText(store.RadarStatePath, """
            {
              "notifiedEventIds": ["model_iq:remote"],
              "lastNotifiedEventId": "model_iq:remote",
              "lastNotifiedModelIqSnapshot": {
                "model": "gpt-5.6-sol",
                "reasoning_effort": "max",
                "score": 103.6,
                "status": "green"
              }
            }
            """);
        var imported = store.LoadRadarState();
        Equal("max", imported.LastNotifiedModelIqSnapshot?.ReasoningEffort, "remote snake-case effort import");

        const string corrupt = "{not-json";
        File.WriteAllText(store.RadarStatePath, corrupt);
        var recovered = store.LoadRadarState();
        Equal<string?>(null, recovered.LastNotifiedEventId, "corrupt Radar state resets");
        Equal(corrupt, File.ReadAllText(store.RadarStatePath + ".corrupt.bak"), "corrupt Radar state backup");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestSettingsV2PluginMigration()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"zgstokenbar-settings-v2-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        const string original = """
            {
              "enabledProviders": ["claude"],
              "enableRadar": true,
              "enableAiGatewayBalance": true,
              "refreshMinutes": 7
            }
            """;
        var settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, original);
        var store = new AppSettingsStore(
            directory,
            Path.Combine(directory, "missing-legacy.json"));
        var loaded = store.Load();

        Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion, "settings schema migrated");
        Equal(true, loaded.IsPluginEnabled("zgstokenbar.provider.claude"), "Claude plugin migrated");
        Equal(false, loaded.IsPluginEnabled("zgstokenbar.provider.codex"), "disabled Codex migrated");
        Equal(false, loaded.IsPluginEnabled("zgstokenbar.usage.codex-local"), "Codex usage dependency migrated");
        Equal(true, loaded.IsPluginEnabled("zgstokenbar.intelligence.radar"), "Radar plugin migrated");
        Equal(true, loaded.IsPluginEnabled("zgstokenbar.provider.ai-gateway"), "AI Gateway plugin migrated");
        Equal(original, File.ReadAllText(settingsPath + ".v1.bak"), "v1 backup is exact");
        using var migrated = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
        Equal(
            AppSettings.CurrentSchemaVersion,
            migrated.RootElement.GetProperty("schemaVersion").GetInt32(),
            "saved settings are v2");

        var manager = new SettingsMigrationManager(directory);
        Equal(true, manager.Status().V1BackupExists, "migration status sees v1 backup");
        manager.RestoreV1();
        Equal(original, File.ReadAllText(settingsPath), "restore returns exact v1 settings");
        Equal(true, File.Exists(settingsPath + ".v2.rollback"), "v2 rollback is preserved");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void TestPluginProfileStateRedaction()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"zgstokenbar-profile-state-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var configuration = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["endpoint"] = JsonSerializer.SerializeToElement("https://safe.example"),
            ["apiKey"] = JsonSerializer.SerializeToElement("top-level-api-key"),
            ["authorizationHeader"] = JsonSerializer.SerializeToElement("top-level-authorization"),
            ["cookie"] = JsonSerializer.SerializeToElement("top-level-cookie"),
            ["connection"] = JsonSerializer.SerializeToElement(new
            {
                endpoint = "https://nested.example",
                api_key = "nested-api-key",
                items = new[]
                {
                    new { mode = "safe", password = "nested-password" },
                },
            }),
        };
        var profile = new EffectiveProfile(
            1,
            "audit",
            [],
            [new ProfilePlugin("test.plugin", "1.0.0", true, 0, configuration)]);

        ProfileStateStore.SaveLastKnownGood(directory, profile);

        var serialized = File.ReadAllText(Path.Combine(directory, "profile.last-known-good.json"));
        foreach (var secret in new[]
                 {
                     "top-level-api-key",
                     "top-level-authorization",
                     "top-level-cookie",
                     "nested-api-key",
                     "nested-password",
                 })
        {
            Equal(false, serialized.Contains(secret, StringComparison.Ordinal), $"profile state removes {secret}");
        }
        Equal(true, serialized.Contains("https://safe.example", StringComparison.Ordinal), "safe top-level profile configuration remains");
        Equal(true, serialized.Contains("https://nested.example", StringComparison.Ordinal), "safe nested profile configuration remains");
        Equal(true, serialized.Contains("\"mode\":\"safe\"", StringComparison.Ordinal), "safe array configuration remains");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void TestPluginMalformedManifestRejection()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"zgstokenbar-plugin-package-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var packagePath = Path.Combine(directory, "malformed.zgsplugin");
        var manifest = new PluginManifest(
            1,
            "test.plugin",
            "1.0.0",
            ZgsHostApi.Major,
            ZgsHostApi.Minor,
            PluginRuntime.Process,
            false,
            "test.plugin",
            [],
            false,
            0,
            [])
        {
            Entrypoint = "plugin.exe",
            Files = [null!],
            HandshakeTimeoutSeconds = 1,
            CallTimeoutSeconds = 1,
            DisposeTimeoutSeconds = 1,
        };
        using (var archive = System.IO.Compression.ZipFile.Open(
                   packagePath,
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("plugin-manifest.v1.json");
            using (var output = manifestEntry.Open())
            {
                output.Write(JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    PluginSdkJsonContext.Default.PluginManifest));
            }
            using var pluginOutput = archive.CreateEntry("plugin.exe").Open();
            pluginOutput.WriteByte(0);
        }
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        try
        {
            new PluginPackageManager(directory).Install(packagePath, digest);
            throw new InvalidOperationException("Malformed plugin manifest should be rejected.");
        }
        catch (PluginTrustException exception)
        {
            Equal("Process plugin manifest is incompatible.", exception.SafeMessage, "malformed plugin manifest fails through the trust boundary");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void TestPluginHostCatalog()
{
    var directory = Path.Combine(
        Path.GetTempPath(),
        $"zgstokenbar-plugin-host-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var plugins = GeneratedBuiltinPluginRegistry.Create();
    try
    {
        Equal(4, plugins.Count, "four public built-in plugins registered");
        Equal(0, PluginValidation.ValidateCatalog(
            plugins.Select(plugin => plugin.Manifest).ToArray()).Count,
            "built-in catalog validates");
        Equal(
            "zgstokenbar.metrics.system",
            plugins.OrderBy(plugin => plugin.Manifest.Order).First().Manifest.Id,
            "required base plugin is first");

        var host = new ZgsTokenBarHost(
            plugins,
            BuiltinProfiles.Headless(new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["zgstokenbar.intelligence.radar"] = true,
            }),
            "test",
            directory);
        try
        {
            host.StartAsync().AsTask().GetAwaiter().GetResult();
            host.RefreshPluginAsync(
                    "zgstokenbar.metrics.system",
                    "test",
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var snapshot = host.Snapshot(includeValues: true);
            var system = snapshot.Plugins.Single(plugin =>
                plugin.PluginId == "zgstokenbar.metrics.system");
            Equal(1L, system.DataRevision, "plugin data revision increments");
            Equal(1, system.Cards?.Count ?? 0, "system plugin contributes one card");
            Equal(1L, snapshot.Revisions.Revision, "global revision increments");

            host.SetEnabledAsync(
                    "zgstokenbar.metrics.system",
                    false,
                    snapshot.Revisions.ConfigRevision,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            Equal(
                false,
                host.ListPlugins().Single(plugin => plugin.Manifest.Id == "zgstokenbar.metrics.system").Enabled,
                "system metrics plugin can be disabled independently");

            const string radarPluginId = "zgstokenbar.provider.claude";
            var capturedAt = DateTimeOffset.Parse("2026-08-23T08:00:00Z");
            host.Publish(new PluginDataSnapshot(
                radarPluginId,
                capturedAt,
                new(
                    PluginHealthCode.Current,
                    true,
                    false,
                    capturedAt,
                    "radar.current"),
                [
                    new(
                        "card.radar-a",
                        radarPluginId,
                        "radar",
                        ContributionKind.Metric,
                        0,
                        "radar.a",
                        "radar.icon",
                        "accent.radar",
                        [new("radar.value", new("integer", Integer: 1))]),
                    new(
                        "card.radar-b",
                        radarPluginId,
                        "radar",
                        ContributionKind.Metric,
                        1,
                        "radar.b",
                        "radar.icon",
                        "accent.radar",
                        [new("radar.value", new("integer", Integer: 2))]),
                ],
                [],
                []));
            var radarPage = host.ReadPluginData(radarPluginId, null, 1);
            Equal(true, radarPage.NextCursor is not null, "multi-item plugin data creates a cursor");
            var beforeRadarDisable = host.Describe().Revisions;
            host.SetEnabledAsync(
                    radarPluginId,
                    false,
                    beforeRadarDisable.ConfigRevision,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var afterRadarDisable = host.Describe().Revisions;
            Equal(
                beforeRadarDisable.DataRevisions[radarPluginId] + 1,
                afterRadarDisable.DataRevisions[radarPluginId],
                "plugin disable increments its data revision");
            try
            {
                host.ReadPluginData(radarPluginId, radarPage.NextCursor, 1);
                throw new InvalidOperationException("Old cursor should be invalid after plugin disable.");
            }
            catch (HostCommandException exception)
            {
                Equal("data_changed", exception.Code, "plugin disable invalidates existing cursors");
            }
            host.SetEnabledAsync(
                    radarPluginId,
                    false,
                    afterRadarDisable.ConfigRevision,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var afterNoOpDisable = host.Describe().Revisions;
            Equal(afterRadarDisable.Revision, afterNoOpDisable.Revision, "idempotent disable keeps global revision");
            Equal(
                afterRadarDisable.DataRevisions[radarPluginId],
                afterNoOpDisable.DataRevisions[radarPluginId],
                "idempotent disable keeps data revision");

            try
            {
                host.SetEnabledAsync(
                        "zgstokenbar.provider.codex",
                        false,
                        host.Describe().Revisions.ConfigRevision,
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                throw new InvalidOperationException("Enabled dependent should block disable.");
            }
            catch (HostCommandException exception)
            {
                Equal("plugin_required", exception.Code, "enabled dependent blocks disable");
            }
            host.SetEnabledAsync(
                    "zgstokenbar.usage.codex-local",
                    false,
                    host.Describe().Revisions.ConfigRevision,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            host.SetEnabledAsync(
                    "zgstokenbar.provider.codex",
                    false,
                    host.Describe().Revisions.ConfigRevision,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            Equal(false, host.DescribePlugin("zgstokenbar.provider.codex")?.Enabled, "dependency-order disable succeeds");
        }
        finally
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void TestPluginRequestIdValidation()
{
    Equal(false, PluginValidation.IsRequestId(null), "null request IDs are invalid");
    Equal(false, PluginValidation.IsRequestId(string.Empty), "empty request IDs are invalid");
    Equal(false, PluginValidation.IsRequestId(new string('a', 65)), "oversized request IDs are invalid");
    Equal(false, PluginValidation.IsRequestId("line\nbreak"), "control characters are invalid in request IDs");
    Equal(true, PluginValidation.IsRequestId("request-123"), "printable request IDs remain valid");
}

static void TestSettingsNormalization()
{
    var settings = new AppSettings
    {
        EnabledProviders = ["codex", "codex", "unknown"],
        RefreshMinutes = 99,
        TaskbarPosition = 1.5,
        TaskbarMonitor = "  \\\\.\\DISPLAY2  ",
        TaskbarPositions = new Dictionary<string, double>
        {
            [" DISPLAY2 "] = 1.5,
            ["DISPLAY3"] = double.NaN,
        },
    };
    settings.Normalize();
    Equal(1, settings.EnabledProviders.Length, "deduplicated providers");
    Equal("codex", settings.EnabledProviders[0], "provider");
    Equal(60, settings.RefreshMinutes, "refresh clamp");
    Equal(1d, settings.TaskbarPosition, "taskbar position upper clamp");
    Equal(@"\\.\DISPLAY2", settings.TaskbarMonitor, "taskbar monitor trims whitespace");
    Equal(1d, settings.TaskbarPositions[@"DISPLAY2"], "per-monitor taskbar position clamps");
    Equal(false, settings.TaskbarPositions.ContainsKey("DISPLAY3"), "invalid per-monitor position is removed");
    Equal(true, new AppSettings().AutoRefreshClaudeOAuth, "Claude OAuth refresh defaults on");
    Equal(false, new AppSettings().EnableRadar, "Radar is opt-in by default");
    Equal(false, new AppSettings().EnableRadarAlerts, "Radar alerts are opt-in by default");
    Equal(true, new AppSettings().EnableCodexEconomyBar, "Codex economy Bar control defaults visible");
    Equal("midnight", new AppSettings().BackgroundPalette, "original background palette defaults on");
    Equal(false, new AppSettings().KeepRunning, "keep-running watchdog is opt-in by default");

    var keepRunning = new AppSettings { KeepRunning = true, OpenAtLogin = false };
    keepRunning.Normalize();
    Equal(true, keepRunning.OpenAtLogin, "keep-running mode also starts with Windows");

    var noProviders = new AppSettings { EnabledProviders = [] };
    noProviders.Normalize();
    Equal(0, noProviders.EnabledProviders.Length, "an explicit empty provider list remains independently disabled");

    var disabledSystemMetrics = new AppSettings
    {
        PluginEnabled = new(StringComparer.Ordinal) { ["zgstokenbar.metrics.system"] = false },
    };
    disabledSystemMetrics.Normalize();
    Equal(false, disabledSystemMetrics.PluginEnabled["zgstokenbar.metrics.system"], "system metrics can be disabled independently");

    var codexDependencies = new AppSettings
    {
        EnabledProviders = ["claude"],
        EnableSub2ApiPool = true,
        PluginEnabled = new(StringComparer.Ordinal) { ["zgstokenbar.usage.codex-local"] = true },
    };
    codexDependencies.Normalize();
    Equal(false, codexDependencies.EnableSub2ApiPool, "Sub2API is disabled when Codex is disabled");
    Equal(false, codexDependencies.PluginEnabled["zgstokenbar.usage.codex-local"], "local Codex usage is disabled when Codex is disabled");

    var palette = new AppSettings { BackgroundPalette = " NAVY " };
    palette.Normalize();
    Equal("navy", palette.BackgroundPalette, "background palette normalizes case and whitespace");
    palette.BackgroundPalette = "custom-bright";
    palette.Normalize();
    Equal("midnight", palette.BackgroundPalette, "unknown background palette restores the safe default");

    var radarAlertsWithoutPreview = new AppSettings { EnableRadarAlerts = true };
    radarAlertsWithoutPreview.Normalize();
    Equal(false, radarAlertsWithoutPreview.EnableRadarAlerts, "Radar alerts require Radar preview");

    var invalidTaskbarPosition = new AppSettings { TaskbarPosition = double.NaN };
    invalidTaskbarPosition.Normalize();
    Equal<double?>(null, invalidTaskbarPosition.TaskbarPosition, "invalid taskbar position reset");

    var mixedCase = new AppSettings
    {
        EnabledProviders = [" CODEX ", "Claude", "codex", null!],
    };
    mixedCase.Normalize();
    Equal(2, mixedCase.EnabledProviders.Length, "case-insensitive provider normalization count");
    Equal("codex", mixedCase.EnabledProviders[0], "normalized Codex provider");
    Equal("claude", mixedCase.EnabledProviders[1], "normalized Claude provider");

    var nullProviders = new AppSettings { EnabledProviders = null! };
    nullProviders.Normalize();
    Equal(0, nullProviders.EnabledProviders.Length, "an invalid null provider list normalizes to empty");

    var collapsedGroups = new AppSettings
    {
        CollapsedMiniGroups =
        [
            " CLAUDE:QUOTA:0 ",
            "codex:quota:0",
            "codex:quota:0",
            "codex:account@example.test:0",
            "unknown:quota:0",
        ],
    };
    collapsedGroups.Normalize();
    Equal(true, collapsedGroups.MiniAreaLayouts[MiniAreaIds.Claude].Collapsed, "legacy Claude collapse migrates to its area");
    Equal(true, collapsedGroups.MiniAreaLayouts[MiniAreaIds.Codex].Collapsed, "legacy Codex collapse migrates to its area");
    Equal(false, collapsedGroups.MiniProviderAreaCollapsed, "legacy global collapse flag is cleared after migration");
    Equal(0, collapsedGroups.CollapsedMiniGroups.Length, "legacy per-card keys are removed after migration");

    var legacyAiGatewayCollapse = new AppSettings { AiGatewayMiniCollapsed = true };
    legacyAiGatewayCollapse.Normalize();
    Equal(true, legacyAiGatewayCollapse.MiniAreaLayouts[MiniAreaIds.AiGateway].Collapsed, "legacy AI Gateway collapse migrates to its area");
    Equal(false, legacyAiGatewayCollapse.AiGatewayMiniCollapsed, "legacy AI Gateway flag is cleared after migration");

    var legacyGlobalCollapse = new AppSettings { MiniProviderAreaCollapsed = true };
    legacyGlobalCollapse.Normalize();
    Equal(true, legacyGlobalCollapse.MiniAreaLayouts[MiniAreaIds.Claude].Collapsed, "legacy global collapse seeds Claude");
    Equal(true, legacyGlobalCollapse.MiniAreaLayouts[MiniAreaIds.Codex].Collapsed, "legacy global collapse seeds Codex");
    Equal(true, legacyGlobalCollapse.MiniAreaLayouts[MiniAreaIds.AiGateway].Collapsed, "legacy global collapse seeds AI Gateway");

    var systemAreaLayout = new AppSettings
    {
        MiniAreaLayouts = new(StringComparer.Ordinal)
        {
            [MiniAreaIds.SystemMetrics] = new(true, 120),
        },
    };
    systemAreaLayout.Normalize();
    Equal(true, systemAreaLayout.MiniAreaLayouts[MiniAreaIds.SystemMetrics].Collapsed, "system collapse survives settings normalization");
    Equal(120, systemAreaLayout.MiniAreaLayouts[MiniAreaIds.SystemMetrics].Width, "system width survives settings normalization");

    var economyAreaLayout = new AppSettings
    {
        MiniAreaLayouts = new(StringComparer.Ordinal)
        {
            [MiniAreaIds.CodexEconomy] = new(false, 180),
        },
    };
    economyAreaLayout.Normalize();
    Equal(null, economyAreaLayout.MiniAreaLayouts[MiniAreaIds.CodexEconomy].Width, "economy button ignores legacy resizable width state");

    var miniAreaOrder = new AppSettings
    {
        MiniAreaOrder = [" zgstokenbar.provider.codex ", "zgstokenbar.provider.codex", "invalid..id", "plugin.future"],
    };
    miniAreaOrder.Normalize();
    Equal(
        true,
        miniAreaOrder.MiniAreaOrder.SequenceEqual([MiniAreaIds.Codex, "plugin.future"], StringComparer.Ordinal),
        "Mini area order keeps valid future modules and removes duplicates");
}

static void TestCodexMiniDisplayModeSettings()
{
    Equal(
        CodexMiniDisplayModes.Accounts,
        new AppSettings().CodexMiniDisplayMode,
        "Codex Mini defaults to the existing per-account presentation");
    Equal(
        CodexMiniDisplayModes.Accounts,
        CodexMiniDisplayModes.Normalize(null),
        "missing Codex Mini display mode uses the account presentation");
    Equal(
        CodexMiniDisplayModes.Accounts,
        CodexMiniDisplayModes.Normalize("future-mode"),
        "unknown Codex Mini display mode fails back to accounts");
    Equal(
        CodexMiniDisplayModes.Pool,
        CodexMiniDisplayModes.Normalize(" POOL "),
        "Codex Mini pool mode normalizes case and whitespace");

    var normalized = new AppSettings { CodexMiniDisplayMode = " POOL " };
    normalized.Normalize();
    Equal(
        CodexMiniDisplayModes.Pool,
        normalized.CodexMiniDisplayMode,
        "settings normalization preserves the supported pool mode");
    normalized.CodexMiniDisplayMode = "unsupported";
    normalized.Normalize();
    Equal(
        CodexMiniDisplayModes.Accounts,
        normalized.CodexMiniDisplayMode,
        "settings normalization restores accounts for unsupported modes");

    var directory = Path.Combine(Path.GetTempPath(), $"wmt-codex-mini-mode-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new AppSettingsStore(directory);
        store.Save(new AppSettings { CodexMiniDisplayMode = CodexMiniDisplayModes.Pool });
        using var document = JsonDocument.Parse(File.ReadAllBytes(store.SettingsPath));
        Equal(
            CodexMiniDisplayModes.Pool,
            document.RootElement.GetProperty("codexMiniDisplayMode").GetString(),
            "Codex Mini display mode is written to settings JSON");
        Equal(
            CodexMiniDisplayModes.Pool,
            store.Load().CodexMiniDisplayMode,
            "Codex Mini display mode survives the settings JSON round trip");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void TestKeepRunningWatchdogPolicy()
{
    const string executablePath = @"C:\Program Files\ZGSTokenBar\ZGSTokenBar.exe";
    Equal<string?>(
        null,
        StartupManager.BuildCommand(executablePath, openAtLogin: false, keepRunning: false),
        "disabled startup removes the Run command");
    Equal(
        $"\"{executablePath}\"",
        StartupManager.BuildCommand(executablePath, openAtLogin: true, keepRunning: false),
        "ordinary startup launches the quoted executable directly");
    Equal(
        $"\"{executablePath}\" --watchdog",
        StartupManager.BuildCommand(executablePath, openAtLogin: false, keepRunning: true),
        "keep-running startup launches watchdog mode");
    Equal(
        StartupRegistrationAction.None,
        StartupManager.RequiredAction(null, null),
        "missing disabled startup remains untouched");
    Equal(
        StartupRegistrationAction.None,
        StartupManager.RequiredAction($"\"{executablePath}\"", $"\"{executablePath}\""),
        "matching startup command is not rewritten");
    Equal(
        StartupRegistrationAction.Set,
        StartupManager.RequiredAction(
            "\"C:\\Old\\ZGSTokenBar.exe\"",
            $"\"{executablePath}\""),
        "an upgraded executable path replaces the startup command");
    Equal(
        StartupRegistrationAction.Set,
        StartupManager.RequiredAction(
            $"\"{executablePath}\"",
            $"\"{executablePath}\" --watchdog"),
        "changing keep-running mode replaces the startup command");
    Equal(
        StartupRegistrationAction.Delete,
        StartupManager.RequiredAction($"\"{executablePath}\"", null),
        "disabling startup removes an existing command");

    Equal(true, WatchdogManager.IsWatchdogRequest(["--WATCHDOG"]), "watchdog switch is case-insensitive");
    Equal(false, WatchdogManager.IsWatchdogRequest(["--settings"]), "settings launch stays in the main app");
    Equal(true, WatchdogManager.ShouldStartApplication(true, false), "missing main app is recovered");
    Equal(false, WatchdogManager.ShouldStartApplication(true, true), "running main app is not duplicated");
    Equal(false, WatchdogManager.ShouldStartApplication(false, false), "disabled watchdog does not recover the app");
    Equal(2_000, WatchdogManager.PollMilliseconds, "watchdog restart attempts are throttled");
}

static void TestReleaseUpdateDiscovery()
{
    static JsonDocument Release(string tag, string page, bool includePackage = true, bool includeChecksums = true)
    {
        var version = tag.TrimStart('v', 'V');
        var assets = new List<object>();
        if (includePackage)
        {
            assets.Add(new
            {
                name = $"ZGSTokenBar-Portable-v{version}.zip",
                browser_download_url = $"https://github.com/ZeroGameStudio-CN/ZGSTokenBar/releases/download/{tag}/ZGSTokenBar-Portable-v{version}.zip",
            });
        }
        if (includeChecksums)
        {
            assets.Add(new
            {
                name = $"ZGSTokenBar-v{version}-SHA256.txt",
                browser_download_url = $"https://github.com/ZeroGameStudio-CN/ZGSTokenBar/releases/download/{tag}/ZGSTokenBar-v{version}-SHA256.txt",
            });
        }
        return JsonDocument.Parse(JsonSerializer.Serialize(new { tag_name = tag, html_url = page, assets }));
    }

    using (var document = Release(
        "v3.0.1",
        "https://github.com/ZeroGameStudio-CN/ZGSTokenBar/releases/tag/v3.0.1"))
    {
        var update = ReleaseUpdateChecker.Parse(document.RootElement, new Version(3, 0, 0));
        Equal(new Version(3, 0, 1), update?.Version, "newer complete release is discovered");
        Equal("github.com", update?.PageUri.Host, "release page stays on GitHub");
    }
    using (var document = Release(
        "v3.0.0",
        "https://github.com/ZeroGameStudio-CN/ZGSTokenBar/releases/tag/v3.0.0"))
    {
        Equal<ReleaseUpdateInfo?>(
            null,
            ReleaseUpdateChecker.Parse(document.RootElement, new Version(3, 0, 0)),
            "current release does not notify");
    }
    using (var document = Release(
        "v3.0.1",
        "https://github.com/ZeroGameStudio-CN/ZGSTokenBar/releases/tag/v3.0.1",
        includeChecksums: false))
    {
        Equal<ReleaseUpdateInfo?>(
            null,
            ReleaseUpdateChecker.Parse(document.RootElement, new Version(3, 0, 0)),
            "release without checksums fails closed");
    }

    var untrustedRejected = false;
    try
    {
        using var document = Release("v3.0.1", "https://example.com/releases/tag/v3.0.1");
        ReleaseUpdateChecker.Parse(document.RootElement, new Version(3, 0, 0));
    }
    catch (JsonException)
    {
        untrustedRejected = true;
    }
    Equal(true, untrustedRejected, "untrusted release page is rejected");
    Equal(false, ReleaseUpdateChecker.TryParseTag("3.0.1", out _), "version tag requires v prefix");
    Equal(false, ReleaseUpdateChecker.TryParseTag("v3.0.1-beta", out _), "prerelease tag is rejected");

    var handler = new RecordingHandler(request =>
    {
        Equal("api.github.com", request.RequestUri?.Host, "update check uses GitHub API");
        Equal(true, request.Headers.UserAgent.Any(), "update check identifies the product");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });
    using var checker = new ReleaseUpdateChecker(new HttpClient(handler));
    Equal<ReleaseUpdateInfo?>(
        null,
        checker.CheckAsync(new Version(3, 0, 0), CancellationToken.None).GetAwaiter().GetResult(),
        "missing latest release is a normal no-update result");
}

static void TestPositionPersistence()
{
    var directory = Path.Combine(Path.GetTempPath(), $"ztb-position-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var topology = DisplayTopology.CreateSnapshot(
            "console",
            [new DisplayTopologySource(
                @"\\.\DISPLAY2",
                "path:monitor-b",
                true,
                new Rectangle(0, 0, 1920, 1080),
                new Rectangle(0, 0, 1920, 1032))]);
        var monitorKey = topology.Primary.MonitorKey;
        var store = new AppSettingsStore(directory);
        store.Save(new AppSettings
        {
            TaskbarDocked = false,
            WindowX = 281,
            WindowY = 173,
            TaskbarPosition = .42,
            TaskbarMonitor = @"\\.\DISPLAY2",
            TaskbarPositions = new Dictionary<string, double>
            {
                [@"\\.\DISPLAY1"] = .18,
                [@"\\.\DISPLAY2"] = .42,
            },
            PlacementMigrationSeed = new PlacementMigrationSeed
            {
                TaskbarDocked = true,
                TaskbarMonitor = @"\\.\DISPLAY2",
                TaskbarPosition = .42,
                TaskbarPositions = new Dictionary<string, double>
                {
                    [@"\\.\DISPLAY2"] = .42,
                },
            },
            PlacementProfiles = new Dictionary<string, WindowPlacementProfile>
            {
                [topology.Key] = new()
                {
                    IsDocked = true,
                    DockedMonitorKey = monitorKey,
                    TaskbarPositions = new Dictionary<string, double> { [monitorKey] = .42 },
                    FloatingMonitorKey = monitorKey,
                    FloatingX = .2,
                    FloatingY = .3,
                },
            },
        });

        var restarted = store.Load();
        Equal(false, restarted.TaskbarDocked, "floating mode persists");
        Equal(281, restarted.WindowX, "floating window X persists");
        Equal(173, restarted.WindowY, "floating window Y persists");
        Equal(.42, restarted.TaskbarPosition, "taskbar fallback position persists");
        Equal(@"\\.\DISPLAY2", restarted.TaskbarMonitor, "selected taskbar monitor persists");
        Equal(.18, restarted.TaskbarPositions[@"\\.\DISPLAY1"], "primary monitor position persists");
        Equal(.42, restarted.TaskbarPositions[@"\\.\DISPLAY2"], "secondary monitor position persists");
        Equal(@"\\.\DISPLAY2", restarted.PlacementMigrationSeed?.TaskbarMonitor, "placement migration seed persists");
        Equal(.42, restarted.PlacementProfiles[topology.Key].TaskbarPositions[monitorKey], "topology profile persists");

        var invalidTopology = DisplayTopology.CreateSnapshot(
            "rdp",
            [new DisplayTopologySource(
                @"\\.\DISPLAY1",
                "gdi:remote",
                true,
                new Rectangle(0, 0, 1280, 720),
                new Rectangle(0, 0, 1280, 680))]);
        var profiles = new JsonObject
        {
            [topology.Key] = JsonSerializer.SerializeToNode(restarted.PlacementProfiles[topology.Key]),
            [invalidTopology.Key] = new JsonObject { ["floatingX"] = "damaged" },
        };
        File.WriteAllText(store.SettingsPath, new JsonObject
        {
            ["locale"] = "en",
            ["placementSchemaVersion"] = 1,
            ["placementProfiles"] = profiles,
        }.ToJsonString());
        var tolerant = store.Load();
        Equal(1, tolerant.PlacementProfiles.Count, "one damaged profile is isolated");
        Equal(true, tolerant.PlacementProfiles.ContainsKey(topology.Key), "valid profile survives damaged sibling");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static void TestDisplayTopologyIdentity()
{
    DisplayTopologySource[] localSources =
    [
        new(@"\\.\DISPLAY1", "path:monitor-a", false, new Rectangle(-1920, 0, 1920, 1080), new Rectangle(-1920, 0, 1920, 1032)),
        new(@"\\.\DISPLAY2", "path:monitor-b", true, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1032)),
        new(@"\\.\DISPLAY3", "path:monitor-c", false, new Rectangle(1920, 0, 1707, 960), new Rectangle(1920, 0, 1707, 912)),
    ];
    var local = DisplayTopology.CreateSnapshot("console", localSources);
    var reordered = DisplayTopology.CreateSnapshot(
        "console",
        [
            new(@"\\.\DISPLAY9", "path:monitor-c", true, new Rectangle(0, -2160, 3840, 2160), new Rectangle(0, -2160, 3840, 2100)),
            new(@"\\.\DISPLAY8", "path:monitor-a", false, new Rectangle(-2560, 0, 2560, 1440), new Rectangle(-2560, 0, 2560, 1400)),
            new(@"\\.\DISPLAY7", "path:monitor-b", false, new Rectangle(0, 0, 2560, 1440), new Rectangle(0, 0, 2500, 1440)),
        ]);
    Equal(local.Key, reordered.Key, "topology key ignores GDI numbering, order, geometry, resolution, primary, and work area");
    Equal(false, string.Equals(local.IdentitySignature, reordered.IdentitySignature, StringComparison.Ordinal), "settle signature retains GDI mapping changes");

    var remote = DisplayTopology.CreateSnapshot("rdp", localSources);
    Equal(false, string.Equals(local.Key, remote.Key, StringComparison.Ordinal), "console and RDP topologies are isolated");
    var single = DisplayTopology.CreateSnapshot("console", [localSources[1]]);
    Equal(false, string.Equals(local.Key, single.Key, StringComparison.Ordinal), "single and multi display topologies are isolated");
    var clone = DisplayTopology.CreateSnapshot(
        "console",
        [new DisplayTopologySource(
            @"\\.\DISPLAY1",
            "path:monitor-a\0path:monitor-b",
            true,
            new Rectangle(0, 0, 1920, 1080),
            new Rectangle(0, 0, 1920, 1032))]);
    Equal(false, string.Equals(local.Key, clone.Key, StringComparison.Ordinal), "clone and extended desktop views are isolated");
    Equal(true, PlacementKey.IsTopology(local.Key), "topology key uses the persisted v1 hash format");
    Equal(true, local.Screens.All(screen => PlacementKey.IsMonitor(screen.MonitorKey)), "monitor keys use the persisted v1 hash format");
}

static void TestTopologyPlacementIsolation()
{
    var local = DisplayTopology.CreateSnapshot(
        "console",
        [
            new(@"\\.\DISPLAY1", "path:monitor-a", true, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1032)),
            new(@"\\.\DISPLAY2", "path:monitor-b", false, new Rectangle(1920, 0, 1920, 1080), new Rectangle(1920, 0, 1920, 1032)),
        ]);
    var remote = DisplayTopology.CreateSnapshot(
        "rdp",
        [new(@"\\.\DISPLAY1", "gdi:remote-primary", true, new Rectangle(0, 0, 1280, 720), new Rectangle(0, 0, 1280, 680))]);
    var localSingle = DisplayTopology.CreateSnapshot(
        "console",
        [new(@"\\.\DISPLAY2", "path:monitor-b", true, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1032))]);
    var localB = local.FindByGdiName(@"\\.\DISPLAY2")!;
    var localA = local.FindByGdiName(@"\\.\DISPLAY1")!;
    var settings = new AppSettings
    {
        PlacementProfiles = new Dictionary<string, WindowPlacementProfile>
        {
            [local.Key] = new()
            {
                IsDocked = true,
                DockedMonitorKey = localB.MonitorKey,
                TaskbarPositions = new Dictionary<string, double>
                {
                    [localA.MonitorKey] = .12,
                    [localB.MonitorKey] = .04,
                },
                FloatingMonitorKey = localB.MonitorKey,
                FloatingX = .2,
                FloatingY = .3,
            },
        },
    };
    var coordinator = new WindowPlacementCoordinator(settings);
    var localActivation = coordinator.Activate(local, new Size(400, 40));
    Equal(localB.MonitorKey, localActivation.Profile.DockedMonitorKey, "local multi display restores its desired monitor");
    Equal(.12, coordinator.PositionForResolvedMonitor(local, localActivation.Profile, @"\\.\DISPLAY1", .04), "fallback uses resolved monitor position");
    Equal(localB.MonitorKey, coordinator.ActiveProfile?.DockedMonitorKey, "resolved fallback does not mutate desired monitor");
    Equal(.04, coordinator.ActiveProfile!.TaskbarPositions[localB.MonitorKey], "resolved fallback does not mutate desired position");

    var remoteActivation = coordinator.Activate(remote, new Size(400, 40));
    Equal<WindowPlacementCommit?>(null, remoteActivation.MigrationCommit, "new remote topology projection is not persisted automatically");
    var remoteCommit = coordinator.CommitDocked(@"\\.\DISPLAY1", .77, new Size(400, 40));
    Equal(true, remoteCommit is not null, "remote placement commit is accepted");

    var restoredLocal = coordinator.Activate(local, new Size(400, 40));
    Equal(localB.MonitorKey, restoredLocal.Profile.DockedMonitorKey, "returning local topology restores monitor B");
    Equal(.04, restoredLocal.Profile.TaskbarPositions[localB.MonitorKey], "returning local topology restores 0.04");

    _ = coordinator.Activate(localSingle, new Size(400, 40));
    _ = coordinator.CommitDocked(@"\\.\DISPLAY2", .33, new Size(400, 40));
    Equal(.04, coordinator.Activate(local, new Size(400, 40)).Profile.TaskbarPositions[localB.MonitorKey], "local single profile does not overwrite local multi profile");
    Equal(.77, coordinator.Activate(remote, new Size(400, 40)).Profile.TaskbarPositions[remote.Primary.MonitorKey], "remote profile restores independently");
    Equal(.33, coordinator.Activate(localSingle, new Size(400, 40)).Profile.TaskbarPositions[localSingle.Primary.MonitorKey], "local single profile restores independently");

    _ = coordinator.Activate(local, new Size(400, 40));
    var floatingCommit = coordinator.CommitFloating(new Rectangle(2300, 500, 400, 40));
    Equal(true, floatingCommit?.Profile.IsDocked == false, "floating commit changes only the active mode");
    Equal(.04, floatingCommit!.Profile.TaskbarPositions[localB.MonitorKey], "floating commit retains the last docked position");
    var floatingX = floatingCommit.Profile.FloatingX;
    var floatingY = floatingCommit.Profile.FloatingY;
    var dockedAgain = coordinator.CommitDocked(@"\\.\DISPLAY2", .09, new Size(400, 40));
    Equal(true, dockedAgain?.Profile.IsDocked == true, "docked commit restores docked mode");
    Equal(floatingX, dockedAgain!.Profile.FloatingX, "docked commit retains the last floating X");
    Equal(floatingY, dockedAgain.Profile.FloatingY, "docked commit retains the last floating Y");
}

static void TestTopologyPlacementMigration()
{
    var directory = Path.Combine(Path.GetTempPath(), $"ztb-topology-migration-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new AppSettingsStore(directory);
        File.WriteAllText(
            store.SettingsPath,
            """{"taskbarDocked":true,"taskbarMonitor":"\\\\.\\DISPLAY2","taskbarPosition":0.04,"taskbarPositions":{"\\\\.\\DISPLAY2":0.04,"\\\\.\\DISPLAY3":0.23},"windowX":154,"windowY":152}""");
        var settings = store.Load();
        Equal(@"\\.\DISPLAY2", settings.PlacementMigrationSeed?.TaskbarMonitor, "legacy target is frozen before mirrors can change");

        settings.TaskbarMonitor = @"\\.\DISPLAY1";
        settings.TaskbarPosition = .77;
        store.Save(settings);
        var mirrored = store.Load();
        Equal(@"\\.\DISPLAY2", mirrored.PlacementMigrationSeed?.TaskbarMonitor, "migration seed stays immutable after legacy mirror changes");

        var remote = DisplayTopology.CreateSnapshot(
            "rdp",
            [new(@"\\.\DISPLAY1", "gdi:remote", true, new Rectangle(0, 0, 1280, 720), new Rectangle(0, 0, 1280, 680))]);
        var local = DisplayTopology.CreateSnapshot(
            "console",
            [
                new(@"\\.\DISPLAY1", "path:monitor-a", false, new Rectangle(-1920, 0, 1920, 1080), new Rectangle(-1920, 0, 1920, 1032)),
                new(@"\\.\DISPLAY2", "path:monitor-b", true, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1032)),
                new(@"\\.\DISPLAY3", "path:monitor-c", false, new Rectangle(1920, 0, 1707, 960), new Rectangle(1920, 0, 1707, 912)),
            ]);
        var coordinator = new WindowPlacementCoordinator(mirrored);
        var remoteActivation = coordinator.Activate(remote, new Size(400, 40));
        Equal<WindowPlacementCommit?>(null, remoteActivation.MigrationCommit, "missing legacy DISPLAY2 is not impersonated by RDP primary");
        _ = coordinator.CommitDocked(@"\\.\DISPLAY1", .77, new Size(400, 40));

        var localActivation = coordinator.Activate(local, new Size(400, 40));
        Equal(true, localActivation.MigrationCommit?.IsMigration == true, "local topology migrates later from immutable seed");
        var display2 = local.FindByGdiName(@"\\.\DISPLAY2")!;
        var display3 = local.FindByGdiName(@"\\.\DISPLAY3")!;
        Equal(display2.MonitorKey, localActivation.Profile.DockedMonitorKey, "legacy DISPLAY2 maps exactly");
        Equal(.04, localActivation.Profile.TaskbarPositions[display2.MonitorKey], "legacy DISPLAY2 position migrates");
        Equal(.23, localActivation.Profile.TaskbarPositions[display3.MonitorKey], "legacy DISPLAY3 position migrates");
        Equal(.77, coordinator.Activate(remote, new Size(400, 40)).Profile.TaskbarPositions[remote.Primary.MonitorKey], "RDP commit survives later local migration");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}

static void TestFloatingPlacementNormalization()
{
    var cases = new[]
    {
        (Area: new Rectangle(-1920, 0, 1920, 1032), Window: new Size(400, 40), Location: new Point(-1700, 600)),
        (Area: new Rectangle(0, 0, 1280, 680), Window: new Size(520, 60), Location: new Point(700, 510)),
        (Area: new Rectangle(200, 100, 300, 200), Window: new Size(500, 300), Location: new Point(-100, -100)),
    };
    foreach (var item in cases)
    {
        var normalized = WindowPlacementCoordinator.NormalizeFloatingLocation(item.Area, item.Window, item.Location);
        var restored = WindowPlacementCoordinator.RestoreFloatingLocation(item.Area, item.Window, normalized.X, normalized.Y);
        var expectedX = Math.Clamp(item.Location.X, item.Area.Left, Math.Max(item.Area.Left, item.Area.Right - item.Window.Width));
        var expectedY = Math.Clamp(item.Location.Y, item.Area.Top, Math.Max(item.Area.Top, item.Area.Bottom - item.Window.Height));
        Equal(true, Math.Abs(expectedX - restored.X) <= 1, "floating X round-trip stays within one pixel");
        Equal(true, Math.Abs(expectedY - restored.Y) <= 1, "floating Y round-trip stays within one pixel");
        Equal(true, normalized.X is >= 0 and <= 1 && normalized.Y is >= 0 and <= 1, "floating ratios stay normalized");
    }

    Equal(
        new Point(200, 100),
        WindowPlacementCoordinator.RestoreFloatingLocation(
            new Rectangle(200, 100, 300, 200),
            new Size(500, 300),
            1,
            1),
        "oversized floating Mini anchors at working-area origin");
}

static void TestSettingsLocaleMigration()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-locale-{Guid.NewGuid():N}");
    var legacyDirectory = Path.Combine(Path.GetTempPath(), $"wmt-legacy-locale-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    Directory.CreateDirectory(legacyDirectory);
    var legacyPath = Path.Combine(legacyDirectory, "config.json");
    try
    {
        var store = new AppSettingsStore(directory, legacyPath);
        Equal("zh-CN", store.Load().Locale, "new install defaults to Chinese");

        File.WriteAllText(store.SettingsPath, """{"refreshMinutes":5}""");
        var prePalette = store.Load();
        Equal("en", prePalette.Locale, "pre-localization native settings stay English");
        Equal("midnight", prePalette.BackgroundPalette, "existing settings inherit the original background");

        File.Delete(store.SettingsPath);
        File.WriteAllText(legacyPath, """{"locale":"en","enabledProviders":["codex"]}""");
        var importedEnglish = store.Load();
        Equal("en", importedEnglish.Locale, "Electron English locale imported");
        Equal("codex", importedEnglish.EnabledProviders.Single(), "Electron settings imported");

        File.WriteAllText(legacyPath, """{"locale":"zh-CN"}""");
        Equal("zh-CN", store.Load().Locale, "Electron Chinese locale imported");

        File.WriteAllText(legacyPath, """{"locale":"future-locale"}""");
        Equal("zh-CN", store.Load().Locale, "invalid imported locale normalizes safely");

        var persisted = new AppSettings { Locale = "en", BackgroundPalette = "plum" };
        store.Save(persisted);
        var reloaded = store.Load();
        Equal("en", reloaded.Locale, "native locale persists independently");
        Equal("plum", reloaded.BackgroundPalette, "background palette persists across restart");
    }
    finally
    {
        Directory.Delete(directory, true);
        Directory.Delete(legacyDirectory, true);
    }
}

static void TestSettingsTaskbarLayoutMigration()
{
    var directory = Path.Combine(Path.GetTempPath(), $"ztb-taskbar-layout-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new AppSettingsStore(directory);
        File.WriteAllText(
            store.SettingsPath,
            """{"useTaskbarRings":false,"windowX":240,"windowY":160}""");
        var floating = store.Load();
        Equal(false, floating.TaskbarDocked, "legacy floating bar remains floating");
        Equal(240, floating.WindowX, "legacy floating X position remains available");
        Equal(160, floating.WindowY, "legacy floating Y position remains available");

        File.WriteAllText(store.SettingsPath, """{"useTaskbarRings":true}""");
        Equal(true, store.Load().TaskbarDocked, "legacy taskbar Mini remains docked");

        File.WriteAllText(
            store.SettingsPath,
            """{"useTaskbarRings":true,"taskbarDocked":false}""");
        Equal(false, store.Load().TaskbarDocked, "explicit docking state overrides the legacy mode");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestAtomicCredentialWrites()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-atomic-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "auth.json");
        File.WriteAllText(path, "old");
        Equal(false, CredentialSupport.AtomicWrite(path, "stale", "different"), "stale credential write rejected");
        Equal("old", File.ReadAllText(path), "stale credential write preserves winner");
        Equal(true, CredentialSupport.AtomicWrite(path, "new", "old"), "matching credential write succeeds");
        Equal("new", File.ReadAllText(path), "credential replacement contents");

        File.WriteAllText(path, "race");
        using var start = new ManualResetEventSlim(false);
        var first = Task.Run(() => { start.Wait(); return CredentialSupport.AtomicWrite(path, "first", "race"); });
        var second = Task.Run(() => { start.Wait(); return CredentialSupport.AtomicWrite(path, "second", "race"); });
        start.Set();
        Task.WaitAll(first, second);
        Equal(1, new[] { first.Result, second.Result }.Count(result => result), "one concurrent credential writer wins");
        Equal(true, File.ReadAllText(path) is "first" or "second", "concurrent winner remains intact");
        Equal(0, Directory.GetFiles(directory, "*.tmp").Length, "credential temporary files cleaned");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestCodexEconomyRouter()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-economy-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var home = Path.Combine(directory, "profile");
        Directory.CreateDirectory(home);
        var profile = CodexEconomyRouter.ResolveProfile(home);
        var router = new CodexEconomyRouter();
        var initial = router.Inspect(profile);
        Equal(CodexEconomyMode.Unconfigured, initial.Mode, "new Codex profile is unconfigured");
        Equal(false, initial.SkillInstalled, "new Codex profile has no installed Skill");

        const string originalText = "model = \"gpt-test\"\r\n\r\n[agents]\r\nmax_concurrent_threads_per_session = 4\r\n";
        var originalBody = Encoding.UTF8.GetBytes(originalText);
        var originalBytes = Encoding.UTF8.GetPreamble().Concat(originalBody).ToArray();
        File.WriteAllBytes(profile.ConfigPath, originalBytes);

        var ask = router.SetMode(profile, CodexEconomyMode.Ask);
        Equal(CodexEconomyMode.Ask, ask.Mode, "ask mode read-back");
        Equal(true, ask.SkillInstalled, "ask installs the embedded Skill");
        Equal(true, File.Exists(profile.SkillPath), "installed Skill entrypoint exists");
        Equal(
            true,
            File.Exists(Path.Combine(profile.SkillDirectory, ".zgstokenbar-skill.json")),
            "installed Skill has an ownership manifest");
        var askBytes = File.ReadAllBytes(profile.ConfigPath);
        Equal(true, askBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "config UTF-8 BOM is preserved");
        var askText = Encoding.UTF8.GetString(askBytes[Encoding.UTF8.Preamble.Length..]);
        Equal(true, askText.Contains("\r\n", StringComparison.Ordinal), "config CRLF style is preserved");
        Equal(
            true,
            askText.Contains("max_concurrent_threads_per_session = 4", StringComparison.Ordinal),
            "unmanaged agent settings are preserved");
        Equal(
            false,
            askText.Contains("default_subagent_model", StringComparison.Ordinal),
            "ask does not install child defaults");

        var on = router.SetMode(profile, CodexEconomyMode.On);
        Equal(CodexEconomyMode.On, on.Mode, "on mode read-back");
        var onBytes = File.ReadAllBytes(profile.ConfigPath);
        var onText = Encoding.UTF8.GetString(onBytes[Encoding.UTF8.Preamble.Length..]);
        Equal(
            true,
            onText.Contains($"default_subagent_model = \"{CodexEconomyRouter.EconomyModel}\"", StringComparison.Ordinal),
            "on installs Luna default");
        Equal(
            true,
            onText.Contains($"default_subagent_reasoning_effort = \"{CodexEconomyRouter.EconomyEffort}\"", StringComparison.Ordinal),
            "on installs Max effort default");

        var off = router.SetMode(profile, CodexEconomyMode.Off);
        Equal(CodexEconomyMode.Off, off.Mode, "off mode read-back");
        var offBytes = File.ReadAllBytes(profile.ConfigPath);
        var offText = Encoding.UTF8.GetString(offBytes[Encoding.UTF8.Preamble.Length..]);
        Equal(false, offText.Contains("default_subagent_model", StringComparison.Ordinal), "off removes managed defaults");
        Equal(true, offText.Contains("enabled = false", StringComparison.Ordinal), "off disables the Skill");
        router.SetMode(profile, CodexEconomyMode.Off);
        Equal(
            true,
            offBytes.AsSpan().SequenceEqual(File.ReadAllBytes(profile.ConfigPath)),
            "reapplying off is byte-idempotent");

        File.WriteAllText(Path.Combine(home, "team.config.toml"), "model = \"team\"\n");
        Equal(true, router.Inspect(profile).HasNamedConfigLayers, "named config layer warning is detected narrowly");

        var conflictHome = Path.Combine(directory, "unmanaged-default");
        Directory.CreateDirectory(conflictHome);
        var conflictProfile = CodexEconomyRouter.ResolveProfile(conflictHome);
        const string conflictText = "[agents]\ndefault_subagent_model = \"other\"\n";
        File.WriteAllText(conflictProfile.ConfigPath, conflictText);
        var conflictRejected = false;
        try
        {
            router.SetMode(conflictProfile, CodexEconomyMode.On);
        }
        catch (CodexEconomyException)
        {
            conflictRejected = true;
        }
        Equal(true, conflictRejected, "unmanaged child default blocks on mode");
        Equal(conflictText, File.ReadAllText(conflictProfile.ConfigPath), "unmanaged conflict preserves config bytes");
        Equal(false, Directory.Exists(conflictProfile.SkillDirectory), "config preflight conflict does not install the Skill");

        var spacedHeaderHome = Path.Combine(directory, "spaced-agents-header");
        Directory.CreateDirectory(spacedHeaderHome);
        var spacedHeaderProfile = CodexEconomyRouter.ResolveProfile(spacedHeaderHome);
        const string spacedHeaderText = "[ agents ] # formatting belongs to the user\nmax_concurrent_threads_per_session = 6\n";
        File.WriteAllText(spacedHeaderProfile.ConfigPath, spacedHeaderText);
        Equal(
            CodexEconomyMode.On,
            router.SetMode(spacedHeaderProfile, CodexEconomyMode.On).Mode,
            "on mode extends a semantically equivalent spaced agents table");
        var spacedHeaderUpdated = File.ReadAllText(spacedHeaderProfile.ConfigPath);
        Equal(true, spacedHeaderUpdated.StartsWith("[ agents ] # formatting belongs to the user\n", StringComparison.Ordinal), "spaced agents header is preserved");
        Equal(false, spacedHeaderUpdated.Contains("\n[agents]\n", StringComparison.Ordinal), "spaced agents header is not duplicated");

        var quotedConflictHome = Path.Combine(directory, "quoted-agent-key");
        Directory.CreateDirectory(quotedConflictHome);
        var quotedConflictProfile = CodexEconomyRouter.ResolveProfile(quotedConflictHome);
        const string quotedConflictText = "[\"agents\"]\n\"default_subagent_model\" = \"other\"\n";
        File.WriteAllText(quotedConflictProfile.ConfigPath, quotedConflictText);
        var quotedConflictRejected = false;
        try
        {
            router.SetMode(quotedConflictProfile, CodexEconomyMode.On);
        }
        catch (CodexEconomyException)
        {
            quotedConflictRejected = true;
        }
        Equal(true, quotedConflictRejected, "quoted TOML keys cannot bypass unmanaged default conflict detection");
        Equal(quotedConflictText, File.ReadAllText(quotedConflictProfile.ConfigPath), "quoted-key conflict fails before writing config");

        var inlineAgentsHome = Path.Combine(directory, "inline-agents");
        Directory.CreateDirectory(inlineAgentsHome);
        var inlineAgentsProfile = CodexEconomyRouter.ResolveProfile(inlineAgentsHome);
        const string inlineAgentsText = "agents = { enabled = false }\n";
        File.WriteAllText(inlineAgentsProfile.ConfigPath, inlineAgentsText);
        var inlineAgentsRejected = false;
        try
        {
            router.SetMode(inlineAgentsProfile, CodexEconomyMode.Ask);
        }
        catch (CodexEconomyException)
        {
            inlineAgentsRejected = true;
        }
        Equal(true, inlineAgentsRejected, "inline agents tables fail closed instead of bypassing enabled=false");
        Equal(inlineAgentsText, File.ReadAllText(inlineAgentsProfile.ConfigPath), "inline agents conflict preserves config bytes");

        var inlineSkillsHome = Path.Combine(directory, "inline-skills");
        Directory.CreateDirectory(inlineSkillsHome);
        var inlineSkillsProfile = CodexEconomyRouter.ResolveProfile(inlineSkillsHome);
        const string inlineSkillsText = "[skills]\nconfig = [{ name = \"sol-luna-delegation\", enabled = false }]\n";
        File.WriteAllText(inlineSkillsProfile.ConfigPath, inlineSkillsText);
        var inlineSkillsRejected = false;
        try
        {
            router.SetMode(inlineSkillsProfile, CodexEconomyMode.Off);
        }
        catch (CodexEconomyException)
        {
            inlineSkillsRejected = true;
        }
        Equal(true, inlineSkillsRejected, "inline skills config arrays fail closed before appending an array table");
        Equal(inlineSkillsText, File.ReadAllText(inlineSkillsProfile.ConfigPath), "inline skills conflict preserves config bytes");

        var multilineHome = Path.Combine(directory, "multiline-values");
        Directory.CreateDirectory(multilineHome);
        var multilineProfile = CodexEconomyRouter.ResolveProfile(multilineHome);
        var multilineText = "[mcp_servers.fixture.env]\n"
            + "PROMPT = \"\"\"\n"
            + "[agents]\n"
            + $"{CodexEconomyRouter.AgentBegin}\n"
            + $"{CodexEconomyRouter.AgentEnd}\n"
            + "\"\"\"\n"
            + "LITERAL = '''\n"
            + $"{CodexEconomyRouter.SkillBegin}\n"
            + "[[skills.config]]\n"
            + $"{CodexEconomyRouter.SkillEnd}\n"
            + "'''\n";
        File.WriteAllText(multilineProfile.ConfigPath, multilineText);
        Equal(
            CodexEconomyMode.On,
            router.SetMode(multilineProfile, CodexEconomyMode.On).Mode,
            "headers and markers inside multiline TOML strings are ignored lexically");
        Equal(
            true,
            File.ReadAllText(multilineProfile.ConfigPath).StartsWith(multilineText, StringComparison.Ordinal),
            "multiline TOML string contents remain byte-for-byte intact before managed blocks");

        var unterminatedHome = Path.Combine(directory, "unterminated-multiline");
        Directory.CreateDirectory(unterminatedHome);
        var unterminatedProfile = CodexEconomyRouter.ResolveProfile(unterminatedHome);
        const string unterminatedText = "PROMPT = \"\"\"\n[agents]\n";
        File.WriteAllText(unterminatedProfile.ConfigPath, unterminatedText);
        Equal(CodexEconomyMode.Inconsistent, router.Inspect(unterminatedProfile).Mode, "unterminated multiline TOML is inconsistent");
        var unterminatedRejected = false;
        try
        {
            router.SetMode(unterminatedProfile, CodexEconomyMode.Off);
        }
        catch (CodexEconomyException)
        {
            unterminatedRejected = true;
        }
        Equal(true, unterminatedRejected, "unterminated multiline TOML fails closed");
        Equal(unterminatedText, File.ReadAllText(unterminatedProfile.ConfigPath), "unterminated multiline TOML remains untouched");

        var spoofedMarkerHome = Path.Combine(directory, "spoofed-marker");
        Directory.CreateDirectory(spoofedMarkerHome);
        var spoofedMarkerProfile = CodexEconomyRouter.ResolveProfile(spoofedMarkerHome);
        var encodedSpoofedSkillPath = JsonSerializer.Serialize(spoofedMarkerProfile.SkillPath);
        var spoofedMarkerText = $"""
            [agents]
            default_subagent_model = "{CodexEconomyRouter.EconomyModel}"
            default_subagent_reasoning_effort = "{CodexEconomyRouter.EconomyEffort}"

            {CodexEconomyRouter.AgentBegin}
            {CodexEconomyRouter.AgentEnd}
            {CodexEconomyRouter.SkillBegin}
            {CodexEconomyRouter.SkillEnd}

            [[skills.config]]
            path = {encodedSpoofedSkillPath}
            enabled = true
            """;
        File.WriteAllText(spoofedMarkerProfile.ConfigPath, spoofedMarkerText);
        Equal(
            CodexEconomyMode.Inconsistent,
            router.Inspect(spoofedMarkerProfile).Mode,
            "empty markers cannot claim ownership of values outside their blocks");

        var trailingHome = Path.Combine(directory, "trailing-whitespace");
        Directory.CreateDirectory(trailingHome);
        var trailingProfile = CodexEconomyRouter.ResolveProfile(trailingHome);
        const string trailingText = "model = \"gpt-test\"  \n\n  \n";
        File.WriteAllText(trailingProfile.ConfigPath, trailingText);
        router.SetMode(trailingProfile, CodexEconomyMode.Ask);
        Equal(
            true,
            File.ReadAllText(trailingProfile.ConfigPath).StartsWith(trailingText, StringComparison.Ordinal),
            "unmanaged trailing whitespace is preserved before appended managed blocks");

        var partialHome = Path.Combine(directory, "partial-marker");
        Directory.CreateDirectory(partialHome);
        var partialProfile = CodexEconomyRouter.ResolveProfile(partialHome);
        var partialText = $"{CodexEconomyRouter.SkillBegin}\n[[skills.config]]\n";
        File.WriteAllText(partialProfile.ConfigPath, partialText);
        Equal(CodexEconomyMode.Inconsistent, router.Inspect(partialProfile).Mode, "partial marker is inconsistent");
        var partialRejected = false;
        try
        {
            router.SetMode(partialProfile, CodexEconomyMode.Off);
        }
        catch (CodexEconomyException)
        {
            partialRejected = true;
        }
        Equal(true, partialRejected, "partial marker fails closed");
        Equal(partialText, File.ReadAllText(partialProfile.ConfigPath), "partial marker preserves original config");

        var unmanagedSkillHome = Path.Combine(directory, "unmanaged-skill");
        var unmanagedSkillProfile = CodexEconomyRouter.ResolveProfile(unmanagedSkillHome);
        Directory.CreateDirectory(unmanagedSkillProfile.SkillDirectory);
        File.WriteAllText(unmanagedSkillProfile.SkillPath, "unmanaged");
        var installRejected = false;
        try
        {
            router.Install(unmanagedSkillProfile);
        }
        catch (CodexEconomyException)
        {
            installRejected = true;
        }
        Equal(true, installRejected, "different unmanaged Skill cannot be adopted");
        Equal("unmanaged", File.ReadAllText(unmanagedSkillProfile.SkillPath), "unmanaged Skill remains untouched");

        var invalidManifestHome = Path.Combine(directory, "invalid-ownership-manifest");
        var invalidManifestProfile = CodexEconomyRouter.ResolveProfile(invalidManifestHome);
        Directory.CreateDirectory(invalidManifestProfile.SkillDirectory);
        File.WriteAllText(
            Path.Combine(invalidManifestProfile.SkillDirectory, ".zgstokenbar-skill.json"),
            """{"schemaVersion":"one","skill":42,"files":[]}""");
        var invalidManifestRejected = false;
        try
        {
            router.Install(invalidManifestProfile);
        }
        catch (CodexEconomyException)
        {
            invalidManifestRejected = true;
        }
        Equal(true, invalidManifestRejected, "invalid ownership manifest types fail as a controlled economy conflict");

        var racePath = Path.Combine(directory, "race.toml");
        var raceExpected = Encoding.UTF8.GetBytes("race\n");
        File.WriteAllBytes(racePath, raceExpected);
        using var start = new ManualResetEventSlim(false);
        var first = Task.Run(() =>
        {
            start.Wait();
            try
            {
                CodexEconomyRouter.AtomicWriteForTesting(racePath, Encoding.UTF8.GetBytes("first\n"), raceExpected);
                return true;
            }
            catch (CodexEconomyException)
            {
                return false;
            }
        });
        var second = Task.Run(() =>
        {
            start.Wait();
            try
            {
                CodexEconomyRouter.AtomicWriteForTesting(racePath, Encoding.UTF8.GetBytes("second\n"), raceExpected);
                return true;
            }
            catch (CodexEconomyException)
            {
                return false;
            }
        });
        start.Set();
        Task.WaitAll(first, second);
        Equal(1, new[] { first.Result, second.Result }.Count(value => value), "one concurrent config writer wins");
        Equal(true, File.ReadAllText(racePath) is "first\n" or "second\n", "concurrent winner remains intact");
        Equal(0, Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories).Length, "router temporary files cleaned");

        var discoveryUser = Path.Combine(directory, "discovery-user");
        var environmentHome = Path.Combine(directory, "environment-home");
        var cockpitHome = Path.Combine(directory, "cockpit-home");
        Directory.CreateDirectory(environmentHome);
        Directory.CreateDirectory(cockpitHome);
        var manifestPath = Path.Combine(directory, "codex_instances.json");
        File.WriteAllText(
            manifestPath,
            $$"""{"instances":[42,{"userDataDir":123},{"id":7,"name":false,"userDataDir":{{JsonSerializer.Serialize(cockpitHome)}}}]}""");
        var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", environmentHome);
            var profiles = CodexEconomyRouter.DiscoverProfiles(discoveryUser, manifestPath);
            Equal(3, profiles.Count, "profile discovery uses environment, default, and cockpit manifest only");
            Equal(environmentHome, profiles[0].HomeDirectory, "environment profile is recommended first");
            Equal(true, profiles.Any(item => item.HomeDirectory == cockpitHome), "cockpit manifest profile is included");
            Equal(true, profiles.Single(item => item.HomeDirectory == cockpitHome).DisplayName.StartsWith("Codex Desktop", StringComparison.Ordinal), "malformed optional cockpit labels fall back safely");

            File.WriteAllText(manifestPath, "[]");
            var profilesWithWrongRoot = CodexEconomyRouter.DiscoverProfiles(discoveryUser, manifestPath);
            Equal(2, profilesWithWrongRoot.Count, "non-object cockpit manifest root is ignored best-effort");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
        }
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestSettingsCorruptionRecovery()
{
    var directory = Path.Combine(Path.GetTempPath(), $"wmt-native-settings-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new AppSettingsStore(directory);
        const string corrupt = "{not-json";
        File.WriteAllText(store.SettingsPath, corrupt);
        var settings = store.Load();
        Equal(2, settings.EnabledProviders.Length, "corrupt settings fall back to defaults");
        Equal(corrupt, File.ReadAllText(store.SettingsPath + ".corrupt.bak"), "corrupt settings recovery copy");
        store.Save(settings);
        using var document = JsonDocument.Parse(File.ReadAllText(store.SettingsPath));
        Equal(JsonValueKind.Object, document.RootElement.ValueKind, "saved settings replace corrupt source with valid JSON");
        Equal(0, Directory.GetFiles(directory, "*.tmp").Length, "settings temporary files cleaned");
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static void TestAdaptiveSizing()
{
    Equal(383, BarLayoutMath.ContentWidth(1, 1), "single card width");
    Equal(590, BarLayoutMath.ContentWidth(2, 2), "two-card width");
    Equal(828, BarLayoutMath.ContentWidth(3, 2, true), "overflow width");
    Equal(840, BarLayoutMath.ContentWidth(4, 2), "maximum width");
    Equal(44, TaskbarMiniLayoutMath.Height, "Mini shell leaves a centered taskbar edge clearance");
    Equal(
        125,
        TaskbarMiniLayoutMath.AreaWidth(TaskbarMiniLayoutMath.SystemUsageContentWidth, false)
            + TaskbarMiniLayoutMath.ControlGap
            + TaskbarMiniLayoutMath.ControlsWidth,
        "Mini system area and controls keep a compact right lane");
    Equal(450, TaskbarMiniLayoutMath.ContentWidth(2), "two-card Mini includes provider and system handles");
    Equal(104, TaskbarMiniLayoutMath.ServiceCardWidth, "service card uses compact width");
    Equal(
        410,
        TaskbarMiniLayoutMath.ContentWidth([
            TaskbarMiniLayoutMath.AreaWidth(TaskbarMiniLayoutMath.CardWidth, false),
            TaskbarMiniLayoutMath.AreaWidth(TaskbarMiniLayoutMath.ServiceCardWidth, false),
        ]),
        "mixed quota and service areas include independent handles");
    Equal(
        451,
        TaskbarMiniLayoutMath.ModuleContentWidth([
            TaskbarMiniLayoutMath.AreaWidth(TaskbarMiniLayoutMath.CardWidth, false),
            TaskbarMiniLayoutMath.AreaWidth(TaskbarMiniLayoutMath.CardWidth, false),
            TaskbarMiniLayoutMath.AreaWidth(TaskbarMiniLayoutMath.SystemUsageContentWidth, false),
        ]),
        "all Mini modules use one readable visual gap");
}

static void TestSystemUsageSamplingMath()
{
    var cpu = SystemUsageMath.CpuPercent(
        new CpuUsageTimes(100, 200, 100),
        new CpuUsageTimes(140, 280, 140));
    Equal(true, Math.Abs(66.6667 - cpu!.Value) < .001, "CPU usage subtracts idle time from total system time");
    Equal(true, Math.Abs(75 - SystemUsageMath.Percent(48, 64)!.Value) < .001, "memory usage is normalized to physical total");

    var gpu = SystemUsageMath.AggregateGpu(
    [
        new GpuCounterSample("pid_100_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 35),
        new GpuCounterSample("pid_200_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D#1", 25),
        new GpuCounterSample("pid_300_luid_0x00000000_0x00000001_phys_0_eng_1_engtype_Copy", 78),
        new GpuCounterSample("pid_400_luid_0x00000000_0x00000001_phys_0_eng_2_engtype_Compute", 0),
        new GpuCounterSample("malformed", 100),
    ]);
    Equal<double?>(78, gpu.Percent, "GPU uses the busiest physical engine instead of summing unlike engines");
    Equal("Copy", gpu.Engine, "GPU detail names the busiest engine");
    Equal(3, gpu.ProcessCount, "GPU detail counts active processes without counting idle instances");
    Equal<double>(
        78,
        gpu.ProcessEngines![new ProcessGpuEngineKey(300, new GpuEngineKey(0, 1, 0, 1))],
        "GPU process usage retains its physical engine identity");

    var processes = SystemUsageMath.AggregateProcesses(
    [
        new ProcessCounterSample(1, "build", TimeSpan.FromSeconds(2).Ticks, 100),
        new ProcessCounterSample(2, "browser", 0, 60),
        new ProcessCounterSample(3, "browser", 0, 40),
        new ProcessCounterSample(4, "editor", 0, 400),
    ],
    new Dictionary<int, ProcessCpuBaseline>
    {
        [1] = new("build", 0),
        [2] = new("browser", 0),
        [3] = new("browser", 0),
        [4] = new("editor", 0),
    },
    TimeSpan.FromSeconds(1),
    4,
    1_000,
    new Dictionary<ProcessGpuEngineKey, double>
    {
        [new(2, new GpuEngineKey(0, 1, 0, 0))] = 60,
        [new(3, new GpuEngineKey(0, 1, 0, 0))] = 15,
        [new(3, new GpuEngineKey(0, 1, 0, 1))] = 50,
    });
    Equal("browser", processes[0].Name, "process groups sort by their strongest whole-machine pressure");
    Equal(2, processes[0].ProcessCount, "same-name process instances are grouped");
    Equal<double?>(75, processes[0].GpuPercent, "same-name GPU totals sum only matching physical engines");
    Equal("build", processes[1].Name, "machine-normalized CPU pressure participates in sorting");
    Equal("editor", processes[2].Name, "working-set share participates in sorting");
    Equal<double?>(null, SystemUsageMath.AggregateProcesses(
        [new ProcessCounterSample(5, "new", 1, 1)],
        new Dictionary<int, ProcessCpuBaseline>(),
        TimeSpan.FromSeconds(1),
        4,
        1_000,
        null)[0].CpuPercent,
        "the first process sample has no invented CPU value");

    using var sampler = new SystemUsageSampler();
    var snapshot = sampler.Sample();
    Equal(true, snapshot.MemoryTotalBytes > 0, "Windows physical memory sampling is available");
    Equal(
        true,
        snapshot.DiskActivePercent is null || snapshot.DiskActivePercent is >= 0 and <= 100,
        "disk active time is normalized when the counter is available");
    Equal(
        true,
        snapshot.DiskReadBytesPerSecond is null || snapshot.DiskReadBytesPerSecond >= 0,
        "disk read rate is non-negative when the counter is available");
    Equal(
        true,
        snapshot.DiskWriteBytesPerSecond is null || snapshot.DiskWriteBytesPerSecond >= 0,
        "disk write rate is non-negative when the counter is available");
    Equal(Environment.ProcessorCount, snapshot.LogicalProcessorCount, "logical processor detail comes from the runtime");
    Equal(0, snapshot.TopProcesses.Count, "hidden system usage does not enumerate process details");
    var detailedSnapshot = sampler.Sample(includeProcesses: true);
    Equal(true, detailedSnapshot.TopProcesses.Count is > 0 and <= 5, "interactive sampling returns at most five process groups");
    Equal(true, detailedSnapshot.TopProcesses.All(process => process.ProcessCount > 0), "native process groups retain instance counts");
    Equal(
        true,
        detailedSnapshot.TopProcesses.Any(process => process.PrivateWorkingSetBytes > 0),
        "native process snapshot exposes private working-set memory");
}

static void TestNativeWindowLifecycle()
{
    Exception? failure = null;
    using var finished = new ManualResetEventSlim();
    var thread = new Thread(() =>
    {
        try
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            var bounds = primary?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            var workingArea = primary?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
            var deviceName = primary?.DeviceName ?? @"\\.\DISPLAY1";
            var topology = DisplayTopology.CreateSnapshot(
                "console",
                [new(deviceName, "path:lifecycle-monitor", true, bounds, workingArea)]);
            var settings = new AppSettings
            {
                TaskbarDocked = false,
                EnableAnimations = false,
                PlacementProfiles = new Dictionary<string, WindowPlacementProfile>(StringComparer.Ordinal)
                {
                    [topology.Key] = new()
                    {
                        IsDocked = false,
                        DockedMonitorKey = topology.Primary.MonitorKey,
                        TaskbarPositions = new(StringComparer.Ordinal)
                        {
                            [topology.Primary.MonitorKey] = .5,
                        },
                        FloatingMonitorKey = topology.Primary.MonitorKey,
                        FloatingX = .5,
                        FloatingY = .25,
                    },
                },
            };
            var snapshot = new QuotaSnapshot([], [], DateTimeOffset.UtcNow);
            var captureSync = new object();
            var captureThreads = new HashSet<int>();
            var activeCaptures = 0;
            var maximumActiveCaptures = 0;
            var captureCount = 0;
            DisplayTopologySnapshot Capture()
            {
                lock (captureSync)
                {
                    activeCaptures++;
                    maximumActiveCaptures = Math.Max(maximumActiveCaptures, activeCaptures);
                    captureThreads.Add(Environment.CurrentManagedThreadId);
                    captureCount++;
                }
                Thread.Sleep(20);
                lock (captureSync) activeCaptures--;
                return topology;
            }

            using (var form = new BarForm(settings, snapshot, topologyCapture: Capture))
            {
                form.Opacity = 0;
                var handle = form.Handle;
                Equal(true, handle != 0 && NativeWindowMethods.IsWindow(handle), "real production HWND is created");
                for (var index = 0; index < 4; index++)
                {
                    _ = NativeWindowMethods.SendMessage(handle, 0x007E, 0, 0);
                }
                Equal(
                    true,
                    PumpWindowMessagesUntil(
                        () => form.ActiveTopologyForAcceptance is not null
                            && !form.TopologyCaptureRunningForAcceptance,
                        TimeSpan.FromSeconds(5)),
                    "WndProc display refresh settles a background topology");
                lock (captureSync)
                {
                    Equal(true, captureCount >= 2, "topology stability requires two matching captures");
                    Equal(1, maximumActiveCaptures, "topology capture remains single-flight");
                    Equal(false, captureThreads.Contains(uiThread), "topology probes never execute on the UI thread");
                }
                Equal(true, form.DeviceDpi > 0, "real HWND resolves a positive DPI");
                Equal(true, form.Region is not null, "DPI-aware window region is applied");
                form.Show();
                System.Windows.Forms.Application.DoEvents();
                Equal(true, form.Visible, "production form show path succeeds");
                form.Hide();
                System.Windows.Forms.Application.DoEvents();
                Equal(false, form.Visible, "production form hide path succeeds");
            }

            using var delayedCaptureEntered = new ManualResetEventSlim();
            using var releaseDelayedCapture = new ManualResetEventSlim();
            DisplayTopologySnapshot DelayedCapture()
            {
                delayedCaptureEntered.Set();
                releaseDelayedCapture.Wait(TimeSpan.FromSeconds(5));
                return topology;
            }
            var delayedForm = new BarForm(settings, snapshot, topologyCapture: DelayedCapture);
            var delayedHandle = delayedForm.Handle;
            Equal(true, delayedCaptureEntered.Wait(TimeSpan.FromSeconds(5)), "delayed topology capture starts");
            delayedForm.Dispose();
            releaseDelayedCapture.Set();
            Equal(
                true,
                PumpWindowMessagesUntil(
                    () => !delayedForm.TopologyCaptureRunningForAcceptance,
                    TimeSpan.FromSeconds(5)),
                "disposed form drains its delayed capture without a UI commit");
            Equal<DisplayTopologySnapshot?>(
                null,
                delayedForm.ActiveTopologyForAcceptance,
                "disposed form discards delayed topology results");
            Equal(false, NativeWindowMethods.IsWindow(delayedHandle), "dispose destroys the real HWND");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            finished.Set();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    if (!finished.Wait(TimeSpan.FromSeconds(20)) || !thread.Join(TimeSpan.FromSeconds(2)))
    {
        throw new TimeoutException("Native HWND lifecycle acceptance did not finish.");
    }
    if (failure is not null) throw new InvalidOperationException("Native HWND lifecycle acceptance failed.", failure);
}

static bool PumpWindowMessagesUntil(Func<bool> condition, TimeSpan timeout)
{
    var started = Stopwatch.GetTimestamp();
    while (!condition())
    {
        if (Stopwatch.GetElapsedTime(started) >= timeout) return false;
        System.Windows.Forms.Application.DoEvents();
        Thread.Sleep(10);
    }
    System.Windows.Forms.Application.DoEvents();
    return true;
}

static void TestTaskbarPopoverMotionGeometry()
{
    var anchor = new Point(100, 200);
    Equal(new Point(100, 197), TaskbarPopoverMath.OffsetFromAnchor(anchor, PopoverTailSide.Top, 3), "top entrance offset");
    Equal(new Point(103, 200), TaskbarPopoverMath.OffsetFromAnchor(anchor, PopoverTailSide.Right, 3), "right entrance offset");
    Equal(new Point(97, 200), TaskbarPopoverMath.OffsetFromAnchor(anchor, PopoverTailSide.Left, 3), "left entrance offset");
    Equal(new Point(100, 203), TaskbarPopoverMath.OffsetFromAnchor(anchor, PopoverTailSide.Bottom, 3), "bottom entrance offset");
    Equal(new Point(13, 23), TaskbarPopoverMath.Interpolate(new(10, 20), new(20, 30), .3), "motion interpolation keeps existing rounding");
    Equal(0d, TaskbarPopoverMath.EntranceEase(0), "entrance easing starts at zero");
    Equal(1d, TaskbarPopoverMath.EntranceEase(1), "entrance easing ends at one");
    Equal(0d, TaskbarPopoverMath.ExitEase(0), "exit easing starts at zero");
    Equal(1d, TaskbarPopoverMath.ExitEase(1), "exit easing ends at one");

    var topBody = TaskbarPopoverMath.BodyBounds(PopoverTailSide.Top, 300, 120, 8);
    Equal(new RectangleF(0, 8, 300, 120), topBody, "top tail reserves vertical body space");
    var leftBody = TaskbarPopoverMath.BodyBounds(PopoverTailSide.Left, 300, 120, 8);
    Equal(new RectangleF(8, 0, 300, 120), leftBody, "left tail reserves horizontal body space");
    var bottomTail = TaskbarPopoverMath.TailPoints(PopoverTailSide.Bottom, new(0, 0, 300, 120), 90, 8);
    Equal(new PointF(90, 127.5f), bottomTail[1], "bottom tail tip remains pixel-aligned");
    var rightTail = TaskbarPopoverMath.TailPoints(PopoverTailSide.Right, new(0, 0, 300, 120), 60, 8);
    Equal(new PointF(307.5f, 60), rightTail[1], "right tail tip remains pixel-aligned");
}

static void TestSystemUsageBackgroundSerialization()
{
    using var gate = new SemaphoreSlim(1, 1);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var callerThread = Environment.CurrentManagedThreadId;
    var sampleThread = callerThread;
    var active = 0;
    var maximumActive = 0;
    var includeProcessesObserved = false;
    SystemUsageSnapshot Sample(bool includeProcesses)
    {
        sampleThread = Environment.CurrentManagedThreadId;
        includeProcessesObserved = includeProcesses;
        var current = Interlocked.Increment(ref active);
        maximumActive = Math.Max(maximumActive, current);
        if (current == 1)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        }
        Interlocked.Decrement(ref active);
        return new(
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            1,
            DateTimeOffset.Parse("2026-08-23T08:00:00Z"));
    }

    var first = SystemUsageSampling.TrySampleAsync(
        gate,
        Sample,
        includeProcesses: true,
        CancellationToken.None);
    Equal(true, entered.Wait(TimeSpan.FromSeconds(5)), "background system sampling starts");
    try
    {
        var overlapping = SystemUsageSampling.TrySampleAsync(
                gate,
                Sample,
                includeProcesses: false,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Equal<SystemUsageSnapshot?>(null, overlapping, "an overlapping system sample is skipped");
    }
    finally
    {
        release.Set();
    }

    Equal(true, first.GetAwaiter().GetResult() is not null, "background system sample returns its snapshot");
    Equal(false, callerThread == sampleThread, "system sampling does not execute on the caller UI thread");
    Equal(true, includeProcessesObserved, "detail sampling intent reaches the sampler");
    Equal(1, maximumActive, "overview and detail sampling share one serialization gate");
}

static void TestSystemUsagePopoverPresentation()
{
    var snapshot = new SystemUsageSnapshot(
        78,
        42UL * 1_073_741_824,
        22UL * 1_073_741_824,
        64UL * 1_073_741_824,
        41,
        "3D",
        6,
        32,
        DateTimeOffset.Parse("2026-08-03T14:02:03+08:00"))
    {
        DiskActivePercent = 68,
        DiskReadBytesPerSecond = 125 * 1_048_576d,
        DiskWriteBytesPerSecond = 12 * 1_048_576d,
        TopProcesses =
        [
            new SystemProcessUsage("Unity", 3, 31, 5UL * 1_073_741_824, 38),
            new SystemProcessUsage("chrome", 12, 18, 3_382_312_960, 24),
            new SystemProcessUsage("Code", 7, 12, 1_932_735_283, 3),
            new SystemProcessUsage("MsMpEng", 1, 8, 734_003_200, 0),
            new SystemProcessUsage("SearchHost", 2, null, 398_458_880, 0),
        ],
    };
    var chinese = NativeText.For("zh-CN");
    Equal("32 个逻辑处理器", chinese.SystemUsageCpuDetail(32), "Chinese CPU detail is explicit");
    Equal(
        "42.0 / 64.0 GB · 可用 22.0 GB",
        chinese.SystemUsageMemoryDetail(
            snapshot.MemoryUsedBytes,
            snapshot.MemoryTotalBytes,
            snapshot.MemoryAvailableBytes),
        "Chinese memory detail includes used, total, and available values");
    Equal(
        "最忙引擎 3D · 6 个活动进程",
        chinese.SystemUsageGpuDetail(snapshot.GpuPercent, snapshot.GpuEngine, snapshot.GpuProcessCount),
        "Chinese GPU detail includes the busiest engine and process count");
    Equal(
        "活动 68% · 读 125 MB/s · 写 12.0 MB/s",
        chinese.SystemUsageDiskDetail(
            snapshot.DiskActivePercent,
            snapshot.DiskReadBytesPerSecond,
            snapshot.DiskWriteBytesPerSecond),
        "Chinese disk detail includes active time and read/write rates");
    Equal("同名进程合计", chinese.SystemUsageTopProcesses, "Chinese process table states the grouped-total scope");
    Equal("Grouped totals", NativeText.For("en").SystemUsageTopProcesses, "English process table states the grouped-total scope");
    Equal("chrome ×12", chinese.SystemUsageProcessName("chrome", 12), "grouped process count is explicit");

    using var popover = new SystemUsagePopoverForm();
    using var bitmap = popover.RenderForTest(
        new SystemUsagePopoverContent(snapshot, false),
        chinese,
        QuotaBackgroundPalette.Resolve(AppSettings.DefaultBackgroundPalette),
        96);
    Equal(
        new Size(SystemUsagePopoverForm.LogicalBodyWidth, SystemUsagePopoverForm.LogicalBodyHeight + 8),
        bitmap.Size,
        "system usage popover renders at the deterministic logical size");
    Equal(
        true,
        Enumerable.Range(0, bitmap.Width)
            .Any(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).A > 0)),
        "system usage popover produces visible pixels");
}

static void TestSystemUsageSamplingAllocation()
{
    using var sampler = new SystemUsageSampler();
    for (var index = 0; index < 5; index++) _ = sampler.Sample();

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    const int overviewSamples = 30;
    var overviewLatencies = new double[overviewSamples];
    var overviewAllocationStart = GC.GetAllocatedBytesForCurrentThread();
    for (var index = 0; index < overviewSamples; index++)
    {
        var started = Stopwatch.GetTimestamp();
        _ = sampler.Sample();
        overviewLatencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    var overviewAllocated = GC.GetAllocatedBytesForCurrentThread() - overviewAllocationStart;
    var overviewAverageAllocation = overviewAllocated / (double)overviewSamples;
    Array.Sort(overviewLatencies);
    var overviewP95 = overviewLatencies[(int)Math.Ceiling(overviewSamples * .95) - 1];
    Equal(
        true,
        overviewAverageAllocation < 64 * 1024,
        $"overview sampling stays below 64 KB/call after warmup; actual {overviewAverageAllocation:0} bytes");

    var coldDetailAllocationStart = GC.GetAllocatedBytesForCurrentThread();
    var coldDetailStarted = Stopwatch.GetTimestamp();
    _ = sampler.Sample(includeProcesses: true);
    var coldDetailLatency = Stopwatch.GetElapsedTime(coldDetailStarted).TotalMilliseconds;
    var coldDetailAllocated = GC.GetAllocatedBytesForCurrentThread() - coldDetailAllocationStart;

    const int detailSamples = 10;
    var detailLatencies = new double[detailSamples];
    var detailAllocationStart = GC.GetAllocatedBytesForCurrentThread();
    for (var index = 0; index < detailSamples; index++)
    {
        var started = Stopwatch.GetTimestamp();
        _ = sampler.Sample(includeProcesses: true);
        detailLatencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    var detailAllocated = GC.GetAllocatedBytesForCurrentThread() - detailAllocationStart;
    Array.Sort(detailLatencies);
    Console.WriteLine(
        $"METRIC system usage overview={overviewAverageAllocation / 1024:0.00}KB/call p95={overviewP95:0.000}ms; "
        + $"detail-cold={coldDetailAllocated / 1024d:0.00}KB/{coldDetailLatency:0.000}ms; "
        + $"detail-warm={detailAllocated / (double)detailSamples / 1024:0.00}KB/call "
        + $"p95={detailLatencies[^1]:0.000}ms");
}

static void TestTaskbarMonitorSelection()
{
    var primary = new TaskbarPlacement.PlacementTrack(
        @"\\.\DISPLAY1",
        new Rectangle(0, 0, 1920, 1080),
        true,
        true,
        6,
        1800,
        1000)
    {
        TaskbarBounds = new Rectangle(0, 1032, 1920, 48),
    };
    var secondary = new TaskbarPlacement.PlacementTrack(
        @"\\.\DISPLAY2",
        new Rectangle(1920, 0, 1920, 1080),
        false,
        true,
        1926,
        3720,
        1000)
    {
        TaskbarBounds = new Rectangle(1920, 1032, 1920, 48),
    };
    var left = new TaskbarPlacement.PlacementTrack(
        @"\\.\DISPLAY3",
        new Rectangle(-1920, 0, 1920, 1080),
        false,
        true,
        -1914,
        -456,
        1000)
    {
        TaskbarBounds = new Rectangle(-1920, 1032, 1920, 48),
    };

    Equal(
        @"\\.\DISPLAY2",
        TaskbarPlacement.SelectTrack([primary, secondary], null, new Point(2200, 500)).MonitorName,
        "drag center selects the secondary display");
    Equal(
        @"\\.\DISPLAY2",
        TaskbarPlacement.SelectTrack([primary, secondary], @"\\.\DISPLAY2", new Point(100, 500)).MonitorName,
        "preferred display wins while dragging across monitors");
    Equal(
        @"\\.\DISPLAY1",
        TaskbarPlacement.SelectTrack([primary, secondary], null, null).MonitorName,
        "missing monitor preference falls back to the primary display");
    Equal(
        @"\\.\DISPLAY2",
        TaskbarPlacement.SelectDockTrack([primary, secondary], new Point(2200, 1010), null)?.MonitorName,
        "pointer near the secondary taskbar docks to the secondary display");
    Equal(
        @"\\.\DISPLAY2",
        TaskbarPlacement.SelectDockTrack([primary, secondary], new Point(2200, 1004), null)?.MonitorName,
        "expanded docking edge still catches a near-secondary-taskbar pointer");
    Equal(
        null,
        TaskbarPlacement.SelectDockTrack([primary, secondary], new Point(2200, 980), null),
        "pointer beyond the docking threshold stays floating");
    Equal(
        @"\\.\DISPLAY3",
        TaskbarPlacement.SelectDockTrack([primary, secondary, left], new Point(-1000, 1010), null)?.MonitorName,
        "pointer near a negative-coordinate left taskbar docks to the left display");
    Equal(
        null,
        TaskbarPlacement.SelectDockTrack([primary, secondary, left], new Point(-1000, 980), null),
        "negative-coordinate pointer beyond the left docking threshold stays floating");
    Equal(
        false,
        TaskbarPlacement.CanReuseCachedTracks(
            [primary],
            new Size(400, 40),
            new Size(400, 40),
            @"\\.\DISPLAY2"),
        "cached fallback is refreshed when the persisted monitor is missing");
    Equal(
        true,
        TaskbarPlacement.CanReuseCachedTracks(
            [primary, secondary],
            new Size(400, 40),
            new Size(400, 40),
            @"\\.\DISPLAY2"),
        "complete cached tracks retain the persisted monitor");
    Equal(
        false,
        TaskbarPlacement.CanReuseCachedTracks(
            [primary, secondary],
            new Size(400, 40),
            new Size(600, 60),
            @"\\.\DISPLAY2"),
        "DPI-sized Mini invalidates cached taskbar tracks");
    Equal(
        true,
        BarForm.CanCommitTaskbarDrag("topology-a", "topology-a", false, false, false, false),
        "stable matching topology permits drag commit");
    Equal(
        true,
        BarForm.CanCommitTaskbarDrag("topology-a", "topology-a", false, true, false, false),
        "a non-topology shell refresh does not discard a cross-monitor drag");
    Equal(
        false,
        BarForm.CanCommitTaskbarDrag("topology-a", "topology-a", false, true, true, false),
        "a pending display-topology change blocks a stale drag commit");
    Equal(
        false,
        BarForm.CanCommitTaskbarDrag("topology-a", "topology-a", false, true, false, true),
        "an unconfirmed changed topology candidate blocks drag commit");
    Equal(
        false,
        BarForm.CanCommitTaskbarDrag("topology-a", "topology-b", false, false, false, false),
        "a changed active topology blocks drag commit");
}

static void TestTaskbarMiniCollapseAnchor()
{
    var expanded = new TaskbarPlacement.PlacementTrack(
        @"\\.\DISPLAY1",
        new Rectangle(0, 0, 1920, 1080),
        true,
        true,
        6,
        1000,
        1000);
    var collapsed = expanded with { Maximum = 1220 };
    var anchor = new Point(500, 1000);
    var oldRelativePosition = expanded.RelativePosition(anchor);
    Equal(
        false,
        collapsed.LocationAt(oldRelativePosition).Equals(anchor),
        "reusing the old relative position moves a resized Mini");
    var constrained = collapsed.Constrain(anchor);
    var anchoredRelativePosition = collapsed.RelativePosition(constrained);
    Equal(anchor, collapsed.LocationAt(anchoredRelativePosition), "recommitting the anchor keeps the Mini still");

    var vertical = new TaskbarPlacement.PlacementTrack(
        @"\\.\DISPLAY1",
        new Rectangle(0, 0, 1920, 1080),
        true,
        false,
        6,
        900,
        0);
    var verticalAnchor = new Point(0, 420);
    Equal(verticalAnchor, vertical.Constrain(verticalAnchor), "vertical taskbar keeps the leading anchor");
}

static void TestTaskbarVisibleQuotaWindows()
{
    var reset = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
    QuotaWindow[] windows =
    [
        new("5h", null, null, TimeSpan.FromHours(5)),
        new("1w", 44, reset, TimeSpan.FromDays(7)),
        new("30d", 12, reset, TimeSpan.FromDays(30)),
        new("Fable", 38, reset, TimeSpan.FromDays(7)),
    ];

    var visible = TaskbarMiniLayoutMath.VisibleWindows(windows);
    Equal(3, visible.Count, "Mini keeps a scoped weekly quota alongside core windows");
    Equal("1w", visible[0].Label, "Mini omits the unavailable five-hour placeholder");
    Equal("30d", visible[1].Label, "Mini preserves later available windows");
    Equal("Fable", visible[2].Label, "Mini preserves a third available window");

    var resetOnly = TaskbarMiniLayoutMath.VisibleWindows(
        [new QuotaWindow("1w", null, reset, TimeSpan.FromDays(7))]);
    Equal(1, resetOnly.Count, "Mini keeps partial windows with a reset time");

    var unavailable = TaskbarMiniLayoutMath.VisibleWindows(
        [
            new QuotaWindow("5h", null, null, TimeSpan.FromHours(5)),
            new QuotaWindow("1w", null, null, TimeSpan.FromDays(7)),
        ]);
    Equal(2, unavailable.Count, "Mini keeps both labels when a provider is fully unavailable");
    Equal("5h", unavailable[0].Label, "Mini keeps the unavailable five-hour label");
    Equal("1w", unavailable[1].Label, "Mini keeps the unavailable weekly label");
}

static void TestTaskbarStackedCodexAccounts()
{
    var reset = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
    var claude = new QuotaCard(
        "claude",
        ProviderKind.Claude,
        "Claude",
        null,
        "#f97316",
        true,
        []);
    var firstCodex = new QuotaCard(
        "codex.1",
        ProviderKind.Codex,
        "Codex · 1",
        "pro",
        "#10a37f",
        true,
        [new QuotaWindow("7d", 7, reset, TimeSpan.FromDays(7))]);
    var secondCodex = new QuotaCard(
        "codex.2",
        ProviderKind.Codex,
        "Codex · 2",
        "pro",
        "#10a37f",
        false,
        [
            new QuotaWindow("5h", 0, reset, TimeSpan.FromHours(5)),
            new QuotaWindow("7d", 100, reset, TimeSpan.FromDays(7)),
        ]);

    var groups = TaskbarMiniGrouping.Create([claude, firstCodex, secondCodex]);
    Equal(2, groups.Count, "two Codex accounts share one Mini card slot");
    Equal(false, groups[0].IsStackedCodex, "Claude keeps its own Mini card slot");
    Equal(true, groups[1].IsStackedCodex, "Codex accounts render as stacked rows");
    Equal(2, groups[1].Cards.Count, "both Codex accounts remain individually addressable");
    Equal(450, TaskbarMiniLayoutMath.ContentWidth(groups.Count), "stacking keeps independent provider and system handles");

    var proLow = firstCodex with
    {
        Key = "codex.pro-low",
        Label = "Codex · 3",
        Windows = [new QuotaWindow("7d", 80, reset, TimeSpan.FromDays(7))],
    };
    var proHigh = firstCodex with
    {
        Key = "codex.pro-high",
        Label = "Codex · 2",
        Windows = [new QuotaWindow("7d", 10, reset, TimeSpan.FromDays(7))],
    };
    var plusHighest = firstCodex with
    {
        Key = "codex.plus-highest",
        Label = "Codex · 1",
        Badge = "plus",
        Windows = [new QuotaWindow("7d", 1, reset, TimeSpan.FromDays(7))],
    };
    var orderedCodex = TaskbarMiniGrouping.Create([plusHighest, proLow, proHigh]).Single().Cards;
    Equal(
        "codex.pro-high,codex.pro-low,codex.plus-highest",
        string.Join(',', orderedCodex.Select(card => card.Key)),
        "Pro accounts precede Plus and each plan sorts by displayed remaining quota");
    Equal<double?>(90, TaskbarMiniGrouping.DisplayedRemainingPercent(proHigh), "sorting uses the compact displayed remaining quota");

    var firstRow = TaskbarMiniGrouping.CodexRowWindows(firstCodex);
    Equal(1, firstRow.Count, "missing Codex five-hour window stays hidden");
    Equal("7d", firstRow[0].Label, "weekly Codex window fills the account row");
    Equal<double?>(7, firstRow[0].UsedPercent, "weekly Codex value remains tied to its account");

    var secondRow = TaskbarMiniGrouping.CodexRowWindows(secondCodex);
    Equal(2, secondRow.Count, "available Codex five-hour window opens automatically");
    Equal("5h", secondRow[0].Label, "five-hour Codex window renders first");
    Equal("7d", secondRow[1].Label, "weekly Codex window renders second");

    var emptyServiceRow = TaskbarMiniGrouping.CodexRowWindows(new QuotaCard(
        "codex.service",
        ProviderKind.Codex,
        "Codex API",
        null,
        "#10a37f",
        false,
        [])
    {
        IsService = true,
    });
    Equal(1, emptyServiceRow.Count, "an empty Codex service keeps a drawable fallback row");
    Equal<double?>(null, emptyServiceRow[0].UsedPercent, "the service fallback reports unavailable quota");

    using var perAreaForm = new BarForm(
        new AppSettings { EnableRadar = false, EnableCodexEconomyBar = false },
        new QuotaSnapshot([claude, firstCodex, secondCodex], [], reset),
        renderOnly: true,
        renderDpi: 96);
    Equal(451, perAreaForm.ClientSize.Width, "provider and system areas reserve independent handles");
    var systemArea = perAreaForm.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.SystemMetrics);
    Equal(false, systemArea.Collapsed, "system area starts expanded");
    Equal(88, systemArea.Width, "system area reports the shared default width");
    Equal(
        true,
        perAreaForm.MoveMiniAreaFromCommand(MiniAreaIds.Codex, MiniAreaIds.Claude),
        "provider module order accepts a move before its sibling");
    Equal(
        MiniAreaIds.Codex,
        perAreaForm.GetMiniAreaStates()[0].AreaId,
        "provider module order is reflected by the Mini state directory");
    Equal(
        true,
        perAreaForm.MoveMiniAreaFromCommand(MiniAreaIds.SystemMetrics, MiniAreaIds.Codex),
        "system metrics joins the shared module ordering");
    Equal(
        MiniAreaIds.SystemMetrics,
        perAreaForm.GetMiniAreaStates()[0].AreaId,
        "system metrics can move before provider modules");
    Equal(true, perAreaForm.SetMiniAreaFromCommand(MiniAreaIds.Claude, true, null), "Claude area collapse is accepted");
    Equal(341, perAreaForm.ClientSize.Width, "collapsing Claude changes only its area width");
    Equal(false, perAreaForm.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Codex).Collapsed, "Codex remains expanded");
    Equal(true, perAreaForm.SetMiniAreaFromCommand(MiniAreaIds.Codex, null, 200), "Codex area width is accepted");
    Equal(397, perAreaForm.ClientSize.Width, "Codex width changes independently");
    Equal(200, perAreaForm.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Codex).Width, "Codex width is reported through the area contract");
    Equal(true, perAreaForm.SetMiniAreaFromCommand(MiniAreaIds.SystemMetrics, true, null), "system area collapse is accepted");
    Equal(343, perAreaForm.ClientSize.Width, "collapsing system usage leaves provider layouts unchanged");
    Equal(true, perAreaForm.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Claude).Collapsed, "system collapse preserves Claude state");
    Equal(200, perAreaForm.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Codex).Width, "system collapse preserves Codex width");
    Equal(true, perAreaForm.SetMiniAreaFromCommand(MiniAreaIds.SystemMetrics, false, 120), "system area width is accepted");
    Equal(429, perAreaForm.ClientSize.Width, "system usage width changes independently");
    Equal(120, perAreaForm.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.SystemMetrics).Width, "system width is reported through the area contract");
    Equal(false, perAreaForm.SetMiniAreaFromCommand("missing.area", true, null), "unknown area fails closed");

    using var noSystemForm = new BarForm(
        new AppSettings
        {
            EnableRadar = false,
            EnableCodexEconomyBar = false,
            PluginEnabled = new(StringComparer.Ordinal) { ["zgstokenbar.metrics.system"] = false },
        },
        new QuotaSnapshot([claude, firstCodex, secondCodex], [], reset),
        renderOnly: true,
        renderDpi: 96);
    Equal(
        false,
        noSystemForm.GetMiniAreaStates().Any(area => area.AreaId == MiniAreaIds.SystemMetrics),
        "disabled system metrics do not create a Mini area");
    Equal(false, noSystemForm.WantsSystemUsageDetails, "disabled system metrics never request detailed sampling");

    using var controlsOnlyForm = new BarForm(
        new AppSettings
        {
            EnabledProviders = [],
            EnableRadar = false,
            EnableCodexEconomyBar = false,
            PluginEnabled = new(StringComparer.Ordinal) { ["zgstokenbar.metrics.system"] = false },
        },
        new QuotaSnapshot([], [], reset),
        renderOnly: true,
        renderDpi: 96,
        activeProviders: new HashSet<ProviderKind>());
    Equal(0, controlsOnlyForm.GetMiniAreaStates().Count, "all business modules can be disabled together");
    Equal(true, controlsOnlyForm.ClientSize.Width > 0, "refresh and settings controls remain reachable with every module off");

    using var collapsedForm = new BarForm(
        new AppSettings
        {
            EnableRadar = false,
            EnableCodexEconomyBar = false,
            MiniAreaLayouts = new(StringComparer.Ordinal)
            {
                [MiniAreaIds.Claude] = new(true),
                [MiniAreaIds.Codex] = new(true),
                [MiniAreaIds.SystemMetrics] = new(true),
            },
        },
        new QuotaSnapshot([claude, firstCodex, secondCodex], [], reset),
        renderOnly: true,
        renderDpi: 96);
    Equal(177, collapsedForm.ClientSize.Width, "all collapsed areas keep independent handles and compact icons");
    Equal(true, collapsedForm.AreAllMiniAreasCollapsed, "all-area state includes system metrics");

    using var economyForm = new BarForm(
        new AppSettings { EnableRadar = false },
        new QuotaSnapshot([claude, firstCodex, secondCodex], [], reset),
        renderOnly: true,
        renderDpi: 96);
    var economyArea = economyForm.GetMiniAreaStates()
        .Single(area => area.AreaId == MiniAreaIds.CodexEconomy);
    Equal(
        TaskbarMiniLayoutMath.CodexEconomyContentWidth,
        economyArea.Width,
        "economy quick control is a first-class Mini area by default");
    Equal(44, economyArea.MinimumWidth, "economy button keeps a compact fixed minimum width");
    Equal(44, economyArea.MaximumWidth, "economy button is not stretched into a card-sized module");
    Equal(
        false,
        economyForm.SetMiniAreaFromCommand(MiniAreaIds.CodexEconomy, null, 180),
        "economy button rejects width changes so its full content remains one click target");
    var economyProfile = new CodexEconomyProfile(
        "Default Codex",
        Path.Combine(Path.GetTempPath(), "wmt-economy-bar"),
        true,
        "test");
    economyForm.SetCodexEconomyStatus(new CodexEconomyStatus(
        CodexEconomyMode.Ask,
        economyProfile,
        true,
        false,
        null));
    economyForm.CreateControl();
    using (var bitmap = new Bitmap(economyForm.ClientSize.Width, economyForm.ClientSize.Height))
    {
        economyForm.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    }
    var economyHits = economyForm.GetCodexEconomyHitBoundsForAcceptance();
    Equal(44f, economyHits.Button.Width, "economy button draw and hit bounds keep the compact logical width");
    Equal(true, economyHits.Resize.IsEmpty, "economy button has no resize strip stealing clicks");
    foreach (var point in new[]
             {
                 new PointF(economyHits.Button.Left + .5f, economyHits.Button.Top + .5f),
                 new PointF(economyHits.Button.Right - .5f, economyHits.Button.Top + .5f),
                 new PointF(economyHits.Button.Left + .5f, economyHits.Button.Bottom - .5f),
                 new PointF(economyHits.Button.Right - .5f, economyHits.Button.Bottom - .5f),
             })
    {
        Equal(true, economyForm.IsCodexEconomyButtonPointForAcceptance(point), "every inset corner of the economy button opens the menu");
    }
    Equal(
        false,
        economyForm.IsCodexEconomyButtonPointForAcceptance(new PointF(
            economyHits.Collapse.Left + economyHits.Collapse.Width / 2,
            economyHits.Collapse.Top + economyHits.Collapse.Height / 2)),
        "collapse handle stays outside the economy menu target");
    Equal(
        false,
        economyForm.IsCodexEconomyButtonPointForAcceptance(new PointF(
            economyHits.Reorder.Left + economyHits.Reorder.Width / 2,
            economyHits.Reorder.Top + economyHits.Reorder.Height / 2)),
        "reorder handle stays outside the economy menu target");
    CodexEconomyMode? requestedMode = null;
    var requestedModeCount = 0;
    economyForm.CodexEconomyModeRequested += (_, request) =>
    {
        requestedMode = request.Mode;
        requestedModeCount++;
    };
    using (var menu = economyForm.CreateCodexEconomyMenuForAcceptance())
    {
        Equal(false, menu.ShowImageMargin, "economy menu has no native image gutter");
        Equal(false, menu.ShowCheckMargin, "economy menu uses the shared dark row treatment instead of the native check gutter");
        Equal(typeof(CodexEconomyMenuRenderer), menu.Renderer.GetType(), "economy menu uses the project-styled economy renderer");
        Equal(Color.FromArgb(7, 12, 24), menu.BackColor, "economy menu follows the active Bar popover palette");
        Equal(232, menu.Width, "economy menu has room for localized two-line choices at 96 DPI");
        Equal(
            "经济模式",
            menu.Items.OfType<CodexEconomyMenuHeaderItem>().Single().Text,
            "economy menu carries a localized project-style header");
        var modeItems = menu.Items
            .OfType<CodexEconomyModeMenuItem>()
            .Where(item => item.Tag is string tag && tag.StartsWith("bar.economy.", StringComparison.Ordinal))
            .ToArray();
        Equal(3, modeItems.Length, "economy Bar menu exposes three explicit choices");
        Equal(true, modeItems.Single(item => Equals(item.Tag, "bar.economy.ask")).Checked, "economy Bar menu marks Ask as current");
        Equal(
            true,
            modeItems.Select(item => item.Text).SequenceEqual(["关闭", "询问", "开启"]),
            "economy Bar menu localizes all Chinese mode names");
        Equal(
            "使用前先询问",
            modeItems.Single(item => Equals(item.Tag, "bar.economy.ask")).Description,
            "economy Bar menu gives each choice a concise localized explanation");
        Equal(
            true,
            modeItems.All(item => item.Text is { } text
                && !text.Contains('⌄')
                && !text.Contains('▾')),
            "economy menu rows do not repeat a dropdown affordance");
        modeItems.Single(item => Equals(item.Tag, "bar.economy.on")).PerformClick();
        Equal<CodexEconomyMode?>(
            null,
            requestedMode,
            "economy Bar menu returns from the click before configuration work is dispatched");
        System.Windows.Forms.Application.DoEvents();
        Equal(
            CodexEconomyMode.On,
            requestedMode,
            "economy Bar menu emits the explicitly selected mode on the next UI message");
        System.Windows.Forms.Application.DoEvents();
        Equal(1, requestedModeCount, "economy Bar menu dispatches the selected mode exactly once");
    }
    economyForm.ApplySettings(new AppSettings { EnableRadar = false, EnableCodexEconomyBar = false });
    Equal(
        false,
        economyForm.GetMiniAreaStates().Any(area => area.AreaId == MiniAreaIds.CodexEconomy),
        "economy quick control can be hidden independently from its Codex mode");
}

static void TestTaskbarCodexAreaIsolation()
{
    var reset = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
    var codexAccounts = new[]
    {
        new QuotaCard(
            "codex.1",
            ProviderKind.Codex,
            "Codex · 1",
            "pro",
            "#10a37f",
            true,
            [new QuotaWindow("7d", 7, reset, TimeSpan.FromDays(7))]),
        new QuotaCard(
            "codex.2",
            ProviderKind.Codex,
            "Codex · 2",
            "pro",
            "#10a37f",
            false,
            [new QuotaWindow("7d", 17, reset, TimeSpan.FromDays(7))]),
        new QuotaCard(
            "codex.3",
            ProviderKind.Codex,
            "Codex · 3",
            "pro",
            "#10a37f",
            false,
            [new QuotaWindow("7d", 27, reset, TimeSpan.FromDays(7))]),
    };

    var groups = TaskbarMiniGrouping.Create(codexAccounts);
    var codexGroups = groups.Where(group => group.Cards[0].Provider == ProviderKind.Codex).ToArray();
    Equal(1, codexGroups.Length, "three Codex accounts share one Mini module");
    Equal(
        MiniAreaIds.Codex,
        codexGroups[0].AreaId,
        "the combined Codex Mini keeps the stable area id");
    Equal(3, codexGroups[0].Cards.Count, "all Codex accounts remain individually addressable");

    using var form = new BarForm(
        new AppSettings { EnableRadar = false, EnableCodexEconomyBar = false },
        new QuotaSnapshot(codexAccounts, [], reset),
        renderOnly: true,
        renderDpi: 96);
    var codexAreas = form.GetMiniAreaStates()
        .Where(area => area.AreaId.StartsWith(MiniAreaIds.Codex, StringComparison.Ordinal))
        .ToArray();
    Equal(1, codexAreas.Length, "one Codex module is visible in the Mini state directory");
    Equal(false, codexAreas[0].Collapsed, "the Codex module starts expanded");
    Equal(
        true,
        form.SetMiniAreaFromCommand(MiniAreaIds.Codex, true, null),
        "the combined Codex area accepts a collapse command");
    Equal(
        true,
        form.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Codex).Collapsed,
        "the combined Codex module is collapsed");
    Equal(
        false,
        form.SetMiniAreaFromCommand($"{MiniAreaIds.Codex}.2", true, null),
        "the removed second Codex area fails closed");

    using var singleForm = new BarForm(
        new AppSettings { EnableRadar = false },
        new QuotaSnapshot([codexAccounts[0]], [], reset),
        renderOnly: true,
        renderDpi: 96);
    var singleCodexAreas = singleForm.GetMiniAreaStates()
        .Where(area => area.AreaId.StartsWith(MiniAreaIds.Codex, StringComparison.Ordinal))
        .ToArray();
    Equal(1, singleCodexAreas.Length, "a single Codex account keeps one Mini module");
    Equal(MiniAreaIds.Codex, singleCodexAreas[0].AreaId, "a single Codex module keeps the legacy area id");
    Equal(true, singleForm.SetMiniAreaFromCommand(MiniAreaIds.Codex, true, null), "the legacy Codex area remains command-addressable");
    Equal(false, singleForm.SetMiniAreaFromCommand($"{MiniAreaIds.Codex}.2", true, null), "a missing second Codex area fails closed");
}

static void TestTaskbarCodexPoolGrouping()
{
    var now = DateTimeOffset.Parse("2026-08-24T08:00:00Z");
    var cards = FourCodexPoolSnapshot(now).Cards;

    var accountGroups = TaskbarMiniGrouping.Create(cards, CodexMiniDisplayModes.Accounts);
    Equal(1, accountGroups.Count, "four Codex accounts share one Mini module in accounts mode");
    Equal(
        4,
        accountGroups[0].Cards.Count,
        "accounts mode keeps every account individually addressable");
    Equal(true, accountGroups[0].IsStackedCodex, "accounts mode uses the compact stacked renderer");
    Equal(false, accountGroups[0].IsCodexPool, "accounts mode does not aggregate account quota");

    var poolGroups = TaskbarMiniGrouping.Create(cards, CodexMiniDisplayModes.Pool);
    Equal(1, poolGroups.Count, "four ordinary Codex accounts merge into one pool module");
    Equal(MiniAreaIds.Codex, poolGroups[0].AreaId, "the pool keeps the primary Codex area id");
    Equal(true, poolGroups[0].IsCodexPool, "the merged group is identified as a Codex pool");
    Equal(false, poolGroups[0].IsStackedCodex, "the pool does not reuse the two-account renderer");
    Equal(4, poolGroups[0].Cards.Count, "all four ordinary Codex accounts remain in the pool group");

    var apiService = new QuotaCard(
        "codex.api-service",
        ProviderKind.Codex,
        "API · 1",
        "API key",
        "#10a37f",
        false,
        [new QuotaWindow("API", null, null, TimeSpan.Zero)])
    {
        IsService = true,
        ServiceCount = 1,
        ServiceDisplayName = "sub2api",
    };
    var groupsWithService = TaskbarMiniGrouping.Create(
        cards.Append(apiService).ToArray(),
        CodexMiniDisplayModes.Pool);
    Equal(2, groupsWithService.Count, "API service remains separate from the ordinary Codex pool");
    var serviceGroup = groupsWithService.Single(group => group.Cards.Count == 1 && group.Cards[0].IsService);
    Equal(false, serviceGroup.IsCodexPool, "API service is never marked as a Codex pool");
    Equal(TaskbarMiniGrouping.CodexServiceAreaId, serviceGroup.AreaId, "API service keeps an independent pool-mode area id");
}

static void TestCodexPoolAccountProjection()
{
    var now = DateTimeOffset.Parse("2026-08-24T08:00:00Z");
    var accounts = Enumerable.Range(1, 4)
        .Select(index => new CodexAccountInfo(
            $"pool-account-{index}",
            $"pool-{index}@example.test",
            "pro",
            index <= 2))
        .Append(new CodexAccountInfo("plus-account", "plus@example.test", "plus", false))
        .Append(new CodexAccountInfo("api-service", null, "API_KEY", true, 2))
        .ToArray();
    var fullSnapshot = FourCodexPoolSnapshot(now);
    var first = fullSnapshot.Cards[0] with
    {
        AccountHint = CodexAccountFormatting.MaskEmail(accounts[0].Email),
    };
    var second = fullSnapshot.Cards[1] with
    {
        AccountHint = CodexAccountFormatting.MaskEmail(accounts[1].Email),
    };
    var service = new QuotaCard(
        "codex.api-service",
        ProviderKind.Codex,
        "API · 2",
        "API key",
        "#64748b",
        true,
        [new QuotaWindow("API", null, null, TimeSpan.Zero)])
    {
        IsService = true,
        ServiceCount = 2,
    };
    var sourceCards = new QuotaCard[]
    {
        new(
            "claude",
            ProviderKind.Claude,
            "Claude",
            "Max",
            "#d97757",
            true,
            [new QuotaWindow("7d", 20, now.AddDays(3), TimeSpan.FromDays(7))]),
        first,
        second,
        service,
    };

    var projected = CodexPoolCardProjection.Create(
        sourceCards,
        accounts,
        fullSnapshot.CodexAccounts);
    var projectedAccounts = projected
        .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
        .ToArray();
    Equal(4, projectedAccounts.Length, "the configured four-account Pro cohort defines the pool segment count");
    Equal(true, projectedAccounts.All(card => card.Badge == "pro"), "Plus and API-key entries do not enter the Pro pool");
    Equal(first.Key, projectedAccounts[0].Key, "the first live card identity is preserved through its account quota key");
    Equal(second.Key, projectedAccounts[1].Key, "the second live card identity is preserved through its account quota key");
    Equal(fullSnapshot.CodexAccounts[2].CardKey, projectedAccounts[2].Key, "an inactive account preserves its quota history identity without a visible source card");
    Equal(true, projected.Single(card => card.IsService).Key == service.Key, "the API service remains a separate card");
    Equal(4, fullSnapshot.Cards.Count, "pool projection does not mutate the source snapshot");
    Equal("240/400", CodexPoolPresentation.CapacitySummary(CodexPoolPresentation.Create(projectedAccounts, now)[0]), "all four account quotas produce an exact percentage-point pool total");

    var plusFirst = new QuotaCard(
        "codex.plus",
        ProviderKind.Codex,
        "Codex Plus",
        "plus",
        "#10a37f",
        true,
        [new QuotaWindow("7d", 25, now.AddDays(2), TimeSpan.FromDays(7))]);
    var plusFirstProjection = CodexPoolCardProjection.Create(
        [sourceCards[0], plusFirst, first, second, service],
        accounts,
        fullSnapshot.CodexAccounts);
    var plusFirstAccounts = plusFirstProjection
        .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
        .ToArray();
    Equal(4, plusFirstAccounts.Length, "a leading live Plus card cannot replace the larger four-account Pro cohort");
    Equal(true, plusFirstAccounts.All(card => card.Badge == "pro"), "the largest comparable plan cohort owns the pool");
    Equal(false, plusFirstAccounts.Any(card => card.Key == plusFirst.Key), "a Plus card does not leak into the Pro aggregate");

    var collidingHint = CodexAccountFormatting.MaskEmail("anna@example.test");
    var collidingPro = first with { AccountHint = collidingHint };
    var collidingPlus = plusFirst with { AccountHint = collidingHint };
    var collidingAccounts = accounts
        .Select((account, index) => index == 0
            ? account with { Email = "anna@example.test" }
            : account)
        .ToArray();
    var collidingQuotas = fullSnapshot.CodexAccounts
        .Select((quota, index) => quota with
        {
            CardKey = null,
            Windows = index == 0 ? [] : quota.Windows,
        })
        .ToArray();
    var collisionProjection = CodexPoolCardProjection.Create(
        [sourceCards[0], collidingPlus, collidingPro, second, service],
        collidingAccounts,
        collidingQuotas)
        .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
        .ToArray();
    Equal(collidingPro.Key, collisionProjection[0].Key, "legacy masked-email collisions keep an explicit Pro card in the Pro slot");
    Equal(false, collisionProjection.Any(card => card.Key == collidingPlus.Key), "a colliding masked Plus identity cannot enter the Pro pool");

    var legacySourceCards = sourceCards
        .Select(card => card.Provider == ProviderKind.Codex && !card.IsService
            ? card with { AccountHint = null }
            : card)
        .ToArray();
    var legacyQuotas = fullSnapshot.CodexAccounts
        .Select(quota => quota with { CardKey = null })
        .ToArray();
    var legacyProjected = CodexPoolCardProjection.Create(
        legacySourceCards,
        accounts,
        legacyQuotas)
        .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
        .ToArray();
    Equal(4, legacyProjected.Length, "legacy cards without identity metadata fill existing directory slots instead of adding a fifth segment");
    Equal(true, legacyProjected.Any(card => card.Key == first.Key), "legacy fallback preserves the first real card identity");
    Equal(true, legacyProjected.Any(card => card.Key == second.Key), "legacy fallback preserves the second real card identity");

    var sevenDaySourceCards = sourceCards
        .Select(card => card.Provider == ProviderKind.Codex && !card.IsService
            ? card with
            {
                Windows = card.Windows
                    .Where(window => window.Duration != TimeSpan.FromHours(5))
                    .ToArray(),
            }
            : card)
        .ToArray();
    var partialQuotas = fullSnapshot.CodexAccounts
        .Take(2)
        .Select(quota => quota with
        {
            Windows = quota.Windows
                .Where(window => window.Duration != TimeSpan.FromHours(5))
                .ToArray(),
        })
        .ToArray();
    var partial = CodexPoolCardProjection.Create(
        sevenDaySourceCards,
        accounts,
        partialQuotas);
    var partialAccounts = partial
        .Where(card => card.Provider == ProviderKind.Codex && !card.IsService)
        .ToArray();
    var partialRows = CodexPoolPresentation.Create(partialAccounts, now);
    Equal(4, partialAccounts.Length, "missing quota snapshots retain gray account slots");
    Equal(true, partialAccounts[2].Key.StartsWith("codex.pool.", StringComparison.Ordinal), "an account without source or quota identity receives a stable UI-only key");
    Equal(1, partialRows.Count, "empty account slots do not invent a five-hour horizon");
    Equal("160/200", CodexPoolPresentation.CapacitySummary(partialRows[0]), "partial account coverage totals the two known account capacities");
    Equal<DateTimeOffset?>(now.AddDays(3), partialRows[0].NextResetAt, "partial coverage retains the earliest known reset countdown");

    var withoutDirectory = CodexPoolCardProjection.Create(sourceCards, [], fullSnapshot.CodexAccounts);
    Equal(true, withoutDirectory.SequenceEqual(sourceCards), "native-only Codex falls back to the original cards");
    var withDuplicate = CodexPoolCardProjection.Create(
        sourceCards,
        accounts.Prepend(accounts[0]).ToArray(),
        fullSnapshot.CodexAccounts);
    Equal(
        4,
        withDuplicate.Count(card => card.Provider == ProviderKind.Codex && !card.IsService),
        "duplicate account ids do not duplicate pool segments");

    var snapshot = new QuotaSnapshot(
        [first, second],
        [new ProviderHealth(ProviderKind.Codex, true, "current", ProviderHealthCode.Current)],
        now)
    {
        CodexAccounts = fullSnapshot.CodexAccounts,
    };
    using var form = new BarForm(
        new AppSettings
        {
            EnableAnimations = false,
            EnableRadar = false,
            CodexMiniDisplayMode = CodexMiniDisplayModes.Pool,
        },
        snapshot,
        renderOnly: true,
        renderDpi: 96,
        codexAccounts: accounts.Take(2).ToArray());
    Equal(2, form.CodexPoolAccountCountForAcceptance, "the initial pool follows the supplied account directory");
    var initialSize = form.ClientSize;
    form.SetCodexAccounts(accounts);
    Equal(4, form.CodexPoolAccountCountForAcceptance, "account-directory refresh immediately rebuilds all four pool slots");
    Equal(initialSize, form.ClientSize, "account-directory refresh preserves the fixed pool footprint");
}

static void TestCodexPoolPresentation()
{
    var now = DateTimeOffset.Parse("2026-08-24T08:00:00Z");
    var cards = FourCodexPoolSnapshot(now).Cards;
    var rows = CodexPoolPresentation.Create(cards, now);

    Equal(2, rows.Count, "Codex pool presents both available quota horizons");
    Equal("7d", rows[0].Label, "weekly pool quota occupies the upper row");
    Equal(TimeSpan.FromDays(7), rows[0].Duration, "weekly pool row retains its duration identity");
    Equal("5h", rows[1].Label, "five-hour pool quota occupies the lower row");
    Equal(TimeSpan.FromHours(5), rows[1].Duration, "five-hour pool row retains its duration identity");

    var weekly = rows[0];
    Equal(4, weekly.Segments.Count, "weekly pool has one segment per Pro account");
    Equal(4, weekly.AvailableAccountCount, "all weekly Pro values contribute to coverage");
    Equal(4, weekly.TotalAccountCount, "weekly coverage reports the full account count");
    Equal<double?>(60d, weekly.AggregateRemainingPercent, "weekly pool mean is exact");
    Equal<double?>(2.4d, weekly.RemainingAccountEquivalents, "weekly account equivalents are exact");
    Equal("240/400", CodexPoolPresentation.CapacitySummary(weekly), "weekly pool exposes summed remaining percentage points");
    Equal<DateTimeOffset?>(now.AddDays(1), weekly.NextResetAt, "weekly pool reports the earliest refill");
    Equal(
        true,
        weekly.Segments.Select(segment => segment.RemainingPercent).SequenceEqual(
            new double?[] { 90, 70, 50, 30 }),
        "weekly segments preserve each account's remaining quota");

    var fiveHour = rows[1];
    Equal(4, fiveHour.AvailableAccountCount, "all five-hour Pro values contribute to coverage");
    Equal<double?>(80d, fiveHour.AggregateRemainingPercent, "five-hour pool mean is exact");
    Equal<double?>(3.2d, fiveHour.RemainingAccountEquivalents, "five-hour account equivalents are exact");
    Equal("320/400", CodexPoolPresentation.CapacitySummary(fiveHour), "five-hour pool exposes summed remaining percentage points");
    Equal<DateTimeOffset?>(now.AddHours(1), fiveHour.NextResetAt, "five-hour pool reports the earliest refill");

    var threeKnownCards = cards.ToArray();
    threeKnownCards[3] = threeKnownCards[3] with
    {
        Windows = threeKnownCards[3].Windows
            .Select(window => window.Duration == TimeSpan.FromDays(7)
                ? window with { UsedPercent = null }
                : window)
            .ToArray(),
    };
    var threeKnownWeekly = CodexPoolPresentation.Create(threeKnownCards, now)[0];
    Equal(3, threeKnownWeekly.AvailableAccountCount, "one missing weekly value leaves three known account capacities");
    Equal<double?>(70d, threeKnownWeekly.AggregateRemainingPercent, "three-account weekly mean uses only known capacities");
    Equal("210/300", CodexPoolPresentation.CapacitySummary(threeKnownWeekly), "three-account weekly summary excludes the missing gray slot from capacity");

    var incompleteCards = cards.ToArray();
    incompleteCards[2] = incompleteCards[2] with
    {
        Windows = incompleteCards[2].Windows
            .Select(window => window.Duration == TimeSpan.FromDays(7)
                ? window with { UsedPercent = null }
                : window)
            .ToArray(),
    };
    incompleteCards[3] = incompleteCards[3] with
    {
        Windows = incompleteCards[3].Windows
            .Select(window => window.Duration == TimeSpan.FromDays(7)
                ? window with { ResetsAt = now.AddMinutes(-1) }
                : window)
            .ToArray(),
    };
    var incompleteWeekly = CodexPoolPresentation.Create(incompleteCards, now)[0];
    Equal(2, incompleteWeekly.AvailableAccountCount, "missing and expired weekly values reduce coverage");
    Equal(4, incompleteWeekly.TotalAccountCount, "partial coverage retains the eligible account count");
    Equal<double?>(80d, incompleteWeekly.AggregateRemainingPercent, "partial weekly coverage averages the known account capacities");
    Equal<double?>(1.6d, incompleteWeekly.RemainingAccountEquivalents, "partial weekly coverage totals the known account capacities");
    Equal("160/200", CodexPoolPresentation.CapacitySummary(incompleteWeekly), "partial weekly summary exposes known remaining and known capacity");
    Equal<DateTimeOffset?>(now.AddDays(2), incompleteWeekly.NextResetAt, "partial coverage retains the earliest known future refill");

    var mixedPlanCards = cards.ToArray();
    mixedPlanCards[3] = mixedPlanCards[3] with { Badge = "plus" };
    var mixedPlanWeekly = CodexPoolPresentation.Create(mixedPlanCards, now)[0];
    Equal(4, mixedPlanWeekly.AvailableAccountCount, "mixed plans may still report raw value coverage");
    Equal<double?>(null, mixedPlanWeekly.AggregateRemainingPercent, "mixed plans do not expose an exact aggregate");
    Equal<double?>(null, mixedPlanWeekly.RemainingAccountEquivalents, "mixed plans do not expose account equivalents");
    Equal("— 4/4", CodexPoolPresentation.CapacitySummary(mixedPlanWeekly), "mixed-plan coverage is not mistaken for a full pool");

    var sevenDayOnlyCards = cards
        .Select(card => card with
        {
            Windows = card.Windows
                .Where(window => window.Duration != TimeSpan.FromHours(5))
                .ToArray(),
        })
        .ToArray();
    var sevenDayOnlyRows = CodexPoolPresentation.Create(sevenDayOnlyCards, now);
    Equal(1, sevenDayOnlyRows.Count, "a globally absent five-hour horizon is omitted");
    Equal("7d", sevenDayOnlyRows[0].Label, "the weekly horizon becomes the single pool row");

    var emptyFiveHourCards = cards
        .Select(card => card with
        {
            Windows = card.Windows
                .Select(window => window.Duration == TimeSpan.FromHours(5)
                    ? window with { UsedPercent = null, ResetsAt = null }
                    : window)
                .ToArray(),
        })
        .ToArray();
    var emptyFiveHourRows = CodexPoolPresentation.Create(emptyFiveHourCards, now);
    Equal(1, emptyFiveHourRows.Count, "duration-only five-hour placeholders do not keep an empty pool row visible");
    Equal("7d", emptyFiveHourRows[0].Label, "a valid weekly row remains when empty five-hour placeholders are hidden");

    var partialFiveHourCards = sevenDayOnlyCards.ToArray();
    partialFiveHourCards[0] = cards[0];
    var partialFiveHourRows = CodexPoolPresentation.Create(partialFiveHourCards, now);
    Equal(2, partialFiveHourRows.Count, "one real five-hour window keeps the horizon visible");
    var partialFiveHour = partialFiveHourRows.Single(row => row.Duration == TimeSpan.FromHours(5));
    Equal(1, partialFiveHour.AvailableAccountCount, "partial five-hour coverage reports the one available account");
    Equal<double?>(95d, partialFiveHour.AggregateRemainingPercent, "partial five-hour coverage averages the known account capacity");
    Equal("95/100", CodexPoolPresentation.CapacitySummary(partialFiveHour), "partial five-hour summary exposes the known account capacity");

    var noWindowRows = CodexPoolPresentation.Create(
        cards.Select(card => card with { Windows = [] }).ToArray(),
        now);
    Equal(1, noWindowRows.Count, "a fully unavailable pool retains one stable fallback row");
    Equal("7d", noWindowRows[0].Label, "the unavailable fallback keeps the weekly identity");
}

static void TestTaskbarCodexPoolRendering()
{
    var now = DateTimeOffset.Parse("2026-08-24T08:00:00Z");
    var snapshot = FourCodexPoolSnapshot(now);
    var sevenDayOnlySnapshot = snapshot with
    {
        Cards = snapshot.Cards
            .Select(card => card with
            {
                Windows = card.Windows
                    .Where(window => window.Duration != TimeSpan.FromHours(5))
                    .ToArray(),
            })
            .ToArray(),
    };
    foreach (var dpi in new[] { 96, 144, 192 })
    {
        using var accountsForm = new BarForm(
            new AppSettings
            {
                EnableAnimations = false,
                EnableRadar = false,
                CodexMiniDisplayMode = CodexMiniDisplayModes.Accounts,
            },
            snapshot,
            renderOnly: true,
            renderDpi: dpi);
        using var sevenDayOnlyForm = new BarForm(
            new AppSettings
            {
                EnableAnimations = false,
                EnableRadar = false,
                CodexMiniDisplayMode = CodexMiniDisplayModes.Pool,
            },
            sevenDayOnlySnapshot,
            renderOnly: true,
            renderDpi: dpi);
        using var poolForm = new BarForm(
            new AppSettings
            {
                EnableAnimations = false,
                EnableRadar = false,
                CodexMiniDisplayMode = CodexMiniDisplayModes.Pool,
            },
            snapshot,
            renderOnly: true,
            renderDpi: dpi);

        var expectedHeight = (int)Math.Round(TaskbarMiniLayoutMath.Height * dpi / 96d);
        Equal(expectedHeight, accountsForm.ClientSize.Height, $"accounts mode keeps the 44px logical height at {dpi} DPI");
        Equal(expectedHeight, poolForm.ClientSize.Height, $"pool mode keeps the 44px logical height at {dpi} DPI");
        Equal(expectedHeight, sevenDayOnlyForm.ClientSize.Height, $"single-row pool keeps the 44px logical height at {dpi} DPI");
        Equal(poolForm.ClientSize.Width, sevenDayOnlyForm.ClientSize.Width, $"single-row pool keeps the 184px area width at {dpi} DPI");
        Equal(true, accountsForm.ClientSize.Width < poolForm.ClientSize.Width, $"compact account rows stay narrower than the aggregate pool at {dpi} DPI");
        Equal(
            TaskbarMiniLayoutMath.CodexPoolCardWidth,
            poolForm.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Codex).Width,
            $"pool mode uses the balanced 184px content width at {dpi} DPI");
        Equal(
            1,
            accountsForm.GetMiniAreaStates().Count(area =>
                area.AreaId.StartsWith(MiniAreaIds.Codex, StringComparison.Ordinal)),
            $"accounts mode exposes one Codex area at {dpi} DPI");
        Equal(
            1,
            poolForm.GetMiniAreaStates().Count(area =>
                area.AreaId.StartsWith(MiniAreaIds.Codex, StringComparison.Ordinal)),
            $"pool mode exposes one ordinary Codex area at {dpi} DPI");

        Equal(
            true,
            poolForm.SetMiniAreaFromCommand(MiniAreaIds.Codex, true, null),
            $"pool area accepts collapse at {dpi} DPI");
        Equal(
            true,
            poolForm.GetMiniAreaStates().Single(area => area.AreaId == MiniAreaIds.Codex).Collapsed,
            $"pool area reports its collapsed state at {dpi} DPI");
        Equal(expectedHeight, poolForm.ClientSize.Height, $"collapsed pool retains the logical height at {dpi} DPI");

        poolForm.CreateControl();
        using var bitmap = new Bitmap(
            poolForm.ClientSize.Width,
            poolForm.ClientSize.Height,
            PixelFormat.Format32bppPArgb);
        poolForm.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        Equal(poolForm.ClientSize, bitmap.Size, $"collapsed pool renders at its full client size at {dpi} DPI");
        Equal(
            true,
            bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A > 0,
            $"collapsed pool capture contains rendered pixels at {dpi} DPI");
    }
}

static void TestTaskbarMiniRenderFaultIsolation()
{
    var failCodex = true;
    using var form = new BarForm(
        new AppSettings
        {
            EnableAnimations = false,
            EnableRadar = false,
            EnableCodexEconomyBar = true,
        },
        FourCodexPoolSnapshot(DateTimeOffset.Parse("2026-08-24T08:00:00Z")),
        renderOnly: true,
        renderDpi: 96,
        renderFaultForAcceptance: areaId =>
            failCodex && string.Equals(areaId, MiniAreaIds.Codex, StringComparison.Ordinal)
                ? new InvalidOperationException("synthetic render fault")
                : null);
    form.CreateControl();
    using var bitmap = new Bitmap(
        form.ClientSize.Width,
        form.ClientSize.Height,
        PixelFormat.Format32bppPArgb);

    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    Equal(
        true,
        form.TaskbarRenderFailureAreaIdsForAcceptance.Contains(MiniAreaIds.Codex),
        "a failed module is isolated and recorded without escaping the paint pass");
    Equal(
        false,
        form.GetCodexEconomyHitBoundsForAcceptance().Button.IsEmpty,
        "a later healthy module still renders and remains interactive");

    failCodex = false;
    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    Equal(
        false,
        form.TaskbarRenderFailureAreaIdsForAcceptance.Contains(MiniAreaIds.Codex),
        "the isolated module automatically rejoins after a later successful paint");
}

static void TestTaskbarCompactProviderSummaries()
{
    var reset = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
    var claude = new QuotaCard(
        "claude",
        ProviderKind.Claude,
        "Claude",
        "pro",
        "#d97757",
        true,
        [
            new QuotaWindow("7d", 12, reset, TimeSpan.FromDays(7)),
            new QuotaWindow("5h", 29, reset, TimeSpan.FromHours(5)),
        ]);
    var claudeWindow = BarForm.CollapsedQuotaWindow(claude);
    Equal("5h", claudeWindow?.Label, "collapsed Claude prefers the shortest usable quota window");
    var claudeSummary = BarForm.CollapsedQuotaSummary(claude);
    Equal("71%", claudeSummary.Value, "collapsed Claude displays remaining quota");
    Equal(QuotaColorScale.ForRemaining(71), claudeSummary.Color, "collapsed Claude status follows remaining quota");

    var inactiveCodex = new QuotaCard(
        "codex.inactive",
        ProviderKind.Codex,
        "Codex · 1",
        "pro",
        "#10a37f",
        false,
        [new QuotaWindow("5h", 90, reset, TimeSpan.FromHours(5))]);
    var activeCodex = new QuotaCard(
        "codex.active",
        ProviderKind.Codex,
        "Codex · 2",
        "pro",
        "#10a37f",
        true,
        [new QuotaWindow("5h", 4, reset, TimeSpan.FromHours(5))]);
    var codexGroup = TaskbarMiniGrouping.Create([inactiveCodex, activeCodex]).Single();
    Equal("codex.active", BarForm.PrimaryTaskbarCard(codexGroup).Key, "collapsed Codex keeps the active account primary");
    Equal("96%", BarForm.CollapsedQuotaSummary(BarForm.PrimaryTaskbarCard(codexGroup)).Value, "collapsed Codex displays the active account remaining quota");

    var unavailable = new QuotaCard(
        "claude.unavailable",
        ProviderKind.Claude,
        "Claude",
        null,
        "#d97757",
        true,
        []);
    Equal("—", BarForm.CollapsedQuotaSummary(unavailable).Value, "collapsed quota keeps an unavailable value explicit");
}

static void TestTaskbarCompactAiGatewayBalance()
{
    var now = DateTimeOffset.UtcNow;
    var loadingCard = AiGatewayBalanceService.UnavailableCard(now);
    Equal(
        ProviderKind.AiGateway,
        loadingCard.Provider,
        "AI Gateway loading fallback keeps the DeepSeek provider identity");
    Equal(
        AiGatewayBalanceStatus.Unavailable,
        loadingCard.Balance?.Status,
        "initial AI Gateway card is honestly unavailable until refresh completes");

    var card = new QuotaCard(
        "ai-gateway.balance",
        ProviderKind.AiGateway,
        "AI 网关",
        null,
        "#8b5cf6",
        true,
        [new QuotaWindow("AI", null, null, TimeSpan.Zero)])
    {
        CapturedAt = now,
        IsService = true,
        Balance = new AiGatewayBalance(
            AiGatewayBalanceStatus.Available,
            "CNY",
            42.36m,
            42.36m,
            0m,
            now),
    };
    var groups = TaskbarMiniGrouping.Create([card]);
    Equal(1, groups.Count, "AI Gateway keeps one Mini card slot");
    Equal(TaskbarMiniLayoutMath.ServiceCardWidth, TaskbarMiniLayoutMath.CardWidthFor(card), "AI Gateway uses service width");
    Equal("¥0", AiGatewayBalanceFormatting.CompactAmount(0m), "compact balance keeps a zero balance explicit");
    Equal("¥9.9", AiGatewayBalanceFormatting.CompactAmount(9.94m), "compact balance retains a decimal below ten yuan");
    Equal("¥42", AiGatewayBalanceFormatting.CompactAmount(42.36m), "compact balance rounds to a readable whole yuan");
    Equal("¥1.2K", AiGatewayBalanceFormatting.CompactAmount(1_234m), "compact balance abbreviates thousands");
    Equal("¥1.2M", AiGatewayBalanceFormatting.CompactAmount(1_234_000m), "compact balance abbreviates millions");
    Equal("—", AiGatewayBalanceFormatting.CompactAmount(null), "compact balance keeps an unavailable value explicit");
    Equal(254, TaskbarMiniLayoutMath.ContentWidth([
        TaskbarMiniLayoutMath.AreaWidth(TaskbarMiniLayoutMath.ServiceCardWidth, false),
    ]), "service-only Mini includes its area handle");
    Equal(
        184,
        TaskbarMiniLayoutMath.ContentWidth([
            TaskbarMiniLayoutMath.AreaWidth(TaskbarMiniLayoutMath.ServiceCardWidth, true),
        ]),
        "collapsed service-only Mini saves the card width");

    using var form = new BarForm(
        new AppSettings { EnableRadar = false, EnableCodexEconomyBar = false },
        new QuotaSnapshot(
            [card],
            [new ProviderHealth(ProviderKind.AiGateway, true, "current", ProviderHealthCode.Current)],
            now),
        renderOnly: true,
        renderDpi: 96,
        activeProviders: new HashSet<ProviderKind> { ProviderKind.AiGateway });
    Equal(254, form.ClientSize.Width, "rendered AI Gateway Mini includes provider and system handles");

    using var collapsedForm = new BarForm(
        new AppSettings
        {
            EnableRadar = false,
            EnableCodexEconomyBar = false,
            MiniAreaLayouts = new(StringComparer.Ordinal)
            {
                [MiniAreaIds.AiGateway] = new(true),
            },
        },
        new QuotaSnapshot(
            [card],
            [new ProviderHealth(ProviderKind.AiGateway, true, "current", ProviderHealthCode.Current)],
            now),
        renderOnly: true,
        renderDpi: 96,
        activeProviders: new HashSet<ProviderKind> { ProviderKind.AiGateway });
    Equal(184, collapsedForm.ClientSize.Width, "collapsed AI Gateway Mini keeps its handle while system stays expanded");
}

static void TestTaskbarMiniPopoverPlacement()
{
    var body = new Size(
        QuotaPopoverForm.LogicalBodyWidth * 2,
        QuotaPopoverForm.LogicalBodyHeight * 2);
    var bottom = TaskbarMiniPopoverMath.Place(
        new Rectangle(800, 1040, 200, 40),
        body,
        16,
        6,
        new Rectangle(0, 0, 1920, 1040));
    Equal(PopoverTailSide.Bottom, bottom.TailSide, "bottom taskbar bubble points down");
    Equal(new Point(660, 730), bottom.Location, "bottom taskbar bubble opens above its capsule");
    Equal(new Size(480, 304), bottom.WindowSize, "bottom taskbar bubble includes tail height");

    var top = TaskbarMiniPopoverMath.Place(
        new Rectangle(800, 0, 200, 40),
        body,
        16,
        6,
        new Rectangle(0, 40, 1920, 1040));
    Equal(PopoverTailSide.Top, top.TailSide, "top taskbar bubble points up");
    Equal(new Point(660, 46), top.Location, "top taskbar bubble opens below its capsule");

    var left = TaskbarMiniPopoverMath.Place(
        new Rectangle(0, 400, 80, 200),
        body,
        16,
        6,
        new Rectangle(80, 0, 1840, 1080));
    Equal(PopoverTailSide.Left, left.TailSide, "left taskbar bubble points left");
    Equal(new Point(86, 356), left.Location, "left taskbar bubble opens inside the work area");

    var right = TaskbarMiniPopoverMath.Place(
        new Rectangle(1840, 400, 80, 200),
        body,
        16,
        6,
        new Rectangle(0, 0, 1840, 1080));
    Equal(PopoverTailSide.Right, right.TailSide, "right taskbar bubble points right");
    Equal(new Point(1338, 356), right.Location, "right taskbar bubble opens inside the work area");

    var leftEdge = TaskbarMiniPopoverMath.Place(
        new Rectangle(0, 1040, 100, 40),
        body,
        16,
        6,
        new Rectangle(0, 0, 1920, 1040));
    Equal(0, leftEdge.Location.X, "bottom taskbar bubble clamps to the screen edge");
    Equal(50, leftEdge.TailOffset, "edge-clamped tail still points to its capsule");
}

static void TestQuotaRemainingColorGradient()
{
    Equal(Color.FromArgb(251, 113, 133), QuotaColorScale.ForRemaining(0), "empty quota is red");
    Equal(Color.FromArgb(251, 191, 36), QuotaColorScale.ForRemaining(50), "half quota is amber");
    Equal(Color.FromArgb(52, 211, 153), QuotaColorScale.ForRemaining(100), "full quota is green");
    Equal(QuotaColorScale.ForRemaining(0), QuotaColorScale.ForRemaining(-10), "values below zero clamp");
    Equal(QuotaColorScale.ForRemaining(100), QuotaColorScale.ForRemaining(110), "values above 100 clamp");

    var distinctWholePercentColors = Enumerable.Range(0, 101)
        .Select(remaining => QuotaColorScale.ForRemaining(remaining).ToArgb())
        .Distinct()
        .Count();
    Equal(true, distinctWholePercentColors >= 95, "whole-percent values form a continuous color scale");

    for (var remaining = 0; remaining < 100; remaining++)
    {
        var current = QuotaColorScale.ForRemaining(remaining);
        var next = QuotaColorScale.ForRemaining(remaining + 1);
        var largestChannelStep = Math.Max(
            Math.Abs(next.R - current.R),
            Math.Max(Math.Abs(next.G - current.G), Math.Abs(next.B - current.B)));
        Equal(true, largestChannelStep <= 7, $"no visible color jump at {remaining}%");
    }
}

static void TestNativeBackgroundPaletteContract()
{
    Equal(4, QuotaBackgroundPalette.All.Count, "four curated background presets");
    Equal(
        "midnight,graphite,navy,plum",
        string.Join(',', QuotaBackgroundPalette.All.Select(theme => theme.Id)),
        "background preset ids remain stable");

    var midnight = QuotaBackgroundPalette.Resolve(null);
    Equal("midnight", midnight.Id, "missing palette uses the original background");
    Equal(Color.FromArgb(2, 6, 23), midnight.Outer, "original outer background is exact");
    Equal(Color.FromArgb(6, 11, 22), midnight.ProviderGroup, "provider group background is exact");
    Equal(Color.FromArgb(10, 18, 32), midnight.QuotaGroup, "dominant quota group background is exact");
    Equal(Color.FromArgb(7, 12, 24), midnight.Popover, "original popover background is exact");
    Equal("midnight", QuotaBackgroundPalette.Resolve("unknown").Id, "unknown palette fails safely");
    Equal(Color.FromArgb(7, 28, 44), QuotaBackgroundPalette.Resolve("NAVY").ProviderGroup, "palette ids normalize case");
    Equal(Color.FromArgb(11, 38, 56), QuotaBackgroundPalette.Resolve("NAVY").QuotaGroup, "navy quota group is exact");
    var graphite = QuotaBackgroundPalette.Resolve("graphite");
    Equal(Color.FromArgb(8, 9, 11), graphite.Outer, "graphite outer background is neutral charcoal");
    Equal(Color.FromArgb(18, 19, 22), graphite.ProviderGroup, "graphite provider group is visibly distinct");
    Equal(Color.FromArgb(27, 28, 31), graphite.QuotaGroup, "graphite quota group is visibly distinct");
    Equal(Color.FromArgb(16, 17, 20), graphite.Popover, "graphite popover is neutral charcoal");

    foreach (var theme in QuotaBackgroundPalette.All)
    {
        Equal(255, theme.Outer.A, $"{theme.Id} outer background is opaque");
        Equal(255, theme.ProviderGroup.A, $"{theme.Id} provider group background is opaque");
        Equal(255, theme.QuotaGroup.A, $"{theme.Id} quota group background is opaque");
        Equal(255, theme.Popover.A, $"{theme.Id} popover background is opaque");
    }

    var miniSettings = new AppSettings
    {
        Locale = "en",
        UseTaskbarRings = true,
        EnableAnimations = false,
        EnableRadar = false,
        BackgroundPalette = "midnight",
    };
    using (var mini = new BarForm(
               miniSettings,
               TaskbarMiniCaptureSnapshot(includeClaude: false),
               renderOnly: true,
               renderDpi: 96))
    {
        mini.ApplySettings(new AppSettings
        {
            Locale = "en",
            UseTaskbarRings = true,
            EnableAnimations = false,
            EnableRadar = false,
            BackgroundPalette = "graphite",
        });
        mini.CreateControl();
        using var bitmap = new Bitmap(mini.ClientSize.Width, mini.ClientSize.Height, PixelFormat.Format32bppPArgb);
        mini.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var pixels = new Dictionary<int, int>();
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var argb = bitmap.GetPixel(x, y).ToArgb();
                pixels[argb] = pixels.GetValueOrDefault(argb) + 1;
            }
        }
        Equal(true, pixels.GetValueOrDefault(graphite.Outer.ToArgb()) > 100, "live palette apply repaints the outer shell");
        Equal(true, pixels.GetValueOrDefault(graphite.ProviderGroup.ToArgb()) > 100, "live palette apply repaints the provider group");
        Equal(true, pixels.GetValueOrDefault(graphite.QuotaGroup.ToArgb()) > 100, "live palette apply repaints the quota group");
    }

    using var settingsForm = new SettingsForm(
        new AppSettings { Locale = "en", BackgroundPalette = "navy" },
        192);
    LayoutControlTree(settingsForm);
    var paletteIds = QuotaBackgroundPalette.All.Select(theme => theme.Id).ToHashSet(StringComparer.Ordinal);
    var paletteButtons = DescendantControls(settingsForm)
        .OfType<System.Windows.Forms.Button>()
        .Where(button => button.Tag is string id && paletteIds.Contains(id))
        .ToArray();
    Equal(4, paletteButtons.Length, "settings exposes every background preset");
    Equal(
        2,
        paletteButtons.Single(button => Equals(button.Tag, "navy")).FlatAppearance.BorderSize,
        "selected palette has a strong outline");
    Equal(
        true,
        paletteButtons.All(button => button.Parent is not null
            && button.Left >= 0
            && button.Right <= button.Parent.ClientSize.Width),
        "palette choices remain inside the settings row at 192 DPI");
}

static void TestNativeSettingsPanelContract()
{
    using (var hiddenSettings = new System.Windows.Forms.Form
           {
               ShowInTaskbar = false,
               StartPosition = System.Windows.Forms.FormStartPosition.Manual,
               Location = new Point(-20_000, -20_000),
           })
    {
        hiddenSettings.Show();
        System.Windows.Forms.Application.DoEvents();
        hiddenSettings.Hide();
        System.Windows.Forms.Application.DoEvents();
        Equal(false, hiddenSettings.Visible, "settings fixture starts hidden");

        QuotaApplicationContext.RestoreSettingsWindow(hiddenSettings);
        System.Windows.Forms.Application.DoEvents();
        Equal(true, hiddenSettings.Visible, "opening settings restores an existing hidden window");
        hiddenSettings.Hide();
    }

    var visibleSettingsBounds = new Rectangle(-1200, 100, 720, 540);
    Equal(
        visibleSettingsBounds.Location,
        QuotaApplicationContext.RestoredSettingsLocation(
            visibleSettingsBounds,
            [new Rectangle(-1920, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080)],
            new Rectangle(0, 0, 1920, 1080)),
        "settings already intersecting a monitor keep their location");
    Equal(
        new Point(240, 330),
        QuotaApplicationContext.RestoredSettingsLocation(
            new Rectangle(-20_000, -20_000, 720, 540),
            [new Rectangle(0, 0, 1920, 1080)],
            new Rectangle(100, 200, 1000, 800)),
        "off-screen settings center inside the Bar working area");

    Equal("3.0.0", SettingsForm.ReadSemanticVersion(), "settings reads the Native assembly semantic version");
    Equal(
        new Size(992, 688),
        SettingsForm.ConstrainOuterSize(new Size(1240, 1280), new Rectangle(0, 0, 1024, 720), 16),
        "settings clamps both axes to the synthetic work area with a physical safe margin");
    Equal(
        new Size(620, 640),
        SettingsForm.ConstrainOuterSize(new Size(620, 640), new Rectangle(0, 0, 1920, 1080), 16),
        "settings clamp never enlarges the requested window");

    var rejectedSyntheticArea = false;
    try
    {
        using var invalid = new SettingsForm(
            new AppSettings(),
            96,
            renderOnly: false,
            renderWorkingArea: new Rectangle(0, 0, 1024, 720));
    }
    catch (ArgumentException)
    {
        rejectedSyntheticArea = true;
    }
    Equal(true, rejectedSyntheticArea, "production settings reject synthetic monitor geometry");

    foreach (var locale in new[] { "zh-CN", "en" })
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            foreach (var constrained in new[] { false, true })
            {
                using var form = new SettingsForm(
                    new AppSettings
                    {
                        Locale = locale,
                        EnableRadar = true,
                        EnableRadarAlerts = true,
                    },
                    dpi,
                    renderOnly: true,
                    renderWorkingArea: constrained ? new Rectangle(0, 0, 1024, 720) : null);
                LayoutControlTree(form);
                var descendants = DescendantControls(form).ToArray();
                Equal(false, form.TopMost, "settings stay in the normal window band");
                Equal(true, form.ShowInTaskbar, "settings remain reachable like a normal window");
                Equal(
                    7,
                    descendants.Count(control => control.Tag is string tag && tag.StartsWith("settings.page.", StringComparison.Ordinal) && tag != "settings.page.title"),
                    $"{locale} {dpi} DPI {(constrained ? "constrained" : "default")} exposes seven settings pages");
                var viewport = descendants.Single(control => Equals(control.Tag, "settings.content"));
                Equal(true, viewport.Width > 0 && viewport.Height > 0, "settings viewport remains usable");
                Equal(
                    false,
                    descendants.OfType<System.Windows.Forms.Button>().Single(button => Equals(button.Tag, "settings.save")).Enabled,
                    "save is disabled for the untouched draft");
                Equal(
                    true,
                    descendants
                        .Where(control => !string.IsNullOrWhiteSpace(control.Text)
                            && control is System.Windows.Forms.Label
                                or System.Windows.Forms.ButtonBase
                                or System.Windows.Forms.ComboBox)
                        .All(control => control.Font.Unit == GraphicsUnit.Pixel),
                    "settings text uses explicitly scaled pixel fonts without a second Windows DPI scale");
                Equal(
                    true,
                    descendants.Where(control => control.Tag is string tag && tag.StartsWith("settings.page.", StringComparison.Ordinal) && tag != "settings.page.title")
                        .All(page => page.Parent is not null && page.Left >= 0 && page.Right <= page.Parent.ClientSize.Width),
                    "settings pages stay inside the content host");
                if (constrained)
                {
                    Equal(true, form.Width <= 992 && form.Height <= 688, "constrained settings fit the synthetic work area");
                }
            }
        }
    }

    using var draft = new SettingsForm(
        new AppSettings
        {
            Locale = "en",
            EnabledProviders = ["claude", "codex"],
            EnableSub2ApiPool = true,
            PluginEnabled = new(StringComparer.Ordinal)
            {
                ["zgstokenbar.metrics.system"] = true,
                ["zgstokenbar.usage.codex-local"] = true,
            },
            EnableAnimations = true,
            EnableRadar = true,
            EnableRadarAlerts = true,
            WindowX = 27,
            WindowY = 41,
            TaskbarPosition = .4,
        },
        96,
        renderOnly: true,
        codexEconomyStatus: new CodexEconomyStatus(
            CodexEconomyMode.Ask,
            new CodexEconomyProfile("Default Codex", Path.Combine(Path.GetTempPath(), "wmt-ui-default"), true, "test"),
            true,
            true,
            null));
    draft.Show();
    System.Windows.Forms.Application.DoEvents();
    LayoutControlTree(draft);
    var controls = DescendantControls(draft).ToArray();
    var pageKeys = new[] { "general", "providers", "notifications", "display", "radar", "advanced", "about" };
    for (var pass = 0; pass < 4; pass++)
    {
        foreach (var pageKey in pageKeys)
        {
            draft.SelectPageForRendering(pageKey);
            System.Windows.Forms.Application.DoEvents();
            Equal($"settings.page.{pageKey}", draft.ScrollViewport.Tag, "page switching updates the active viewport atomically");
            Equal(
                1,
                controls.Count(control => control.Tag is string tag
                    && tag.StartsWith("settings.page.", StringComparison.Ordinal)
                    && tag != "settings.page.title"
                    && control.Visible),
                "page switching leaves exactly one visible page");
        }
    }
    draft.SelectPageForRendering("general");
    var save = controls.OfType<System.Windows.Forms.Button>().Single(button => Equals(button.Tag, "settings.save"));
    var claude = controls.OfType<System.Windows.Forms.CheckBox>().Single(toggle => toggle.AccessibleName == "Claude");
    PerformClick(claude);
    Equal(false, claude.Checked, "Claude can be disabled independently");
    var codex = controls.OfType<System.Windows.Forms.CheckBox>().Single(toggle => toggle.AccessibleName == "Codex");
    var codexLocal = controls.OfType<System.Windows.Forms.CheckBox>()
        .Single(toggle => toggle.AccessibleName == "Codex local usage");
    var sub2Api = controls.OfType<System.Windows.Forms.CheckBox>()
        .Single(toggle => toggle.AccessibleName == "Sub2API pool");
    PerformClick(codex);
    Equal(false, codex.Checked, "Codex can be disabled when Claude is already off");
    Equal(false, codexLocal.Checked, "disabling Codex clears local usage in the draft");
    Equal(false, sub2Api.Checked, "disabling Codex clears Sub2API in the draft");
    Equal(false, codexLocal.Enabled, "local usage is unavailable while Codex is off");
    Equal(false, sub2Api.Enabled, "Sub2API is unavailable while Codex is off");
    PerformClick(codex);
    Equal(true, codexLocal.Checked, "re-enabling Codex restores local usage draft state");
    Equal(true, sub2Api.Checked, "re-enabling Codex restores Sub2API draft state");
    PerformClick(claude);
    Equal(false, save.Enabled, "restoring provider and dependency choices clears dirty state");

    var systemMetrics = controls.OfType<System.Windows.Forms.CheckBox>()
        .Single(toggle => toggle.AccessibleName == "System usage");
    PerformClick(systemMetrics);
    Equal(true, save.Enabled, "system usage has an independent module switch");
    PerformClick(systemMetrics);
    Equal(false, save.Enabled, "restoring system usage clears dirty state");

    var aiGateway = controls.OfType<System.Windows.Forms.CheckBox>()
        .Single(toggle => toggle.AccessibleName == "AI Gateway");
    PerformClick(aiGateway);
    Equal(true, save.Enabled, "AI Gateway has an independent module switch");
    PerformClick(aiGateway);
    Equal(false, save.Enabled, "restoring AI Gateway clears dirty state");

    var animations = controls.OfType<System.Windows.Forms.CheckBox>().Single(toggle => toggle.AccessibleName == "Animation effects");
    PerformClick(animations);
    Equal(true, save.Enabled, "editing a setting enables Save");
    PerformClick(animations);
    Equal(false, save.Enabled, "restoring the original draft disables Save");

    var radar = controls.OfType<System.Windows.Forms.CheckBox>().Single(toggle => toggle.AccessibleName == "Show Radar");
    var radarAlerts = controls.OfType<System.Windows.Forms.CheckBox>().Single(toggle => toggle.AccessibleName == "Radar alerts");
    PerformClick(radar);
    Equal(false, radarAlerts.Checked, "disabling Radar visually clears alert draft state");
    Equal(false, radarAlerts.Enabled, "Radar alerts are unavailable while Radar is off");
    PerformClick(radar);
    Equal(true, radarAlerts.Checked, "re-enabling Radar restores the prior alert draft");

    var testRaised = false;
    draft.RadarTestNotificationRequested += (_, _) => testRaised = true;
    draft.SelectPageForRendering("radar");
    System.Windows.Forms.Application.DoEvents();
    controls.OfType<System.Windows.Forms.Button>().Single(button => Equals(button.Tag, "settings.radar.test")).PerformClick();
    Equal(true, testRaised, "Radar test notification follows the current draft without saving");

    draft.SelectPageForRendering("advanced");
    System.Windows.Forms.Application.DoEvents();
    Equal(
        1,
        controls.Count(control => Equals(control.Tag, "settings.economy.panel")),
        "Advanced embeds Codex economy management without opening another window");
    Equal(
        true,
        controls.Single(control => Equals(control.Tag, "economy.status"))
            .Text.Contains("Ask", StringComparison.Ordinal),
        "Advanced exposes the injected Codex economy status without reading production config");
    draft.SelectPageForRendering("providers");
    System.Windows.Forms.Application.DoEvents();
    var economyBar = controls.OfType<System.Windows.Forms.CheckBox>()
        .Single(toggle => toggle.AccessibleName == "Bar quick control");
    PerformClick(economyBar);
    Equal(true, save.Enabled, "the Bar economy component has an independent visibility switch");
    PerformClick(economyBar);
    Equal(false, save.Enabled, "restoring the Bar economy component visibility clears dirty state");

    TestCodexEconomySettingsPanelContract();

    PerformClick(animations);
    save.PerformClick();
    Equal(true, draft.ResultSettings is not null, "Save produces a complete settings draft");
    Equal(27, draft.ResultSettings?.WindowX, "Save preserves floating window X");
    Equal(41, draft.ResultSettings?.WindowY, "Save preserves floating window Y");
    Equal(.4, draft.ResultSettings?.TaskbarPosition, "Save preserves taskbar position");
}

static void TestCodexEconomySettingsPanelContract()
{
    var first = new CodexEconomyProfile(
        "Default Codex",
        Path.Combine(Path.GetTempPath(), "wmt-economy-ui-default"),
        true,
        "test");
    var second = new CodexEconomyProfile(
        "Fixture Codex",
        Path.Combine(Path.GetTempPath(), "wmt-economy-ui-fixture"),
        false,
        "test");

    foreach (var locale in new[] { "zh-CN", "en" })
    {
        foreach (var dpi in new[] { 96, 144, 192 })
        {
            var selectedMode = CodexEconomyMode.Ask;
            var writes = 0;
            CodexEconomyStatus Inspect(CodexEconomyProfile profile) => new(
                selectedMode,
                profile,
                true,
                profile == first,
                null);
            CodexEconomyStatus SetMode(CodexEconomyProfile profile, CodexEconomyMode mode)
            {
                writes++;
                selectedMode = mode;
                return Inspect(profile);
            }

            using var panel = new CodexEconomySettingsPanel(
                NativeText.For(locale),
                dpi,
                renderOnly: true,
                profiles: [second, first],
                inspect: Inspect,
                setMode: SetMode);
            panel.Width = (int)Math.Round(640 * dpi / 96d);
            LayoutControlTree(panel);
            var controls = DescendantControls(panel).ToArray();
            Equal(first, panel.SelectedProfile, "embedded economy panel selects the explicitly recommended profile");
            Equal(CodexEconomyMode.Ask, panel.SelectedMode, "embedded economy panel reflects the inspected mode");
            Equal(0, writes, "opening and inspecting the embedded economy panel does not write config");
            Equal(
                3,
                controls.OfType<System.Windows.Forms.RadioButton>()
                    .Count(option => option.Tag is string tag && tag.StartsWith("economy.mode.", StringComparison.Ordinal)),
                $"{locale} {dpi} DPI exposes three textual economy modes");
            Equal(
                true,
                controls.Where(control => !string.IsNullOrWhiteSpace(control.Text)
                        && control is System.Windows.Forms.Label
                            or System.Windows.Forms.ButtonBase
                            or System.Windows.Forms.ComboBox
                            or System.Windows.Forms.TextBox)
                    .All(control => control.Font.Unit == GraphicsUnit.Pixel),
                $"{locale} {dpi} DPI economy text uses explicitly scaled pixel fonts");
            Equal(
                true,
                controls.Where(control => control.Parent is not null)
                    .All(control => control.Left >= 0 && control.Right <= control.Parent!.ClientSize.Width),
                $"{locale} {dpi} DPI economy controls stay within their parent width");

            PerformClick(controls.Single(control => Equals(control.Tag, "economy.mode.on")));
            Equal(CodexEconomyMode.On, panel.SelectedMode, "choosing On updates only the local draft");
            Equal(0, writes, "choosing an economy mode does not write before Apply");
            PerformClick(controls.Single(control => Equals(control.Tag, "economy.apply")));
            Equal(1, writes, "Apply performs exactly one economy write");
            Equal(CodexEconomyMode.On, panel.AppliedStatus?.Mode, "Apply records the read-back verified mode in place");
        }
    }

    using var readOnly = new CodexEconomySettingsPanel(
        NativeText.For("en"),
        96,
        renderOnly: true,
        profiles: [first],
        inspect: profile => new CodexEconomyStatus(CodexEconomyMode.Off, profile, false, false, null));
    LayoutControlTree(readOnly);
    Equal(
        false,
        DescendantControls(readOnly).Single(control => Equals(control.Tag, "economy.apply")).Enabled,
        "render-only embedded economy panel cannot write without an injected set-mode function");
}

static void LayoutControlTree(System.Windows.Forms.Control control)
{
    control.CreateControl();
    control.PerformLayout();
    foreach (System.Windows.Forms.Control child in control.Controls)
    {
        LayoutControlTree(child);
    }
    control.PerformLayout();
}

static void PerformClick(System.Windows.Forms.Control control)
{
    var onClick = control.GetType().GetMethod(
        "OnClick",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    onClick?.Invoke(control, [EventArgs.Empty]);
}

static IEnumerable<System.Windows.Forms.Control> DescendantControls(
    System.Windows.Forms.Control parent)
{
    foreach (System.Windows.Forms.Control child in parent.Controls)
    {
        yield return child;
        foreach (var descendant in DescendantControls(child)) yield return descendant;
    }
}

static void TestCompactResetLabels()
{
    var now = DateTimeOffset.Parse("2026-07-17T04:00:00Z");
    Equal("5h", QuotaDisplayFormatting.FormatWindowShort(new("primary", 0, null, TimeSpan.FromHours(5))), "five-hour display label");
    Equal("7d", QuotaDisplayFormatting.FormatWindowShort(new("1w", 0, null, TimeSpan.FromDays(7))), "weekly display label");
    Equal("30d", QuotaDisplayFormatting.FormatWindowShort(new("30d", 0, null, TimeSpan.FromDays(30))), "other display label");
    Equal("Fb", QuotaDisplayFormatting.FormatWindowTiny(new("Fable", 0, null, TimeSpan.FromDays(7))), "Fable Mini label");
    Equal("--", QuotaDisplayFormatting.FormatResetShort(null, now), "missing reset");
    Equal("now", QuotaDisplayFormatting.FormatResetShort(now - TimeSpan.FromMinutes(1), now), "elapsed reset");
    Equal("48m", QuotaDisplayFormatting.FormatResetShort(now + TimeSpan.FromMinutes(48), now), "minute reset");
    Equal("2h15m", QuotaDisplayFormatting.FormatResetShort(now + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(15), now), "hour reset");
    Equal("1d2h", QuotaDisplayFormatting.FormatResetShort(now + TimeSpan.FromHours(26), now), "day reset");
    Equal("5d12h", QuotaDisplayFormatting.FormatResetShort(now + TimeSpan.FromDays(5) + TimeSpan.FromHours(12), now), "weekly reset");

    var weeklyReset = now.AddHours(22);
    var blockedFiveHour = new QuotaWindow("5h", 0, null, TimeSpan.FromHours(5));
    var exhaustedClaude = new QuotaCard(
        "claude.account",
        ProviderKind.Claude,
        "Claude",
        "Max",
        "#d97757",
        true,
        [
            blockedFiveHour,
            new QuotaWindow("1w", 100, weeklyReset, TimeSpan.FromDays(7)),
        ]);
    Equal(
        weeklyReset,
        QuotaDisplayFormatting.WeeklyBlockReset(exhaustedClaude, blockedFiveHour, now),
        "Claude five-hour window is blocked until exhausted weekly quota resets");
    Equal<DateTimeOffset?>(
        null,
        QuotaDisplayFormatting.WeeklyBlockReset(
            exhaustedClaude with
            {
                Windows =
                [
                    blockedFiveHour,
                    new QuotaWindow("1w", 99, weeklyReset, TimeSpan.FromDays(7)),
                ],
            },
            blockedFiveHour,
            now),
        "available weekly quota does not block five-hour window");
    Equal<DateTimeOffset?>(
        null,
        QuotaDisplayFormatting.WeeklyBlockReset(
            exhaustedClaude with
            {
                Windows =
                [
                    blockedFiveHour,
                    new QuotaWindow("1w", 99, weeklyReset, TimeSpan.FromDays(7)),
                    new QuotaWindow("Fable", 100, weeklyReset, TimeSpan.FromDays(7)),
                ],
            },
            blockedFiveHour,
            now),
        "Fable-only exhaustion does not block the general five-hour window");
    Equal<DateTimeOffset?>(
        null,
        QuotaDisplayFormatting.WeeklyBlockReset(
            exhaustedClaude,
            blockedFiveHour with { ResetsAt = now.AddHours(3) },
            now),
        "five-hour window with its own reset remains active");
}

static void TestDailyQuotaBudgetMarker()
{
    var cycle = new QuotaCyclePace(40, -20);
    var weekly = new QuotaWindow(
        "7d",
        20,
        DateTimeOffset.Parse("2026-08-07T16:00:00Z"),
        TimeSpan.FromDays(7));
    var shanghaiMorning = DateTimeOffset.Parse("2026-08-01T02:00:00Z");
    var shanghaiBeforeMidnight = DateTimeOffset.Parse("2026-08-01T15:59:00Z");
    var shanghaiMidnight = DateTimeOffset.Parse("2026-08-01T16:00:00Z");

    Equal(
        85.714,
        Math.Round(QuotaDisplayFormatting.BudgetMarkerRemaining(weekly, cycle, shanghaiMorning)!.Value, 3),
        "weekly marker targets the next Shanghai midnight");
    Equal(
        85.714,
        Math.Round(QuotaDisplayFormatting.BudgetMarkerRemaining(weekly, cycle, shanghaiBeforeMidnight)!.Value, 3),
        "weekly marker stays fixed within one Shanghai day");
    Equal(
        71.429,
        Math.Round(QuotaDisplayFormatting.BudgetMarkerRemaining(weekly, cycle, shanghaiMidnight)!.Value, 3),
        "weekly marker advances at Shanghai midnight");

    var resetsBeforeMidnight = weekly with
    {
        ResetsAt = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
    };
    Equal(
        0d,
        QuotaDisplayFormatting.BudgetMarkerRemaining(resetsBeforeMidnight, cycle, shanghaiMorning),
        "real reset caps a daily target that would extend past the cycle");
    Equal<double?>(
        null,
        QuotaDisplayFormatting.BudgetMarkerRemaining(resetsBeforeMidnight, cycle, shanghaiMidnight),
        "expired cycle hides its budget marker");

    var fiveHour = new QuotaWindow(
        "5h",
        40,
        shanghaiMorning.AddHours(3),
        TimeSpan.FromHours(5));
    Equal(
        60d,
        QuotaDisplayFormatting.BudgetMarkerRemaining(fiveHour, cycle, shanghaiMorning),
        "short windows keep the current cycle budget marker");
}

static void TestQuotaMilestoneAlerts()
{
    var fiveHourReset = DateTimeOffset.Parse("2026-07-17T09:00:00Z");
    var weekReset = DateTimeOffset.Parse("2026-07-23T00:00:00Z");
    var tracker = new QuotaMilestoneTracker(Snapshot(24, 49, fiveHourReset, weekReset));

    var first = tracker.Observe(Snapshot(26, 51, fiveHourReset, weekReset));
    Equal(2, first.Count, "independent milestone count");
    Equal("5h", first[0].WindowLabel, "five-hour alert label");
    Equal(25, first[0].Threshold, "five-hour threshold");
    Equal("1w", first[1].WindowLabel, "weekly alert label");
    Equal(50, first[1].Threshold, "weekly threshold");

    var jumped = tracker.Observe(Snapshot(77, 92, fiveHourReset, weekReset));
    Equal(2, jumped.Count, "jumped milestone count");
    Equal(75, jumped[0].Threshold, "highest five-hour crossed threshold");
    Equal(90, jumped[1].Threshold, "highest weekly crossed threshold");

    Equal(0, tracker.Observe(Snapshot(74, 89, fiveHourReset, weekReset)).Count, "falling usage does not alert");
    Equal(0, tracker.Observe(Snapshot(78, 93, fiveHourReset, weekReset)).Count, "crossed milestones do not repeat");

    var nextFiveHourReset = fiveHourReset.AddHours(5);
    Equal(0, tracker.Observe(Snapshot(4, 93, nextFiveHourReset, weekReset)).Count, "new cycle establishes a baseline");
    var rearmed = tracker.Observe(Snapshot(26, 93, nextFiveHourReset, weekReset));
    Equal(1, rearmed.Count, "new cycle rearms milestones");
    Equal(25, rearmed[0].Threshold, "rearmed threshold");

    var exhausted = tracker.Observe(Snapshot(100, 93, nextFiveHourReset, weekReset));
    Equal(1, exhausted.Count, "exhausted quota alert count");
    Equal(100, exhausted[0].Threshold, "exhausted quota threshold");
}

static QuotaSnapshot Snapshot(
    double fiveHourUsed,
    double weekUsed,
    DateTimeOffset fiveHourReset,
    DateTimeOffset weekReset) => new(
    [
        new QuotaCard(
            "codex.account",
            ProviderKind.Codex,
            "Codex",
            "Pro",
            "#10a37f",
            true,
            [
                new QuotaWindow("5h", fiveHourUsed, fiveHourReset, TimeSpan.FromHours(5)),
                new QuotaWindow("1w", weekUsed, weekReset, TimeSpan.FromDays(7)),
            ]),
    ],
    [new ProviderHealth(ProviderKind.Codex, true, "Codex quota is current.")],
    DateTimeOffset.UtcNow);

static QuotaSnapshot SingleWindowSnapshot(double used, DateTimeOffset reset) => new(
    [
        new QuotaCard(
            "codex.account",
            ProviderKind.Codex,
            "Codex",
            "Pro",
            "#10a37f",
            true,
            [new QuotaWindow("5h", used, reset, TimeSpan.FromHours(5))]),
    ],
    [new ProviderHealth(ProviderKind.Codex, true, "Codex quota is current.")],
    DateTimeOffset.UtcNow);

static void Equal<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}

static string ProfileBodyWithDailyUsage(
    long lifetimeTokens,
    IEnumerable<(DateOnly Date, long Tokens)> buckets) =>
    JsonSerializer.Serialize(new
    {
        stats = new
        {
            lifetime_tokens = lifetimeTokens,
            daily_usage_buckets = buckets.Select(bucket => new
            {
                start_date = bucket.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                tokens = bucket.Tokens,
                compatible_extra_field = true,
            }),
        },
    });

sealed class MemoryAiGatewayConnectionStore : IAiGatewayConnectionStore
{
    private AiGatewayConnection? _connection;

    public MemoryAiGatewayConnectionStore(AiGatewayConnection? connection) => _connection = connection;
    public AiGatewayConnection? Read() => _connection;
    public void Write(AiGatewayConnection connection) => _connection = connection;
    public void Delete() => _connection = null;
}

sealed class MemorySub2ApiPoolConnectionStore : ISub2ApiPoolConnectionStore
{
    private Sub2ApiPoolConnection? _connection;

    public MemorySub2ApiPoolConnectionStore(Sub2ApiPoolConnection? connection) => _connection = connection;
    public Sub2ApiPoolConnection? Read() => _connection;
    public void Write(Sub2ApiPoolConnection connection) => _connection = connection;
    public void Delete() => _connection = null;
}

sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(responseFactory(request));
}

sealed class RecordingProxy(Uri proxy) : IWebProxy
{
    public Uri Proxy { get; } = proxy;
    public int GetProxyCalls { get; private set; }
    public int IsBypassedCalls { get; private set; }
    public ICredentials? Credentials { get; set; }

    public Uri GetProxy(Uri destination)
    {
        GetProxyCalls++;
        return Proxy;
    }

    public bool IsBypassed(Uri host)
    {
        IsBypassedCalls++;
        return false;
    }
}

sealed class UnknownLengthContent(byte[] payload) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(payload).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

static class NativeWindowMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}

sealed record CodexProfileRequest(
    string Path,
    HttpMethod Method,
    string? Authorization,
    string? AccountId,
    bool NoCache,
    string UserAgent);

sealed class CodexProfileUsageHandler(long lifetimeTokens) : HttpMessageHandler
{
    public List<CodexProfileRequest> Requests { get; } = [];
    public HttpStatusCode ProfileStatus { get; set; } = HttpStatusCode.OK;
    public string ProfileBody { get; set; } = $"{{\"stats\":{{\"lifetime_tokens\":{lifetimeTokens}}}}}";

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(new CodexProfileRequest(
            request.RequestUri?.AbsolutePath ?? "",
            request.Method,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("ChatGPT-Account-Id", out var accountIds)
                ? accountIds.SingleOrDefault()
                : null,
            request.Headers.CacheControl?.NoCache == true
                && request.Headers.Pragma.Any(value =>
                    string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase)),
            request.Headers.UserAgent.ToString()));
        if (request.RequestUri?.AbsolutePath.EndsWith("/profiles/me", StringComparison.Ordinal) == true)
        {
            return Task.FromResult(new HttpResponseMessage(ProfileStatus)
            {
                Content = new StringContent(ProfileBody, Encoding.UTF8, "application/json"),
            });
        }

        var usage = JsonSerializer.Serialize(new
        {
            plan_type = "pro",
            rate_limit = new
            {
                primary_window = new
                {
                    used_percent = 12,
                    limit_window_seconds = 604_800,
                    reset_at = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(),
                },
            },
        });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(usage, Encoding.UTF8, "application/json"),
        });
    }
}

sealed class CodexUsageHandler(string expectedToken, double usedPercent) : HttpMessageHandler
{
    private int _calls;
    private int _expectedCalls;

    public int Calls => Volatile.Read(ref _calls);
    public int ExpectedCalls => Volatile.Read(ref _expectedCalls);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/profiles/me", StringComparison.Ordinal) == true)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        Interlocked.Increment(ref _calls);
        if (!string.Equals(
                request.Headers.Authorization?.Parameter,
                expectedToken,
                StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"expired"}""", Encoding.UTF8, "application/json"),
            });
        }

        Interlocked.Increment(ref _expectedCalls);
        var payload = JsonSerializer.Serialize(new
        {
            plan_type = "pro",
            rate_limit = new
            {
                primary_window = new
                {
                    used_percent = usedPercent,
                    limit_window_seconds = 604_800,
                    reset_at = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(),
                },
            },
        });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });
    }
}

sealed class CodexUsageMapHandler(
    IReadOnlyDictionary<string, double> usageByToken,
    IReadOnlyDictionary<string, string>? planByToken = null) : HttpMessageHandler
{
    private int _calls;
    private int _expectedCalls;
    private int _profileCalls;

    public int Calls => Volatile.Read(ref _calls);
    public int ExpectedCalls => Volatile.Read(ref _expectedCalls);
    public int ProfileCalls => Volatile.Read(ref _profileCalls);
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> RequestedTokens { get; } =
        new(StringComparer.Ordinal);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/profiles/me", StringComparison.Ordinal) == true)
        {
            Interlocked.Increment(ref _profileCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        Interlocked.Increment(ref _calls);
        var token = request.Headers.Authorization?.Parameter;
        if (token is not null) RequestedTokens.TryAdd(token, 0);
        if (token is null || !usageByToken.TryGetValue(token, out var usedPercent))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"expired"}""", Encoding.UTF8, "application/json"),
            });
        }

        Interlocked.Increment(ref _expectedCalls);
        var plan = token is not null
            && planByToken is not null
            && planByToken.TryGetValue(token, out var mappedPlan)
            ? mappedPlan
            : "pro";
        var payload = JsonSerializer.Serialize(new
        {
            plan_type = plan,
            rate_limit = new
            {
                primary_window = new
                {
                    used_percent = usedPercent,
                    limit_window_seconds = 604_800,
                    reset_at = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(),
                },
            },
        });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });
    }
}

sealed class ClaudeRateLimitHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    public bool SawNoCache { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Calls++;
        SawNoCache |= request.Headers.CacheControl?.NoCache == true;
        if (Calls == 1)
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
            return Task.FromResult(limited);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "five_hour": { "utilization": 12, "resets_at": "2026-07-27T18:00:00Z" },
                  "seven_day": { "utilization": 34, "resets_at": "2026-07-30T00:00:00Z" }
                }
                """),
        });
    }
}

sealed class ClaudeOAuthRefreshHandler : HttpMessageHandler
{
    public int RefreshCalls { get; private set; }
    public int UsageCalls { get; private set; }
    public bool SawRefreshedToken { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post)
        {
            RefreshCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "access_token": "token-b",
                      "refresh_token": "refresh-b",
                      "expires_in": 3600
                    }
                    """),
            });
        }

        UsageCalls++;
        SawRefreshedToken = request.Headers.Authorization?.Parameter == "token-b";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "five_hour": { "utilization": 98, "resets_at": "2026-07-27T18:00:00Z" },
                  "seven_day": { "utilization": 98, "resets_at": "2026-07-30T00:00:00Z" },
                  "limits": [
                    {
                      "kind": "weekly_scoped",
                      "percent": 38,
                      "resets_at": "2026-07-30T00:01:00Z",
                      "scope": { "model": { "display_name": "Fable" } }
                    }
                  ]
                }
                """),
        });
    }
}

sealed class RadarRequestHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    public List<Uri> RequestUris { get; } = [];
    public bool AcceptsJson { get; private set; } = true;
    public bool SawNoCache { get; private set; } = true;
    public bool HasProductUserAgent { get; private set; } = true;
    public bool HasAuthorization { get; private set; }
    public bool HasCookie { get; private set; }
    public bool ReturnEmptyRecommendations { get; init; }
    public bool ReturnEmptyRecommendationsAfterFirst { get; init; }
    public bool RequireConcurrentSupplementalRequests { get; init; }
    public bool ConcurrentSupplementalRequestsObserved => _supplementalRequestCount >= 2;
    private int _recommendationCalls;
    private int _supplementalRequestCount;
    private readonly TaskCompletionSource _supplementalRequestsStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var isRecommendations =
            request.RequestUri?.AbsoluteUri == RadarService.RecommendationsUri.AbsoluteUri;
        var isMeasurements =
            request.RequestUri?.AbsoluteUri == RadarService.MeasurementsUri.AbsoluteUri;

        if (RequireConcurrentSupplementalRequests && (isRecommendations || isMeasurements))
        {
            if (Interlocked.Increment(ref _supplementalRequestCount) >= 2)
            {
                _supplementalRequestsStarted.TrySetResult();
            }
            await _supplementalRequestsStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(1),
                cancellationToken);
        }

        string content;
        lock (RequestUris)
        {
            Calls++;
            if (request.RequestUri is { } uri) RequestUris.Add(uri);
            AcceptsJson &= request.Headers.Accept.Any(value =>
                string.Equals(value.MediaType, "application/json", StringComparison.OrdinalIgnoreCase));
            SawNoCache &= request.Headers.CacheControl?.NoCache == true;
            HasProductUserAgent &= request.Headers.UserAgent.Any(value =>
                string.Equals(value.Product?.Name, "ZGSTokenBar", StringComparison.Ordinal));
            HasAuthorization |= request.Headers.Authorization is not null;
            HasCookie |= request.Headers.Contains("Cookie");
            if (isRecommendations) _recommendationCalls++;
            content = isMeasurements
                ? Measurements
                : isRecommendations
                ? ReturnEmptyRecommendations
                    || ReturnEmptyRecommendationsAfterFirst && _recommendationCalls > 1
                    ? EmptyRecommendations
                    : Recommendations
                : PrimarySummary;
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        };
    }

    private const string PrimarySummary = """
        {
          "model_iq": {
            "updated_at": "2026-07-19T12:00:00Z",
            "latest": {
              "date": "2026-07-19",
              "model": "gpt-5.6-sol",
              "reasoning_effort": "max",
              "score": 100,
              "status": "green"
            }
          }
        }
        """;

    private const string Recommendations = """
        {
          "schema": 1,
          "mode": "latest_valid_per_task",
          "generated_at": "2026-08-01T13:39:13+08:00",
          "source_updated_at": "2026-08-01T13:39:08+08:00",
          "recommendations": [
            {
              "key": "daily_development",
              "title": "日常开发",
              "rule": "上游日常规则",
              "items": [
                {
                  "model": "gpt-5.5",
                  "effort": "xhigh",
                  "iq": 91.07,
                  "passed": 68,
                  "samples": 112,
                  "average_cost_usd": 5.870495,
                  "cost_samples": 111,
                  "average_duration_minutes": 23.41,
                  "duration_samples": 112,
                  "combined_cost_index": 7877.209,
                  "rule": "上游速度位规则",
                  "slot": "speed"
                },
                {
                  "model": "gpt-5.6-sol",
                  "effort": "xhigh",
                  "iq": 96.43,
                  "passed": 72,
                  "samples": 112,
                  "average_cost_usd": 6.635433,
                  "cost_samples": 112,
                  "average_duration_minutes": 25.2,
                  "duration_samples": 112,
                  "combined_cost_index": 11158.159,
                  "rule": "上游聪明位规则",
                  "slot": "smart"
                }
              ]
            },
            {
              "key": "hard_problems",
              "title": "难题攻坚",
              "rule": "上游攻坚规则",
              "items": [
                {
                  "model": "gpt-5.6-sol",
                  "effort": "ultra",
                  "iq": 109.82,
                  "passed": 82,
                  "samples": 112,
                  "average_cost_usd": 18.804978,
                  "cost_samples": 61,
                  "average_duration_minutes": 52.12,
                  "duration_samples": 112,
                  "combined_cost_index": 290652.363,
                  "rule": "上游攻坚规则"
                },
                {
                  "model": "gpt-5.6-sol",
                  "effort": "max",
                  "iq": 103.12,
                  "passed": 77,
                  "samples": 112,
                  "average_cost_usd": 9.549178,
                  "cost_samples": 112,
                  "average_duration_minutes": 33.49,
                  "duration_samples": 112,
                  "combined_cost_index": 38267.965,
                  "rule": "上游攻坚规则"
                }
              ]
            },
            {
              "key": "background_automation",
              "title": "后台自动化",
              "rule": "上游自动化规则",
              "items": [
                {
                  "model": "gpt-5.6-luna",
                  "effort": "max",
                  "iq": 80.36,
                  "passed": 60,
                  "samples": 112,
                  "average_cost_usd": 0.457463,
                  "cost_samples": 112,
                  "average_duration_minutes": 29.73,
                  "duration_samples": 112,
                  "combined_cost_index": 1274.144,
                  "rule": "上游自动化规则"
                },
                {
                  "model": "gpt-5.6-sol",
                  "effort": "medium",
                  "iq": 87.05,
                  "passed": 65,
                  "samples": 112,
                  "average_cost_usd": 3.890851,
                  "cost_samples": 110,
                  "average_duration_minutes": 17.41,
                  "duration_samples": 112,
                  "combined_cost_index": 2113.88,
                  "rule": "上游自动化规则"
                }
              ]
            },
            {
              "key": "future_category",
              "title": "未来类别",
              "rule": "未来规则",
              "items": [
                {
                  "model": "gpt-5.6-terra",
                  "effort": "medium",
                  "iq": 57.59,
                  "passed": 43,
                  "samples": 112,
                  "average_cost_usd": 0.639837,
                  "cost_samples": 110,
                  "average_duration_minutes": 9.06,
                  "duration_samples": 111,
                  "combined_cost_index": 47.315,
                  "rule": "未来规则"
                }
              ]
            }
          ]
        }
        """;

    private const string Measurements = """
        {
          "schema": 2,
          "source_updated_at": "2026-08-01T13:38:00+08:00",
          "points": [
            {
              "model": "gpt-5.6-sol",
              "effort": "max",
              "iq": 101.5,
              "passed": 76,
              "valid_tasks": 112,
              "average_price_usd": 9.5,
              "average_minutes": 34
            },
            {
              "model": "deepseek-v4-flash",
              "effort": "max",
              "iq": 79.02,
              "passed": 59,
              "valid_tasks": 112,
              "average_price_usd": 0.10,
              "average_minutes": 22.29
            },
            {
              "model": "deepseek-v4-flash",
              "effort": "high",
              "iq": 50.89,
              "passed": 38,
              "valid_tasks": 112,
              "average_price_usd": 0.098,
              "average_minutes": 24.18
            }
          ],
          "history": [
            {
              "at": "2026-08-01T12:00:00+08:00",
              "points": [
                { "model": "deepseek-v4-flash", "effort": "max", "iq": 80 },
                { "model": "deepseek-v4-flash", "effort": "high", "iq": 48 }
              ]
            },
            {
              "at": "2026-08-02T12:00:00+08:00",
              "points": [
                { "model": "deepseek-v4-flash", "effort": "max", "iq": 78 },
                { "model": "deepseek-v4-flash", "effort": "high", "iq": 52 }
              ]
            }
          ]
        }
        """;

    private const string EmptyRecommendations = """
        {
          "schema": 1,
          "mode": "latest_valid_per_task",
          "source_updated_at": "2026-08-01T13:40:00+08:00",
          "recommendations": [
            {
              "key": "daily_development",
              "title": "日常开发",
              "items": []
            }
          ]
        }
        """;
}
