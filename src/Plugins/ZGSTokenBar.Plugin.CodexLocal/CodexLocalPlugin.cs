using ZGSTokenBar.Core;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.CodexLocal;

public sealed class CodexLocalPlugin : BuiltinPluginBase, IDataSource
{
    private readonly AppSettingsStore _store = new();
    private CodexTokenUsageReader? _reader;

    public override PluginManifest Manifest => new(
        1, "zgstokenbar.usage.codex-local", "1.0.0", 1, 0, PluginRuntime.Builtin,
        false, "codex-usage", ["usage", "details", "cache"],
        false, 120, ["zgstokenbar.provider.codex"])
    {
        DisplayName = "Codex Local Usage",
    };

    public ValueTask<PluginDataSnapshot> RefreshAsync(
        PluginRefreshContext context,
        CancellationToken cancellationToken)
    {
        _reader ??= new CodexTokenUsageReader(_store.LoadCodexTokenUsageIndex());
        var result = _reader.Refresh(context.Now, cancellationToken);
        _reader = new CodexTokenUsageReader(result.Index);
        if (result.Changed)
        {
            try
            {
                _store.SaveCodexTokenUsageIndex(result.Index);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A busy cache must not hide a valid local usage snapshot.
            }
        }
        return ValueTask.FromResult(CorePluginProjection.CodexUsage(
            Manifest.Id,
            result.Summary,
            context.Now));
    }
}
