using ZGSTokenBar.Core;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.CodexLocal;

public sealed class CodexLocalPlugin : BuiltinPluginBase, IDataSource
{
    private CodexTokenUsageReader _reader = new();

    public override PluginManifest Manifest => new(
        1, "zgstokenbar.usage.codex-local", "1.0.0", 1, 0, PluginRuntime.Builtin,
        false, "codex-usage", ["usage", "details", "cache"],
        true, 120, ["zgstokenbar.provider.codex"])
    {
        DisplayName = "Codex Local Usage",
    };

    public ValueTask<PluginDataSnapshot> RefreshAsync(
        PluginRefreshContext context,
        CancellationToken cancellationToken)
    {
        var result = _reader.Refresh(context.Now, cancellationToken);
        _reader = new CodexTokenUsageReader(result.Index);
        return ValueTask.FromResult(CorePluginProjection.CodexUsage(
            Manifest.Id,
            result.Summary,
            context.Now));
    }
}
