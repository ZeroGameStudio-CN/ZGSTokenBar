using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;
using ZGSTokenBar.Transport.NamedPipe;

namespace ZGSTokenBar.Cli;

internal sealed record AcceptanceCheck(string Id, bool Passed, string Detail);
internal sealed record AcceptanceResult(
    bool Passed,
    IReadOnlyList<AcceptanceCheck> Checks,
    IReadOnlyList<string> Artifacts);

internal static class IsolatedAcceptance
{
    public static async Task<AcceptanceResult> RunAsync(
        string artifactsDirectory,
        CancellationToken cancellationToken)
    {
        var artifacts = Path.GetFullPath(artifactsDirectory);
        Directory.CreateDirectory(artifacts);
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            $"zgstokenbar-acceptance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        var checks = new List<AcceptanceCheck>();
        var outputFiles = new List<string>();
        var plugin = new AcceptancePlugin();
        var profile = new EffectiveProfile(
            1,
            "headless",
            ["acceptance"],
            [
                new(
                    plugin.Manifest.Id,
                    plugin.Manifest.Version,
                    true,
                    0,
                    new Dictionary<string, JsonElement>(StringComparer.Ordinal)),
            ]);
        var desktop = new AcceptanceDesktop();
        await using var host = new ZgsTokenBarHost(
            [plugin],
            profile,
            "acceptance",
            dataRoot,
            desktop);
        var pipeName = $"ZGSTokenBar.acceptance.{Guid.NewGuid():N}";
        await using var server = new ZgsNamedPipeServer(host, pipeName);
        try
        {
            await host.StartAsync(cancellationToken);
            server.Start();
            var client = new ZgsNamedPipeClient(pipeName);

            var describe = await client.InvokeAsync(
                Request("host.describe"),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            checks.Add(new(
                "pipe.describe",
                describe.Ok,
                describe.Ok ? "versioned host response received" : describe.Error?.Code ?? "failed"));
            outputFiles.Add(WriteArtifact(
                artifacts,
                "host-describe.json",
                describe,
                ApiJsonContext.Default.ApiResponseEnvelope));

            var refreshed = await client.InvokeAsync(
                Request(
                    "plugin.refresh",
                    Object(("pluginId", plugin.Manifest.Id))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            var snapshot = host.Snapshot(true);
            checks.Add(new(
                "plugin.refresh",
                refreshed.Ok
                    && snapshot.Plugins.Single().DataRevision == 1
                    && snapshot.Plugins.Single().Cards?.Count == 1,
                "fake clock/data source committed one immutable snapshot"));

            var mini = await client.InvokeAsync(
                Request("ui.mini.get"),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            var initial = mini.Result?.Deserialize(ApiJsonContext.Default.MiniState);
            var collapse = await client.InvokeAsync(
                Request(
                    "ui.mini.setCollapsed",
                    Object(
                        ("collapsed", true),
                        ("expectedUiRevision", initial?.UiRevision ?? -1))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            var mutation = collapse.Result?.Deserialize(ApiJsonContext.Default.MiniMutationResult);
            checks.Add(new(
                "mini.collapse",
                collapse.Ok
                    && mutation is { Collapsed: true, AnchorPreserved: true, Persisted: true },
                "idempotent desktop control preserved its anchor"));

            var conflict = await client.InvokeAsync(
                Request(
                    "ui.mini.setCollapsed",
                    Object(
                        ("collapsed", false),
                        ("expectedUiRevision", initial?.UiRevision ?? -1))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            checks.Add(new(
                "revision.conflict",
                !conflict.Ok && conflict.Error?.Code == "state_conflict",
                "stale uiRevision was rejected"));

            var areaMutationResponse = await client.InvokeAsync(
                Request(
                    "ui.mini.setArea",
                    Object(
                        ("areaId", MiniAreaIds.Codex),
                        ("collapsed", false),
                        ("width", 180),
                        ("expectedUiRevision", mutation?.UiRevision ?? -1))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            var areaMutation = areaMutationResponse.Result?.Deserialize(
                ApiJsonContext.Default.MiniMutationResult);
            checks.Add(new(
                "mini.area-layout",
                areaMutationResponse.Ok
                    && areaMutation is { AreaId: MiniAreaIds.Codex, Width: 180 }
                    && areaMutation.Areas.Single(area => area.AreaId == MiniAreaIds.Claude).Collapsed
                    && !areaMutation.Areas.Single(area => area.AreaId == MiniAreaIds.Codex).Collapsed
                    && areaMutation.Areas.Single(area => area.AreaId == MiniAreaIds.SystemMetrics).Collapsed,
                "one area expanded and resized without changing its sibling"));

            var reorderResponse = await client.InvokeAsync(
                Request(
                    "ui.mini.moveArea",
                    Object(
                        ("areaId", MiniAreaIds.Codex),
                        ("beforeAreaId", MiniAreaIds.Claude),
                        ("expectedUiRevision", areaMutation?.UiRevision ?? -1))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            var reorderMutation = reorderResponse.Result?.Deserialize(
                ApiJsonContext.Default.MiniMutationResult);
            checks.Add(new(
                "mini.area-order",
                reorderResponse.Ok
                    && reorderMutation is { AreaId: MiniAreaIds.Codex, AnchorPreserved: true, Persisted: true }
                    && reorderMutation.Areas.Take(2).Select(area => area.AreaId).SequenceEqual(
                        [MiniAreaIds.Codex, MiniAreaIds.Claude],
                        StringComparer.Ordinal),
                "provider modules reordered without moving the Mini anchor"));

            var systemMutationResponse = await client.InvokeAsync(
                Request(
                    "ui.mini.setArea",
                    Object(
                        ("areaId", MiniAreaIds.SystemMetrics),
                        ("collapsed", false),
                        ("width", 120),
                        ("expectedUiRevision", reorderMutation?.UiRevision ?? -1))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            var systemMutation = systemMutationResponse.Result?.Deserialize(
                ApiJsonContext.Default.MiniMutationResult);
            checks.Add(new(
                "mini.system-area-layout",
                systemMutationResponse.Ok
                    && systemMutation is { AreaId: MiniAreaIds.SystemMetrics, Width: 120 }
                    && systemMutation.Areas.Single(area => area.AreaId == MiniAreaIds.Claude).Collapsed
                    && systemMutation.Areas.Single(area => area.AreaId == MiniAreaIds.Codex).Width == 180
                    && !systemMutation.Areas.Single(area => area.AreaId == MiniAreaIds.SystemMetrics).Collapsed,
                "system metrics expanded and resized without changing provider areas"));

            var systemReorderResponse = await client.InvokeAsync(
                Request(
                    "ui.mini.moveArea",
                    Object(
                        ("areaId", MiniAreaIds.SystemMetrics),
                        ("beforeAreaId", MiniAreaIds.Codex),
                        ("expectedUiRevision", systemMutation?.UiRevision ?? -1))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            var systemReorderMutation = systemReorderResponse.Result?.Deserialize(
                ApiJsonContext.Default.MiniMutationResult);
            checks.Add(new(
                "mini.system-area-order",
                systemReorderResponse.Ok
                    && systemReorderMutation is { AreaId: MiniAreaIds.SystemMetrics, AnchorPreserved: true, Persisted: true }
                    && systemReorderMutation.Areas.Take(2).Select(area => area.AreaId).SequenceEqual(
                        [MiniAreaIds.SystemMetrics, MiniAreaIds.Codex],
                        StringComparer.Ordinal),
                "system metrics reordered without moving the Mini anchor"));

            var unknown = await client.InvokeAsync(
                Request("unknown.method"),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            checks.Add(new(
                "unknown.fail-closed",
                !unknown.Ok && unknown.Error?.Code == "unknown_method",
                "unknown methods fail closed"));

            var refreshDefault = await client.InvokeAsync(
                Request("app.requestRefresh"),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            var refreshReload = await client.InvokeAsync(
                Request("app.requestRefresh", Object(("reloadSettings", true))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            checks.Add(new(
                "app.refresh-settings",
                refreshDefault.Ok
                    && refreshReload.Ok
                    && desktop.RefreshReloadSettings.SequenceEqual([false, true]),
                "legacy refresh remains compatible and explicit reload reaches the desktop"));

            var invalidWatch = await client.InvokeAsync(
                new(
                    2,
                    Guid.NewGuid().ToString("N"),
                    "events.watch",
                    Object(("includeValues", true))),
                TimeSpan.FromSeconds(3),
                cancellationToken);
            checks.Add(new(
                "events.schema-fail-closed",
                !invalidWatch.Ok && invalidWatch.Error?.Code == "api_version_unsupported",
                "streaming transport validates the same versioned envelope"));

            checks.Add(new(
                "isolation.data-root",
                dataRoot.StartsWith(
                    Path.GetFullPath(Path.GetTempPath()),
                    StringComparison.OrdinalIgnoreCase),
                "data root is an internally-created OS temporary directory"));

            var processResult = await RunProcessPluginAcceptanceAsync(
                dataRoot,
                cancellationToken);
            checks.Add(new(
                "process.discovery",
                processResult.Discovered,
                "signed local package was discovered through the installed catalog"));
            checks.Add(new(
                "process.handshake-refresh",
                processResult.Handshake && processResult.Refresh,
                "process fixture completed identity handshake and refresh"));
            checks.Add(new(
                "process.credential-bridge",
                processResult.CredentialBridge,
                "declared credential slot stayed on the private stdio channel"));
            checks.Add(new(
                "process.error-isolation",
                processResult.ErrorIsolation,
                "plugin error stayed inside its refresh call"));
            checks.Add(new(
                "process.cancellation-isolation",
                processResult.CancellationIsolation,
                "caller cancellation recycled the process before the next request"));
            checks.Add(new(
                "process.timeout-isolation",
                processResult.TimeoutIsolation,
                "timed out plugin process was terminated by its job"));
            checks.Add(new(
                "process.digest-drift",
                processResult.DigestDriftDetected,
                "post-install file drift disabled trust"));
            outputFiles.Add(WriteArtifact(
                artifacts,
                "process-plugin.json",
                processResult,
                CliJsonContext.Default.ProcessAcceptanceArtifact));
        }
        finally
        {
            try
            {
                if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
            }
            catch
            {
                checks.Add(new("isolation.cleanup", false, "temporary data root could not be removed"));
            }
        }
        return new(checks.All(check => check.Passed), checks, outputFiles);
    }

    private static async Task<ProcessAcceptanceArtifact> RunProcessPluginAcceptanceAsync(
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var executable = Path.Combine(baseDirectory, "ZGSTokenBar.Cli.exe");
        if (!File.Exists(executable))
        {
            return new(
                "test.process-fixture",
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false);
        }
        var packagePath = Path.Combine(dataRoot, "process-fixture.zgsplugin");
        var sourceFiles = Directory.EnumerateFiles(baseDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                "plugin-manifest.v1.json",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(
                Path.GetExtension(path),
                ".pdb",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceFiles.Length is <= 0 or > PluginPackageManager.MaximumFiles)
        {
            return new(
                "test.process-fixture",
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false);
        }
        var files = sourceFiles
            .Select(path => new PluginPackageFile(
                Path.GetFileName(path),
                new FileInfo(path).Length,
                Digest(path)))
            .ToArray();
        var manifest = new PluginManifest(
            1,
            "test.process-fixture",
            "1.0.0",
            1,
            0,
            PluginRuntime.Process,
            false,
            "process-fixture",
            ["metric"],
            true,
            0,
            [])
        {
            DisplayName = "Process Fixture",
            Entrypoint = "ZGSTokenBar.Cli.exe",
            Files = files,
            CredentialSlots = ["fixture"],
            HandshakeTimeoutSeconds = 3,
            CallTimeoutSeconds = 1,
            DisposeTimeoutSeconds = 1,
        };
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("plugin-manifest.v1.json");
            await using (var output = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(
                    output,
                    manifest,
                    PluginSdkJsonContext.Default.PluginManifest,
                    cancellationToken);
            }
            foreach (var source in sourceFiles)
            {
                archive.CreateEntryFromFile(
                    source,
                    Path.GetFileName(source),
                    CompressionLevel.Optimal);
            }
        }

        var manager = new PluginPackageManager(dataRoot);
        var installed = manager.Install(packagePath, Digest(packagePath));
        var discovered = manager.LoadProcessPlugins(new AcceptanceCredentialBroker());
        var proxy = discovered.OfType<ProcessPluginProxy>().SingleOrDefault();
        if (proxy is null)
        {
            foreach (var plugin in discovered) await plugin.DisposeAsync();
            return new(
                manifest.Id,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false);
        }

        var handshake = false;
        var refresh = false;
        var errorIsolation = false;
        var credentialBridge = false;
        var timeoutIsolation = false;
        var cancellationIsolation = false;
        try
        {
            var pluginDataRoot = Path.Combine(dataRoot, "plugin-data", manifest.Id);
            var startContext = new PluginStartContext(
                "headless",
                pluginDataRoot,
                new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
            await proxy.StartAsync(startContext, cancellationToken);
            handshake = proxy.ProcessId is not null;
            var snapshot = await proxy.RefreshAsync(
                new(
                    new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                    "acceptance",
                    0),
                cancellationToken);
            refresh = snapshot.PluginId == manifest.Id && snapshot.MiniCards.Count == 1;
            var credentialSnapshot = await proxy.RefreshAsync(
                new(
                    new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                    "credential",
                    1),
                cancellationToken);
            credentialBridge = credentialSnapshot.PluginId == manifest.Id;
            try
            {
                await proxy.RefreshAsync(
                    new(
                        new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                        "error",
                        1),
                    cancellationToken);
            }
            catch (HostCommandException exception)
            {
                errorIsolation = exception.Code == "fixture_error"
                    && proxy.ProcessId is not null;
            }

            var cancellationMarker = Path.Combine(pluginDataRoot, "cancel-received");
            using (var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var cancelledProcessId = proxy.ProcessId;
                var cancelledCall = proxy.RefreshAsync(
                        new(
                            new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                            "cancel",
                            1),
                        callerCancellation.Token)
                    .AsTask();
                var markerDeadline = DateTime.UtcNow.AddSeconds(1);
                while (!File.Exists(cancellationMarker)
                       && !cancelledCall.IsCompleted
                       && DateTime.UtcNow < markerDeadline)
                {
                    await Task.Delay(10, cancellationToken);
                }
                callerCancellation.Cancel();
                var propagated = false;
                try
                {
                    await cancelledCall;
                }
                catch (OperationCanceledException)
                {
                    propagated = true;
                }
                cancellationIsolation = File.Exists(cancellationMarker)
                    && propagated
                    && proxy.ProcessId is null
                    && (cancelledProcessId is null || !ProcessExists(cancelledProcessId.Value));
            }
            await proxy.StartAsync(startContext, cancellationToken);
            var postCancellation = await proxy.RefreshAsync(
                new(
                    new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                    "after-cancel",
                    1),
                cancellationToken);
            cancellationIsolation = cancellationIsolation
                && postCancellation.PluginId == manifest.Id
                && proxy.ProcessId is not null;

            var processId = proxy.ProcessId;
            try
            {
                await proxy.RefreshAsync(
                    new(
                        new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                        "timeout",
                        1),
                    cancellationToken);
            }
            catch (HostCommandException exception)
            {
                timeoutIsolation = exception.Code == "timeout"
                    && proxy.ProcessId is null
                    && (processId is null || !ProcessExists(processId.Value));
            }
        }
        finally
        {
            foreach (var plugin in discovered) await plugin.DisposeAsync();
        }

        var driftTarget = Path.Combine(installed.Path, files[0].Path);
        await using (var stream = new FileStream(
                         driftTarget,
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.None))
        {
            await stream.WriteAsync(new byte[] { 0x00 }, cancellationToken);
        }
        var driftDetected = manager.InspectInstalled().Any(status =>
            status.PluginId == manifest.Id && !status.Valid);
        return new(
            manifest.Id,
            true,
            handshake,
            refresh,
            credentialBridge,
            errorIsolation,
            cancellationIsolation,
            timeoutIsolation,
            driftDetected);
    }

    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ApiRequestEnvelope Request(string method, JsonElement? parameters = null) =>
        new(1, Guid.NewGuid().ToString("N"), method, parameters);

    private static JsonElement Object(params (string Key, object Value)[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case string text: writer.WriteStringValue(text); break;
                    case bool boolean: writer.WriteBooleanValue(boolean); break;
                    case long number: writer.WriteNumberValue(number); break;
                    case int number: writer.WriteNumberValue(number); break;
                    default: throw new InvalidOperationException();
                }
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static string WriteArtifact<T>(
        string directory,
        string name,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, typeInfo),
            System.Text.Encoding.UTF8);
        return path;
    }

    private sealed class AcceptancePlugin : BuiltinPluginBase, IDataSource
    {
        private static readonly DateTimeOffset FixedTime =
            new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

        public override PluginManifest Manifest => new(
            1,
            "test.fixture",
            "1.0.0",
            1,
            0,
            PluginRuntime.Builtin,
            false,
            "fixture",
            ["metric"],
            true,
            0,
            []);

        public ValueTask<PluginDataSnapshot> RefreshAsync(
            PluginRefreshContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PluginDataSnapshot(
                Manifest.Id,
                FixedTime,
                new(
                    PluginHealthCode.Current,
                    true,
                    false,
                    FixedTime,
                    "fixture.current"),
                [
                    new(
                        "card.fixture",
                        Manifest.Id,
                        "fixture",
                        ContributionKind.Metric,
                        0,
                        "fixture.title",
                        "fixture.icon",
                        "accent.fixture",
                        [new("fixture.value", new("integer", Integer: 42))]),
                ],
                [],
                []));
        }
    }

    private sealed class AcceptanceDesktop : IDesktopControl
    {
        private bool _claudeCollapsed;
        private bool _codexCollapsed;
        private bool _systemCollapsed;
        private int _codexWidth = 144;
        private int _systemWidth = TaskbarMiniLayoutMath.SystemUsageContentWidth;
        private readonly List<string> _areaOrder = [
            MiniAreaIds.Claude,
            MiniAreaIds.Codex,
            MiniAreaIds.SystemMetrics,
        ];
        private long _uiRevision;
        public List<bool> RefreshReloadSettings { get; } = [];

        public ValueTask PersistPluginEnabledAsync(
            string pluginId,
            bool enabled,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<MiniState> GetMiniStateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MiniState(
                AllCollapsed,
                true,
                Bounds,
                "20,30",
                _uiRevision,
                Areas()));

        public ValueTask<MiniMutationResult> SetMiniCollapsedAsync(
            bool collapsed,
            long expectedUiRevision,
            CancellationToken cancellationToken)
        {
            if (expectedUiRevision != _uiRevision)
            {
                throw HostCommandException.Conflict("ui", _uiRevision);
            }
            var before = Bounds;
            if (_claudeCollapsed != collapsed || _codexCollapsed != collapsed || _systemCollapsed != collapsed)
            {
                _claudeCollapsed = collapsed;
                _codexCollapsed = collapsed;
                _systemCollapsed = collapsed;
                _uiRevision++;
            }
            var after = Bounds;
            return ValueTask.FromResult(new MiniMutationResult(
                _uiRevision,
                _uiRevision,
                AllCollapsed,
                before,
                after,
                before.X == after.X && before.Y == after.Y,
                true,
                null,
                null,
                Areas()));
        }

        public ValueTask<MiniMutationResult> SetMiniAreaAsync(
            string areaId,
            bool? collapsed,
            int? width,
            long expectedUiRevision,
            CancellationToken cancellationToken)
        {
            if (expectedUiRevision != _uiRevision)
            {
                throw HostCommandException.Conflict("ui", _uiRevision);
            }
            if (areaId is not (MiniAreaIds.Claude or MiniAreaIds.Codex or MiniAreaIds.SystemMetrics))
            {
                throw new HostCommandException("area_not_found", "Mini area was not found.");
            }
            var before = Bounds;
            var beforeArea = Areas().Single(area => area.AreaId == areaId);
            if (areaId == MiniAreaIds.Claude)
            {
                _claudeCollapsed = collapsed ?? _claudeCollapsed;
            }
            else if (areaId == MiniAreaIds.Codex)
            {
                _codexCollapsed = collapsed ?? _codexCollapsed;
                _codexWidth = width ?? _codexWidth;
            }
            else
            {
                _systemCollapsed = collapsed ?? _systemCollapsed;
                _systemWidth = width ?? _systemWidth;
            }
            var afterArea = Areas().Single(area => area.AreaId == areaId);
            if (beforeArea != afterArea) _uiRevision++;
            var after = Bounds;
            return ValueTask.FromResult(new MiniMutationResult(
                _uiRevision,
                _uiRevision,
                AllCollapsed,
                before,
                after,
                before.X == after.X && before.Y == after.Y,
                true,
                areaId,
                afterArea.Width,
                Areas()));
        }

        public ValueTask<MiniMutationResult> MoveMiniAreaAsync(
            string areaId,
            string? beforeAreaId,
            long expectedUiRevision,
            CancellationToken cancellationToken)
        {
            if (expectedUiRevision != _uiRevision)
            {
                throw HostCommandException.Conflict("ui", _uiRevision);
            }
            if (!_areaOrder.Contains(areaId)
                || (beforeAreaId is not null
                    && !string.Equals(beforeAreaId, areaId, StringComparison.Ordinal)
                    && !_areaOrder.Contains(beforeAreaId)))
            {
                throw new HostCommandException("area_not_found", "Mini area was not found.");
            }
            var before = Bounds;
            if (!string.Equals(areaId, beforeAreaId, StringComparison.Ordinal))
            {
                var original = _areaOrder.ToArray();
                _areaOrder.Remove(areaId);
                var insertion = beforeAreaId is null
                    ? _areaOrder.Count
                    : _areaOrder.IndexOf(beforeAreaId);
                _areaOrder.Insert(insertion, areaId);
                if (!original.SequenceEqual(_areaOrder, StringComparer.Ordinal)) _uiRevision++;
            }
            var after = Bounds;
            var area = Areas().Single(item => item.AreaId == areaId);
            return ValueTask.FromResult(new MiniMutationResult(
                _uiRevision,
                _uiRevision,
                AllCollapsed,
                before,
                after,
                before.X == after.X && before.Y == after.Y,
                true,
                areaId,
                area.Width,
                Areas()));
        }

        private bool AllCollapsed => _claudeCollapsed && _codexCollapsed && _systemCollapsed;
        private UiBounds Bounds => new(
            20,
            30,
            20
                + TaskbarMiniLayoutMath.AreaWidth(144, _claudeCollapsed)
                + TaskbarMiniLayoutMath.AreaWidth(_codexWidth, _codexCollapsed)
                + TaskbarMiniLayoutMath.AreaWidth(_systemWidth, _systemCollapsed),
            42);
        private MiniAreaState[] Areas() =>
        [
            .. _areaOrder.Select(areaId => areaId switch
            {
                MiniAreaIds.Claude => new MiniAreaState(MiniAreaIds.Claude, "Claude", _claudeCollapsed, 144, 88, 240),
                MiniAreaIds.Codex => new MiniAreaState(MiniAreaIds.Codex, "Codex", _codexCollapsed, _codexWidth, 88, 240),
                _ => new MiniAreaState(MiniAreaIds.SystemMetrics, "System usage", _systemCollapsed, _systemWidth, 88, 240),
            }),
        ];

        public ValueTask<WindowInspection> InspectWindowAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WindowInspection(
                true,
                Environment.ProcessId,
                null,
                true,
                Bounds,
                true,
                96));

        public ValueTask RequestRefreshAsync(
            bool reloadSettings,
            CancellationToken cancellationToken)
        {
            RefreshReloadSettings.Add(reloadSettings);
            return ValueTask.CompletedTask;
        }

        public ValueTask OpenSettingsAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask RequestExitAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class AcceptanceCredentialBroker : IPluginCredentialBroker
    {
        public ValueTask<string?> ResolveAsync(
            string pluginId,
            string slot,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(
                pluginId == "test.process-fixture" && slot == "fixture"
                    ? "fixture-secret"
                    : null);
    }
}
