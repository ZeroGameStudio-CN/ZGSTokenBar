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

/// <summary>Settings: dialog lifecycle, persistence, placement and applying changes.</summary>
internal sealed partial class QuotaApplicationContext
{
    private void OpenSettings()
    {
        if (_quitting) return;
        if (_settingsDialog is { IsDisposed: false } existing)
        {
            RestoreSettingsWindow(existing, Screen.FromControl(_bar).WorkingArea);
            return;
        }

        var pluginStatuses = _pluginHost.ListPlugins();
        var dialog = new SettingsForm(
            _settings,
            _bar.DeviceDpi,
            plugins: pluginStatuses);
        _settingsDialog = dialog;
        dialog.RadarTestNotificationRequested += (_, _) => ShowRadarTestNotification();
        dialog.FormClosed += SettingsDialogClosed;
        var area = Screen.FromControl(_bar).WorkingArea;
        dialog.Location = new Point(
            area.Left + Math.Max(0, (area.Width - dialog.Width) / 2),
            area.Top + Math.Max(0, (area.Height - dialog.Height) / 2));
        try
        {
            RestoreSettingsWindow(dialog, area);
        }
        catch
        {
            _settingsDialog = null;
            dialog.Dispose();
            throw;
        }
    }

    private void SettingsDialogClosed(object? sender, FormClosedEventArgs eventArgs)
    {
        if (sender is not SettingsForm dialog) return;
        _settingsDialog = null;
        try
        {
            if (_quitting || dialog.DialogResult != DialogResult.OK || dialog.ResultSettings is null) return;
            var nextSettings = dialog.ResultSettings;
            nextSettings.CopyPlacementStateFrom(_settings);
            nextSettings.CopyMiniAreaLayoutsFrom(_settings);
            if (!TrySaveSettings(nextSettings, true)) return;
            var localeOnlyChange = IsLocaleOnlyChange(_settings, nextSettings);
            ApplySettingsSnapshot(nextSettings);
            _ = SyncPluginEnablementAsync();
            if (!localeOnlyChange)
            {
                _ = RefreshAsync();
                if (_settings.EnableRadarAlerts) _ = RefreshRadarAsync();
            }
        }
        finally
        {
            dialog.Dispose();
        }
    }

    private void SavePlacement(WindowPlacementCommit commit)
    {
        if (_quitting) return;
        var previous = new AppSettings();
        previous.CopyPlacementStateFrom(_settings);
        _settings.PlacementSchemaVersion = AppSettings.CurrentPlacementSchemaVersion;
        _settings.PlacementProfiles[commit.TopologyKey] = commit.Profile.Copy();
        _settings.TaskbarDocked = commit.Profile.IsDocked;
        if (commit.DockedMonitorName is not null && commit.DockedPosition is { } position)
        {
            _settings.TaskbarMonitor = commit.DockedMonitorName;
            _settings.TaskbarPosition = Math.Clamp(position, 0, 1);
            _settings.TaskbarPositions = new Dictionary<string, double>(
                commit.LegacyTaskbarPositions,
                StringComparer.OrdinalIgnoreCase);
        }
        if (commit.FloatingLocation is { } floating)
        {
            _settings.WindowX = floating.X;
            _settings.WindowY = floating.Y;
        }
        if (!TrySaveSettings(_settings))
        {
            _settings.CopyPlacementStateFrom(previous);
        }
    }

    private Task SyncPluginEnablementAsync() =>
        SyncPluginEnablementAsync(suppressErrors: true, _shutdown.Token);

    private async Task SyncPluginEnablementAsync(
        bool suppressErrors,
        CancellationToken cancellationToken)
    {
        try
        {
            var statuses = _pluginHost.ListPlugins();
            var changes = statuses
                .Where(plugin => !plugin.Manifest.Required)
                .Where(plugin => plugin.Enabled != _settings.IsPluginEnabled(
                    plugin.Manifest.Id,
                    plugin.Manifest.DefaultEnabled))
                .ToArray();
            foreach (var plugin in changes
                         .Where(plugin => !Desired(plugin))
                         .OrderByDescending(plugin => plugin.Manifest.Order))
            {
                await SetPluginEnabledFromSettingsAsync(
                    plugin.Manifest.Id,
                    false,
                    cancellationToken);
            }
            foreach (var plugin in changes
                         .Where(Desired)
                         .OrderBy(plugin => plugin.Manifest.Order))
            {
                await SetPluginEnabledFromSettingsAsync(
                    plugin.Manifest.Id,
                    true,
                    cancellationToken);
            }
        }
        catch when (suppressErrors)
        {
            // The saved setting remains authoritative; Host health exposes isolated failures.
        }

        bool Desired(PluginStatus plugin) => _settings.IsPluginEnabled(
            plugin.Manifest.Id,
            plugin.Manifest.DefaultEnabled);
    }

    private async Task SetPluginEnabledFromSettingsAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var revision = _pluginHost.Describe().Revisions.ConfigRevision;
        await _pluginHost.SetEnabledAsync(pluginId, enabled, revision, cancellationToken);
    }

    private void ApplySettingsSnapshot(AppSettings settings)
    {
        _settings = settings;
        _text = NativeText.For(_settings.Locale);
        if (_allowGlobalStartupRegistration)
        {
            StartupManager.Apply(_settings.OpenAtLogin);
        }
        ApplyRefreshInterval();
        _bar.ApplySettings(_settings);
        _ = UpdateProviderActivityAsync(requestRefresh: false);
        _bar.SetQuotaPaceEstimates(QuotaPaceEstimates(_snapshot, DateTimeOffset.UtcNow));
        _bar.SetRadarProviders(_settings.EnableRadar ? [_radarService.Provider] : []);
        ApplyRadarInterval();
        ApplyClockInterval();
        ApplyText();
        _radarMenuItem.Visible = _settings.EnableRadar;
        ApplyGenericPluginCards();
    }

    private void SaveMiniAreaLayout()
    {
        if (_quitting) return;
        var previous = AppSettings.CopyMiniAreaLayouts(_settings.MiniAreaLayouts);
        _settings.MiniAreaLayouts = AppSettings.CopyMiniAreaLayouts(_bar.MiniAreaLayouts);
        if (!TrySaveSettings(_settings))
        {
            _settings.MiniAreaLayouts = previous;
            _bar.SetMiniAreaLayouts(previous, preserveAnchor: true);
        }
        else if (!SettingsChangeDetector.MiniAreaLayoutsEqual(previous, _settings.MiniAreaLayouts))
        {
            _uiRevision++;
        }
    }

    private void SaveHiddenDockItems()
    {
        if (_quitting) return;
        var previous = AppSettings.CopyHiddenDockItems(_settings.HiddenDockItems);
        _settings.HiddenDockItems = AppSettings.CopyHiddenDockItems(_bar.HiddenDockItems);
        if (!TrySaveSettings(_settings))
        {
            _settings.HiddenDockItems = previous;
            _bar.SetHiddenDockItems(previous);
        }
        else if (!previous.SequenceEqual(_settings.HiddenDockItems, StringComparer.Ordinal))
        {
            _uiRevision++;
        }
    }

    private void SaveMiniAreaOrder()
    {
        if (_quitting) return;
        var previous = AppSettings.CopyMiniAreaOrder(_settings.MiniAreaOrder);
        _settings.MiniAreaOrder = AppSettings.CopyMiniAreaOrder(_bar.MiniAreaOrder);
        if (!TrySaveSettings(_settings))
        {
            _settings.MiniAreaOrder = previous;
            _bar.SetMiniAreaOrder(previous, preserveAnchor: true);
        }
        else if (!previous.SequenceEqual(_settings.MiniAreaOrder, StringComparer.Ordinal))
        {
            _uiRevision++;
        }
    }

    private bool TrySaveSettings(AppSettings settings, bool showError = false)
    {
        try
        {
            _store.Save(settings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (showError && !_bar.IsDisposed)
            {
                MessageBox.Show(
                    _bar,
                    _text.SettingsSaveFailed,
                    _text.SettingsNotSaved,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return false;
        }
    }

    private void ApplyText()
    {
        _refreshMenuItem.Text = _text.RefreshNow;
        _radarMenuItem.Text = _text.OpenRadarWebsite;
        if (_availableUpdate is { } update) _updateMenuItem.Text = _text.UpdateTo(update.Version);
        _settingsMenuItem.Text = _text.Settings;
        _quitMenuItem.Text = _text.Quit;
        _tray.Text = _text.TrayText;
    }

    internal static void RestoreSettingsWindow(Form dialog, Rectangle? fallbackWorkingArea = null) =>
        SettingsWindowPlacement.Restore(dialog, fallbackWorkingArea);

    internal static Point RestoredSettingsLocation(
        Rectangle bounds,
        IReadOnlyList<Rectangle> workingAreas,
        Rectangle fallbackWorkingArea) =>
        SettingsWindowPlacement.RestoredLocation(bounds, workingAreas, fallbackWorkingArea);

    internal static bool IsLocaleOnlyChange(AppSettings previous, AppSettings next) =>
        SettingsChangeDetector.IsLocaleOnlyChange(previous, next);
}
