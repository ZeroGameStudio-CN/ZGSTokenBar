using ZGSTokenBar.Core;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.Claude;

public sealed class ClaudePlugin : BuiltinPluginBase, IDataSource
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly AppSettingsStore _settingsStore = new();

    public override PluginManifest Manifest => new(
        1, "zgstokenbar.provider.claude", "1.0.0", 1, 0, PluginRuntime.Builtin,
        false, "claude", ["quota", "health", "settings", "commands"],
        true, 100, [])
    {
        DisplayName = "Claude",
    };

    public async ValueTask<PluginDataSnapshot> RefreshAsync(
        PluginRefreshContext context,
        CancellationToken cancellationToken)
    {
        var result = await new ClaudeQuotaService(_httpClient).FetchAsync(
            _settingsStore.Load(),
            cancellationToken,
            allowOAuthRefresh: string.Equals(context.Reason, "manual", StringComparison.Ordinal));
        return CorePluginProjection.Provider(
            Manifest.Id,
            "claude",
            "provider.claude.icon",
            "accent.claude",
            result);
    }

    public override ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
