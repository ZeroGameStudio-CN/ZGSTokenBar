using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Host;

public static class ProfileComposition
{
    public static EffectiveProfile IncludePlugins(
        EffectiveProfile profile,
        IEnumerable<PluginManifest> manifests,
        IReadOnlyDictionary<string, bool>? enabled = null)
    {
        var plugins = profile.Plugins.ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);
        foreach (var manifest in manifests)
        {
            if (plugins.ContainsKey(manifest.Id)) continue;
            plugins[manifest.Id] = new(
                manifest.Id,
                manifest.Version,
                enabled?.TryGetValue(manifest.Id, out var value) == true
                    ? value || manifest.Required
                    : manifest.DefaultEnabled || manifest.Required,
                manifest.Order,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal));
        }
        return profile with
        {
            Plugins = plugins.Values
                .OrderBy(plugin => plugin.Order)
                .ThenBy(plugin => plugin.Id, StringComparer.Ordinal)
                .ToArray(),
        };
    }
}
