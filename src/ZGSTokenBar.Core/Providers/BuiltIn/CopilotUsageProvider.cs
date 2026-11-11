namespace ZGSTokenBar.Core;

public sealed class CopilotUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.Copilot, "Copilot", "#6e40c9", 930, UsageSurface.Mini,
        "GET https://api.github.com/copilot_internal/user with the GitHub CLI token");

    public override IReadOnlyList<string> Windows => ["premium_interactions", "chat", "completions"];

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) => UsageAccountDirectory.DetectCopilot(context.Paths);

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => Get(
        "https://api.github.com/copilot_internal/user",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = BearerOf(credential),
            ["Accept"] = "application/json",
            ["User-Agent"] = "GitHubCopilotChat/0.26.7",
            ["Editor-Version"] = "vscode/1.96.2",
            ["Editor-Plugin-Version"] = "copilot-chat/0.26.7",
        });

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseCopilot(body);
}
