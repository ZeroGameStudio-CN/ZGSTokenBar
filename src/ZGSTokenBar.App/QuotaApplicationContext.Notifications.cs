using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using ZGSTokenBar.Builtins;
using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginAdapters;
using ZGSTokenBar.PluginSdk;
using ZGSTokenBar.Transport.NamedPipe;

namespace ZGSTokenBar.App;

/// <summary>Update checks and quota milestone notifications.</summary>
internal sealed partial class QuotaApplicationContext
{
    private async Task CheckForUpdatesAsync()
    {
        if (!await _updateGate.WaitAsync(0)) return;
        try
        {
            if (!Version.TryParse(ProductVersion(), out var currentVersion)) return;
            var update = await _updateChecker.CheckAsync(currentVersion, _shutdown.Token);
            if (update is null || _shutdown.IsCancellationRequested || _bar.IsDisposed) return;
            _availableUpdate = update;
            _updateMenuItem.Text = _text.UpdateTo(update.Version);
            _updateMenuItem.Visible = true;
            if (_notifiedUpdateVersion == update.Version || _quitting) return;
            _notifiedUpdateVersion = update.Version;
            _updateBalloonActive = true;
            _tray.ShowBalloonTip(
                8_000,
                _text.UpdateAvailableTitle(update.Version),
                _text.UpdateAvailableBody,
                ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or InvalidDataException
            or System.Text.Json.JsonException
            or InvalidOperationException)
        {
            // Update discovery is best-effort and must not disturb the quota bar.
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private void OpenUpdatePage()
    {
        if (_availableUpdate is not { } update) return;
        Process.Start(new ProcessStartInfo(update.PageUri.AbsoluteUri) { UseShellExecute = true });
    }

    private void ShowMilestoneAlerts(IReadOnlyList<QuotaMilestoneAlert> alerts)
    {
        if (alerts.Count == 0 || _quitting) return;
        _updateBalloonActive = false;
        var now = DateTimeOffset.UtcNow;
        var warning = alerts.Any(alert => alert.Threshold >= 90);
        var title = alerts.Count == 1
            ? _text.QuotaMilestoneTitle(
                alerts[0].CardLabel,
                alerts[0].WindowLabel,
                alerts[0].Threshold)
            : _text.QuotaMilestonesTitle(alerts.Count);
        var body = alerts.Count == 1
            ? AlertDetail(alerts[0], now, true)
            : string.Join(Environment.NewLine, alerts.Take(3).Select(alert =>
                $"{alert.CardLabel} {_text.WindowLimit(alert.WindowLabel)}: {AlertDetail(alert, now, false)}"));
        if (alerts.Count > 3) body += $"{Environment.NewLine}{_text.MoreAlerts(alerts.Count - 3)}";
        title = NativeText.TruncateTextElements(title, 60);
        body = NativeText.TruncateTextElements(body, 250);

        try
        {
            _tray.ShowBalloonTip(8_000, title, body, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (InvalidOperationException)
        {
            // The notification area can disappear while Windows is shutting down.
        }
    }

    private string AlertDetail(QuotaMilestoneAlert alert, DateTimeOffset now, bool includeCurrent)
    {
        var used = (int)Math.Round(alert.UsedPercent);
        var remaining = Math.Max(0, 100 - used);
        return _text.QuotaMilestoneDetail(
            alert.Threshold,
            used,
            remaining,
            alert.ResetsAt,
            now,
            includeCurrent);
    }
}
