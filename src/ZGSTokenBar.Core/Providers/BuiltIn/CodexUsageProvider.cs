namespace ZGSTokenBar.Core;

/// <summary>Codex usage is read by the existing quota pipeline and only projected onto the board.</summary>
public sealed class CodexUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.Codex, "Codex", "#10a37f", 110, UsageSurface.Projected,
        "Existing ChatGPT wham/usage read, one snapshot per local account");

    public override string IconResource => "openai-official-ios-icon.png";
    public override IReadOnlyList<string> Windows => ["5h", "7d"];
    public override bool FetchesUsage => false;
}
