using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>OpenCode: local auth file.</summary>
public static partial class UsageAccountDirectory
{
    internal static IReadOnlyList<UsageCredential> DetectOpenCode(UsagePaths paths)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(paths.Home, ".local", "share", "opencode", "auth.json"),
            Path.Combine(paths.Home, ".config", "opencode", "auth.json"),
        })
        {
            var document = Read(candidate);
            if (document is null) continue;
            using (document)
            {
                var root = document.RootElement;
                var key = FirstString(root, "key", "apiKey", "api_key")
                    ?? NestedString(root, "opencode", "key")
                    ?? NestedString(root, "zen", "key");
                if (key is null) continue;
                return [new UsageCredential("opencode", "OpenCode", "opencode-auth", key)];
            }
        }

        return [];
    }
}
