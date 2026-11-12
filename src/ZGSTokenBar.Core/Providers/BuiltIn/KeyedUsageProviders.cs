namespace ZGSTokenBar.Core;

public sealed class DeepSeekUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.DeepSeek, "DeepSeek", "#4d6bfe", 940, UsageSurface.Mini,
        "GET https://api.deepseek.com/user/balance; Harness balance still projects in");

    public override string IconResource => "deepseek-whale-icon.png";
    public override IReadOnlyList<string> Windows => ["balance"];

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) =>
        UsageAccountDirectory.DetectKeyed(context.Environment, "DEEPSEEK_API_KEY", "deepseek-api", "DeepSeek");

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => Get("https://api.deepseek.com/user/balance", Bearer(credential));

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseDeepSeek(body);
}

public sealed class OpenCodeUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.OpenCode, "OpenCode", "#f97316", 950, UsageSurface.Mini,
        "GET https://opencode.ai/zen/go/v1/usage with the local OpenCode key");

    public override string IconResource => "opencode-official-icon.png";
    public override IReadOnlyList<string> Windows => ["rolling", "weekly", "monthly"];

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) => UsageAccountDirectory.DetectOpenCode(context.Paths);

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => Get("https://opencode.ai/zen/go/v1/usage", Bearer(credential));

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseOpenCode(body);
}

public sealed class XaiUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.Xai, "xAI", "#a3e635", 960, UsageSurface.Mini,
        "GET https://management-api.x.ai/v1/billing/teams/{team}/prepaid/balance");

    public override string IconResource => "xai-official-icon.png";
    public override IReadOnlyList<string> Windows => ["balance"];

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) =>
        UsageAccountDirectory.DetectXai(context.Paths, context.Environment);

    public override UsageHttpRequest? BuildRequest(UsageCredential credential) =>
        credential.Fields.TryGetValue("team", out var team)
            ? Get("https://management-api.x.ai/v1/billing/teams/" + team + "/prepaid/balance", Bearer(credential))
            : null;

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseXai(body);
}

public sealed class OpenRouterUsageProvider : UsageProvider
{
    public override ProviderDefinition Definition { get; } = new(UsageProviderIds.OpenRouter, "OpenRouter", "#6467f2", 970, UsageSurface.Mini,
        "GET https://openrouter.ai/api/v1/credits");

    public override string IconResource => "openrouter-official-icon.png";
    public override IReadOnlyList<string> Windows => ["credits"];

    public override IReadOnlyList<UsageCredential> Detect(UsageDetectContext context) =>
        UsageAccountDirectory.DetectKeyed(context.Environment, "OPENROUTER_API_KEY", "openrouter-api", "OpenRouter");

    public override UsageHttpRequest BuildRequest(UsageCredential credential) => Get("https://openrouter.ai/api/v1/credits", Bearer(credential));

    public override UsageParseResult? Parse(string? body) => UsageParsers.ParseOpenRouter(body);
}
