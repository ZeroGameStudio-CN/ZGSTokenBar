namespace ZGSTokenBar.Core;

/// <summary>Where a provider looks for local sign-ins and installs.</summary>
public sealed record UsageDetectContext(UsagePaths Paths, IUsageEnvironment Environment, bool SkipPrimaryClaude);

/// <summary>
/// A provider's local CLI fallback: run when the HTTP read produced no usable reading.
/// <paramref name="Attempts"/> are tried in order until one parses; <paramref name="CacheFor"/>
/// reuses a reading for CLIs that are expensive to start.
/// </summary>
public sealed record UsageCliSource(
    string SourceKind,
    Func<UsageDetectContext, string?> Executable,
    IReadOnlyList<string[]> Attempts,
    Func<string?, UsageParseResult?> Parse,
    TimeSpan CacheFor)
{
    /// <summary>The signed-in account the CLI reads for (identity only), shown in the hover header.</summary>
    public Func<UsageDetectContext, string?>? Account { get; init; }
}

/// <summary>
/// Everything about one usage provider in one place: metadata, how its local sign-in is detected,
/// the usage request, how the response is parsed and any CLI fallback.
/// (Shape follows CodexBar's provider descriptors: steipete/CodexBar, nesszer/Win-CodexBar.)
/// </summary>
public interface IUsageProvider
{
    ProviderDefinition Definition { get; }

    /// <summary>Bundled icon file name under the app's Assets, or null.</summary>
    string? IconResource { get; }

    /// <summary>The usage windows the provider reports, for documentation and layout hints.</summary>
    IReadOnlyList<string> Windows { get; }

    /// <summary>How often the live read is repeated.</summary>
    TimeSpan RefreshInterval { get; }

    /// <summary>False when another pipeline owns the live read and the board only projects it.</summary>
    bool FetchesUsage { get; }

    UsageCliSource? Cli { get; }

    IReadOnlyList<UsageCredential> Detect(UsageDetectContext context);

    /// <summary>True when the tool is installed, so a loading card is shown before the first read.</summary>
    bool IsInstalled(UsageDetectContext context);

    /// <summary>Last chance to renew a credential before it is used (e.g. an expired OIDC token).</summary>
    Task<UsageCredential> PrepareAsync(UsageCredential credential, UsageLoadRequest request, CancellationToken cancellationToken);

    UsageHttpRequest? BuildRequest(UsageCredential credential);

    UsageParseResult? Parse(string? body);

    /// <summary>Adds local-only data (e.g. token ledgers) once the accounts are read.</summary>
    IReadOnlyList<UsageAccountSnapshot> Enrich(IReadOnlyList<UsageAccountSnapshot> accounts, UsageLoadRequest request);
}

/// <summary>Defaults for <see cref="IUsageProvider"/>; built-in providers override what they need.</summary>
public abstract class UsageProvider : IUsageProvider
{
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(2);

    public abstract ProviderDefinition Definition { get; }
    public virtual string? IconResource => null;
    public virtual IReadOnlyList<string> Windows => [];
    public virtual TimeSpan RefreshInterval => DefaultRefreshInterval;
    public virtual bool FetchesUsage => true;
    public virtual UsageCliSource? Cli => null;

    public virtual IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) => [];

    public virtual bool IsInstalled(UsageDetectContext context) => false;

    public virtual Task<UsageCredential> PrepareAsync(
        UsageCredential credential,
        UsageLoadRequest request,
        CancellationToken cancellationToken) => Task.FromResult(credential);

    public virtual UsageHttpRequest? BuildRequest(UsageCredential credential) => null;

    public virtual UsageParseResult? Parse(string? body) => null;

    public virtual IReadOnlyList<UsageAccountSnapshot> Enrich(IReadOnlyList<UsageAccountSnapshot> accounts, UsageLoadRequest request) => accounts;

    protected static string BearerOf(UsageCredential credential) => "Bearer " + credential.Secret;

    protected static Dictionary<string, string> Bearer(UsageCredential credential) => new(StringComparer.Ordinal)
    {
        ["Authorization"] = BearerOf(credential),
        ["Accept"] = "application/json",
    };

    protected static UsageHttpRequest Get(string url, IReadOnlyDictionary<string, string> headers) =>
        new("GET", new Uri(url), headers);
}
