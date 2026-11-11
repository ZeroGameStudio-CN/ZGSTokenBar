using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>Official provider icons, shared by the dock, the usage panel and popovers.</summary>
internal static class ProviderIcons
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Image?> Cache = new(StringComparer.Ordinal);

    internal static string? Resource(string providerId) => UsageProviderRegistry.Find(providerId)?.IconResource;

    /// <summary>Process-lifetime icon for a provider id, or null when none is bundled.</summary>
    internal static Image? For(string providerId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(providerId, out var cached)) return cached;
            Image? image = null;
            if (Resource(providerId) is { } resource)
            {
                try
                {
                    image = BarForm.LoadEmbeddedImage("ZGSTokenBar.App.Assets." + resource);
                }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
                {
                    image = null;
                }
            }
            Cache[providerId] = image;
            return image;
        }
    }
}
