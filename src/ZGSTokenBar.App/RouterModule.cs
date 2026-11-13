using ZGSTokenBar.Host;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

internal sealed class RouterModule : IDisposable
{
    private readonly BarForm _bar;
    private readonly ZgsTokenBarHost _host;
    private readonly Func<bool> _enabled;
    private readonly CancellationToken _shutdown;
    internal const string RefreshSource = "router-monitor";
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    /// <summary>A failing monitor read backs off 2 → 4 → 8 → 16 s and returns to 2 s on success.</summary>
    internal static readonly TimeSpan MaxRefreshBackoff = TimeSpan.FromSeconds(16);
    private readonly RefreshScheduler _scheduler;
    private readonly System.Windows.Forms.Timer _warmup = new() { Interval = 600 };
    private RouterMonitorForm? _window;
    private RouterHoverCardForm? _hover;
    private bool _disposed;
    public bool Visible => _window is { Visible: true, IsDisposed: false };
    public RouterSnapshot? Snapshot => _window is { IsDisposed: false } ? _window.Snapshot : null;

    public RouterModule(BarForm bar, ZgsTokenBarHost host, Func<bool> enabled, CancellationToken shutdown, RefreshScheduler scheduler)
    {
        (_bar, _host, _enabled, _shutdown, _scheduler) = (bar, host, enabled, shutdown, scheduler);
        _bar.PluginPopoverRequested += Requested;
        _scheduler.Add(RefreshSource, () => RefreshInterval, RefreshMonitorAsync, maxBackoff: MaxRefreshBackoff);
        // Build the (control-heavy) panel and warm initial data shortly after startup so first click is instant.
        _warmup.Tick += (_, _) =>
        {
            _warmup.Stop();
            if (_shutdown.IsCancellationRequested || !_enabled()) return;
            var window = EnsureWindow();
            window.Prewarm(_bar.UiScale, _bar.UiTheme);
            _ = window.WarmInBackgroundAsync();
        };
        _warmup.Start();
    }
    /// <summary>One monitor read; false (back off) when the Router host rejects or fails it.</summary>
    private async Task<bool> RefreshMonitorAsync()
    {
        if (_disposed || _shutdown.IsCancellationRequested) return true;
        if (!_enabled()) { _bar.HidePluginPopover(); _window?.Close(); return true; }
        try
        {
            await _host.RefreshPluginAsync(RouterPlugin.Id, "monitor", _shutdown);
            if (_window is { IsDisposed: false, Visible: false } && !_bar.IsDisposed && _bar.IsHandleCreated)
            {
                try { _bar.BeginInvoke(() => { if (_window is { IsDisposed: false, Visible: false } window) _ = window.WarmInBackgroundAsync(); }); }
                catch (InvalidOperationException) { }
            }
            return true;
        }
        catch (OperationCanceledException) { return true; }
        catch (Exception e) when (e is HostCommandException or InvalidOperationException) { return false; }
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
        _disposed = true;
        _scheduler.Remove(RefreshSource);
        _warmup.Stop(); _warmup.Dispose(); _window?.Dispose(); _hover?.Dispose();
        // The monitor and all Editor jobs have their own lifetime and ownership.
    }
}
