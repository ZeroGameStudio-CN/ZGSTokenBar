using Microsoft.Win32;

namespace ZGSTokenBar.App;

/// <summary>
/// Sleep/resume: timers keep their old phase after a resume, so quota could stay stale for a full
/// interval. A short settle delay (network/VPN back up) then one normal refresh of every source.
/// </summary>
internal sealed partial class QuotaApplicationContext
{
    private const int ResumeSettleMs = 10_000;
    private System.Windows.Forms.Timer? _resumeTimer;

    private void SubscribePowerEvents() => SystemEvents.PowerModeChanged += OnPowerModeChanged;

    private void UnsubscribePowerEvents()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _resumeTimer?.Dispose();
        _resumeTimer = null;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Raised on the SystemEvents thread; hop to the UI thread that owns the timers.
        if (e.Mode != PowerModes.Resume || _quitting || _bar.IsDisposed || !_bar.IsHandleCreated) return;
        try
        {
            _bar.BeginInvoke(ScheduleResumeRefresh);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ScheduleResumeRefresh()
    {
        if (_quitting) return;
        _resumeTimer ??= CreateResumeTimer();
        _resumeTimer.Stop();
        _resumeTimer.Start();
    }

    private System.Windows.Forms.Timer CreateResumeTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = ResumeSettleMs };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_quitting) return;
            _ = RefreshAsync();
            _otherToolsModule.RequestRefresh();
        };
        return timer;
    }
}
