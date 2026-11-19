using System.Text.RegularExpressions;

namespace ZGSTokenBar.App;

internal sealed record RouterTaskHeading(string Title, string Context, string Original);

internal static class RouterTaskPresentation
{
    internal static RouterTaskHeading Heading(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—") return new("暂无正在执行的任务", "", value);
        var original = value;
        var stage = Regex.Match(value, @"^\d+/\d+\s*·\s*");
        var prefix = stage.Success ? stage.Value.TrimEnd() + " " : "";
        if (stage.Success) value = value[stage.Length..];
        if (new[] { ".py", ".ps1", ".sh", ".cs" }.Any(extension => value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            return new(prefix + Path.GetFileName(value), "", original);
        var split = LastScopeSeparator(value);
        if (split < 0) return new(prefix + value, "", original);
        var context = value[..split];
        context = context[(LastScopeSeparator(context) + 1)..];
        return new(prefix + Words(value[(split + 1)..]), Words(context), original);
    }

    internal static string Source(string value)
    {
        var labelEnd = value.IndexOf(" · ", StringComparison.Ordinal);
        var label = labelEnd is > 0 and < 32 ? value[..(labelEnd + 3)] : "";
        var caption = value[label.Length..].Trim();
        while (caption.StartsWith("[$", StringComparison.Ordinal))
        {
            var link = Regex.Match(caption, @"^\[\$[^\]\r\n]+\]\((?:[^()\r\n]|\([^()\r\n]*\))*\)\s*");
            if (!link.Success) return label + "技能引用";
            caption = caption[link.Length..].TrimStart();
        }
        if (caption.StartsWith("## Referenced chats with Codex", StringComparison.OrdinalIgnoreCase))
        {
            const string request = "## My request:";
            var requestAt = caption.IndexOf(request, StringComparison.OrdinalIgnoreCase);
            caption = requestAt >= 0 ? caption[(requestAt + request.Length)..] : "引用对话";
        }
        caption = Regex.Replace(caption.TrimStart(), @"^#{1,6}\s+", "");
        if (caption.Length == 0 && value.AsSpan(label.Length).TrimStart().StartsWith("[$", StringComparison.Ordinal))
            caption = "技能引用";
        if (Guid.TryParse(caption, out _)) caption = "原对话";
        var text = Regex.Replace(label + caption, @"\s+", " ").Trim();
        return text.Length > 72 ? text[..71] + "…" : text;
    }

    private static int LastScopeSeparator(string value)
    {
        if (value.Length == 0 || !IsIdentifierStart(value[0])) return -1;
        var depth = 0;
        var result = -1;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (character is '(' or '[' or '<') depth++;
            else if (character is ')' or ']' or '>') depth--;
            else if (depth == 0)
            {
                if (character == '.')
                {
                    if (i + 1 >= value.Length || !IsIdentifierStart(value[i + 1])) return -1;
                    result = i;
                }
                else if (!char.IsAsciiLetterOrDigit(character) && character != '_') return -1;
            }
            if (depth < 0) return -1;
        }
        return depth == 0 ? result : -1;
    }
    private static bool IsIdentifierStart(char character) => char.IsAsciiLetter(character) || character == '_';
    private static string Words(string value) => Regex.Replace(value, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Replace("_", " · ");
}
