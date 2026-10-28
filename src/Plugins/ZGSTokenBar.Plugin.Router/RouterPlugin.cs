using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.Router;

public sealed class RouterPlugin : BuiltinPluginBase, IDataSource
{
    public const string Id = "zgstokenbar.monitor.router";
    private readonly RouterMonitorClient _client;
    private bool _seen;
    public RouterPlugin() : this(new RouterMonitorClient()) { }
    public RouterPlugin(RouterMonitorClient client) => _client = client;
    public override PluginManifest Manifest => new(1, Id, "1.0.0", 1, 0, PluginRuntime.Builtin,
        false, "router", ["router-monitor", "details"], false, 50, []) { DisplayName = "Router" };

    public async ValueTask<PluginDataSnapshot> RefreshAsync(PluginRefreshContext context, CancellationToken cancellationToken)
    {
        string state, detail;
        string iconState = "offline";
        string project = "", phaseLabel = "状态未确认", elapsed = "";
        var waiting = 0;
        var connected = false;
        try
        {
            var snapshot = await _client.SnapshotAsync(cancellationToken: cancellationToken);
            _seen = true;
            connected = snapshot.IsFresh(DateTimeOffset.UtcNow);
            state = connected ? RouterJson.Text(snapshot.View, "headline") : "数据过期";
            detail = RouterJson.Text(snapshot.View, "task");
            waiting = RouterJson.Integer(snapshot.View, "queuedCount");
            var phase = RouterJson.Text(RouterJson.Field(snapshot.View, "currentExecution"), "phase");
            project = snapshot.Projects.EnumerateArray().Where(row => RouterJson.Text(row, "key") == snapshot.ProjectKey)
                .Select(row => RouterJson.Text(row, "name")).FirstOrDefault() ?? "Router";
            iconState = !connected ? "offline" : phase switch
            {
                "blocked" => "attention",
                "settling" => "cleanup",
                "running" => RouterJson.Text(snapshot.View, "progressState") switch
                { "unconfirmed" => "unknown", "native_error" => "attention", _ => "running" },
                _ => waiting > 0 || RouterJson.Field(snapshot.View, "waitingForEditor").ValueKind == System.Text.Json.JsonValueKind.Object ? "waiting" : "idle",
            };
            phaseLabel = !connected ? "状态未确认" : iconState switch
            {
                "cleanup" => "清理", "attention" => phase == "blocked" ? "待处理" : "有错误", "waiting" => "等待", "idle" => "空闲", "unknown" => "进度未确认",
                _ => RouterJson.Text(RouterJson.Field(snapshot.View, "currentExecution"), "kind") switch
                { "compile" => "编译", "test" => "测试", _ => "脚本" },
            };
            if (connected && RouterJson.Field(snapshot.View, "currentExecution").ValueKind == System.Text.Json.JsonValueKind.Object)
                elapsed = RouterJson.Text(snapshot.View, "elapsed");
            if (waiting > 0) state += $" · 等待 {waiting}";
        }
        catch (RouterCompatibilityException error) { _seen = true; state = "接口待更新"; detail = error.Message; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        { state = "连接中断"; detail = "状态未确认，原任务继续运行。"; }
        var summary = new ContributionSummaryItem[]
        {
            new("router.state", new("text", Text: state), iconState),
            new("router.task", new("text", Text: detail)),
            new("router.queued", new("integer", Integer: waiting)),
            new("router.project", new("text", Text: project)),
            new("router.phase", new("text", Text: phaseLabel)),
            new("router.elapsed", new("text", Text: elapsed)),
        };
        return new(Id, context.Now, new(connected ? PluginHealthCode.Current : PluginHealthCode.Unavailable,
            connected, true, context.Now, connected ? "router.current" : "router.unavailable"),
            _seen ? [new("card.router", Id, "router", ContributionKind.Metric, 0, "Router", "router.icon", "accent.router", summary)] : [],
            [], []);
    }

    public override async ValueTask DisposeAsync() { _client.Dispose(); await base.DisposeAsync(); }
}
