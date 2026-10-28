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
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value.Length > 72 ? value[..71] + "…" : value;
    }

    private static int LastScopeSeparator(string value)
    {
        var depth = 0; var result = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] is '(' or '[' or '<') depth++;
            else if (value[i] is ')' or ']' or '>') depth = Math.Max(0, depth - 1);
            else if (value[i] == '.' && depth == 0) result = i;
        }
        return result;
    }
    private static string Words(string value) => Regex.Replace(value, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Replace("_", " · ");
}
