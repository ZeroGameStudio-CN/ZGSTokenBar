using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Host;

public interface IDesktopControl
{
    ValueTask PersistPluginEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken);
    ValueTask<MiniState> GetMiniStateAsync(CancellationToken cancellationToken);
    ValueTask<MiniMutationResult> SetMiniCollapsedAsync(
        bool collapsed,
        long expectedUiRevision,
        CancellationToken cancellationToken);
    ValueTask<MiniMutationResult> SetMiniAreaAsync(
        string areaId,
        bool? collapsed,
        int? width,
        long expectedUiRevision,
        CancellationToken cancellationToken);
    ValueTask<MiniMutationResult> MoveMiniAreaAsync(
        string areaId,
        string? beforeAreaId,
        long expectedUiRevision,
        CancellationToken cancellationToken);
    ValueTask<WindowInspection> InspectWindowAsync(CancellationToken cancellationToken);
    ValueTask<CodexHistoryExportChunk> ReadCodexHistoryAsync(CodexHistoryExportRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<CodexHistoryExportChunk>(new HostCommandException("command_requires_desktop", "History export requires the native desktop."));
    ValueTask RequestRefreshAsync(bool reloadSettings, CancellationToken cancellationToken);
    ValueTask OpenSettingsAsync(CancellationToken cancellationToken);
    ValueTask SetRouterVisibleAsync(bool visible, CancellationToken cancellationToken) =>
        ValueTask.FromException(new HostCommandException("command_requires_desktop", "Router view requires the native desktop."));
    ValueTask RequestExitAsync(CancellationToken cancellationToken);
}
