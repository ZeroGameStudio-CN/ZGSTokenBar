using ZGSTokenBar.Host;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

internal sealed class RouterModule : IDisposable
{
    private readonly BarForm _bar;
    private readonly ZgsTokenBarHost _host;
    private readonly Func<bool> _enabled;
    private readonly CancellationToken _shutdown;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private RouterMonitorForm? _window;
    private bool _refreshing;
    public bool Visible => _window is { Visible: true, IsDisposed: false };
    public RouterSnapshot? Snapshot => _window is { IsDisposed: false } ? _window.Snapshot : null;

    public RouterModule(BarForm bar, ZgsTokenBarHost host, Func<bool> enabled, CancellationToken shutdown)
    {
        (_bar, _host, _enabled, _shutdown) = (bar, host, enabled, shutdown);
        _bar.PluginPopoverRequested += Requested;
        _timer.Tick += async (_, _) =>
        {
            if (_shutdown.IsCancellationRequested) return;
            if (!_enabled()) { _bar.HidePluginPopover(); _window?.Close(); return; }
            if (_refreshing) return;
            _refreshing = true;
            try { await _host.RefreshPluginAsync(RouterPlugin.Id, "monitor", _shutdown); }
            catch (Exception e) when (e is OperationCanceledException or HostCommandException or InvalidOperationException) { }
            finally { _refreshing = false; }
        };
        _timer.Start();
    }
    private void Requested(object? sender, PluginPopoverRequest request)
    {
        if (request.PluginId != RouterPlugin.Id || !_enabled()) return;
        if (_window is null || _window.IsDisposed)
        {
            _window = new RouterMonitorForm();
            _window.PinChanged += (_, pinned) => _bar.PinPluginPopover(pinned);
            _window.CloseRequested += (_, _) => _bar.HidePluginPopover();
        }
        _window.ShowFor(_bar, request.Anchor, request.Pinned, request.Scale, request.Theme);
        request.Popup = _window;
    }
    public void SetVisible(bool visible)
    {
        if (!visible) { _bar.HidePluginPopover(); return; }
        if (!_enabled()) throw new HostCommandException("plugin_disabled", "Router module is disabled.");
        _bar.OpenPluginPopover(RouterPlugin.Id, pinned: true);
    }
    public void Dispose()
    {
        _bar.PluginPopoverRequested -= Requested;
        _timer.Stop(); _timer.Dispose(); _window?.Dispose();
        // The monitor and all Editor jobs have their own lifetime and ownership.
    }
}
