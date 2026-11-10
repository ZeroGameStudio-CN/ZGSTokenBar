using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>Official provider icons, shared by the dock, the usage panel and popovers.</summary>
internal static class ProviderIcons
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Image?> Cache = new(StringComparer.Ordinal);

    internal static string? Resource(string providerId) => providerId switch
    {
        UsageProviderIds.Grok => "grok-official-icon.png",
        UsageProviderIds.Xai => "xai-official-icon.png",
        UsageProviderIds.Antigravity => "antigravity-official-icon.png",
        UsageProviderIds.Cursor => "cursor-official-icon.png",
        UsageProviderIds.OpenCode => "opencode-official-icon.png",
        UsageProviderIds.OpenRouter => "openrouter-official-icon.png",
        UsageProviderIds.DeepSeek => "deepseek-whale-icon.png",
        UsageProviderIds.Claude => "claude-icon-rounded.png",
        UsageProviderIds.Codex => "openai-official-ios-icon.png",
        _ => null,
    };

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
