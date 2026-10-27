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
        _bar.PluginActivated += Activated;
        _timer.Tick += async (_, _) =>
        {
            if (_shutdown.IsCancellationRequested) return;
            if (!_enabled()) { _window?.Close(); return; }
            if (_refreshing) return;
            _refreshing = true;
            try { await _host.RefreshPluginAsync(RouterPlugin.Id, "monitor", _shutdown); }
            catch (Exception e) when (e is OperationCanceledException or HostCommandException or InvalidOperationException) { }
            finally { _refreshing = false; }
        };
        _timer.Start();
    }
    private void Activated(object? sender, string pluginId) { if (pluginId == RouterPlugin.Id) SetVisible(true); }
    public void SetVisible(bool visible)
    {
        if (!visible) { _window?.Close(); return; }
        if (!_enabled()) throw new HostCommandException("plugin_disabled", "Router module is disabled.");
        if (_window is null || _window.IsDisposed) _window = new RouterMonitorForm();
        if (!_window.Visible) _window.Show(_bar);
        _window.Activate();
    }
    public void Dispose()
    {
        _bar.PluginActivated -= Activated;
        _timer.Stop(); _timer.Dispose(); _window?.Dispose();
        // The monitor and all Editor jobs have their own lifetime and ownership.
    }
}
