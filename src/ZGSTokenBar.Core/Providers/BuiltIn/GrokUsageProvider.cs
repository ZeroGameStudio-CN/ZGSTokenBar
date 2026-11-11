namespace ZGSTokenBar.Core;

public sealed class GrokUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.Grok, "Grok", "#38bdf8", 900, UsageSurface.Mini,
        "GET https://cli-chat-proxy.grok.com/v1/billing?format=credits with ~/.grok/auth.json, else grok usage --json");

    public override string IconResource => "grok-official-icon.png";
    public override IReadOnlyList<string> Windows => ["credits"];

    public override UsageCliSource Cli { get; } = new(
        "grok-cli",
        context => UsageProbes.GrokExecutable(context.Paths, context.Environment),
        [["usage", "--json"]],
        UsageParsers.ParseGrok,
        TimeSpan.Zero);

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) =>
        UsageAccountDirectory.DetectGrok(context.Paths, context.Environment);

    public override bool IsInstalled(UsageDetectContext context) => UsageProbes.GrokPresent(context.Paths, context.Environment);

    /// <summary>
    /// An expired Grok CLI access token is renewed with the CLI's own refresh token (and written back
    /// for the CLI), so a signed-in CLI never shows "sign-in expired" just for being idle.
    /// </summary>
    public override async Task<UsageCredential> PrepareAsync(UsageCredential credential, UsageLoadRequest request, CancellationToken cancellationToken)
    {
        if (credential.Secret is null
            && credential.Fields.TryGetValue(GrokTokenRefresher.PathField, out var authPath)
            && credential.Fields.TryGetValue(GrokTokenRefresher.EntryField, out var authEntry)
            && await GrokTokenRefresher.RefreshAsync(authPath, authEntry, request.Transport, request.Now, cancellationToken)
                .ConfigureAwait(false) is { } renewed)
        {
            var fields = credential.Fields
                .Where(field => field.Key is not GrokTokenRefresher.PathField and not GrokTokenRefresher.EntryField)
                .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);
            return new UsageCredential(credential.AccountId, credential.Label, credential.SourceKind, renewed, fields);
        }

        return credential;
    }

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => Get(
        "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = BearerOf(credential),
            ["Accept"] = "application/json",
            ["x-xai-token-auth"] = "xai-grok-cli",
        });

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseGrok(body);

    public override IReadOnlyList<UsageAccountSnapshot> Enrich(IReadOnlyList<UsageAccountSnapshot> accounts, UsageLoadRequest request)
    {
        if (accounts.Count == 0) return accounts;
        var home = request.Environment.Get("GROK_HOME");
        var ledger = UsageLedgerReader.ReadGrok(
            string.IsNullOrWhiteSpace(home) ? Path.Combine(request.Paths.Home, ".grok") : home,
            request.Now);
        if (ledger is null) return accounts;
        var enriched = accounts.ToArray();
        enriched[0] = enriched[0] with { Ledger = ledger };
        return enriched;
    }
}
