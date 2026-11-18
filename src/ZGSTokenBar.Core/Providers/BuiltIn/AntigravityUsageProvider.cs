namespace ZGSTokenBar.Core;

public sealed class AntigravityUsageProvider : UsageProvider
{
    private readonly AntigravityLocalQuota _local = new();

    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.Antigravity, "Antigravity", "#4285f4", 910, UsageSurface.Mini,
        "AGY headless quota command or legacy Cloud Code quota summary")
        { ActivityProcessNames = ["antigravity", "agy"] };

    public override string IconResource => "antigravity-official-icon.png";
    public override IReadOnlyList<string> Windows => ["weekly", "5h"];

    public override Task<UsageAccountSnapshot?> ReadLocalAccountAsync(
        UsageDetectContext context, DateTimeOffset now, CancellationToken cancellationToken) => _local.ReadAsync(context.Paths, now, cancellationToken);

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) => UsageAccountDirectory.DetectAntigravity(context.Paths);

    public override bool IsInstalled(UsageDetectContext context) => UsageProbes.AntigravityPresent(context.Paths);

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => new(
        "POST",
        new Uri("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"),
        Bearer(credential),
        "{}");

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseAntigravity(body);
}
