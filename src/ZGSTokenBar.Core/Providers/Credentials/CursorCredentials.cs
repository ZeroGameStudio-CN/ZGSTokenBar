using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>Cursor: the local state database session.</summary>
public static partial class UsageAccountDirectory
{
    internal static IReadOnlyList<UsageCredential> DetectCursor(UsagePaths paths)
    {
        var accounts = new List<UsageCredential>();
        foreach (var candidate in new[]
        {
            Path.Combine(paths.ApplicationData, "Cursor", "User", "globalStorage", "state.vscdb"),
            Path.Combine(paths.Home, ".cursor", "auth.json"),
        })
        {
            var token = candidate.EndsWith(".vscdb", StringComparison.OrdinalIgnoreCase)
                ? ProbeSqliteText(candidate, "cursorAuth/accessToken")
                : ReadTokenFile(candidate, "accessToken", "access_token");
            if (token is null) continue;
            var user = JwtSubject(token) ?? "cursor-local";
            accounts.Add(new UsageCredential(user, "Cursor", "cursor-session", token, new Dictionary<string, string>
            {
                ["user"] = user,
            }));
            break;
        }

        return accounts;
    }
}
