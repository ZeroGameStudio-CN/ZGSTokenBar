using System.Text.Json;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;

internal static class PluginLifecycleRegressionTests
{
    internal static void Run()
    {
        DisableWhileRefreshingAsync().GetAwaiter().GetResult();
        CancelDisableAsync().GetAwaiter().GetResult();
    }

    private static async Task DisableWhileRefreshingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zgstokenbar-plugin-lifecycle-{Guid.NewGuid():N}");
        var plugin = new BlockingSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var host = CreateHost(plugin, root);
            await host.StartAsync(timeout.Token);
            var first = host.RefreshPluginAsync(plugin.Manifest.Id, "first", timeout.Token).AsTask();
            await plugin.Entered.Task.WaitAsync(timeout.Token);
            var queued = host.RefreshPluginAsync(plugin.Manifest.Id, "queued", timeout.Token).AsTask();
            var disable = host.SetEnabledAsync(plugin.Manifest.Id, false, 0, timeout.Token).AsTask();
            try
            {
                Require(!host.DescribePlugin(plugin.Manifest.Id)!.Enabled, "disable immediately rejects new work");
                Require(!disable.IsCompleted, "stop waits for the running refresh to leave its lifecycle gate");
            }
            finally { plugin.Release.TrySetResult(); }
            Require(await ErrorCodeAsync(first) == "plugin_disabled", "an in-flight result cannot publish after disable starts");
            Require(await ErrorCodeAsync(queued) == "plugin_disabled", "a queued refresh rechecks enablement before execution");
            await disable;
            Require(plugin.RefreshCalls == 1 && plugin.RefreshCallsAfterStop == 0,
                "the queued refresh never invokes the disabled provider");
            Require(!plugin.StoppedWhileRefreshing, "stop and refresh never overlap");
            var disabled = host.Snapshot(true).Plugins.Single();
            Require(!disabled.Enabled && disabled.Health.Code == PluginHealthCode.Disabled,
                "the final host snapshot remains disabled");
            var revision = disabled.DataRevision;
            host.Publish(plugin.Success(DateTimeOffset.UtcNow));
            Require(host.Snapshot(true).Plugins.Single().DataRevision == revision,
                "a late external publication cannot resurrect a disabled plugin");

            plugin.Block = false;
            await host.SetEnabledAsync(plugin.Manifest.Id, true, host.Describe().Revisions.ConfigRevision, timeout.Token);
            await host.RefreshPluginAsync(plugin.Manifest.Id, "re-enabled", timeout.Token);
            Require(host.DescribePlugin(plugin.Manifest.Id) is { Enabled: true, Health.Code: PluginHealthCode.Current },
                "re-enabling starts the plugin before allowing refresh");
            Require(plugin.StartCalls == 2, "re-enabling performs exactly one new start");
        }
        finally
        {
            plugin.Release.TrySetResult();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CancelDisableAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"zgstokenbar-plugin-cancel-{Guid.NewGuid():N}");
        var plugin = new BlockingSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var host = CreateHost(plugin, root);
            await host.StartAsync(timeout.Token);
            var refresh = host.RefreshPluginAsync(plugin.Manifest.Id, "active", timeout.Token).AsTask();
            await plugin.Entered.Task.WaitAsync(timeout.Token);
            using var cancel = new CancellationTokenSource();
            var disable = host.SetEnabledAsync(plugin.Manifest.Id, false, 0, cancel.Token).AsTask();
            cancel.Cancel();
            var cancelled = false;
            try { await disable; }
            catch (OperationCanceledException) { cancelled = true; }
            finally { plugin.Release.TrySetResult(); }
            await refresh;
            Require(cancelled && host.DescribePlugin(plugin.Manifest.Id)!.Enabled,
                "cancelling a waiting disable restores the original enablement");
            Require(plugin.StopCalls == 0 && host.Describe().Revisions.ConfigRevision == 0,
                "cancelled disable does not stop the plugin or commit a configuration revision");
            await host.SetEnabledAsync(plugin.Manifest.Id, false, 0, timeout.Token);
            Require(host.DescribePlugin(plugin.Manifest.Id)!.Health.Code == PluginHealthCode.Disabled,
                "a later disable still owns and releases the lifecycle gate");
        }
        finally
        {
            plugin.Release.TrySetResult();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ZgsTokenBarHost CreateHost(BlockingSource plugin, string root) => new(
        [plugin],
        new EffectiveProfile(1, "test", [],
            [new ProfilePlugin(plugin.Manifest.Id, plugin.Manifest.Version, true, 0, new Dictionary<string, JsonElement>())]),
        "test", root, persistProfileState: false);

    private static async Task<string?> ErrorCodeAsync(Task<PluginDataSnapshot> task)
    {
        try { await task; return null; }
        catch (HostCommandException exception) { return exception.Code; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BlockingSource : BuiltinPluginBase, IDataSource
    {
        public override PluginManifest Manifest { get; } = new(1, "test.lifecycle.source", "1.0.0", 1, 0,
            PluginRuntime.Builtin, false, "lifecycle-source", ["metric"], true, 0, []);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; set; } = true;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public int RefreshCallsAfterStop { get; private set; }
        public bool StoppedWhileRefreshing { get; private set; }
        private bool _stopped;
        private bool _refreshing;

        public override ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
        {
            StartCalls++;
            _stopped = false;
            return ValueTask.CompletedTask;
        }

        public override ValueTask StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            _stopped = true;
            StoppedWhileRefreshing |= _refreshing;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<PluginDataSnapshot> RefreshAsync(PluginRefreshContext context, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            if (_stopped) RefreshCallsAfterStop++;
            _refreshing = true;
            try
            {
                if (Block)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(cancellationToken);
                }
                return Success(context.Now);
            }
            finally { _refreshing = false; }
        }

        public PluginDataSnapshot Success(DateTimeOffset now) => new(Manifest.Id, now,
            new PluginHealth(PluginHealthCode.Current, true, false, now, "lifecycle.current"), [], [], []);
    }
}
