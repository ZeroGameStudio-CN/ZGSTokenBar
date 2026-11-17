namespace ZGSTokenBar.Core;

public sealed class AntigravityUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.Antigravity, "Antigravity", "#4285f4", 910, UsageSurface.Mini,
        "Cloud Code retrieveUserQuotaSummary; background reads never start interactive sign-in")
        { ActivityProcessNames = ["antigravity", "agy"] };

    public override string IconResource => "antigravity-official-icon.png";
    public override IReadOnlyList<string> Windows => ["weekly", "5h"];

    // Even print-mode /usage can launch browser sign-in when the CLI cannot authenticate.
    // A passive quota reader must never start that flow. Expired credentials stay unavailable.

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) => UsageAccountDirectory.DetectAntigravity(context.Paths);

    public override bool IsInstalled(UsageDetectContext context) => UsageProbes.AntigravityPresent(context.Paths);

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => new(
        "POST",
        new Uri("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"),
        Bearer(credential),
        "{}");

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseAntigravity(body);
}
