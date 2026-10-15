using ZGSTokenBar.Core;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Plugin.CodexLocal;

public sealed class CodexLocalPlugin : BuiltinPluginBase, IDataSource
{
    private readonly Func<AppSettingsStore, CodexTokenUsageLedger> _createLedger;
    private CodexTokenUsageLedger? _ledger;
    private string? _dataRoot;

    public CodexLocalPlugin() : this(store => new CodexTokenUsageLedger(store)) { }

    internal CodexLocalPlugin(Func<AppSettingsStore, CodexTokenUsageLedger> createLedger) =>
        _createLedger = createLedger;

    public override PluginManifest Manifest => new(
        1, "zgstokenbar.usage.codex-local", "1.0.0", 1, 0, PluginRuntime.Builtin,
        false, "codex-usage", ["usage", "details", "cache"],
        false, 120, ["zgstokenbar.provider.codex"])
    {
        DisplayName = "Codex Local Usage",
    };

    public override ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataRoot = Path.GetFullPath(context.HostDataRoot ?? context.DataRoot);
        if (!string.Equals(_dataRoot, dataRoot, StringComparison.OrdinalIgnoreCase))
        {
            _ledger = _createLedger(new AppSettingsStore(dataRoot));
            _dataRoot = dataRoot;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginDataSnapshot> RefreshAsync(
        PluginRefreshContext context,
        CancellationToken cancellationToken)
    {
        var ledger = _ledger ?? throw new InvalidOperationException("Plugin has not started.");
        var summary = ledger.Refresh(context.Now, cancellationToken);
        return ValueTask.FromResult(CorePluginProjection.CodexUsage(
            Manifest.Id,
            summary,
            context.Now));
    }
}
