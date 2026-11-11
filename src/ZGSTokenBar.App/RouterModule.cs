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
    private readonly System.Windows.Forms.Timer _warmup = new() { Interval = 4_000 };
    private RouterMonitorForm? _window;
    private RouterHoverCardForm? _hover;
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
        // Build the (control-heavy) panel once while idle so the first click only has to show it.
        _warmup.Tick += (_, _) =>
        {
            _warmup.Stop();
            if (!_shutdown.IsCancellationRequested && _enabled()) EnsureWindow().Prewarm(_bar.UiScale, _bar.UiTheme);
        };
        _warmup.Start();
    }
    private void Requested(object? sender, PluginPopoverRequest request)
    {
        if (request.PluginId != RouterPlugin.Id || !_enabled()) return;
        if (!request.Pinned)
        {
            // Hover: the lightweight cached card; the full monitor panel is only built on click.
            using var hover = PerfTrace.Measure("router.hover.show");
            if (_window is { Visible: true, IsDisposed: false }) { request.Popup = _window; return; }
            _hover ??= new RouterHoverCardForm();
            _hover.ShowFor(_bar, request.Anchor, _bar.RouterCard, _bar.IsChinese, request.Scale);
            request.Popup = _hover;
            return;
        }
        var clicked = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_hover is { Visible: true }) _hover.Hide();
        EnsureWindow();
        if (!_window!.Visible) _window.MarkOpenRequested(clicked);
        using (PerfTrace.Measure("router.panel.show"))
            _window.ShowFor(_bar, request.Anchor, request.Pinned, request.Scale, request.Theme);
        request.Popup = _window;
    }
    private RouterMonitorForm EnsureWindow()
    {
        if (_window is { IsDisposed: false } existing) return existing;
        using var create = PerfTrace.Measure("router.panel.create");
        _window = new RouterMonitorForm();
        _window.PinChanged += (_, pinned) => _bar.PinPluginPopover(pinned);
        _window.CloseRequested += (_, _) => _bar.HidePluginPopover();
        return _window;
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
        _timer.Stop(); _timer.Dispose(); _warmup.Stop(); _warmup.Dispose(); _window?.Dispose(); _hover?.Dispose();
        // The monitor and all Editor jobs have their own lifetime and ownership.
    }
}
