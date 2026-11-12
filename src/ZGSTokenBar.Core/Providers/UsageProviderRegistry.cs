namespace ZGSTokenBar.Core;

/// <summary>
/// The single list of usage providers. Order is the board/catalog order; adding a provider means
/// adding one <see cref="IUsageProvider"/> here and nothing else.
/// </summary>
public static class UsageProviderRegistry
{
    public static IReadOnlyList<IUsageProvider> All { get; } =
    [
        new GrokUsageProvider(),
        new ClaudeUsageProvider(),
        new CodexUsageProvider(),
        new CursorUsageProvider(),
        new AntigravityUsageProvider(),
        new CopilotUsageProvider(),
        new DeepSeekUsageProvider(),
        new OpenCodeUsageProvider(),
        new XaiUsageProvider(),
        new OpenRouterUsageProvider(),
    ];

    private static readonly Dictionary<string, IUsageProvider> ById =
        All.ToDictionary(provider => provider.Definition.Id, StringComparer.Ordinal);

    public static IUsageProvider? Find(string? id) =>
        id is not null && ById.TryGetValue(id, out var provider) ? provider : null;

    /// <summary>The shortest refresh any provider asks for; drives the shared refresh timer.</summary>
    public static TimeSpan RefreshInterval =>
        All.Where(provider => provider.FetchesUsage).Select(provider => provider.RefreshInterval).DefaultIfEmpty(UsageProvider.DefaultRefreshInterval).Min();
}
