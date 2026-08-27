using System.Text.Json;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Builtins;

public static class BuiltinProfiles
{
    public static EffectiveProfile Desktop(IReadOnlyDictionary<string, bool>? enabled = null) =>
        Compose("desktop", ["zgstokenbar.base", "zgstokenbar.providers"], enabled);

    public static EffectiveProfile Headless(IReadOnlyDictionary<string, bool>? enabled = null) =>
        Compose("headless", ["zgstokenbar.base", "zgstokenbar.providers"], enabled);

    private static EffectiveProfile Compose(
        string name,
        IReadOnlyList<string> bundles,
        IReadOnlyDictionary<string, bool>? enabled)
    {
        var plugins = GeneratedBuiltinPluginRegistry.Create();
        try
        {
            return new(
                1,
                name,
                bundles,
                plugins
                    .Select(plugin => plugin.Manifest)
                    .OrderBy(manifest => manifest.Order)
                    .ThenBy(manifest => manifest.Id, StringComparer.Ordinal)
                    .Select(manifest => new ProfilePlugin(
                        manifest.Id,
                        manifest.Version,
                        enabled?.TryGetValue(manifest.Id, out var overrideValue) == true
                            ? overrideValue || manifest.Required
                            : manifest.DefaultEnabled,
                        manifest.Order,
                        new Dictionary<string, JsonElement>(StringComparer.Ordinal)))
                    .ToArray());
        }
        finally
        {
            foreach (var plugin in plugins) plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
