using ZGSTokenBar.Core;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.Codex;

public sealed class CodexPlugin : BuiltinPluginBase, IDataSource
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    public override PluginManifest Manifest => new(
        1, "zgstokenbar.provider.codex", "1.0.0", 1, 0, PluginRuntime.Builtin,
        false, "codex", ["quota", "health", "accounts", "settings", "commands"],
        true, 110, [])
    {
        DisplayName = "Codex",
    };

    public async ValueTask<PluginDataSnapshot> RefreshAsync(
        PluginRefreshContext context,
        CancellationToken cancellationToken)
    {
        var result = await new CodexQuotaService(_httpClient).FetchAsync(cancellationToken);
        return CorePluginProjection.Provider(
            Manifest.Id,
            "codex",
            "provider.codex.icon",
            "accent.codex",
            result);
    }

    public override ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
