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

/// <summary>Plugin runtime: quota/provider plugin publishing, generic plugin cards and assets.</summary>
internal sealed partial class QuotaApplicationContext
{
    private void PublishQuotaPlugins(QuotaSnapshot snapshot)
    {
        PublishProviderPlugin(
            snapshot,
            ProviderKind.Claude,
            "zgstokenbar.provider.claude",
            "claude",
            "provider.claude.icon",
            "accent.claude");
        PublishProviderPlugin(
            snapshot,
            ProviderKind.Codex,
            "zgstokenbar.provider.codex",
            "codex",
            "provider.codex.icon",
            "accent.codex");
    }

    private async Task RefreshGenericPluginsAsync()
    {
        foreach (var plugin in _pluginHost.ListPlugins().Where(plugin =>
                     plugin.Enabled && !LegacyRenderedPluginIds.Contains(plugin.Manifest.Id)))
        {
            try
            {
                await _pluginHost.RefreshPluginAsync(
                    plugin.Manifest.Id,
                    "scheduler",
                    _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Process plugin failures are isolated by the Host.
            }
        }
    }

    private Task WatchPluginEventsAsync() => PluginCardEventWatcher.RunAsync(
        _pluginHost, _pluginSubscription,
        hostEvent => !_bar.IsDisposed && (hostEvent is not null && !IsGenericPluginEvent(hostEvent)
            || QueueGenericPluginCardsUpdate()), _shutdown.Token);

    private bool QueueGenericPluginCardsUpdate()
    {
        if (Interlocked.Exchange(ref _genericCardsUpdateQueued, 1) != 0) return true;
        try
        {
            _bar.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _genericCardsUpdateQueued, 0);
                ApplyGenericPluginCards();
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref _genericCardsUpdateQueued, 0);
            return false;
        }
    }

    private static bool IsGenericPluginEvent(HostEvent hostEvent)
    {
        if (hostEvent.Type is not ("plugin.data.changed" or "plugin.config.changed")) return false;
        return hostEvent.Payload.TryGetProperty("value", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
            && value.GetString() is { } pluginId
            && !LegacyRenderedPluginIds.Contains(pluginId);
    }

    private void ApplyGenericPluginCards()
    {
        if (_bar.IsDisposed) return;
        var genericPlugins = _pluginHost.ListPlugins()
            .Where(plugin => plugin.Enabled && !LegacyRenderedPluginIds.Contains(plugin.Manifest.Id))
            .ToDictionary(plugin => plugin.Manifest.Id, StringComparer.Ordinal);
        var assets = genericPlugins.ToDictionary(
            pair => pair.Key,
            pair => PluginAssetsFor(pair.Key),
            StringComparer.Ordinal);
        var cards = _pluginHost.Snapshot(includeValues: true).Plugins
            .Where(plugin => genericPlugins.ContainsKey(plugin.PluginId))
            .SelectMany(plugin =>
                (plugin.Cards ?? []).Select(card => new PluginMiniCardView(
                    plugin.PluginId,
                    assets[plugin.PluginId].Text.TryGetValue(card.TitleKey, out var title)
                        ? title
                        : genericPlugins[plugin.PluginId].Manifest.DisplayName ?? plugin.PluginId,
                    card,
                    assets[plugin.PluginId].Text,
                    assets[plugin.PluginId].Icon)))
            .ToList();
        cards.AddRange(_otherToolsModule.Cards(
            _settings.Locale == "zh-CN",
            id => _settings.IsPluginEnabled(id, true)));
        _bar.SetPluginMiniCards(cards);
    }

    private PluginAssets PluginAssetsFor(string pluginId)
    {
        var key = (pluginId, _settings.Locale);
        if (_pluginAssets.TryGetValue(key, out var assets)) return assets;
        if (pluginId == ZGSTokenBar.Plugin.Router.RouterPlugin.Id)
        {
            assets = new PluginAssets(null, new Dictionary<string, string>
            {
                ["Router"] = "Router",
                ["router.state"] = _settings.Locale == "zh-CN" ? "状态" : "State",
                ["router.task"] = _settings.Locale == "zh-CN" ? "任务" : "Task",
                ["router.queued"] = _settings.Locale == "zh-CN" ? "排队（全部项目）" : "Queued (all projects)",
                ["router.project"] = _settings.Locale == "zh-CN" ? "项目" : "Project",
                ["router.phase"] = _settings.Locale == "zh-CN" ? "阶段" : "Phase",
                ["router.elapsed"] = _settings.Locale == "zh-CN" ? "已运行" : "Elapsed",
            });
            _pluginAssets[key] = assets;
            return assets;
        }
        try
        {
            assets = new PluginAssets(
                _pluginHost.GetPluginIconPng(pluginId),
                _pluginHost.GetPluginLocalization(pluginId, _settings.Locale));
        }
        catch (HostCommandException)
        {
            assets = new PluginAssets(
                null,
                new Dictionary<string, string>(StringComparer.Ordinal));
        }
        _pluginAssets[key] = assets;
        return assets;
    }

    private void PublishProviderPlugin(
        QuotaSnapshot snapshot,
        ProviderKind provider,
        string pluginId,
        string groupId,
        string iconKey,
        string accentToken)
    {
        if (!HasPlugin(pluginId)) return;

        var health = snapshot.Health.FirstOrDefault(item => item.Provider == provider);
        if (health is null) return;
        var result = new ProviderResult(
            provider,
            snapshot.Cards.Where(card => card.Provider == provider).ToArray(),
            health)
        {
            CodexAccounts = provider == ProviderKind.Codex ? snapshot.CodexAccounts : [],
        };
        _pluginHost.Publish(CorePluginProjection.Provider(
            pluginId,
            groupId,
            iconKey,
            accentToken,
            result));
    }

    private bool HasPlugin(string pluginId) => _pluginHost.DescribePlugin(pluginId) is not null;

    public ValueTask PersistPluginEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken) =>
        new(InvokeOnUiAsync(
            () =>
            {
                var previous = _settings.IsPluginEnabled(pluginId);
                _settings.SetPluginEnabled(pluginId, enabled);
                if (!TrySaveSettings(_settings))
                {
                    _settings.SetPluginEnabled(pluginId, previous);
                    throw new IOException("Plugin setting could not be saved.");
                }
                _activeProviders = ActiveProviders(_settings);
                _bar.ApplySettings(_settings);
                _bar.SetActiveProviders(_activeProviders);
                _ = RefreshAsync(userInitiated: true);
                return true;
            },
            cancellationToken));

    private sealed record PluginAssets(
        byte[]? Icon,
        IReadOnlyDictionary<string, string> Text);
}
