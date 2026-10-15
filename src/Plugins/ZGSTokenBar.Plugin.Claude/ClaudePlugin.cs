using ZGSTokenBar.Core;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.Claude;

public sealed class ClaudePlugin : BuiltinPluginBase, IDataSource, ILocalCredentialProbe
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private AppSettingsStore? _settingsStore;

    public override PluginManifest Manifest => new(
        1, "zgstokenbar.provider.claude", "1.0.0", 1, 0, PluginRuntime.Builtin,
        false, "claude", ["quota", "health", "settings", "commands", "local-credentials"],
        false, 100, [])
    {
        DisplayName = "Claude",
    };

    public override ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settingsStore = new AppSettingsStore(context.HostDataRoot ?? context.DataRoot);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PluginDataSnapshot> RefreshAsync(
        PluginRefreshContext context,
        CancellationToken cancellationToken)
    {
        var result = await new ClaudeQuotaService(_httpClient).FetchAsync(
            (_settingsStore ?? throw new InvalidOperationException("Plugin has not started.")).Load(),
            cancellationToken,
            allowOAuthRefresh: string.Equals(context.Reason, "manual", StringComparison.Ordinal));
        return CorePluginProjection.Provider(
            Manifest.Id,
            "claude",
            "provider.claude.icon",
            "accent.claude",
            result);
    }

    public ValueTask<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ClaudeQuotaService.HasLocalCredentials());
    }

    public override ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
