using System.Text.RegularExpressions;

namespace ZGSTokenBar.Core;

/// <summary>
/// Last line of defence for diagnostics: masks bearer tokens, JWTs, API keys and token-like JSON
/// fields before an exception or note is written to errors.log / traces.
/// </summary>
public static partial class SecretRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = Bearer().Replace(text, "$1***");
        result = Jwt().Replace(result, "***jwt***");
        result = ApiKey().Replace(result, "$1***");
        result = JsonSecret().Replace(result, "$1\"***\"");
        return QuerySecret().Replace(result, "$1***");
    }

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9\-._~+/=]{8,}")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(sk-|xai-|ghp_|gho_|ghu_|github_pat_|sk-or-v1-)[A-Za-z0-9_\-]{8,}")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"(?i)(""(?:access_token|refresh_token|id_token|api_key|apiKey|token|secret|password|session_token)""\s*:\s*)""[^""]*""")]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"(?i)([?&](?:access_token|token|key|api_key|code)=)[^&\s""]+")]
    private static partial Regex QuerySecret();
}
