namespace ZGSTokenBar.Core;

public sealed class AntigravityUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.Antigravity, "Antigravity", "#4285f4", 910, UsageSurface.Mini,
        "agy /usage text or Cloud Code retrieveUserQuotaSummary");

    public override string IconResource => "antigravity-official-icon.png";
    public override IReadOnlyList<string> Windows => ["weekly", "5h"];

    // agy spends ~10 s of CPU starting up and its limits move slowly, so a reading is reused
    // instead of spawning it on every refresh.
    public override UsageCliSource Cli { get; } = new(
        "agy-cli",
        context => UsageProbes.AntigravityExecutable(context.Paths),
        // JSON first: it carries the exact remaining fractions; the text table is rounded.
        [["-p", "/usage", "--output-format", "json", "--print-timeout", "20s"], ["-p", "/usage", "--print-timeout", "20s"]],
        UsageParsers.ParseAntigravity,
        TimeSpan.FromMinutes(10))
    {
        Account = context => UsageAccountDirectory.AntigravitySignedInAccount(context.Paths),
    };

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) => UsageAccountDirectory.DetectAntigravity(context.Paths);

    public override bool IsInstalled(UsageDetectContext context) => UsageProbes.AntigravityPresent(context.Paths);

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => new(
        "POST",
        new Uri("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"),
        Bearer(credential),
        "{}");

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseAntigravity(body);
}
