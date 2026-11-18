using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal sealed partial class QuotaApplicationContext
{
    private readonly RuntimeHealthCheck _runtimeHealth = new();
    private DateTimeOffset? _lastRuntimeHealthCheck;
    private (int Pending, long Bytes, int Failures)? _previousLogCheck;
    private (bool? Healthy, long Version) _codexQuotaHealth;
    private bool _observedLiveCodexQuota;

    private void RegisterRuntimeHealthCheck() => _refreshScheduler.Add("runtime-health",
        () => TimeSpan.FromMilliseconds(RuntimeHealthCheck.IntervalMs), () =>
        {
            if (!_quitting && !_bar.IsDisposed) CheckRuntimeHealth();
            return Task.FromResult(true);
        });

    private void CheckRuntimeHealth()
    {
        var now = Environment.TickCount64;
        var router = _routerModule.MonitorHealth;
        var quota = _activeProviders.Contains(ProviderKind.Codex) ? _codexQuotaHealth : default;
        bool? intake = IntakeEnabled && _intakeSummaryHealthy is { } healthy
            ? healthy && now - _intakeSummaryObservedAt < BarForm.IntakeCountLifetimeMs : null;
        // Once a count is stale, its absence is a local observation on each check.
        var intakeVersion = now - _intakeSummaryObservedAt < BarForm.IntakeCountLifetimeMs ? _intakeSummaryObservedAt : 0;
        var warnings = _runtimeHealth.Observe(now,
        [
            new("taskbar", _bar.ObserveDockHealth()),
            new("codex-log", ObserveCodexLogHealth()),
            new("codex-quota", quota.Healthy, quota.Version),
            new("router", router.Healthy, router.Version),
            new("intake", intake, intakeVersion),
        ], notify: _bar.TaskbarEmbedStatus != "hidden:fullscreen");
        _lastRuntimeHealthCheck = DateTimeOffset.UtcNow;
        if (warnings.Count == 0) return;
        _updateBalloonActive = false;
        try
        {
            _tray.ShowBalloonTip(8_000, _text.IsChinese ? "TokenBar 检测到持续异常" : "TokenBar noticed persistent issues",
                string.Join(Environment.NewLine, warnings.Select(HealthWarningText)), ToolTipIcon.Warning);
        }
        catch (InvalidOperationException) { }
    }

    private bool? ObserveCodexLogHealth()
    {
        if (!_activeProviders.Contains(ProviderKind.Codex) || _cachedCodexTokenUsage is not { } current)
        {
            _previousLogCheck = null;
            return null;
        }
        var previous = _previousLogCheck;
        var bytes = current.ConversationScannedBytes + current.ConversationRestoredBytes;
        _previousLogCheck = (current.PendingConversationCount, bytes, current.ConversationCheckpointFailures);
        if (previous is not { } last) return null;
        var stalled = current.PendingConversationCount > 0 && last.Pending > 0
            && current.PendingConversationCount >= last.Pending && bytes == last.Bytes;
        return !stalled && current.ConversationCheckpointFailures <= last.Failures;
    }

    private void ObserveCodexQuotaHealth(QuotaSnapshot snapshot)
    {
        var health = snapshot.Health.FirstOrDefault(item => item.Provider == ProviderKind.Codex);
        if (health is { Connected: true, Code: ProviderHealthCode.Current or ProviderHealthCode.Unknown }
            && snapshot.Cards.Any(card => card.Provider == ProviderKind.Codex && !card.IsService
                && card.Windows.Any(window => window.UsedPercent is not null)))
        {
            _observedLiveCodexQuota = true;
            _codexQuotaHealth = (true, Environment.TickCount64);
        }
        else if (health?.Code == ProviderHealthCode.MissingCredentials)
        {
            // A user can intentionally sign out; start a new baseline after signing in again.
            _observedLiveCodexQuota = false;
            _codexQuotaHealth = default;
        }
        else if (_observedLiveCodexQuota && (health?.Code is ProviderHealthCode.OAuthExpired or ProviderHealthCode.OAuthRefreshFailed
            or ProviderHealthCode.HttpError or ProviderHealthCode.Timeout or ProviderHealthCode.EndpointBlocked or ProviderHealthCode.Unavailable))
        {
            _codexQuotaHealth = (false, Environment.TickCount64);
        }
    }

    private string HealthWarningText(string issue) => (_text.IsChinese, issue) switch
    {
        (true, "taskbar") => "任务栏位置尚未恢复",
        (true, "codex-log") => "Codex 日志续读持续未推进或保存失败",
        (true, "codex-quota") => "Codex 额度连续读取失败",
        (true, "router") => "Router 状态持续无法读取",
        (true, "intake") => "自动分拣状态持续无法确认",
        (false, "taskbar") => "Taskbar placement has not recovered",
        (false, "codex-log") => "Codex log catch-up is stalled or cannot save",
        (false, "codex-quota") => "Codex quota reads have repeatedly failed",
        (false, "router") => "Router status is still unavailable",
        (false, "intake") => "Intake service status is still unavailable",
        _ => issue,
    };
}
