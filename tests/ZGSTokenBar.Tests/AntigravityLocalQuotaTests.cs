using System.Diagnostics;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;

internal static class AntigravityLocalQuotaTests
{
    private const string Quota = """
        {"status":"SUCCESS","num_turns":0,"command":{"name":"usage","data":{"groups":[
          {"name":"Gemini 4 Argon","buckets":[{"name":"Gemini 4 Argon Weekly Limit","window":"weekly",
            "remaining_fraction":0.364,"reset_time":"2026-11-24T00:00:00Z"}]}]}}}
        """;

    internal static void Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("zgs-agy-native-quota-");
        var paths = new UsagePaths(root.FullName, root.FullName, root.FullName);
        var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "agy", "bin"));
        var exe = Path.Combine(bin.FullName, "agy.exe");
        var profile = Directory.CreateDirectory(Path.Combine(root.FullName, "Antigravity"));
        var appStorage = Path.Combine(profile.FullName, "app_storage.json");
        var now = DateTimeOffset.Parse("2026-11-18T10:00:00Z");
        var calls = new List<string>();
        var version = "1.2.9";
        string? reply = Quota;
        var switchProfile = false;
        var source = new AntigravityLocalQuota((path, args, _) =>
        {
            Check(path == exe, "Only the registered local AGY executable is used.");
            calls.Add(args[0]);
            if (args.SequenceEqual(["--version"])) return Task.FromResult<string?>(version);
            Check(args.SequenceEqual(["-p", "/usage", "--output-format", "json", "--print-timeout", "20s"]),
                "Only the bounded read-only command is sent to AGY.");
            if (switchProfile)
                File.WriteAllText(appStorage, """{"agy.onboarding.lastLoginUsername":"second@example.test"}""");
            return Task.FromResult<string?>(reply);
        });
        try
        {
            Check(await source.ReadAsync(paths, now, default) is null && calls.Count == 0,
                "A missing executable leaves the legacy route available.");
            File.WriteAllText(exe, "unqualified fixture");
            Check(await source.ReadAsync(paths, now, default) is null && calls.SequenceEqual(["--version"]),
                "An older CLI never receives a quota query.");

            version = "1.5.3";
            File.WriteAllText(exe, "updated fixture for a qualified executable");
            var current = await source.ReadAsync(paths, now, default);
            Check(current is { Freshness: UsageFreshness.Current, SourceKind: "agy-cli", AccountId: "antigravity", Label: "Antigravity" }
                && current.Windows.Single().UsedPercent is { } used && Math.Abs(used - 63.6) < .0001,
                "The native route reads quota without any legacy credential file and retains exact fractions.");

            File.WriteAllText(appStorage, """{"agy.onboarding.lastLoginUsername":"second@example.test"}""");
            var withAccount = await source.ReadAsync(paths, now.AddMinutes(1), default);
            Check(withAccount is { Freshness: UsageFreshness.Current, AccountId: "antigravity", Label: "second@example.test" },
                "Stable signed-in account labels the AGY snapshot while keeping AccountId fixed.");

            foreach (var failure in new[] { null, Quota.Replace("\"num_turns\":0", "\"num_turns\":1"),
                         Quota.Replace("\"usage\"", "\"other\""), Quota.Replace("\"SUCCESS\"", "\"ERROR\"") })
            {
                reply = failure;
                var unavailable = await source.ReadAsync(paths, now.AddMinutes(2), default);
                Check(unavailable is { Freshness: UsageFreshness.Unavailable, SampledAt: null, Windows.Count: 0, Label: "Antigravity" },
                    "A transport failure or nonquota result cannot become a current percentage, re-enable the legacy account, or keep stale labels.");
            }
            reply = Quota;
            switchProfile = true;
            File.WriteAllText(appStorage, """{"agy.onboarding.lastLoginUsername":"first@example.test"}""");
            Check(await source.ReadAsync(paths, now, default) is { Freshness: UsageFreshness.Unavailable, Label: "Antigravity" },
                "A login change during the read discards the result.");
            switchProfile = false;
            Check(await source.ReadAsync(paths, now.AddMinutes(2), default) is { Freshness: UsageFreshness.Current, Label: "second@example.test" }
                && calls.Count(call => call == "--version") == 2,
                "The next refresh recovers on the current account, reusing only the executable qualification.");
            await VerifyTokenJwtAndFreshestIdentityAsync(root.FullName, now, exe, appStorage, source);
            await VerifyDetachedChildAsync(root.FullName);
            Check(UsageCliFallback.DefaultProcessTimeout == TimeSpan.FromSeconds(25)
                && AntigravityLocalQuota.ProcessTimeout == TimeSpan.FromSeconds(35),
                "Default CLI timeout stays 25s while AGY keeps 35s cold-start headroom.");
            VerifySystemProxyPropagation();
            await VerifyPersistentQualificationCacheAsync(root.FullName);
        }
        finally
        {
            File.Delete(exe);
            File.Delete(appStorage);
            var tokenDir = Path.Combine(root.FullName, ".gemini", "antigravity-cli");
            if (Directory.Exists(tokenDir)) Directory.Delete(tokenDir, true);
            var geminiDir = Path.Combine(root.FullName, ".gemini");
            if (Directory.Exists(geminiDir)) Directory.Delete(geminiDir, true);
            File.Delete(Path.Combine(root.FullName, "pipe-started"));
            profile.Delete();
            bin.Delete();
            Directory.Delete(Path.Combine(root.FullName, "agy"));
            root.Delete();
        }
    }

    private static async Task VerifyTokenJwtAndFreshestIdentityAsync(
        string root,
        DateTimeOffset now,
        string exe,
        string appStorage,
        AntigravityLocalQuota source)
    {
        var paths = new UsagePaths(root, root, root);
        var cliDir = Directory.CreateDirectory(Path.Combine(root, ".gemini", "antigravity-cli"));
        var tokenFile = Path.Combine(cliDir.FullName, "antigravity-oauth-token");
        static string Jwt(string email)
        {
            static string B64(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"email\":\"{email}\"}}")}.sig";
        }

        if (File.Exists(appStorage)) File.Delete(appStorage);
        File.WriteAllText(tokenFile, $$$"""{"token":{"access_token":"ya29.test"},"id_token":"{{{Jwt("jwt@example.com")}}}"}""");
        Check(UsageAccountDirectory.AntigravitySignedInAccount(paths) == "jwt@example.com",
            "AGY extracts email from the CLI OAuth id_token claim when app_storage.json is absent.");
        var fromJwt = await source.ReadAsync(paths, now.AddMinutes(3), default);
        Check(fromJwt is { Freshness: UsageFreshness.Current, Label: "jwt@example.com" },
            "AGY snapshot labels the JWT-derived account.");

        File.WriteAllText(appStorage, """{"agy.onboarding.lastLoginUsername":"older-app@example.com"}""");
        File.SetLastWriteTimeUtc(appStorage, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(tokenFile, DateTime.UtcNow);
        Check(UsageAccountDirectory.AntigravitySignedInAccount(paths) == "jwt@example.com",
            "When both files exist, the fresher CLI token file wins.");

        File.WriteAllText(appStorage, """{"agy.onboarding.lastLoginUsername":"newer-app@example.com"}""");
        File.SetLastWriteTimeUtc(appStorage, DateTime.UtcNow.AddMinutes(1));
        Check(UsageAccountDirectory.AntigravitySignedInAccount(paths) == "newer-app@example.com",
            "When app_storage.json is newer than the token file, app_storage.json wins.");
        _ = exe;
    }

    private static async Task VerifyDetachedChildAsync(string root)
    {
        var output = await UsageCliFallback.RunProcessAsync(Environment.ProcessPath!, ["--quota-pipe-fixture"]);
        Check(output == "no-console:true;stdin:eof", "The native child has no console and receives EOF while stderr is drained.");

        var marker = Path.Combine(root, "pipe-started");
        using var cancellation = new CancellationTokenSource();
        var running = UsageCliFallback.RunProcessAsync(Environment.ProcessPath!, ["--quota-pipe-fixture", marker], cancellation.Token);
        Process? child = null;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(marker) && !running.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(8))
                await Task.Delay(25);
            Check(File.Exists(marker), "The fixture reached the blocked-pipe state.");
            child = Process.GetProcessById(int.Parse(File.ReadAllText(marker)));
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Check(running.IsCanceled && child.WaitForExit(5000), "A cancelled quota request promptly terminates the blocked writer.");
        }
        finally { cancellation.Cancel(); child?.Dispose(); }
    }

    internal static int RunPipeFixture(string? marker)
    {
        if (GetConsoleWindow() != 0 || !Console.IsInputRedirected || Console.In.Read() != -1) return 1;
        if (marker is not null)
        {
            if (!Path.GetFullPath(marker).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(marker) != "pipe-started") return 1;
            File.WriteAllText(marker + ".tmp", Environment.ProcessId.ToString());
            File.Move(marker + ".tmp", marker);
            Thread.Sleep(TimeSpan.FromSeconds(20));
            return 1;
        }
        Console.Error.Write(new string('e', 70_000));
        Console.Write("no-console:true;stdin:eof");
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    private static void VerifySystemProxyPropagation()
    {
        var probe = new Uri("https://cloudcode-pa.googleapis.com/");
        var emptyEnv = new ProcessStartInfo("cmd.exe");
        foreach (var key in new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" })
            emptyEnv.Environment.Remove(key);
        UsageCliFallback.ApplySystemProxy(emptyEnv, probe, _ => new Uri("http://127.0.0.1:7897/"));
        Check(emptyEnv.Environment["HTTPS_PROXY"] == "http://127.0.0.1:7897"
            && emptyEnv.Environment["HTTP_PROXY"] == "http://127.0.0.1:7897"
            && emptyEnv.Environment["https_proxy"] == "http://127.0.0.1:7897",
            "Missing proxy environment variables receive the resolved Windows system proxy.");

        var explicitEnv = new ProcessStartInfo("cmd.exe");
        foreach (var key in new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" })
            explicitEnv.Environment.Remove(key);
        explicitEnv.Environment["HTTPS_PROXY"] = "http://10.0.0.2:8080";
        UsageCliFallback.ApplySystemProxy(explicitEnv, probe, _ => new Uri("http://127.0.0.1:7897/"));
        Check(explicitEnv.Environment["HTTPS_PROXY"] == "http://10.0.0.2:8080"
            && !explicitEnv.Environment.ContainsKey("HTTP_PROXY"),
            "An explicit proxy environment variable is never overwritten by the system proxy fallback.");

        var directEnv = new ProcessStartInfo("cmd.exe");
        foreach (var key in new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" })
            directEnv.Environment.Remove(key);
        UsageCliFallback.ApplySystemProxy(directEnv, probe, uri => uri);
        Check(!directEnv.Environment.ContainsKey("HTTPS_PROXY"),
            "A direct route adds no proxy variables.");
    }

    private static async Task VerifyPersistentQualificationCacheAsync(string root)
    {
        var cacheDir = Path.Combine(root, "qual-cache");
        var paths = new UsagePaths(root, root, root) { CacheDirectory = cacheDir };
        var exe = Path.Combine(root, "agy", "bin", "agy.exe");
        var now = DateTimeOffset.Parse("2026-11-18T12:00:00Z");
        var versionCalls = 0;

        AntigravityLocalQuota CreateReader() => new((path, args, _) =>
        {
            if (args.SequenceEqual(["--version"]))
            {
                versionCalls++;
                return Task.FromResult<string?>("1.5.14");
            }
            return Task.FromResult<string?>(Quota);
        });

        try
        {
            var first = await CreateReader().ReadAsync(paths, now, default);
            Check(first is { Freshness: UsageFreshness.Current } && versionCalls == 1,
                "Initial read probes --version once and writes stamp cache.");

            var second = await CreateReader().ReadAsync(paths, now.AddMinutes(1), default);
            Check(second is { Freshness: UsageFreshness.Current } && versionCalls == 1,
                "A fresh instance reuses the cached --version when executable stamp matches.");

            File.WriteAllText(exe, "modified agy binary fixture payload");
            var afterUpdate = await CreateReader().ReadAsync(paths, now.AddMinutes(2), default);
            Check(afterUpdate is { Freshness: UsageFreshness.Current } && versionCalls == 2,
                "Changing the executable stamp invalidates the cached --version.");

            File.WriteAllText(Path.Combine(cacheDir, "agy-cli-qualification.json"), "{corrupt");
            var afterCorrupt = await CreateReader().ReadAsync(paths, now.AddMinutes(3), default);
            Check(afterCorrupt is { Freshness: UsageFreshness.Current } && versionCalls == 3,
                "A corrupt qualification cache file recovers cleanly without failing the quota read.");
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
