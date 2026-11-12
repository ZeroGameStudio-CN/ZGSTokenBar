namespace ZGSTokenBar.Core;

/// <summary>The primary Claude login is read by the quota pipeline; this adds the extra accounts.</summary>
public sealed class ClaudeUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.Claude, "Claude", "#d97757", 100, UsageSurface.Projected,
        "Existing OAuth usage read; extra accounts from ~/.claude/accounts");

    public override string IconResource => "claude-icon-rounded.png";
    public override IReadOnlyList<string> Windows => ["5h", "7d"];

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) =>
        UsageAccountDirectory.DetectClaude(context.Paths, context.SkipPrimaryClaude);

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => Get(
        "https://api.anthropic.com/api/oauth/usage",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = BearerOf(credential),
            ["Accept"] = "application/json",
            ["anthropic-version"] = "2023-06-01",
            ["anthropic-beta"] = "oauth-2025-04-20",
            ["User-Agent"] = "claude-code/1.0",
        });

    public override UsageParseResult? Parse(string? body)
    {
        try
        {
            return body is null ? null : UsageParsers.FromClaude(ClaudeUsageParser.Parse(body));
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
