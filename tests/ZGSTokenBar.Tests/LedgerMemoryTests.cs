using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ZGSTokenBar.Core;

internal static class LedgerMemoryTests
{
    private static readonly AppSettingsJsonContext Context = new(new JsonSerializerOptions
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true });

    internal static void Run()
    {
        using var fixture = new Fixture();
        foreach (var header in new[] { "", "\"schemaVersion\":1,", "\"schemaVersion\":9," })
        {
            File.WriteAllText(fixture.Store.CodexTokenUsageIndexPath, "{" + header + "\"files\":[],\"conversationHistory\":[]}");
            if (!fixture.Store.TryLoadCodexTokenUsageIndex(out var index) || index.SchemaVersion != CodexTokenUsageIndex.CurrentSchemaVersion)
                throw new InvalidOperationException("Legacy and current index headers remain readable.");
        }
        foreach (var header in new[] { "\"schemaVersion\":0", "\"schemaVersion\":999", "\"SchemaVersion\":999", "\"schemaVersion\":\"9\"", "\"schemaVersion\":null",
                     "\"schemaVersion\":999,\"SchemaVersion\":9", "\"schemaVersion\":999,\"schemaVersion\":9" })
        {
            using var invalid = new Fixture();
            var original = "{" + header + ",\"files\":[]}";
            File.WriteAllText(invalid.Store.CodexTokenUsageIndexPath, original);
            invalid.Store.LoadCodexTokenUsageIndex();
            if (File.ReadAllText(invalid.Store.CodexTokenUsageIndexPath + ".corrupt.bak") != original)
                throw new InvalidOperationException("Unsupported headers retain the original bytes as recovery evidence.");
        }
    }

    // Run baseline and optimized modes in separate fresh processes, on the same
    // private snapshot. No forced GC or file writes target the running App.
    internal static void Probe(string mode, string sourceCopy, string output)
    {
        if (mode is not ("baseline" or "optimized") || !Path.GetFullPath(sourceCopy).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a baseline/optimized probe on an OS-temporary copy.");
        using var fixture = new Fixture();
        File.Copy(sourceCopy, fixture.Store.CodexTokenUsageIndexPath);
        CodexTokenUsageIndex Load() => mode == "baseline" ? PreviousLoad(fixture.Store.CodexTokenUsageIndexPath) : fixture.Store.LoadCodexTokenUsageIndex();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        var index = Load(); var coldMilliseconds = watch.Elapsed.TotalMilliseconds;
        var coldAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
        for (var i = 0; i < 3; i++) index = Load();
        var warmMilliseconds = watch.Elapsed.TotalMilliseconds / 3;
        var warmAllocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 3;
        var retained = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(index);
        using var process = Process.GetCurrentProcess();
        var privateBytes = process.PrivateMemorySize64; var peakWorkingSetBytes = process.PeakWorkingSet64;
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(index, Context.CodexTokenUsageIndex)));
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            mode, inputBytes = new FileInfo(sourceCopy).Length, files = index.Files.Count, turns = index.ConversationHistory.Count,
            coldMilliseconds, warmMilliseconds, coldAllocatedBytes = coldAllocated, warmAllocatedBytes = warmAllocated,
            retainedManagedBytes = retained, privateBytes, peakWorkingSetBytes,
            normalizedIndexSha256 = digest,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static CodexTokenUsageIndex PreviousLoad(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var version = document.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.TryGetInt32(out var value) ? value : 1;
        if (version is < 1 or > CodexTokenUsageIndex.CurrentSchemaVersion) throw new JsonException();
        var index = JsonSerializer.Deserialize(json, Context.CodexTokenUsageIndex) ?? throw new JsonException();
        if (index.HistoricalBaselineTokens is < 0 || (index.HistoricalBaselineTokens is null) != (index.HistoricalBaselineAt is null)) throw new JsonException();
        index.Files ??= []; index.ConversationHistory ??= []; index.SchemaVersion = CodexTokenUsageIndex.CurrentSchemaVersion;
        return index;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("tokenbar-ledger-memory-").FullName;
        internal AppSettingsStore Store { get; }
        internal Fixture() => Store = new(_root);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
