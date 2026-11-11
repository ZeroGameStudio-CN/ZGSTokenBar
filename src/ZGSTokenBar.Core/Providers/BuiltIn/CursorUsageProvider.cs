namespace ZGSTokenBar.Core;

public sealed class CursorUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.Cursor, "Cursor", "#8b5cf6", 920, UsageSurface.Mini,
        "GET https://cursor.com/api/usage-summary with the local Cursor session");

    public override string IconResource => "cursor-official-icon.png";
    public override IReadOnlyList<string> Windows => ["auto", "api", "included"];

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) => UsageAccountDirectory.DetectCursor(context.Paths);

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => Get(
        "https://cursor.com/api/usage-summary",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Cookie"] = "WorkosCursorSessionToken=" + Uri.EscapeDataString(
                (credential.Fields.TryGetValue("user", out var user) ? user : credential.AccountId)
                + "::"
                + credential.Secret),
            ["Accept"] = "application/json",
        });

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseCursor(body);
}
