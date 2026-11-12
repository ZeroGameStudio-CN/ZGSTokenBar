using System.Drawing.Drawing2D;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ZGSTokenBar.Core;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

/// <summary>Settings form state ⇄ AppSettings: building, copying and comparing the editable settings.</summary>
internal sealed partial class SettingsForm
{
    /// <summary>Hidden ids: items switched off here, plus hidden ids not listed (e.g. a disabled provider).</summary>
    private string[] BuildHiddenDockItems()
    {
        var listed = _dockItemToggles.Select(item => item.Entry.Id).ToHashSet(StringComparer.Ordinal);
        return AppSettings.CopyHiddenDockItems(_original.HiddenDockItems.Where(id => !listed.Contains(id))
            .Concat(_dockItemToggles.Where(item => !item.Toggle.Checked).Select(item => item.Entry.Id)));
    }

    private AppSettings BuildSettings()
    {
        var providers = new List<string>(2);
        if (_claude.Checked) providers.Add("claude");
        if (_codex.Checked) providers.Add("codex");
        var refresh = _refreshMinutes.SelectedItem as RefreshChoice;
        var settings = new AppSettings
        {
            EnabledProviders = providers.ToArray(),
            RefreshMinutes = refresh?.Minutes ?? _original.RefreshMinutes,
            AutoRefreshClaudeOAuth = _refreshClaudeOAuth.Checked,
            OpenAtLogin = _openAtLogin.Checked,
            EnableAlerts = _usageAlerts.Checked,
            UseTaskbarRings = true,
            EnableAnimations = _animations.Checked,
            EmbedInTaskbar = _embedTaskbar.Checked,
            GlassDockMaxRings = _original.GlassDockMaxRings,
            EnableAiGatewayBalance = _externalPluginToggles.TryGetValue(
                MiniAreaIds.AiGateway,
                out var aiGatewayToggle)
                ? aiGatewayToggle.Checked
                : _original.EnableAiGatewayBalance,
            EnableSub2ApiPool = _codex.Checked && _sub2ApiPool.Checked,
            MiniAreaLayouts = AppSettings.CopyMiniAreaLayouts(_original.MiniAreaLayouts),
            MiniAreaOrder = AppSettings.CopyMiniAreaOrder(_original.MiniAreaOrder),
            HiddenDockItems = BuildHiddenDockItems(),
            GlassDockExpanded = _original.GlassDockExpanded,
            BackgroundPalette = _backgroundPalette,
            AntigravityPrimaryGroup = (_antigravityGroup.SelectedItem as AntigravityGroupChoice)?.Value ?? _original.AntigravityPrimaryGroup,
            Locale = (_locale.SelectedItem as LocaleChoice)?.Locale ?? _original.Locale,
            PluginEnabled = new Dictionary<string, bool>(_original.PluginEnabled, StringComparer.Ordinal),
            PluginEnablementDecisions = [.. _original.PluginEnablementDecisions],
            AutoEnabledPlugins = [.. _original.AutoEnabledPlugins],
        };
        ApplyPluginToggle(settings, "zgstokenbar.metrics.system", _systemMetrics.Checked, true);
        ApplyPluginToggle(settings, "zgstokenbar.provider.claude", _claude.Checked, false);
        ApplyPluginToggle(settings, "zgstokenbar.provider.codex", _codex.Checked, false);
        if (_original.IsPluginEnabled(OtherToolsModule.GrokId, true) != _grok.Checked)
            ApplyPluginToggle(settings, OtherToolsModule.GrokId, _grok.Checked, true);
        if (_original.IsPluginEnabled(OtherToolsModule.AntigravityId, true) != _antigravity.Checked)
            ApplyPluginToggle(settings, OtherToolsModule.AntigravityId, _antigravity.Checked, true);
        foreach (var (id, toggle) in _usageProviderToggles)
        {
            if (_original.IsPluginEnabled(id, true) != toggle.Checked) ApplyPluginToggle(settings, id, toggle.Checked, true);
        }
        ApplyPluginToggle(
            settings,
            "zgstokenbar.usage.codex-local",
            _codex.Checked && _codexLocalUsage.Checked,
            _original.IsEnabled(ProviderKind.Codex));
        foreach (var pair in _externalPluginToggles)
        {
            ApplyPluginToggle(
                settings,
                pair.Key,
                pair.Value.Checked,
                _plugins.FirstOrDefault(plugin => plugin.Manifest.Id == pair.Key)?.Manifest.DefaultEnabled == true);
        }
        settings.CopyPlacementStateFrom(_original);
        return settings;
    }

    private void ApplyPluginToggle(
        AppSettings settings,
        string pluginId,
        bool enabled,
        bool fallback)
    {
        var changed = _original.IsPluginEnabled(pluginId, fallback) != enabled;
        settings.SetPluginEnabled(pluginId, enabled, explicitChoice: changed);
    }

    private static bool EditableEquals(AppSettings left, AppSettings right) =>
        left.EnabledProviders.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(right.EnabledProviders)
        && left.RefreshMinutes == right.RefreshMinutes
        && left.AutoRefreshClaudeOAuth == right.AutoRefreshClaudeOAuth
        && left.OpenAtLogin == right.OpenAtLogin
        && left.EnableAlerts == right.EnableAlerts
        && left.EnableAnimations == right.EnableAnimations
        && left.EmbedInTaskbar == right.EmbedInTaskbar
        && left.EnableAiGatewayBalance == right.EnableAiGatewayBalance
        && left.EnableSub2ApiPool == right.EnableSub2ApiPool
        && left.IsPluginEnabled("zgstokenbar.metrics.system", true)
            == right.IsPluginEnabled("zgstokenbar.metrics.system", true)
        && left.IsPluginEnabled("zgstokenbar.usage.codex-local", left.IsEnabled(ProviderKind.Codex))
            == right.IsPluginEnabled("zgstokenbar.usage.codex-local", right.IsEnabled(ProviderKind.Codex))
        && MiniAreaLayoutsEqual(left.MiniAreaLayouts, right.MiniAreaLayouts)
        && left.MiniAreaOrder.SequenceEqual(right.MiniAreaOrder, StringComparer.Ordinal)
        && left.HiddenDockItems.SequenceEqual(right.HiddenDockItems, StringComparer.Ordinal)
        && DictionaryEqual(left.PluginEnabled, right.PluginEnabled)
        && string.Equals(left.BackgroundPalette, right.BackgroundPalette, StringComparison.Ordinal)
        && string.Equals(left.AntigravityPrimaryGroup, right.AntigravityPrimaryGroup, StringComparison.Ordinal)
        && string.Equals(left.Locale, right.Locale, StringComparison.Ordinal);

    private static AppSettings Copy(AppSettings settings)
    {
        var copy = new AppSettings
        {
            EnabledProviders = [.. settings.EnabledProviders],
            RefreshMinutes = settings.RefreshMinutes,
            AutoRefreshClaudeOAuth = settings.AutoRefreshClaudeOAuth,
            OpenAtLogin = settings.OpenAtLogin,
            EnableAlerts = settings.EnableAlerts,
            UseTaskbarRings = true,
            EnableAnimations = settings.EnableAnimations,
            EmbedInTaskbar = settings.EmbedInTaskbar,
            GlassDockMaxRings = settings.GlassDockMaxRings,
            EnableAiGatewayBalance = settings.EnableAiGatewayBalance,
            EnableSub2ApiPool = settings.EnableSub2ApiPool,
            MiniAreaLayouts = AppSettings.CopyMiniAreaLayouts(settings.MiniAreaLayouts),
            MiniAreaOrder = AppSettings.CopyMiniAreaOrder(settings.MiniAreaOrder),
            HiddenDockItems = AppSettings.CopyHiddenDockItems(settings.HiddenDockItems),
            GlassDockExpanded = settings.GlassDockExpanded,
            BackgroundPalette = AppSettings.NormalizeBackgroundPalette(settings.BackgroundPalette),
            AntigravityPrimaryGroup = settings.AntigravityPrimaryGroup,
            Locale = settings.Locale,
            PluginEnabled = new Dictionary<string, bool>(settings.PluginEnabled, StringComparer.Ordinal),
            PluginEnablementDecisions = [.. settings.PluginEnablementDecisions],
            AutoEnabledPlugins = [.. settings.AutoEnabledPlugins],
        };
        copy.CopyPlacementStateFrom(settings);
        return copy;
    }

    private string PluginDescription(PluginManifest manifest) =>
        string.Equals(_original.Locale, "zh-CN", StringComparison.OrdinalIgnoreCase)
            ? $"本地插件 · {manifest.Id} · v{manifest.Version}"
            : $"Local plugin · {manifest.Id} · v{manifest.Version}";

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, bool> left,
        IReadOnlyDictionary<string, bool> right)
    {
        var leftExternal = left.Where(pair => !BuiltinPluginIds.Contains(pair.Key)).ToArray();
        var rightExternal = right.Where(pair => !BuiltinPluginIds.Contains(pair.Key)).ToArray();
        if (leftExternal.Length != rightExternal.Length) return false;
        return leftExternal.All(pair =>
            right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static bool MiniAreaLayoutsEqual(
        IReadOnlyDictionary<string, MiniAreaLayout> left,
        IReadOnlyDictionary<string, MiniAreaLayout> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out var value) && value == entry.Value);
}
